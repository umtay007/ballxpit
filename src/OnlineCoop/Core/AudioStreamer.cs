using System;
using System.Threading;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// Takes the game's mixed output (pushed from Unity's audio thread) and sends it to the guests as
/// 20 ms IMA ADPCM packets: stereo 48 kHz costs about 390 kbit/s. Each packet carries its own
/// decoder state, so a dropped packet only drops 20 ms.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
        1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    private readonly HostServer _server;
    private readonly Thread _thread;
    private readonly float[] _ring = new float[48000 * 2 * 2]; // two seconds of stereo
    private long _written; // total stereo samples (L and R counted separately) ever written
    private long _read;
    private volatile int _sampleRate;
    private volatile bool _stopping;
    private uint _sequence;
    private readonly int[] _predictor = new int[2];
    private readonly int[] _index = new int[2];

    public AudioStreamer(HostServer server)
    {
        _server = server;
        _thread = new Thread(SendLoop) { IsBackground = true, Name = "OnlineCoop audio" };
        _thread.Start();
    }

    public bool Enabled { get; set; } = true;
    /// <summary>Asked before each packet: true sends half the sample rate (half the bytes) for slow connections.</summary>
    public Func<bool>? HalfRate { get; set; }
    public long SamplesPushed => Interlocked.Read(ref _written) / 2;
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Audio thread: append interleaved samples. Anything that isn't stereo is folded to stereo.
    /// Never blocks; if the sender falls behind by two seconds the oldest audio is overwritten.
    /// </summary>
    public void Push(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        if (!Enabled || channels <= 0 || sampleRate <= 0) return;
        _sampleRate = sampleRate;
        long w = Interlocked.Read(ref _written);
        int frames = interleaved.Length / channels;
        int mask = _ring.Length; // not a power of two; use modulo
        for (int f = 0; f < frames; f++)
        {
            float left = interleaved[f * channels];
            float right = channels > 1 ? interleaved[f * channels + 1] : left;
            _ring[(int)(w % mask)] = left;
            _ring[(int)((w + 1) % mask)] = right;
            w += 2;
        }
        Interlocked.Exchange(ref _written, w);
    }

    private void SendLoop()
    {
        var stereo = new float[48000 * 2];
        while (!_stopping)
        {
            Thread.Sleep(10);
            int rate = _sampleRate;
            if (rate <= 0) continue;
            int framesPerPacket = rate / 50; // 20 ms
            long written = Interlocked.Read(ref _written);
            if (written - _read > _ring.Length) _read = written - _ring.Length / 2; // fell far behind
            if (!_server.HasGuests || !Enabled)
            {
                _read = written;
                continue;
            }
            while (written - _read >= framesPerPacket * 2)
            {
                for (int i = 0; i < framesPerPacket * 2; i++)
                    stereo[i] = _ring[(int)((_read + i) % _ring.Length)];
                _read += framesPerPacket * 2;
                try
                {
                    if (HalfRate?.Invoke() == true)
                    {
                        // Every two frames averaged into one: a simple low-pass before halving the rate.
                        int half = framesPerPacket / 2;
                        for (int f = 0; f < half; f++)
                        {
                            stereo[f * 2] = (stereo[f * 4] + stereo[f * 4 + 2]) * 0.5f;
                            stereo[f * 2 + 1] = (stereo[f * 4 + 1] + stereo[f * 4 + 3]) * 0.5f;
                        }
                        _server.SendAudio(Encode(stereo, half, rate / 2));
                    }
                    else
                    {
                        _server.SendAudio(Encode(stereo, framesPerPacket, rate));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Audio packet failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Packet: [0x02][u32 seq][u32 rate][u8 channels=2][u16 frames]
    /// [i16 predictor, u8 index, u8 0] x 2, then frames x 2 nibbles (L, R), low nibble first.
    /// </summary>
    internal byte[] Encode(float[] stereo, int frames, int rate)
    {
        const int headerSize = 12 + 8;
        var packet = new byte[headerSize + frames]; // 2 nibbles per frame = 1 byte per frame
        packet[0] = 2;
        VideoStreamer.BitConverterLE(packet, 1, ++_sequence);
        VideoStreamer.BitConverterLE(packet, 5, (uint)rate);
        packet[9] = 2;
        packet[10] = (byte)frames;
        packet[11] = (byte)(frames >> 8);
        for (int ch = 0; ch < 2; ch++)
        {
            int o = 12 + ch * 4;
            short p = (short)_predictor[ch];
            packet[o] = (byte)p;
            packet[o + 1] = (byte)(p >> 8);
            packet[o + 2] = (byte)_index[ch];
            packet[o + 3] = 0;
        }

        int outPos = headerSize;
        for (int f = 0; f < frames; f++)
        {
            int left = EncodeSample(0, ToPcm(stereo[f * 2]));
            int right = EncodeSample(1, ToPcm(stereo[f * 2 + 1]));
            packet[outPos++] = (byte)(left | (right << 4));
        }
        return packet;
    }

    private static int ToPcm(float sample)
    {
        int v = (int)(sample * 32767f);
        return v > 32767 ? 32767 : v < -32768 ? -32768 : v;
    }

    private int EncodeSample(int channel, int sample)
    {
        int predictor = _predictor[channel];
        int index = _index[channel];
        int step = StepTable[index];
        int diff = sample - predictor;
        int code = 0;
        if (diff < 0)
        {
            code = 8;
            diff = -diff;
        }
        int delta = step >> 3;
        if (diff >= step) { code |= 4; diff -= step; delta += step; }
        step >>= 1;
        if (diff >= step) { code |= 2; diff -= step; delta += step; }
        step >>= 1;
        if (diff >= step) { code |= 1; delta += step; }
        predictor += (code & 8) != 0 ? -delta : delta;
        if (predictor > 32767) predictor = 32767;
        else if (predictor < -32768) predictor = -32768;
        index += IndexTable[code];
        if (index < 0) index = 0;
        else if (index > 88) index = 88;
        _predictor[channel] = predictor;
        _index[channel] = index;
        return code;
    }

    public void Dispose()
    {
        _stopping = true;
    }
}
