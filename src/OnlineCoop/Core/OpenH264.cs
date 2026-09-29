using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// Cisco's OpenH264 library ("OpenH264 Video Codec provided by Cisco Systems, Inc."). Its license
/// lets programs download Cisco's binary at run time (Cisco covers the H.264 patent fees for it),
/// so it isn't shipped with the mod: the first host downloads it once from Cisco and checks it
/// against the SHA-256 below before loading it.
/// </summary>
public static class OpenH264Library
{
    public const string Version = "2.6.0";
    public const string LicenseUrl = "https://www.openh264.org/BINARY_LICENSE.txt";

    private sealed record Build(string FileName, string Url, string CompressedSha256, string Sha256);

    // The Linux build is only used by the automated tests.
    private static readonly Build Windows = new("openh264-2.6.0-win64.dll", "https://ciscobinary.openh264.org/openh264-2.6.0-win64.dll.bz2",
        "dab5f2a872777f9a58b69bfa9fbcf20d9f82f2d6ec91383fd70bff49bd34ac9f", "2076cb5675ec6c1a4c70e7a2a322552f547b6eeed649d6dfcd9e02a543b24691");
    private static readonly Build Linux = new("libopenh264-2.6.0-linux64.8.so", "https://ciscobinary.openh264.org/libopenh264-2.6.0-linux64.8.so.bz2",
        "27ab53323c110b76214c1c72222f459d17febbcd1e252136cadc292b0308d75b", "2f0cde7c6a6abcf5cae76942894ea42897fa677bce4ed6c91a24dd1b041d5f04");

    private static Build? Current => !Environment.Is64BitProcess ? null
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Linux
        : null;

    private static readonly object Lock = new();
    private static IntPtr _handle;
    private static string _status = "";

    public static string Status
    {
        get { lock (Lock) return _status; }
        private set { lock (Lock) _status = value; }
    }

    public static bool IsLoaded => _handle != IntPtr.Zero;
    internal static IntPtr Handle => _handle;

