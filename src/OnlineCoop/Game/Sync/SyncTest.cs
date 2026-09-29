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
    public const int TestFrames = 60 * FixedFrameClock.FramesPerSecond;
    private const int FramesAfterMismatch = 3 * FixedFrameClock.FramesPerSecond;
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
    private static bool _seedForced, _stabilized, _clockFixed;
    private static IntPtr _saveWhenArmed;
    private static int _firstMismatch = -1;
    private static int _mismatchedFrames;
    private static string _firstMismatchText = "";
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
        _seedForced = _stabilized = _clockFixed = false;
        _firstMismatch = -1;
        _mismatchedFrames = 0;
        _firstMismatchText = "";
        _endReason = "";
        _saveWhenArmed = BattleSaveData.I?.Pointer ?? IntPtr.Zero;
        _phase = Phase.WaitingForRun;
        _status = mode == SyncTestMode.Record
            ? "Start a NEW run now (any character and level). Then don't touch anything for a minute."
            : $"Start a NEW run with {_reference!.Describe()}. Then don't touch anything for a minute.";
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

    /// <summary>A new run is being laid out: fix its seed and the frame clock before anything is generated.</summary>
    private static void InitGridPrefix(LoadMode loadMode)
    {
        try
        {
            if (_phase != Phase.WaitingForRun || loadMode != LoadMode.kNewGame) return;
            BattleSaveData save = BattleSaveData.I;
            if (save != null)
            {
                save.Seed = TestSeed;
                _seedForced = true;
            }
            EngineCalls.InitRandom(TestSeed);
            _clockFixed = FixedFrameClock.Engage();
            _phase = Phase.Starting;
            _startFrames = 0;
            _status = "Test run starting: hands off.";
            Plugin.Logger.LogInfo($"Sync test: new run, seed {TestSeed}, clock {(_clockFixed ? "fixed at 1/60 s per frame" : "NOT fixed: " + EngineCalls.Missing)}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Sync test (seed): " + ex);
        }
    }

    /// <summary>Reseed the level's other random number generators, in case they were seeded from the clock.</summary>
    private static void InitGridPostfix(LoadMode loadMode)
    {
        try
        {
            if (_phase != Phase.Starting || loadMode != LoadMode.kNewGame || _stabilized) return;
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
            switch (_phase)
            {
                case Phase.WaitingForRun:
                    CheckForUnseededRun();
                    break;
                case Phase.Starting:
                    WaitForPlay();
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
    /// If a new run reaches play without going through InitGrid, the test still runs, just without a
    /// fixed seed. (Only once play has begun: while it loads, InitGrid may still be on its way.)
    /// </summary>
    private static void CheckForUnseededRun()
    {
        BattleSaveData save = BattleSaveData.I;
        GameMgr game = GameMgr.I;
        if (save == null || game == null || save.Pointer == _saveWhenArmed) return;
        if (game.CurState != GameState.kPlaying) return;
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
        TimeMgr time = TimeMgr.I;
        _gameTimeAtStart = time != null ? time._gameTime : 0;
        _physicsTimeAtStart = time != null ? time._physicsTime : 0;
        _setup = SyncProbe.Setup();
        _phase = Phase.Running;
        Plugin.Logger.LogInfo($"Sync test: playing after {_startFrames} loading frames. Setup: "
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
                GameState.kGameOver or GameState.kRevive => "you died",
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
            string? difference = Compare(_reference.Frames[index], sample);
            if (difference != null)
            {
                _mismatchedFrames++;
                if (_firstMismatch < 0)
                {
                    _firstMismatch = index;
                    _firstMismatchText = difference;
                    Plugin.Logger.LogWarning($"Sync test: frame {index} differs: {difference}");
                }
            }
            if (_firstMismatch >= 0 && index - _firstMismatch >= FramesAfterMismatch)
            {
                Finish("it had already gone out of sync");
                return;
            }
        }
        int limit = _reference?.Frames.Count ?? TestFrames;
        _status = _firstMismatch >= 0
            ? $"Out of sync at {Seconds(_firstMismatch)} s. Finishing..."
            : $"Test run: {Seconds(index)} s of {Seconds(limit)} s, hands off" + (_reference != null ? ", in sync so far" : "");
        if (Frames.Count >= limit) Finish(_reference != null ? "it reached the end of the recording" : "a minute had passed");
    }

    /// <summary>Null when the frames match; otherwise the differing values, grouped.</summary>
    private static string? Compare(double[] expected, double[] actual)
    {
        var groups = new List<string>();
        var seen = new HashSet<string>();
        int n = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) <= SyncProbe.Tolerance[i]) continue;
            string group = SyncProbe.Groups[i];
            if (!seen.Add(group + SyncProbe.Names[i])) continue;
            groups.Add($"{group}: {SyncProbe.Names[i]} {Format(actual[i])} here, {Format(expected[i])} in the recording");
        }
        return groups.Count == 0 ? null : string.Join("; ", groups.Take(6));
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
            _status = "Test stopped before the run began: " + reason + ".";
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
                _status = $"OUT OF SYNC after {Seconds(_firstMismatch)} s (frame {_firstMismatch}); everything before that matched.{caveat}";
                ReportLines.Add(_status);
                foreach (string part in _firstMismatchText.Split("; ")) ReportLines.Add("  " + part);
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
            sb.AppendLine($"Ended because: {reason}. Frames: {Frames.Count}. Loading frames: {_startFrames}. Mismatched frames: {_mismatchedFrames}.");
            sb.AppendLine($"Seed fixed: {_seedForced}. Random numbers reseeded: {_stabilized}. Clock fixed: {_clockFixed}.");
            sb.AppendLine("Setup here:");
            foreach (var (name, value) in _setup) sb.AppendLine($"  {name} = {value}");
            if (_firstMismatch >= 0 && _reference != null)
            {
                sb.AppendLine("Frames around the first difference (here / recording):");
                for (int f = Math.Max(0, _firstMismatch - 2); f <= Math.Min(Frames.Count - 1, _firstMismatch + 5); f++)
                {
                    sb.AppendLine($"  frame {f}:");
                    for (int i = 0; i < SyncProbe.Names.Length; i++)
                    {
                        double a = Frames[f][i], b = f < _reference.Frames.Count ? _reference.Frames[f][i] : double.NaN;
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
            json.WriteNumber("format", 1);
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
            if (root.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("it's from a different version of the test");
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
