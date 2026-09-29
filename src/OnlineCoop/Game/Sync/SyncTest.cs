using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BALLxPITLocalCoop;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game.Sync;

internal enum SyncTestMode { Record, Check }

/// <summary>
/// Step one towards playing on two PCs that both run the game: find out whether the game plays out
/// exactly the same way twice when it gets the same start and the same input. Two copies of the game
/// can only stay together over the internet if it does ("lockstep": each PC runs the whole game and
/// only the players' inputs travel).
///
/// A test run holds everything that could differ on purpose: every frame advances the game by exactly
/// 1/60 s, the run's random seed is fixed (and the game's other random number generators are
/// reseeded), and your controls are replaced by a fixed pattern (stand still, aim up, keep shooting).
/// Each frame's state is recorded. "Record" saves it to BepInEx/sync-test-recording.json; "Check"
/// plays the same character and level again, on this PC or a friend's, and reports the first frame
/// that comes out differently and what differed.
/// </summary>
internal static class SyncTest
{
    public const int TestSeed = 20260929;
    /// <summary>
    /// 3: frame 0 is when the fight starts with P2 released from its walk-in and the extra random
    /// number generators reseeded. 2: generators fingerprinted by their whole state. 1 read nothing.
    /// </summary>
    private const int RecordingFormat = 3;
    public const int TestFrames = 60 * FixedFrameClock.FramesPerSecond;
    private const int MaxWaitForPlayerTwo = 2 * FixedFrameClock.FramesPerSecond;
    private const int MaxStartFrames = 180 * FixedFrameClock.FramesPerSecond;

    private enum Phase { Off, WaitingForRun, Starting, Running, Done }

    private static Phase _phase;
    private static SyncTestMode _mode;
    private static bool _patched;
    private static string _patchProblems = "";

    private static Recording? _reference;
    private static readonly List<double[]> Frames = new();
    private static List<(string Name, string Value)> _setup = new();
    private static int _startFrames;
    private static int _stepsThisFrame;
    private static double _gameTimeAtStart, _physicsTimeAtStart;
    private static bool _seedForced, _stabilized, _clockFixed, _gridSeeded;
    private static IntPtr _saveWhenArmed;
    private static bool _sawLevelLoad;
    private static GameState _lastState = GameState.kNum;
    private static int _firstMismatch = -1;
    private static int _mismatchedFrames;
    /// <summary>For each part of the game (enemies, balls, ...): the first frame it differed, and how.</summary>
    private static readonly Dictionary<string, (int Frame, string Text)> GroupMismatch = new();
    private static int _waitedForPlayerTwo;
    private static string _endReason = "";

    private static string _status = "";
    private static readonly List<string> ReportLines = new();

    public static bool IsActive => _phase is Phase.WaitingForRun or Phase.Starting or Phase.Running;
    private static bool ControlsHeld => _phase is Phase.Starting or Phase.Running;
    public static string Status => _status;
    public static IReadOnlyList<string> Report => ReportLines;
    public static string RecordingPath => Path.Combine(Paths.BepInExRootPath, "sync-test-recording.json");
    public static string ResultPath => Path.Combine(Paths.BepInExRootPath, "sync-test-result.txt");
    public static bool HasRecording => File.Exists(RecordingPath);

    /// <summary>Game thread. <paramref name="guestPlaying"/>: someone controls P2 from a browser (their input would differ).</summary>
    public static void Begin(SyncTestMode mode, bool guestPlaying)
    {
        if (IsActive) return;
        ReportLines.Clear();
        if (guestPlaying)
        {
            _status = "Kick P2 or stop hosting first: a guest's input would make the runs differ.";
            return;
        }
        _reference = null;
        if (mode == SyncTestMode.Check)
        {
            try
            {
                _reference = Recording.Load(RecordingPath);
            }
            catch (Exception ex)
            {
                _status = "Couldn't read the recording: " + ex.Message;
                return;
            }
        }
        if (!InstallPatches())
        {
            _status = "The test can't run in this game version: " + _patchProblems;
            return;
        }

        _mode = mode;
        Frames.Clear();
        _setup = new();
        _startFrames = 0;
        _stepsThisFrame = 0;
        _seedForced = _stabilized = _clockFixed = _gridSeeded = false;
        _sawLevelLoad = false;
        _lastState = GameState.kNum;
        _firstMismatch = -1;
        _mismatchedFrames = 0;
        GroupMismatch.Clear();
        _waitedForPlayerTwo = 0;
        _endReason = "";
        _saveWhenArmed = BattleSaveData.I?.Pointer ?? IntPtr.Zero;
        _phase = Phase.WaitingForRun;
        _status = mode == SyncTestMode.Record
            ? "Start a new run (any character and level) or restart one. Then don't touch anything for a minute."
            : $"Start or restart a run with {_reference!.Describe()}. Then don't touch anything for a minute.";
        Plugin.Logger.LogInfo($"Sync test ({mode}) armed.");
    }

