using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BALLxPITOnlineCoop.Core;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game;

/// <summary>
/// Copies the finished frame (game and UI) off the back buffer at the end of the frame and hands it
/// to the encoder. Only runs when a guest is ready for another picture.
/// </summary>
internal static class FrameGrabber
{
    private const int GiveUpAfterFailures = 30;

    private static Texture2D? _texture;
    private static int _width, _height;
    private static MethodInfo? _writableImageData;
    private static bool _fastPathChecked;
    private static bool _fastPathVerified;
    private static int _failures;
    private static bool _disabled;

    public static string? Error { get; private set; }

    /// <summary>A coroutine that calls <paramref name="onEndOfFrame"/> after every rendered frame.</summary>
    public static IEnumerator EndOfFrameLoop(Action onEndOfFrame)
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            onEndOfFrame();
        }
    }

    public static void Capture(VideoStreamer video, double now)
    {
        if (_disabled) return;
        try
        {
            CaptureFrame(video, now);
            _failures = 0;
            Error = null;
        }
        catch (Exception ex)
        {
            _failures++;
            Error = ex.GetType().Name + ": " + ex.Message;
            if (_failures == 1) Plugin.Logger.LogError("Screen capture failed: " + ex);
            if (_failures >= GiveUpAfterFailures)
            {
                _disabled = true;
                Error = "Screen capture doesn't work in this game build (" + ex.GetType().Name + "). See the BepInEx log.";
                Plugin.Logger.LogError("Giving up on screen capture after repeated failures; guests will not get a picture.");
            }
        }
    }

    public static void Reset()
    {
        if (_texture != null) UnityEngine.Object.Destroy(_texture);
        _texture = null;
        _width = _height = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CaptureFrame(VideoStreamer video, double now)
    {
        int width = Screen.width, height = Screen.height;
        if (width < 32 || height < 32) return;
        if (_texture == null || width != _width || height != _height)
        {
            Reset();
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _width = width;
            _height = height;
        }
        // At the end of the frame the active render target is the back buffer; ReadPixels copies it
        // into the texture's CPU-side pixels (bottom row first).
        _texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        byte[] pixels = video.GetCaptureBuffer(width, height);
        CopyPixels(_texture, pixels, width * height * 4);
        video.Submit(width, height, bottomUp: true, now);
    }

    /// <summary>
    /// Copies the texture's CPU pixels into <paramref name="destination"/>. The fast path reads them in
    /// place through Texture2D.GetWritableImageData (what GetRawTextureData&lt;T&gt; uses); it's checked
    /// once against GetRawTextureData(), which is slower because it allocates a new array every frame.
    /// </summary>
    private static void CopyPixels(Texture2D texture, byte[] destination, int size)
    {
        if (!_fastPathChecked)
        {
            _fastPathChecked = true;
            _writableImageData = typeof(Texture2D).GetMethod("GetWritableImageData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (_writableImageData != null && _writableImageData.ReturnType != typeof(IntPtr)) _writableImageData = null;
            Plugin.Logger.LogInfo(_writableImageData != null
                ? "Screen capture: using Texture2D.GetWritableImageData."
                : "Screen capture: GetWritableImageData isn't available; using GetRawTextureData.");
        }

        if (_writableImageData != null)
        {
            IntPtr data = IntPtr.Zero;
            try
            {
                data = (IntPtr)_writableImageData.Invoke(texture, new object[] { 0 })!;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("GetWritableImageData failed, switching to GetRawTextureData: " + ex.Message);
                _writableImageData = null;
            }
            if (data != IntPtr.Zero)
            {
                Marshal.Copy(data, destination, 0, size);
                if (_fastPathVerified) return;
                if (VerifyFastPath(texture, destination, size)) return;
            }
        }
        CopyRawTextureData(texture, destination, size);
    }

    private static bool VerifyFastPath(Texture2D texture, byte[] fast, int size)
    {
        byte[] reference = new byte[size];
        try
        {
            CopyRawTextureData(texture, reference, size);
        }
        catch (Exception ex)
        {
            // Can't compare, so trust the fast path.
            Plugin.Logger.LogInfo("Could not double-check the capture fast path (" + ex.GetType().Name + "); keeping it.");
            _fastPathVerified = true;
            return true;
        }
        int step = Math.Max(1, size / 8192);
        for (int i = 0; i < size; i += step)
        {
            if (fast[i] != reference[i])
            {
                Plugin.Logger.LogWarning("Capture fast path returned different pixels; using GetRawTextureData.");
                _writableImageData = null;
                Buffer.BlockCopy(reference, 0, fast, 0, size);
                return true;
            }
        }
        _fastPathVerified = true;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyRawTextureData(Texture2D texture, byte[] destination, int size)
    {
        Il2CppStructArray<byte> raw = texture.GetRawTextureData();
        if (raw.Length < size) throw new InvalidOperationException($"Texture data is {raw.Length} bytes, expected {size}.");
        Il2CppArrays.AsSpan(raw).Slice(0, size).CopyTo(destination);
    }
}
