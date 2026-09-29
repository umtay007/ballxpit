using System;
using System.Diagnostics;
using System.Threading;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// Hands captured frames from the game thread to an encoder thread and on to the guests: H.264 for
/// browsers that can decode it (each frame only carries what changed, so it is several times smaller)
/// and JPEG pictures for the rest. The game only captures when a guest is ready for a frame and the
/// encoder is idle, so a slow connection lowers the frame rate instead of adding delay.
/// </summary>
public sealed class VideoStreamer : IDisposable
{
    private readonly HostServer _server;
    private readonly JpegEncoder _encoder;
    private readonly int _threads;
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

    // JPEG picture quality and extra shrinking.
    private volatile int _quality;
    private volatile int _extraDownscale;
    // H.264 bitrate and extra shrinking.
    private volatile int _bitrate;
    private volatile int _h264ExtraDownscale;
    private H264Encoder? _h264;
    private byte[] _i420 = Array.Empty<byte>();
    private byte[] _h264Out = new byte[256 * 1024];
    private double _lastKeyframeAt = -10;
    private long _h264BytesThisWindow;
    private double _lastBitrateCut = -100;
    private readonly double _startedAt = HostServer.Now;

    private double _windowStartedAt;
    private int _windowSamples;
    private int _windowCongestedSamples;
    private int _clearWindows;
    private const int MinAdaptiveQuality = 30;
    private const int MaxExtraDownscale = 2;
    private const int MinBitrate = 300_000;

    public VideoStreamer(HostServer server, int encoderThreads)
    {
        _server = server;
        _encoder = new JpegEncoder(encoderThreads);
        _threads = encoderThreads > 0 ? encoderThreads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        _thread = new Thread(EncodeLoop) { IsBackground = true, Name = "OnlineCoop video encoder", Priority = ThreadPriority.BelowNormal };
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

    /// <summary>Highest H.264 bitrate (bits per second); the stream starts lower and climbs while the connection keeps up.</summary>
    public int MaxBitrate { get; set; } = 6_000_000;
    public int StartBitrate { get; set; } = 2_000_000;
    public int CurrentBitrate => _bitrate > 0 ? _bitrate : Math.Min(StartBitrate, MaxBitrate);

    public double LastEncodeMs { get; private set; }
    public int LastFrameBytes { get; private set; }
    public int FramesPerSecond { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    /// <summary>The picture is squeezed for a slow connection; the sound should save bytes too.</summary>
    public bool LowBandwidth { get; private set; }
    /// <summary>What the last frame went out as.</summary>
    public VideoCodec LastCodec { get; private set; }

    /// <summary>Game thread: should this frame be captured?</summary>
    public bool ShouldCapture(double now)
    {
        Adapt(now);
        if (_busy || now < _nextCaptureAt) return false;
        return _server.WantsVideoFrame();
    }

    /// <summary>
    /// Every two seconds. While the guest's connection is congested (frames piling up on the way),
    /// spend fewer bytes per frame: a lower H.264 bitrate or JPEG quality, and at the bottom of that a
    /// smaller picture. Once it has been clear for a while, climb back.
    /// </summary>
    private void Adapt(double now)
    {
        if (_quality <= 0 || _quality > Quality) _quality = Quality;
        if (_bitrate <= 0 || _bitrate > MaxBitrate) _bitrate = Math.Min(StartBitrate, MaxBitrate);
        if (!AdaptiveQuality)
        {
            _quality = Quality;
            _extraDownscale = 0;
            _h264ExtraDownscale = 0;
            return;
        }
        if (_server.HasGuests)
        {
            _windowSamples++;
            if (_server.IsCongested())
            {
                _windowCongestedSamples++;
                // H.264: react within a second, so frames shrink before the frame rate has to drop.
                if (now - _lastBitrateCut > 1 && _bitrate > MinBitrate)
                {
                    _bitrate = Math.Max(MinBitrate, _bitrate * 4 / 5);
                    _lastBitrateCut = now;
                }
            }
        }
        if (now - _windowStartedAt < 2) return;
        double h264BitsPerSecond = Interlocked.Exchange(ref _h264BytesThisWindow, 0) * 8 / Math.Max(0.5, now - _windowStartedAt);

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

                if (_bitrate <= MinBitrate && _h264ExtraDownscale < MaxExtraDownscale)
                {
                    _h264ExtraDownscale++;
                    _bitrate = Math.Min(MaxBitrate, 600_000);
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
                }

                if (_h264ExtraDownscale > 0 && _clearWindows >= 3 && _bitrate >= 1_500_000)
                {
                    // Room again: back to the bigger picture, at a bitrate that suits it.
                    _h264ExtraDownscale--;
                    _bitrate = Math.Max(MinBitrate, _bitrate / 2);
                    _clearWindows = 0;
                }
                else if (_bitrate < MaxBitrate && h264BitsPerSecond >= _bitrate * 0.6)
                {
                    // Only climb while the stream actually uses what it has: a calm scene needs little,
                    // and a busy one shouldn't suddenly get a bitrate nobody has tested the link with.
                    // Faster once the link has stayed clear for a while, gently just after it filled up.
                    double sinceCut = now - _lastBitrateCut;
                    _bitrate = Math.Min(MaxBitrate, sinceCut < 10 ? _bitrate * 11 / 10 : _clearWindows >= 3 && sinceCut > 20 ? _bitrate * 7 / 5 : _bitrate * 5 / 4);
                }
            }
        }
        _windowStartedAt = now;
        _windowSamples = 0;
        _windowCongestedSamples = 0;

        bool h264 = _server.H264Available && _server.HasGuestsUsing(VideoCodec.H264);
        LowBandwidth = h264
            ? _bitrate < (LowBandwidth ? 1_200_000 : 700_000) || _h264ExtraDownscale > 0
            : _extraDownscale > 0 || _quality <= 36;
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
        _h264?.Dispose();
        _h264 = null;
    }

