using System;
using Il2CppInterop.Runtime;

namespace BALLxPITOnlineCoop.Game.Sync;

/// <summary>
/// Unity engine switches reached through IL2CPP's internal calls, which work even when the game's
/// interop assemblies don't expose the property (the game never used it, so it may have been stripped).
/// </summary>
internal static unsafe class EngineCalls
{
    private static bool _resolved;
    private static delegate* unmanaged<float> _getCaptureDeltaTime;
    private static delegate* unmanaged<float, void> _setCaptureDeltaTime;
    private static delegate* unmanaged<int> _getTargetFrameRate;
    private static delegate* unmanaged<int, void> _setTargetFrameRate;
    private static delegate* unmanaged<int> _getVSyncCount;
    private static delegate* unmanaged<int, void> _setVSyncCount;
    private static delegate* unmanaged<int, void> _initRandom;

    /// <summary>Names of the calls that weren't found, empty when all were.</summary>
    public static string Missing { get; private set; } = "";

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        var missing = new System.Collections.Generic.List<string>();
        IntPtr Find(string name)
        {
            IntPtr p = IL2CPP.il2cpp_resolve_icall(name);
            if (p == IntPtr.Zero) missing.Add(name);
            return p;
        }
        _getCaptureDeltaTime = (delegate* unmanaged<float>)Find("UnityEngine.Time::get_captureDeltaTime");
        _setCaptureDeltaTime = (delegate* unmanaged<float, void>)Find("UnityEngine.Time::set_captureDeltaTime");
        _getTargetFrameRate = (delegate* unmanaged<int>)Find("UnityEngine.Application::get_targetFrameRate");
        _setTargetFrameRate = (delegate* unmanaged<int, void>)Find("UnityEngine.Application::set_targetFrameRate");
        _getVSyncCount = (delegate* unmanaged<int>)Find("UnityEngine.QualitySettings::get_vSyncCount");
        _setVSyncCount = (delegate* unmanaged<int, void>)Find("UnityEngine.QualitySettings::set_vSyncCount");
        _initRandom = (delegate* unmanaged<int, void>)Find("UnityEngine.Random::InitState");
        Missing = string.Join(", ", missing);
    }

    public static bool CanFixFrameTime
    {
        get
        {
            Resolve();
            return _setCaptureDeltaTime != null && _getCaptureDeltaTime != null;
        }
    }

    public static float CaptureDeltaTime
    {
        get { Resolve(); return _getCaptureDeltaTime != null ? _getCaptureDeltaTime() : 0f; }
        set { Resolve(); if (_setCaptureDeltaTime != null) _setCaptureDeltaTime(value); }
    }

    public static int TargetFrameRate
    {
        get { Resolve(); return _getTargetFrameRate != null ? _getTargetFrameRate() : -1; }
        set { Resolve(); if (_setTargetFrameRate != null) _setTargetFrameRate(value); }
    }

    public static int VSyncCount
    {
        get { Resolve(); return _getVSyncCount != null ? _getVSyncCount() : 0; }
        set { Resolve(); if (_setVSyncCount != null) _setVSyncCount(value); }
    }

    public static void InitRandom(int seed)
    {
        Resolve();
        if (_initRandom != null) _initRandom(seed);
    }
}

/// <summary>
/// Makes every frame advance the game by exactly 1/60 s, whatever the real frame time. The game
/// then runs the same steps on any PC; a slow PC just plays slower. Unity's own timing is put back
/// afterwards.
/// </summary>
internal static class FixedFrameClock
{
    public const int FramesPerSecond = 60;

    private static bool _engaged;
    private static float _savedCapture;
    private static int _savedTargetFrameRate;
    private static int _savedVSync;

    public static bool Engaged => _engaged;

    public static bool Engage()
    {
        if (_engaged) return true;
        if (!EngineCalls.CanFixFrameTime) return false;
        _savedCapture = EngineCalls.CaptureDeltaTime;
        _savedTargetFrameRate = EngineCalls.TargetFrameRate;
        _savedVSync = EngineCalls.VSyncCount;
        // Without vsync and with a 60 fps cap the game keeps real-time speed on fast PCs.
        EngineCalls.VSyncCount = 0;
        EngineCalls.TargetFrameRate = FramesPerSecond;
        EngineCalls.CaptureDeltaTime = 1f / FramesPerSecond;
        _engaged = true;
        return true;
    }

    public static void Release()
    {
        if (!_engaged) return;
        _engaged = false;
        EngineCalls.CaptureDeltaTime = _savedCapture;
        EngineCalls.TargetFrameRate = _savedTargetFrameRate;
        EngineCalls.VSyncCount = _savedVSync;
    }
}