    public static void Stop(string reason = "stopped by you")
    {
        if (!IsActive) return;
        Finish(reason);
    }

    // ---- Patches ------------------------------------------------------------------------------

    private static bool InstallPatches()
    {
        if (_patched) return true;
        var problems = new List<string>();
        var harmony = new Harmony(Plugin.PluginGuid + ".synctest");
        void Patch(MethodInfo? target, string what, string? prefix = null, string? postfix = null, bool required = true)
        {
            try
            {
                if (target == null) throw new MissingMethodException(what);
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SyncTest), prefix)) { priority = Priority.First },
                    postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SyncTest), postfix)));
            }
            catch (Exception ex)
            {
                if (required) problems.Add(what);
                Plugin.Logger.LogWarning($"Sync test: couldn't patch {what}: {ex.Message}");
            }
        }
        Patch(AccessTools.Method(typeof(GridMgr), "InitGrid", new[] { typeof(LoadMode) }), "GridMgr.InitGrid", nameof(InitGridPrefix), nameof(InitGridPostfix));
        Patch(AccessTools.Method(typeof(SaveMgr), "StartNewGame", Type.EmptyTypes), "SaveMgr.StartNewGame", postfix: nameof(StartNewGamePostfix), required: false);
        Patch(AccessTools.Method(typeof(TimeMgr), "RunFixedUpdate", new[] { typeof(float) }), "TimeMgr.RunFixedUpdate", postfix: nameof(RunFixedUpdatePostfix), required: false);
        Patch(AccessTools.Method(typeof(InputMgr), "GetAxis", new[] { typeof(GameActionType) }), "InputMgr.GetAxis", nameof(GetAxisPrefix));
        Patch(AccessTools.Method(typeof(InputMgr), "IsBtnHeld", new[] { typeof(GameActionType) }), "InputMgr.IsBtnHeld", nameof(IsBtnHeldPrefix));
        Patch(AccessTools.Method(typeof(InputMgr), "IsBtnDown", new[] { typeof(GameActionType) }), "InputMgr.IsBtnDown", nameof(IsBtnDownPrefix));
        Patch(AccessTools.Method(typeof(InputMgr), "IsBtnUp", new[] { typeof(GameActionType) }), "InputMgr.IsBtnUp", nameof(IsBtnUpPrefix));
        Patch(AccessTools.Method(typeof(Player), "GetMouseWorldPos", Type.EmptyTypes), "Player.GetMouseWorldPos", nameof(MouseWorldPosPrefix), required: false);
        _patchProblems = string.Join(", ", problems);
        _patched = problems.Count == 0;
        return _patched;
    }

    /// <summary>A new run was set up (from the menu, or a restart): fix its seed and the frame clock.</summary>
    private static void StartNewGamePostfix()
    {
        if (_phase != Phase.WaitingForRun) return;
        Plugin.Logger.LogInfo("Sync test: the game set up a new run.");
        BeginRun(); // catches its own errors
    }

    /// <summary>
    /// The level is being laid out: the seed must be fixed before anything is generated. A new game or a
    /// restarted floor counts as a new run; loading a saved run doesn't.
    /// </summary>
    private static void InitGridPrefix(LoadMode loadMode)
    {
        try
        {
            if (!IsActive) return;
            Plugin.Logger.LogInfo($"Sync test: the game lays out a level ({loadMode}).");
            if (loadMode is not (LoadMode.kNewGame or LoadMode.kResetFloor)) return;
            if (_phase == Phase.WaitingForRun) BeginRun();
            else if (_phase == Phase.Starting && !_gridSeeded) ForceSeed();
            _gridSeeded = _phase == Phase.Starting;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Sync test (seed): " + ex);
        }
    }

    private static void BeginRun()
    {
        try
        {
            ForceSeed();
            _clockFixed = FixedFrameClock.Engage();
            _phase = Phase.Starting;
            _startFrames = 0;
            _status = "Test run starting: hands off.";
            Plugin.Logger.LogInfo($"Sync test: new run, seed {TestSeed}, clock {(_clockFixed ? "fixed at 1/60 s per frame" : "NOT fixed: " + EngineCalls.Missing)}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Sync test (start): " + ex);
        }
    }

    private static void ForceSeed()
    {
        BattleSaveData save = BattleSaveData.I;
        if (save != null)
        {
            save.Seed = TestSeed;
            _seedForced = true;
        }
        else
        {
            Plugin.Logger.LogWarning("Sync test: there's no run data yet to fix the seed in.");
        }
        EngineCalls.InitRandom(TestSeed);
    }

    /// <summary>
    /// The level's extra generators (effects and the like) get used every frame, also while the level
    /// loads, which takes a frame or two more or less each time: reseed them when the fight starts.
    /// </summary>
    private static void ReseedExtraRandom()
    {
        GridMgr grid = GridMgr.I;
        if (grid != null) grid.MiscRnd = new Il2CppSystem.Random(TestSeed + 1);
        ThreadSafeRandom._global = new Il2CppSystem.Random(TestSeed + 2);
        ThreadSafeRandom._local = new Il2CppSystem.Random(TestSeed + 3);
        EngineCalls.InitRandom(TestSeed + 4);
    }

    // Local Coop creates P2 on a quarter-second scan and then lets it walk in behind P1 for about a
    // second: both land on different frames from run to run. While the test run loads, P2 is looked
    // for every frame and kept walking in; the fight's first frame releases it.
    private static void HoldPlayerTwoIntro()
    {
        PlayerTwoController controller = PlayerTwoController._instance;
        if (controller == null) return;
        controller._nextScanTime = 0f;
        if (controller._playerTwo == null) return;
        float now = Time.unscaledTime;
        controller._isFollowingIntro = true;
        controller._introStartedAt = now;
        controller._introStableSince = now;
        controller._introObservedPlayerOneMovement = false;
    }

    private static bool PlayerTwoExpected => PlayerTwoController._instance != null;

    private static bool PlayerTwoReady => PlayerTwoController._instance?._playerTwo != null;

    private static void ReleasePlayerTwoIntro()
    {
        PlayerTwoController controller = PlayerTwoController._instance;
        if (controller == null || controller._playerTwo == null || !controller._isFollowingIntro) return;
        controller._isFollowingIntro = false;
        controller.ApplyPlayerTwoMovementOverrides(false);
    }

    /// <summary>Reseed the level's other random number generators, in case they were seeded from the clock.</summary>
    private static void InitGridPostfix(LoadMode loadMode)
    {
        try
        {
            if (_phase != Phase.Starting || loadMode is not (LoadMode.kNewGame or LoadMode.kResetFloor) || _stabilized) return;
            GridMgr grid = GridMgr.I;
            if (grid != null) grid.MiscRnd = new Il2CppSystem.Random(TestSeed + 1);
            ThreadSafeRandom._global = new Il2CppSystem.Random(TestSeed + 2);
            ThreadSafeRandom._local = new Il2CppSystem.Random(TestSeed + 3);
            _stabilized = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Sync test (random numbers): " + ex.Message);
        }
    }

    private static void RunFixedUpdatePostfix()
    {
        if (ControlsHeld) _stepsThisFrame++;
    }

    // Your controls during a test run: stand still, aim up, keep shooting (a fresh press every second).
    private static bool IsHeldAction(GameActionType t) => t is GameActionType.kShoot or GameActionType.kAimHorizontal
        or GameActionType.kMoveHorizontal or GameActionType.kAimVertical or GameActionType.kMoveVertical or GameActionType.kAutofire
        or GameActionType.kIncreaseSpeed or GameActionType.kDecreaseSpeed or GameActionType.kLevelUp;

    private static bool ForPlayerOne => ControlsHeld && !NativePlayerContext.IsPlayerTwoContext;

    private static bool GetAxisPrefix(GameActionType t, ref float __result)
    {
        if (!ForPlayerOne || !IsHeldAction(t)) return true;
        __result = 0f;
        return false;
    }

    private static bool IsBtnHeldPrefix(GameActionType t, ref bool __result)
    {
        if (!ForPlayerOne || !IsHeldAction(t)) return true;
        __result = t == GameActionType.kShoot && _phase == Phase.Running;
        return false;
    }

    private static bool IsBtnDownPrefix(GameActionType t, ref bool __result)
    {
        if (!ForPlayerOne || !IsHeldAction(t)) return true;
        __result = t == GameActionType.kShoot && _phase == Phase.Running && Frames.Count % FixedFrameClock.FramesPerSecond == 0;
        return false;
    }

    private static bool IsBtnUpPrefix(GameActionType t, ref bool __result)
    {
        if (!ForPlayerOne || !IsHeldAction(t)) return true;
        __result = false;
        return false;
    }

    private static bool MouseWorldPosPrefix(Player __instance, ref Vector3 __result)
    {
        if (!ControlsHeld || __instance == null || NativePlayerContext.IsPlayerTwo(__instance)) return true;
        try
        {
            Vector3 pos = NativePlayerContext.GetPlayerPosition(__instance);
            __result = new Vector3(pos.x, pos.y + 5f, pos.z);
            return false;
        }
        catch
        {
            return true;
        }
    }

    // ---- Every frame -----------------------------------------------------------------------------

    /// <summary>After the frame (WaitForEndOfFrame).</summary>
    public static void OnEndOfFrame()
    {
        if (!IsActive) return;
        try
        {
            GameMgr gameMgr = GameMgr.I;
            GameState now = gameMgr != null ? gameMgr.CurState : GameState.kNum;
            if (now != _lastState)
            {
                Plugin.Logger.LogInfo($"Sync test: game state {_lastState} -> {now} ({_phase}).");
                _lastState = now;
            }
            switch (_phase)
            {
                case Phase.WaitingForRun:
                    CheckForUnseededRun();
                    break;
                case Phase.Starting:
                    WaitForPlay();
                    if (_phase == Phase.Starting) HoldPlayerTwoIntro();
                    break;
                case Phase.Running:
                    RunFrame();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Sync test failed: " + ex);
            Finish("the test itself failed (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
        _stepsThisFrame = 0;
    }

    /// <summary>
    /// If a new run reaches play without the game's run-start calls being seen, the test still runs,
    /// just without a fixed seed. (Only once play has begun: while it loads, they may still come.)
    /// </summary>
    private static void CheckForUnseededRun()
    {
        BattleSaveData save = BattleSaveData.I;
        GameMgr game = GameMgr.I;
        if (save == null || game == null) return;
        if (game.CurState == GameState.kEnteringLvl) _sawLevelLoad = true;
        if (game.CurState != GameState.kPlaying || (!_sawLevelLoad && save.Pointer == _saveWhenArmed)) return;
        _clockFixed = FixedFrameClock.Engage();
        _phase = Phase.Starting;
        Plugin.Logger.LogWarning("Sync test: the run started without the seed hook; its random seed isn't fixed.");
    }

    private static void WaitForPlay()
    {
        GameMgr game = GameMgr.I;
        if (game == null || game.CurState != GameState.kPlaying)
        {
            if (++_startFrames > MaxStartFrames) Finish("the run didn't start within 3 minutes");
            return;
        }
        // The fight has begun; with Local Coop, wait (briefly) for P2 so both start together.
        if (PlayerTwoExpected && !PlayerTwoReady && ++_waitedForPlayerTwo <= MaxWaitForPlayerTwo) return;
        ReleasePlayerTwoIntro();
        ReseedExtraRandom();
        _stabilized = true;
        TimeMgr time = TimeMgr.I;
        _gameTimeAtStart = time != null ? time._gameTime : 0;
        _physicsTimeAtStart = time != null ? time._physicsTime : 0;
        _setup = SyncProbe.Setup();
        _phase = Phase.Running;
        Plugin.Logger.LogInfo($"Sync test: playing after {_startFrames} loading frames"
            + (_waitedForPlayerTwo > 0 ? $" and {_waitedForPlayerTwo} frames waiting for P2" : "") + ". Setup: "
            + string.Join("; ", _setup.Where(s => !s.Name.StartsWith("stat ", StringComparison.Ordinal)).Select(s => $"{s.Name}={s.Value}")));

        if (_reference != null)
        {
            string? mismatch = _reference.StartMismatch(_setup);
            if (mismatch != null)
            {
                Finish(mismatch);
                return;
            }
        }
        RunFrame();
    }

    private static void RunFrame()
    {
        GameMgr game = GameMgr.I;
        GameState state = game != null ? game.CurState : GameState.kNum;
        if (state != GameState.kPlaying)
        {
            Finish(state switch
            {
                GameState.kLevelUp or GameState.kBonusBall or GameState.kBonusPassive => "a level-up screen opened (the test covers the fight until then)",
                GameState.kPaused => "the game was paused",
                GameState.kGameOver or GameState.kRevive or GameState.kEndingGame => "the run ended (you died or finished)",
                _ => $"the game left play ({state})",
            });
            return;
        }

        double[] sample = SyncProbe.Sample(_stepsThisFrame, _gameTimeAtStart, _physicsTimeAtStart);
        int index = Frames.Count;
        Frames.Add(sample);
        if (_reference != null)
        {
            if (index >= _reference.Frames.Count)
            {
                Finish($"the recording ends here (it stopped because {_reference.EndReason})");
                return;
            }
            List<(string Group, string Text)> differences = Compare(_reference.Frames[index], sample);
            if (differences.Count > 0)
            {
                _mismatchedFrames++;
                if (_firstMismatch < 0) _firstMismatch = index;
                foreach (var (group, text) in differences)
                {
                    if (GroupMismatch.ContainsKey(group)) continue;
                    GroupMismatch[group] = (index, text);
                    Plugin.Logger.LogWarning($"Sync test: {group} first differ at frame {index}: {text}");
                }
            }
        }
        // Keeps going after a difference: which parts drift, and which stay together, is the point.
        int limit = _reference?.Frames.Count ?? TestFrames;
        _status = $"Test run: {Seconds(index)} s of {Seconds(limit)} s, hands off"
            + (_reference == null ? "" : _firstMismatch < 0 ? ", in sync so far" : $", {GroupMismatch.Count} part(s) differ so far");
        if (Frames.Count >= limit) Finish(_reference != null ? "it reached the end of the recording" : "a minute had passed");
    }

    /// <summary>The parts of the game whose values differ between the frames, each with how.</summary>
    private static List<(string Group, string Text)> Compare(double[] expected, double[] actual)
    {
        var byGroup = new List<(string Group, string Text)>();
        int n = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) <= SyncProbe.Tolerance[i]) continue;
            string group = SyncProbe.Groups[i];
            string text = $"{SyncProbe.Names[i]} {Format(actual[i])} here, {Format(expected[i])} in the recording";
            int at = byGroup.FindIndex(g => g.Group == group);
            if (at < 0) byGroup.Add((group, text));
            else byGroup[at] = (group, byGroup[at].Text + ", " + text);
        }
        return byGroup;
    }

    private static string Format(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string Seconds(int frames) => (frames / (double)FixedFrameClock.FramesPerSecond).ToString("0.0", CultureInfo.InvariantCulture);

    // ---- Results ---------------------------------------------------------------------------------

    private static void Finish(string reason)
    {
        FixedFrameClock.Release();
        _endReason = reason;
        _phase = Phase.Done;
        ReportLines.Clear();
        var notes = new List<string>();
        if (!_seedForced) notes.Add("the run's seed wasn't fixed");
        if (!_clockFixed) notes.Add("the frame clock wasn't fixed (" + (EngineCalls.Missing.Length > 0 ? EngineCalls.Missing : "unavailable") + ")");
        if (!_stabilized) notes.Add("the other random number generators weren't reseeded");
        string caveat = notes.Count > 0 ? " Note: " + string.Join(", ", notes) + "." : "";

        if (Frames.Count == 0)
        {
            _status = "Test stopped before the run began: " + reason + "."
                + (_startFrames == 0 && !_seedForced ? " It starts when you start a new run from the menu or restart one." : "");
            ReportLines.Add(_status);
        }
        else if (_mode == SyncTestMode.Record)
        {
            try
            {
                new Recording(_setup, Frames, _startFrames, reason, _seedForced, _stabilized, _clockFixed).Save(RecordingPath);
                _status = $"Recorded {Seconds(Frames.Count)} s (stopped because {reason}).{caveat}";
                ReportLines.Add(_status);
                ReportLines.Add("Next: click \"Check\" and start the same character and level again, hands off.");
                ReportLines.Add("For your friend's PC: send them BepInEx\\sync-test-recording.json to put in their BepInEx folder.");
            }
            catch (Exception ex)
            {
                _status = "Couldn't save the recording: " + ex.Message;
                ReportLines.Add(_status);
            }
        }
        else
        {
            if (_firstMismatch < 0)
            {
                _status = $"IN SYNC: all {Frames.Count} frames ({Seconds(Frames.Count)} s) matched the recording exactly.{caveat}";
                ReportLines.Add(_status);
                ReportLines.Add($"(Stopped because {reason}.)");
            }
            else
            {
                _status = $"OUT OF SYNC: first difference after {Seconds(_firstMismatch)} s of {Seconds(Frames.Count)} s.{caveat}";
                ReportLines.Add(_status);
                foreach (string group in SyncProbe.Groups.Distinct())
                {
                    ReportLines.Add(GroupMismatch.TryGetValue(group, out var m)
                        ? $"  {group}: differ from {Seconds(m.Frame)} s ({m.Text})"
                        : $"  {group}: in sync the whole time");
                }
            }
            foreach (string line in _reference!.SetupDifferences(_setup).Take(6)) ReportLines.Add("Setup: " + line);
        }
        if (ReportLines.Count == 0) ReportLines.Add(_status);
        WriteResult(reason);
        Plugin.Logger.LogInfo("Sync test finished: " + string.Join(" | ", ReportLines));
    }

    private static void WriteResult(string reason)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BALLxPIT Online Co-op {Plugin.PluginVersion} sync test, {DateTime.Now:yyyy-MM-dd HH:mm}, mode {_mode}");
            foreach (string line in ReportLines) sb.AppendLine(line);
            sb.AppendLine($"Ended because: {reason}. Frames: {Frames.Count}. Loading frames: {_startFrames}. Waited for P2: {_waitedForPlayerTwo}. Mismatched frames: {_mismatchedFrames}.");
            sb.AppendLine($"Seed fixed: {_seedForced}. Random numbers reseeded: {_stabilized}. Clock fixed: {_clockFixed}.");
            sb.AppendLine("Setup here:");
            foreach (var (name, value) in _setup) sb.AppendLine($"  {name} = {value}");
            // The frames around where each part first drifted (at most four places).
            Recording? reference = _reference;
            var starts = reference == null ? new List<int>() : GroupMismatch.Values.Select(m => m.Frame).Distinct().OrderBy(f => f).Take(4).ToList();
            foreach (int start in starts)
            {
                sb.AppendLine($"Frames around {Seconds(start)} s (here / recording):");
                for (int f = Math.Max(0, start - 2); f <= Math.Min(Frames.Count - 1, start + 3); f++)
                {
                    sb.AppendLine($"  frame {f}:");
                    for (int i = 0; i < SyncProbe.Names.Length; i++)
                    {
                        double a = Frames[f][i], b = f < reference!.Frames.Count ? reference.Frames[f][i] : double.NaN;
                        sb.AppendLine($"    {SyncProbe.Names[i],-24} {Format(a),16} {Format(b),16}{(Math.Abs(a - b) > SyncProbe.Tolerance[i] ? "  <--" : "")}");
                    }
                }
            }
            File.WriteAllText(ResultPath, sb.ToString());
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Couldn't write the sync test result: " + ex.Message);
        }
    }

    /// <summary>A banner while a test is on, so nobody touches anything. OnGUI.</summary>
    public static void DrawBanner()
    {
        if (!IsActive) return;
        float width = Math.Min(760f, Screen.width - 20f);
        if (GUI.Button(new Rect((Screen.width - width) / 2f, 50f, width, 30f), "SYNC TEST: " + _status + "   (click to stop)"))
            Stop();
    }

    // ---- The recording file ------------------------------------------------------------------------

    private sealed class Recording
    {
        public readonly List<(string Name, string Value)> Setup;
        public readonly List<double[]> Frames;
        public readonly int StartFrames;
        public readonly string EndReason;
        public readonly bool SeedForced, Stabilized, ClockFixed;

        public Recording(List<(string, string)> setup, List<double[]> frames, int startFrames, string endReason, bool seedForced, bool stabilized, bool clockFixed)
        {
            Setup = setup;
            Frames = frames;
            StartFrames = startFrames;
            EndReason = endReason;
            SeedForced = seedForced;
            Stabilized = stabilized;
            ClockFixed = clockFixed;
        }

        private string Get(string name) => Setup.FirstOrDefault(s => s.Name == name).Value ?? "?";

        public string Describe() => $"character {Pretty(Get("character"))} on {Pretty(Get("level"))} (difficulty {Get("difficulty")}, NG+ {Get("new game plus")})";

        private static string Pretty(string enumName) => enumName.Length > 1 && enumName[0] == 'k' ? enumName.Substring(1) : enumName;

        /// <summary>Why this run can't be compared with the recording at all, or null.</summary>
        public string? StartMismatch(List<(string Name, string Value)> setup)
        {
            foreach (string key in new[] { "character", "level", "difficulty", "new game plus" })
            {
                string here = setup.FirstOrDefault(s => s.Name == key).Value ?? "?";
                if (here != Get(key)) return $"this run is {key} {Pretty(here)}, the recording is {Pretty(Get(key))}. Start {Describe()}";
            }
            return null;
        }

        public IEnumerable<string> SetupDifferences(List<(string Name, string Value)> setup)
        {
            var mine = setup.ToDictionary(s => s.Name, s => s.Value);
            foreach (var (name, value) in Setup)
            {
                if (!mine.TryGetValue(name, out string? here)) continue;
                if (here != value) yield return $"{name} is {here} here, {value} in the recording";
            }
        }

        public void Save(string path)
        {
            using var stream = File.Create(path);
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteNumber("format", RecordingFormat);
            json.WriteString("mod", Plugin.PluginVersion);
            json.WriteNumber("seed", TestSeed);
            json.WriteBoolean("seedForced", SeedForced);
            json.WriteBoolean("stabilized", Stabilized);
            json.WriteBoolean("clockFixed", ClockFixed);
            json.WriteNumber("startFrames", StartFrames);
            json.WriteString("end", EndReason);
            json.WriteStartArray("setup");
            foreach (var (name, value) in Setup)
            {
                json.WriteStartArray();
                json.WriteStringValue(name);
                json.WriteStringValue(value);
                json.WriteEndArray();
            }
            json.WriteEndArray();
            json.WriteStartArray("names");
            foreach (string name in SyncProbe.Names) json.WriteStringValue(name);
            json.WriteEndArray();
            json.WriteStartArray("frames");
            foreach (double[] frame in Frames)
            {
                json.WriteStartArray();
                foreach (double v in frame) json.WriteNumberValue(double.IsFinite(v) ? v : 0);
                json.WriteEndArray();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        public static Recording Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("there is no recording yet; record one first (or put your friend's in the BepInEx folder)");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("format").GetInt32() != RecordingFormat)
                throw new InvalidDataException("it was made by an older version of the test; record a new one with this version (and send that one to your friend)");
            string[] names = root.GetProperty("names").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            if (!names.SequenceEqual(SyncProbe.Names)) throw new InvalidDataException("it was made by a different version of the mod; record a new one with this version");
            var setup = root.GetProperty("setup").EnumerateArray()
                .Select(e => (e[0].GetString() ?? "", e[1].GetString() ?? "")).ToList();
            var frames = root.GetProperty("frames").EnumerateArray()
                .Select(f => f.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToList();
            return new Recording(setup, frames, root.GetProperty("startFrames").GetInt32(), root.GetProperty("end").GetString() ?? "",
                root.GetProperty("seedForced").GetBoolean(), root.GetProperty("stabilized").GetBoolean(), root.GetProperty("clockFixed").GetBoolean());
        }
    }
}