    private void EncodeAndSend()
    {
        int baseDownscale = Math.Max(1, (int)Math.Round(_height / (double)Math.Max(120, MaxHeight)));
        uint id = ++_frameId;
        var watch = Stopwatch.StartNew();
        bool sent = false;
        if (_server.H264Available && _server.HasGuestsUsing(VideoCodec.H264))
        {
            try
            {
                sent |= EncodeH264(id, baseDownscale + _h264ExtraDownscale);
            }
            catch (Exception ex)
            {
                // The guests fall back to JPEG pictures.
                Log.Warn("H.264 encoding failed, sending JPEG pictures instead: " + ex.Message);
                _server.H264Available = false;
                _h264?.Dispose();
                _h264 = null;
            }
        }
        else if (_h264 != null && HostServer.Now - _lastKeyframeAt > 30)
        {
            // Nobody takes H.264 any more.
            _h264.Dispose();
            _h264 = null;
        }
        if (_server.HasGuestsUsing(VideoCodec.Jpeg)) sent |= EncodeJpeg(id, baseDownscale + _extraDownscale);
        LastEncodeMs = watch.Elapsed.TotalMilliseconds;
        if (sent) CountFrame();
    }

    private bool EncodeJpeg(uint id, int downscale)
    {
        while (downscale > 1 && (_width / downscale < 16 || _height / downscale < 16)) downscale--;
        int length = _encoder.Encode(_pixels, _width, _height, _bottomUp, downscale, CurrentQuality, out byte[] jpeg);
        int outWidth = _width / downscale, outHeight = _height / downscale;
        var packet = new byte[9 + length];
        packet[0] = 1;
        BitConverterLE(packet, 1, id);
        WriteSize(packet, 5, outWidth, outHeight);
        Buffer.BlockCopy(jpeg, 0, packet, 9, length);
        _server.SendVideo(id, packet, VideoCodec.Jpeg);
        LastFrameBytes = length;
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        LastCodec = VideoCodec.Jpeg;
        return true;
    }

    private bool EncodeH264(uint id, int downscale)
    {
        while (downscale > 1 && (_width / downscale < 32 || _height / downscale < 32)) downscale--;
        (int width, int height) = I420Converter.OutputSize(_width, _height, downscale);
        double now = HostServer.Now;
        int bitrate = CurrentBitrate;
        if (_h264 == null || _h264.Width != width || _h264.Height != height)
        {
            _h264?.Dispose();
            _h264 = null;
            _h264 = H264Encoder.Create(width, height, bitrate, MaxFps);
            _lastKeyframeAt = now; // a new encoder starts with a keyframe
            Log.Info($"H.264 stream: {width}x{height}, {bitrate / 1000} kbit/s.");
        }
        else
        {
            // The encoder budgets bitrate / MaxFps per frame. It is never told a lower frame rate:
            // that would make each frame bigger, which on a full connection lowers the rate further.
            if (Math.Abs(_h264.Bitrate - bitrate) > _h264.Bitrate / 20) _h264.SetBitrate(bitrate);
            // Keyframes are big: a guest that keeps losing frames gets at most one a second.
            if (_server.WantsKeyframe() && now - _lastKeyframeAt > 1)
            {
                _h264.ForceKeyFrame();
                _lastKeyframeAt = now;
            }
        }

        int size = width * height * 3 / 2;
        if (_i420.Length < size) _i420 = new byte[size];
        I420Converter.Convert(_pixels, _width, _height, _bottomUp, downscale, _i420, width, height, _threads);
        int length = _h264.Encode(_i420, (long)((now - _startedAt) * 1000), ref _h264Out, out bool keyFrame);
        if (length == 0) return false;

        var packet = new byte[10 + length];
        packet[0] = 3;
        BitConverterLE(packet, 1, id);
        WriteSize(packet, 5, width, height);
        packet[9] = (byte)(keyFrame ? 1 : 0);
        Buffer.BlockCopy(_h264Out, 0, packet, 10, length);
        _server.SendVideo(id, packet, VideoCodec.H264, keyFrame);
        Interlocked.Add(ref _h264BytesThisWindow, length);
        LastFrameBytes = length;
        OutputWidth = width;
        OutputHeight = height;
        LastCodec = VideoCodec.H264;
        return true;
    }

    private void CountFrame()
    {
        double now = HostServer.Now;
        _framesThisSecond++;
        if (now - _secondStartedAt >= 1)
        {
            FramesPerSecond = _framesThisSecond;
            _framesThisSecond = 0;
            _secondStartedAt = now;
        }
    }

    private static void WriteSize(byte[] packet, int offset, int width, int height)
    {
        packet[offset] = (byte)width;
        packet[offset + 1] = (byte)(width >> 8);
        packet[offset + 2] = (byte)height;
        packet[offset + 3] = (byte)(height >> 8);
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
