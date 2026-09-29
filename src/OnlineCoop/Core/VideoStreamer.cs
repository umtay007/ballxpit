using System;
using System.Diagnostics;
using System.Threading;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// Hands captured frames from the game thread to a JPEG encoder thread and on to the guests.
/// The game only captures when a guest is ready for a frame and the encoder is idle, so a slow
/// connection lowers the frame rate instead of adding delay.
/// </summary>
public sealed class VideoStreamer : IDisposable
{
    private readonly HostServer _server;
    private readonly JpegEncoder _encoder;
    private readonly Thread _thread;
    private readonly AutoResetEvent _frameReady = new(false);
    private volatile bool _stopping;
    private volatile bool _busy;
    private byte[] _pixels = Array.Empty<byte>();
    private int _width, _height;
    private bool _bottomUp;
    private uint _frameId;
    private double _nextCaptureAt;
    private int _framesThisSecond;
    private double _secondStartedAt;
    private volatile int _quality;
    private volatile int _extraDownscale;
    private double _windowStartedAt;
    private int _windowSamples;
    private int _windowCongestedSamples;
    private int _clearWindows;
    private const int MinAdaptiveQuality = 30;
    private const int MaxExtraDownscale = 2;

    public VideoStreamer(HostServer server, int encoderThreads)
    {
        _server = server;
        _encoder = new JpegEncoder(encoderThreads);
        _thread = new Thread(EncodeLoop) { IsBackground = true, Name = "OnlineCoop JPEG encoder", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public int MaxFps
    {
        get => _maxFps;
        set
        {
            _maxFps = value;
            _server.TargetFps = value;
        }
    }
    private int _maxFps = 30;

    /// <summary>Frames taller than this are shrunk by a whole-number factor (1080 lines become 540).</summary>
    public int MaxHeight { get; set; } = 540;
    /// <summary>Best JPEG quality to use. With <see cref="AdaptiveQuality"/> it drops while the connection can't keep up.</summary>
    public int Quality { get; set; } = 60;
    public bool AdaptiveQuality { get; set; } = true;
    public int CurrentQuality => _quality > 0 ? _quality : Quality;

    public double LastEncodeMs { get; private set; }
    public int LastFrameBytes { get; private set; }
    public int FramesPerSecond { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    /// <summary>Game thread: should this frame be captured?</summary>
    public bool ShouldCapture(double now)
    {
        AdaptQuality(now);
        if (_busy || now < _nextCaptureAt) return false;
        return _server.WantsVideoFrame();
    }

    /// <summary>
    /// Every two seconds. While the guest's connection is congested (frames piling up on the way),
    /// lower the JPEG quality; at the lowest quality, shrink the picture one more step instead. Once
    /// it has been clear for a while, climb back: quality first, then picture size.
    /// </summary>
    private void AdaptQuality(double now)
    {
        if (!AdaptiveQuality)
        {
            _quality = Quality;
            _extraDownscale = 0;
            return;
        }
        if (_quality <= 0 || _quality > Quality) _quality = Quality;
        if (_server.HasGuests)
        {
            _windowSamples++;
            if (_server.IsCongested()) _windowCongestedSamples++;
        }
        if (now - _windowStartedAt < 2) return;

        if (_windowSamples > 0)
        {
            double congested = _windowCongestedSamples / (double)_windowSamples;
            if (congested > 0.4)
            {
                _clearWindows = 0;
                if (_quality > MinAdaptiveQuality) _quality = Math.Max(MinAdaptiveQuality, _quality - 6);
                else if (_extraDownscale < MaxExtraDownscale)
                {
                    _extraDownscale++;
                    _quality = Math.Min(Quality, 45);
                }
            }
            else if (congested < 0.1)
            {
                _clearWindows++;
                if (_quality < Quality) _quality = Math.Min(Quality, _quality + 4);
                else if (_extraDownscale > 0 && _clearWindows >= 3)
                {
                    _extraDownscale--;
                    _quality = Math.Max(MinAdaptiveQuality, Quality - 15);
                    _clearWindows = 0;
                }
            }
        }
        _windowStartedAt = now;
        _windowSamples = 0;
        _windowCongestedSamples = 0;
    }

    /// <summary>Game thread: a buffer for a width x height RGBA32 frame. Fill it, then call <see cref="Submit"/>.</summary>
    public byte[] GetCaptureBuffer(int width, int height)
    {
        int size = width * height * 4;
        if (_pixels.Length < size) _pixels = new byte[size];
        return _pixels;
    }

    /// <summary>Game thread: encode and send the frame in the capture buffer.</summary>
    public void Submit(int width, int height, bool bottomUp, double now)
    {
        if (_busy) return;
        _width = width;
        _height = height;
        _bottomUp = bottomUp;
        _nextCaptureAt = now + 1.0 / Math.Max(1, MaxFps);
        _busy = true;
        _frameReady.Set();
    }

    private void EncodeLoop()
    {
        while (!_stopping)
        {
            _frameReady.WaitOne();
            if (_stopping) break;
            try
            {
                EncodeAndSend();
            }
            catch (Exception ex)
            {
                Log.Warn($"Video frame encode failed: {ex.Message}");
            }
            finally
            {
                _busy = false;
            }
        }
    }

    private void EncodeAndSend()
    {
        int downscale = Math.Max(1, (int)Math.Round(_height / (double)Math.Max(120, MaxHeight))) + _extraDownscale;
        while (downscale > 1 && (_width / downscale < 16 || _height / downscale < 16)) downscale--;
        var watch = Stopwatch.StartNew();
        int length = _encoder.Encode(_pixels, _width, _height, _bottomUp, downscale, CurrentQuality, out byte[] jpeg);
        LastEncodeMs = watch.Elapsed.TotalMilliseconds;

        int outWidth = _width / downscale, outHeight = _height / downscale;
        var packet = new byte[9 + length];
        uint id = ++_frameId;
        packet[0] = 1;
        BitConverterLE(packet, 1, id);
        packet[5] = (byte)outWidth;
        packet[6] = (byte)(outWidth >> 8);
        packet[7] = (byte)outHeight;
        packet[8] = (byte)(outHeight >> 8);
        Buffer.BlockCopy(jpeg, 0, packet, 9, length);
        _server.SendVideo(id, packet);

        LastFrameBytes = length;
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        double now = HostServer.Now;
        _framesThisSecond++;
        if (now - _secondStartedAt >= 1)
        {
            FramesPerSecond = _framesThisSecond;
            _framesThisSecond = 0;
            _secondStartedAt = now;
        }
    }

    internal static void BitConverterLE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    public void Dispose()
    {
        _stopping = true;
        _frameReady.Set();
    }
}