    /// <summary>Loads the library from <paramref name="directory"/>, downloading it first if allowed. Never throws.</summary>
    public static async Task<bool> EnsureLoadedAsync(string directory, bool allowDownload, CancellationToken token)
    {
        if (_handle != IntPtr.Zero) return true;
        Build? build = Current;
        if (build == null)
        {
            Status = "H.264 isn't available on this system";
            return false;
        }
        string path = Path.Combine(directory, build.FileName);
        try
        {
            if (!File.Exists(path) || !HashMatches(File.ReadAllBytes(path), build.Sha256))
            {
                if (!allowDownload)
                {
                    Status = "H.264 off (download not allowed)";
                    return false;
                }
                Status = "Downloading the H.264 encoder from Cisco (one time, 0.5 MB)...";
                Log.Info($"Downloading OpenH264 {Version} from {build.Url}");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                byte[] compressed = await http.GetByteArrayAsync(build.Url, token).ConfigureAwait(false);
                if (!HashMatches(compressed, build.CompressedSha256)) throw new InvalidDataException("the download doesn't match its known checksum");
                byte[] library = BZip2.Decompress(compressed);
                if (!HashMatches(library, build.Sha256)) throw new InvalidDataException("the unpacked library doesn't match its known checksum");
                string temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, library, token).ConfigureAwait(false);
                File.Move(temp, path, true);
            }
            lock (Lock)
            {
                if (_handle == IntPtr.Zero) _handle = NativeLibrary.Load(path);
            }
            Status = $"OpenH264 {Version}";
            Log.Info($"Loaded the H.264 encoder (OpenH264 Video Codec provided by Cisco Systems, Inc., {LicenseUrl}).");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = "H.264 unavailable: " + ex.Message;
            Log.Warn("H.264 encoder unavailable, streaming JPEG pictures instead: " + ex.Message);
            return false;
        }
    }

    private static bool HashMatches(byte[] data, string sha256) =>
        Convert.ToHexString(SHA256.HashData(data)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One OpenH264 encoder (constrained baseline, real-time camera mode, rate-controlled). Calls go
/// through the C++ interface's function table: Initialize, ..., EncodeFrame, ..., SetOption.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    // ISVCEncoder function table.
    private const int VInitialize = 0, VUninitialize = 3, VEncodeFrame = 4, VForceIntraFrame = 6, VSetOption = 7;
    // ENCODER_OPTION values.
    private const int OptionFrameRate = 4, OptionBitrate = 5, OptionMaxBitrate = 6, OptionRcFrameSkip = 9, OptionTraceLevel = 25;
    private const int SpatialLayerAll = 4;
    private const int FrameTypeIdr = 1, FrameTypeI = 2, FrameTypeSkip = 4;
    private const int ColorFormatI420 = 23;
    // SFrameBSInfo layout (checked against the 2.6.0 headers): 128 layer records of 56 bytes from offset 8.
    private const int FrameInfoSize = 7192, LayerRecordsAt = 8, LayerRecordSize = 56, FrameTypeAt = 7176;

    [StructLayout(LayoutKind.Sequential)]
    private struct EncParamBase
    {
        public int UsageType;
        public int Width;
        public int Height;
        public int TargetBitrate;
        public int RcMode;
        public float MaxFrameRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourcePicture
    {
        public int ColorFormat;
        public fixed int Stride[4];
        public IntPtr Data0, Data1, Data2, Data3;
        public int Width;
        public int Height;
        public long TimeStamp;
        public byte PsnrY, PsnrU, PsnrV;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitrateInfo
    {
        public int Layer;
        public int Bitrate;
    }

    private IntPtr _encoder;
    private readonly IntPtr* _table;
    private readonly byte* _info;

    public int Width { get; }
    public int Height { get; }
    public int Bitrate { get; private set; }

    private H264Encoder(IntPtr encoder, int width, int height, int bitrate)
    {
        _encoder = encoder;
        _table = *(IntPtr**)encoder;
        _info = (byte*)NativeMemory.AllocZeroed((nuint)FrameInfoSize);
        Width = width;
        Height = height;
        Bitrate = bitrate;
    }

    /// <summary>Needs <see cref="OpenH264Library"/> loaded. Width and height must be even.</summary>
    public static H264Encoder Create(int width, int height, int bitrate, float frameRate)
    {
        if (!OpenH264Library.IsLoaded) throw new InvalidOperationException("OpenH264 isn't loaded");
        if (width < 16 || height < 16 || (width & 1) != 0 || (height & 1) != 0) throw new ArgumentException($"bad picture size {width}x{height}");
        var create = (delegate* unmanaged<IntPtr*, int>)NativeLibrary.GetExport(OpenH264Library.Handle, "WelsCreateSVCEncoder");
        IntPtr encoder;
        if (create(&encoder) != 0 || encoder == IntPtr.Zero) throw new InvalidOperationException("WelsCreateSVCEncoder failed");
        var result = new H264Encoder(encoder, width, height, bitrate);
        try
        {
            int quiet = 0;
            result.SetOption(OptionTraceLevel, &quiet);
            var param = new EncParamBase
            {
                UsageType = 0, // CAMERA_VIDEO_REAL_TIME
                Width = width,
                Height = height,
                TargetBitrate = bitrate,
                RcMode = 1, // RC_BITRATE_MODE
                MaxFrameRate = Math.Clamp(frameRate, 1f, 30f),
            };
            int rc = ((delegate* unmanaged<IntPtr, EncParamBase*, int>)result._table[VInitialize])(encoder, &param);
            if (rc != 0) throw new InvalidOperationException($"OpenH264 Initialize returned {rc}");
            // Every picture we capture gets encoded: the stream's own flow control decides the frame rate.
            byte skip = 0;
            result.SetOption(OptionRcFrameSkip, &skip);
            result.SetBitrate(bitrate);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private int SetOption(int option, void* value) =>
        ((delegate* unmanaged<IntPtr, int, void*, int>)_table[VSetOption])(_encoder, option, value);

    public void SetBitrate(int bitsPerSecond)
    {
        bitsPerSecond = Math.Max(100_000, bitsPerSecond);
        var info = new BitrateInfo { Layer = SpatialLayerAll, Bitrate = bitsPerSecond };
        SetOption(OptionBitrate, &info);
        var max = new BitrateInfo { Layer = SpatialLayerAll, Bitrate = bitsPerSecond * 3 / 2 };
        SetOption(OptionMaxBitrate, &max);
        Bitrate = bitsPerSecond;
    }

    public void SetFrameRate(float framesPerSecond)
    {
        float fps = Math.Clamp(framesPerSecond, 1f, 30f);
        SetOption(OptionFrameRate, &fps);
    }

    public void ForceKeyFrame() =>
        ((delegate* unmanaged<IntPtr, byte, int, int>)_table[VForceIntraFrame])(_encoder, 1, -1);

    /// <summary>
    /// Encodes one I420 picture (Y plane, then U and V at half size) and appends the Annex-B bytes
    /// to <paramref name="output"/> from offset 0. Returns their length; 0 if the encoder skipped it.
    /// </summary>
    public int Encode(byte[] i420, long timestampMs, ref byte[] output, out bool keyFrame)
    {
        keyFrame = false;
        int lumaSize = Width * Height;
        if (i420.Length < lumaSize * 3 / 2) throw new ArgumentException("picture buffer too small");
        fixed (byte* planes = i420)
        {
            var picture = new SourcePicture
            {
                ColorFormat = ColorFormatI420,
                Width = Width,
                Height = Height,
                TimeStamp = timestampMs,
                Data0 = (IntPtr)planes,
                Data1 = (IntPtr)(planes + lumaSize),
                Data2 = (IntPtr)(planes + lumaSize + lumaSize / 4),
            };
            picture.Stride[0] = Width;
            picture.Stride[1] = Width / 2;
            picture.Stride[2] = Width / 2;
            int rc = ((delegate* unmanaged<IntPtr, SourcePicture*, byte*, int>)_table[VEncodeFrame])(_encoder, &picture, _info);
            if (rc != 0) throw new InvalidOperationException($"OpenH264 EncodeFrame returned {rc}");
        }

        int frameType = *(int*)(_info + FrameTypeAt);
        if (frameType == FrameTypeSkip || frameType == 0) return 0;
        keyFrame = frameType == FrameTypeIdr || frameType == FrameTypeI;
        int layers = *(int*)_info;
        int length = 0;
        for (int layer = 0; layer < layers && layer < 128; layer++)
        {
            byte* record = _info + LayerRecordsAt + layer * LayerRecordSize;
            int nalCount = *(int*)(record + 16);
            int* nalLengths = *(int**)(record + 24);
            byte* data = *(byte**)(record + 32);
            int size = 0;
            for (int n = 0; n < nalCount; n++) size += nalLengths[n];
            if (size <= 0) continue;
            if (output.Length < length + size) Array.Resize(ref output, Math.Max(output.Length * 2, length + size));
            Marshal.Copy((IntPtr)data, output, length, size);
            length += size;
        }
        return length;
    }

    public void Dispose()
    {
        IntPtr encoder = _encoder;
        if (encoder == IntPtr.Zero) return;
        _encoder = IntPtr.Zero;
        try
        {
            ((delegate* unmanaged<IntPtr, int>)_table[VUninitialize])(encoder);
            var destroy = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(OpenH264Library.Handle, "WelsDestroySVCEncoder");
            destroy(encoder);
        }
        finally
        {
            NativeMemory.Free(_info);
        }
    }
}

/// <summary>Shrinks a captured RGBA picture and converts it to I420 (BT.601, video range), in parallel.</summary>
public static class I420Converter
{
    /// <summary>Output size for a source picture: shrunk by <paramref name="downscale"/>, rounded down to even.</summary>
    public static (int Width, int Height) OutputSize(int srcWidth, int srcHeight, int downscale) =>
        ((srcWidth / downscale) & ~1, (srcHeight / downscale) & ~1);

    public static unsafe void Convert(byte[] rgba, int srcWidth, int srcHeight, bool bottomUp, int downscale, byte[] i420, int width, int height, int maxThreads)
    {
        int lumaSize = width * height;
        int chromaWidth = width / 2;
        int rowPairs = height / 2;
        int area = downscale * downscale;
        System.Threading.Tasks.Parallel.For(0, rowPairs, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads) }, pair =>
        {
            fixed (byte* src = rgba)
            fixed (byte* dst = i420)
            {
                byte* yPlane = dst;
                byte* uPlane = dst + lumaSize;
                byte* vPlane = uPlane + lumaSize / 4;
                for (int cx = 0; cx < chromaWidth; cx++)
                {
                    int sumR = 0, sumG = 0, sumB = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int y = pair * 2 + dy;
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int x = cx * 2 + dx;
                            int r = 0, g = 0, b = 0;
                            for (int sy = 0; sy < downscale; sy++)
                            {
                                int row = y * downscale + sy;
                                if (bottomUp) row = srcHeight - 1 - row;
                                byte* p = src + ((long)row * srcWidth + x * downscale) * 4;
                                for (int sx = 0; sx < downscale; sx++, p += 4)
                                {
                                    r += p[0];
                                    g += p[1];
                                    b += p[2];
                                }
                            }
                            r /= area;
                            g /= area;
                            b /= area;
                            yPlane[y * width + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                            sumR += r;
                            sumG += g;
                            sumB += b;
                        }
                    }
                    int ar = sumR >> 2, ag = sumG >> 2, ab = sumB >> 2;
                    uPlane[pair * chromaWidth + cx] = (byte)(((-38 * ar - 74 * ag + 112 * ab + 128) >> 8) + 128);
                    vPlane[pair * chromaWidth + cx] = (byte)(((112 * ar - 94 * ag - 18 * ab + 128) >> 8) + 128);
                }
            }
        });
    }
}
