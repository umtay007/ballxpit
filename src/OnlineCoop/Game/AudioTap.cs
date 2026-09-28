using System;
using BALLxPITOnlineCoop.Core;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game;

/// <summary>
/// Sits next to the AudioListener, where Unity hands OnAudioFilterRead the final mix of everything
/// the host hears. It copies the samples and leaves them untouched.
/// </summary>
public sealed class AudioTap : MonoBehaviour
{
    public static volatile AudioStreamer? Target;
    public static volatile int SampleRate = 48000;
    private static bool _registered;

    /// <summary>Registered only when game sound is first streamed, so a problem here can't stop the plugin loading.</summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        ClassInjector.RegisterTypeInIl2Cpp<AudioTap>();
        _registered = true;
    }

    public AudioTap(IntPtr pointer) : base(pointer)
    {
    }

    // Called by Unity on its audio thread.
    private void OnAudioFilterRead(Il2CppStructArray<float> data, int channels)
    {
        AudioStreamer? target = Target;
        if (target == null || data == null) return;
        try
        {
            target.Push(Il2CppArrays.AsSpan(data), channels, SampleRate);
        }
        catch
        {
            // Never disturb the host's audio.
        }
    }
}

/// <summary>
/// Views an IL2CPP array's elements in place. Same layout Il2CppInterop assumes (object header,
/// bounds pointer, length), without needing its newer AsSpan helper.
/// </summary>
internal static class Il2CppArrays
{
    public static unsafe Span<T> AsSpan<T>(Il2CppStructArray<T> array) where T : unmanaged
    {
        int length = array.Length;
        if (length <= 0) return Span<T>.Empty;
        return new Span<T>((void*)(array.Pointer + 4 * IntPtr.Size), length);
    }
}

/// <summary>The injected MonoBehaviour that gives the add-on Unity's update, GUI and end-of-frame calls.</summary>
public sealed class OnlineCoopBehaviour : MonoBehaviour
{
    public OnlineCoopBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private void Start() => OnlineController.Start(this);

    private void Update() => OnlineController.Update();

    private void OnGUI() => OnlineController.OnGUI();

    private void OnApplicationQuit() => OnlineController.Shutdown();

    private void OnDestroy() => OnlineController.Shutdown();
}
