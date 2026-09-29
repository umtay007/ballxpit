using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
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
/// reseeded), the game's clock starts the fight at an agreed time (its timers round differently at
/// different times), P2 starts on the exact spot, and your controls are replaced by a fixed pattern
/// (stand still, aim up, keep shooting).
/// Each frame's state is recorded. "Record" saves it to BepInEx/sync-test-recording.json; "Check"
/// plays the same character and level again, on this PC or a friend's, and reports the first frame
/// that comes out differently and what differed.
/// </summary>
internal static class SyncTest
{
    public const int TestSeed = 20260929;
    /// <summary>The fingerprint of a generator just made with <see cref="TestSeed"/>, worked out outside the game.</summary>
    private const uint TestSeedFingerprint = 3695218007;
    /// <summary>
    /// 6: the physics clock is set when the fight starts and saved.
    /// 5: generator fingerprints read exactly the seed table (4 read past its end, so identical
    /// generators could differ). 4: the game clock is set when the run starts and saved; the game thread's own generator is
    /// fingerprinted; structs and enums inside generators count. 3: frame 0 is when the fight starts
    /// with P2 released from its walk-in and the extra random number generators reseeded.
    /// 2: generators fingerprinted by their whole state. 1 read nothing.
    /// </summary>
    private const int RecordingFormat = 6;
    /// <summary>The game clock a run starts from is at least this (seconds), and a power of two above what it was.</summary>
    private const float MinGameClock = 1024f;
    /// <summary>The same for the physics clock, which starts over with each level and is set when the fight starts.</summary>
    private const float MinPhysicsClock = 8f;
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
    private static float _gameClock, _physicsClock;
    private static string _clockNote = "";
    private static bool _seedBeforeLayout;
    private static bool _loggedLevelRandom;

    // Where ThreadSafeRandom hands out numbers: the game thread, or others (whose generators can't be reseeded).
    private static int _mainThreadId;
    private static long _drawsMain, _drawsOther;
    private static readonly ConcurrentDictionary<int, byte> OtherThreads = new();
    // A few draws, with the thread's name and the game code that asked, to see what uses other threads.
    private const int MaxDrawSamples = 24;
    private static int _drawSamples;
    private static int _drawGeneration;
    [ThreadStatic] private static int t_drawGeneration;
    [ThreadStatic] private static int t_draws;
    private static readonly ConcurrentQueue<string> DrawSamples = new();

    /// <summary>Things the test found out along the way, for the result file.</summary>
    private static readonly List<string> Details = new();

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
        _gameClock = 0;
        _physicsClock = 0;
        _clockNote = "";
        _seedBeforeLayout = false;
        _loggedLevelRandom = false;
        _mainThreadId = Environment.CurrentManagedThreadId;
        Interlocked.Exchange(ref _drawsMain, 0);
        Interlocked.Exchange(ref _drawsOther, 0);
        OtherThreads.Clear();
        Interlocked.Exchange(ref _drawSamples, 0);
        Interlocked.Increment(ref _drawGeneration);
        DrawSamples.Clear();
        Details.Clear();
        CheckFingerprints();
        try
        {
            Detail(NativeStacks.Prepare());
        }
        catch (Exception ex)
        {
            Detail("stack table failed: " + ex.Message);
        }
        _saveWhenArmed = BattleSaveData.I?.Pointer ?? IntPtr.Zero;
        _phase = Phase.WaitingForRun;
        _status = mode == SyncTestMode.Record
            ? "Waiting: start a NEW run from the menu (any character and level) or Restart from the pause menu. Continuing the current run doesn't count."
            : $"Waiting: start a NEW run with {_reference!.Describe()}, from the menu or with Restart in the pause menu. Continuing the current run doesn't count.";
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
        foreach (MethodInfo draw in new[]
        {
            AccessTools.Method(typeof(ThreadSafeRandom), "Next", Type.EmptyTypes),
            AccessTools.Method(typeof(ThreadSafeRandom), "NextDouble", Type.EmptyTypes),
            AccessTools.Method(typeof(ThreadSafeRandom), "RandomValue", Type.EmptyTypes),
            AccessTools.Method(typeof(ThreadSafeRandom), "RandomRange", new[] { typeof(float), typeof(float) }),
            AccessTools.Method(typeof(ThreadSafeRandom), "RandomRange", new[] { typeof(int), typeof(int) }),
            AccessTools.Method(typeof(ThreadSafeRandom), "RandomSign", Type.EmptyTypes),
        })
            Patch(draw, "ThreadSafeRandom." + draw?.Name, nameof(CountDraw), required: false);
        _patchProblems = string.Join(", ", problems);
        _patched = problems.Count == 0;
        return _patched;
    }

    /// <summary>Counts where ThreadSafeRandom hands out a number during the test run.</summary>
    private static void CountDraw()
    {
        if (_phase != Phase.Running) return;
        int thread = Environment.CurrentManagedThreadId;
        bool main = thread == _mainThreadId;
        if (main)
        {
            Interlocked.Increment(ref _drawsMain);
        }
        else
        {
            Interlocked.Increment(ref _drawsOther);
            OtherThreads.TryAdd(thread, 0);
        }
        int generation = Volatile.Read(ref _drawGeneration);
        if (t_drawGeneration != generation)
        {
            t_drawGeneration = generation;
            t_draws = 0;
        }
        int n = ++t_draws;
        if ((n == 1 || n == 50 || n == 500) && Interlocked.Increment(ref _drawSamples) <= MaxDrawSamples)
        {
            // Runs inside the game's random number calls: nothing may escape from here.
            try
            {
                SampleDraw(main, thread, n);
            }
            catch
            {
                // no sample, then
            }
        }
    }

    private static void SampleDraw(bool main, int thread, int n)
    {
        string stack;
        try
        {
            stack = NativeStacks.Describe();
        }
        catch (Exception ex)
        {
            stack = "(no stack: " + ex.Message + ")";
        }
        DrawSamples.Enqueue($"{(main ? "game thread" : "other thread")} {thread} \"{ThreadName()}\", draw {n}: {stack}");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>The thread's name as Windows knows it (Unity names its worker threads).</summary>
    private static string ThreadName()
    {
        try
        {
            if (GetThreadDescription(GetCurrentThread(), out IntPtr text) < 0 || text == IntPtr.Zero) return "?";
            string name = System.Runtime.InteropServices.Marshal.PtrToStringUni(text) ?? "";
            LocalFree(text);
            return name.Length == 0 ? "unnamed" : name;
        }
        catch
        {
            return "?";
        }
    }

    private static void Detail(string text)
    {
        Details.Add(text);
        Plugin.Logger.LogInfo("Sync test: " + text);
    }

    /// <summary>
    /// Two generators made with the same seed must get the same fingerprint, or the fingerprints say
    /// nothing. Also notes how the runtime lays out its generators, for the result file.
    /// </summary>
    private static void CheckFingerprints()
    {
        try
        {
            var a = new Il2CppSystem.Random(TestSeed);
            var b = new Il2CppSystem.Random(TestSeed);
            uint ha = NativeFields.RandomState(a.Pointer), hb = NativeFields.RandomState(b.Pointer);
            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(a.Pointer);
            string verdict = ha != hb ? $"DIFFERENT fingerprints for the same seed ({ha} / {hb})"
                : ha == TestSeedFingerprint ? "the same fingerprint for the same seed, the one worked out outside the game (good)"
                : $"the same fingerprint for the same seed ({ha}), but not the one worked out outside the game ({TestSeedFingerprint})";
            Detail($"fingerprint check: {verdict}. " + NativeFields.DescribeClass(klass));
            IntPtr threadSafe = Il2CppInterop.Runtime.Il2CppClassPointerStore<ThreadSafeRandom>.NativeClassPtr;
            if (threadSafe != IntPtr.Zero)
                Detail("ThreadSafeRandom " + NativeFields.DescribeStaticField(threadSafe, "_global") + ", " + NativeFields.DescribeStaticField(threadSafe, "_local"));
        }
        catch (Exception ex)
        {
            Detail("fingerprint check failed: " + ex.Message);
        }
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
            BattleSaveData save = BattleSaveData.I;
            Detail($"the game lays out a level ({loadMode}); run data: {(save == null ? "none yet" : $"seed {save.Seed}{(save.Pointer == _saveWhenArmed ? " (the one from before the test)" : "")}")}.");
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
            _seedBeforeLayout = _seedForced;
            _clockFixed = FixedFrameClock.Engage();
            SetGameClock();
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
            Plugin.Logger.LogInfo("Sync test: there's no run data yet to fix the seed in; it's fixed once the game makes it.");
        }
        EngineCalls.InitRandom(TestSeed);
    }

    /// <summary>
    /// The run data may only be made while the level loads (the first run after starting the game), or
    /// be replaced then: keep its seed fixed until the fight starts. The level's generator is made from
    /// the seed each turn (BattleSaveData.GetCurTurnSeed), the first time on the fight's first frame.
    /// </summary>
    private static void EnsureSeed(string when)
    {
        BattleSaveData save = BattleSaveData.I;
        if (save == null) return;
        if (save.Seed != TestSeed)
        {
            Detail($"the run's seed was {save.Seed} {when}; set it to {TestSeed}.");
            save.Seed = TestSeed;
        }
        _seedForced = true;
    }

    /// <summary>
    /// The game's timers count from TimeMgr's clock, a float counting seconds since the game started:
    /// at 600 s it adds each frame's 0.025 s with different rounding than at 60 s, and timers drift
    /// apart. The run starts the clock at an agreed value instead: a power of two, at least
    /// <see cref="MinGameClock"/>, never earlier than the clock already was (timers the game set
    /// before just run out), and the recording's when checking.
    /// </summary>
    /// <summary>
    /// TimeMgr's physics clock restarts with each level but has run a different time by the fight's
    /// first frame (1.42 s one run, 1.30 s the next), and ball movement rounds differently at different
    /// values: start it at an agreed value too.
    /// </summary>
    private static void SetPhysicsClock(TimeMgr time)
    {
        float now = time._physicsTime;
        double wanted = MinPhysicsClock;
        while (wanted < now + 1) wanted *= 2;
        float reference = _reference?.PhysicsClock ?? 0;
        if (reference > 0)
        {
            if (reference >= now + 0.5f) wanted = reference;
            else _clockNote = $"the level took longer to load than in the recording (physics clock {now:0.##} s)";
        }
        time._physicsTime = (float)wanted;
        _physicsClock = (float)wanted;
    }

    private static void SetGameClock()
    {
        TimeMgr time = TimeMgr.I;
        if (time == null)
        {
            _clockNote = "the game clock wasn't there to set";
            return;
        }
        float now = time._gameTime;
        double wanted = MinGameClock;
        while (wanted < now + 60) wanted *= 2;
        float reference = _reference?.GameClock ?? 0;
        if (reference > 0)
        {
            if (reference >= now + 1) wanted = reference;
            else _clockNote = $"this game has been running longer than the recorded one ({now:0} s, the recording starts at {reference:0} s): restart the game and check again";
        }
        time._gameTime = (float)wanted;
        _gameClock = (float)wanted;
        Detail($"game clock set from {now.ToString("0.###", CultureInfo.InvariantCulture)} s to {wanted.ToString(CultureInfo.InvariantCulture)} s.");
    }

    /// <summary>
    /// Starts P2 on the spot Local Coop's walk-in aims for (0.75 to P1's right), exactly: the walk-in
    /// leaves it a hair off, by a different amount each time.
    /// </summary>
    private static void PlacePlayerTwo()
    {
        PlayerTwoController controller = PlayerTwoController._instance;
        Player? p2 = controller?._playerTwo;
        PlayerCharController? p1 = controller?._playerOneController;
        if (p2 == null || p1 == null) return;
        Vector3 p1Position = p1.transform.position;
        var target = new Vector3(p1Position.x + 0.75f, p1Position.y, p1Position.z);
        Vector3 before = NativePlayerContext.GetPlayerPosition(p2);
        Player previous = NativePlayerContext.EnterPlayerTwoContext(p2);
        try
        {
            p2.SetPos(target, true);
        }
        finally
        {
            NativePlayerContext.ExitContext(previous);
        }
        Vector3 after = NativePlayerContext.GetPlayerPosition(p2);
        Detail(FormattableString.Invariant($"P2 placed from ({before.x:0.#####}, {before.y:0.#####}) to ({after.x:0.#####}, {after.y:0.#####})."));
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
            EnsureSeed("after the level was laid out");
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
        SetGameClock();
        _phase = Phase.Starting;
        Plugin.Logger.LogWarning("Sync test: the run started without the seed hook; its random seed isn't fixed.");
    }

    private static void WaitForPlay()
    {
        EnsureSeed("while the level loaded");
        GameMgr game = GameMgr.I;
        if (game == null || game.CurState != GameState.kPlaying)
        {
            if (++_startFrames > MaxStartFrames) Finish("the run didn't start within 3 minutes");
            return;
        }
        // The fight has begun; with Local Coop, wait (briefly) for P2 so both start together.
        if (PlayerTwoExpected && !PlayerTwoReady && ++_waitedForPlayerTwo <= MaxWaitForPlayerTwo) return;
        ReleasePlayerTwoIntro();
        PlacePlayerTwo();
        ReseedExtraRandom();
        _stabilized = true;
        TimeMgr time = TimeMgr.I;
        if (time != null)
        {
            float physicsBefore = time._physicsTime;
            SetPhysicsClock(time);
            Detail(FormattableString.Invariant($"fight starts: game clock {time._gameTime} s, physics clock {physicsBefore} s (set to {time._physicsTime} s), fixed-step leftover {time._timeDebt} s (cleared)."));
            time._timeDebt = 0f;
        }
        _gameTimeAtStart = time != null ? time._gameTime : 0;
        _physicsTimeAtStart = time != null ? time._physicsTime : 0;
        BattleSaveData runData = BattleSaveData.I;
        if (runData != null)
        {
            try
            {
                Detail($"run seed {runData.Seed}, level seed {runData.GetCurLvlSeed()}, turn seed {runData.GetCurTurnSeed()}.");
            }
            catch (Exception ex)
            {
                Detail("couldn't read the run's seeds: " + ex.Message);
            }
        }
        Detail($"random numbers after reseeding: misc {NativeFields.RandomState(GridMgr.I?.MiscRnd?.Pointer ?? IntPtr.Zero)}, shared {NativeFields.RandomState(ThreadSafeRandom._global?.Pointer ?? IntPtr.Zero)}, game thread {NativeFields.RandomState(ThreadSafeRandom._local?.Pointer ?? IntPtr.Zero)}.");
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
        if (!_loggedLevelRandom && sample[Array.IndexOf(SyncProbe.Names, "level random numbers")] != 0)
        {
            _loggedLevelRandom = true;
            Detail($"the level's generator appeared on frame {index} (turn {sample[Array.IndexOf(SyncProbe.Names, "turn")]}).");
        }
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
        if (_clockNote.Length > 0) notes.Add(_clockNote);
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
                new Recording(_setup, Frames, _startFrames, reason, _seedForced, _stabilized, _clockFixed, _gameClock, _physicsClock).Save(RecordingPath);
                _status = $"Recorded {Seconds(Frames.Count)} s (stopped because {reason}).{caveat}";
                ReportLines.Add(_status);
                ReportLines.Add("Next: click \"Check\", then start a NEW run with the same character and level (or Restart from the pause menu), hands off.");
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
        long main = Interlocked.Read(ref _drawsMain), other = Interlocked.Read(ref _drawsOther);
        Detail(other > 0
            ? $"ThreadSafeRandom handed out {main} numbers on the game thread and {other} on {OtherThreads.Count} other thread(s)."
            : $"ThreadSafeRandom handed out {main} numbers, all on the game thread.");
        while (DrawSamples.TryDequeue(out string? sample)) Detail("random number drawn on the " + sample);
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
            sb.AppendLine($"Seed fixed: {_seedForced} ({(_seedBeforeLayout ? "before" : "after")} the level was laid out). Random numbers reseeded: {_stabilized}. Frame clock fixed: {_clockFixed}. Game clock: {_gameClock.ToString(CultureInfo.InvariantCulture)} s. Physics clock: {_physicsClock.ToString(CultureInfo.InvariantCulture)} s.");
            sb.AppendLine("Details:");
            foreach (string line in Details) sb.AppendLine("  " + line);
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
        float width = Math.Min(900f, Screen.width - 20f);
        // Two lines when it's long: the first sentence, then the rest.
        string first = _status, rest = "";
        int cut = _status.IndexOf(": ", StringComparison.Ordinal);
        if (_status.Length > 90 && cut > 0 && cut < 60)
        {
            first = _status.Substring(0, cut);
            rest = _status.Substring(cut + 2);
        }
        float x = (Screen.width - width) / 2f;
        if (GUI.Button(new Rect(x, 50f, width, 30f), "SYNC TEST: " + first + "   (click to stop)")) Stop();
        if (rest.Length > 0 && GUI.Button(new Rect(x, 82f, width, 30f), rest)) Stop();
    }

    // ---- The recording file ------------------------------------------------------------------------

    private sealed class Recording
    {
        public readonly List<(string Name, string Value)> Setup;
        public readonly List<double[]> Frames;
        public readonly int StartFrames;
        public readonly string EndReason;
        public readonly bool SeedForced, Stabilized, ClockFixed;
        public readonly float GameClock, PhysicsClock;

        public Recording(List<(string, string)> setup, List<double[]> frames, int startFrames, string endReason, bool seedForced, bool stabilized, bool clockFixed, float gameClock, float physicsClock)
        {
            GameClock = gameClock;
            PhysicsClock = physicsClock;
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
            json.WriteNumber("gameClock", GameClock);
            json.WriteNumber("physicsClock", PhysicsClock);
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
                root.GetProperty("seedForced").GetBoolean(), root.GetProperty("stabilized").GetBoolean(), root.GetProperty("clockFixed").GetBoolean(),
                root.GetProperty("gameClock").GetSingle(), root.GetProperty("physicsClock").GetSingle());
        }
    }
}
