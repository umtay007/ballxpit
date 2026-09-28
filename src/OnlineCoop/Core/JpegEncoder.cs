using System;
using System.Threading.Tasks;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// Baseline JPEG encoder (4:2:0) for streaming frames. The frame is split into horizontal strips
/// that are encoded in parallel; restart markers between strips keep them independent, so any
/// standard decoder (every browser) reads the result as one image. The DCT, quantisation and
/// entropy coding follow stb_image_write.
/// </summary>
public sealed class JpegEncoder
{
    private static readonly byte[] ZigZag =
    {
        0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42, 3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18, 24, 31, 40, 44, 53,
        10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60, 21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
    };

    private static readonly byte[] LumaQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] ChromaQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    };

    private static readonly float[] AanScale =
    {
        1.0f * 2.828427125f, 1.387039845f * 2.828427125f, 1.306562965f * 2.828427125f, 1.175875602f * 2.828427125f,
        1.0f * 2.828427125f, 0.785694958f * 2.828427125f, 0.541196100f * 2.828427125f, 0.275899379f * 2.828427125f,
    };

    // Standard Huffman tables (ITU T.81 annex K): code counts for lengths 1..16, then symbols.
    private static readonly byte[] DcLumaCounts = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcLumaSymbols = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] AcLumaCounts = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] AcLumaSymbols =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };
    private static readonly byte[] DcChromaCounts = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcChromaSymbols = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] AcChromaCounts = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] AcChromaSymbols =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };

    private static readonly HuffmanTable DcLuma = HuffmanTable.Build(DcLumaCounts, DcLumaSymbols);
    private static readonly HuffmanTable AcLuma = HuffmanTable.Build(AcLumaCounts, AcLumaSymbols);
    private static readonly HuffmanTable DcChroma = HuffmanTable.Build(DcChromaCounts, DcChromaSymbols);
    private static readonly HuffmanTable AcChroma = HuffmanTable.Build(AcChromaCounts, AcChromaSymbols);

    private readonly int _maxThreads;
    private int _quality = -1;
    private readonly byte[] _lumaTable = new byte[64];
    private readonly byte[] _chromaTable = new byte[64];
    private readonly float[] _lumaDivisors = new float[64];
    private readonly float[] _chromaDivisors = new float[64];
    private StripState[] _strips = Array.Empty<StripState>();
    private byte[] _output = new byte[256 * 1024];

    public JpegEncoder(int maxThreads)
    {
        _maxThreads = Math.Clamp(maxThreads, 1, 16);
    }

    /// <summary>
    /// Encodes an RGBA32 image into <paramref name="result"/>. Rows are read bottom-up when
    /// <paramref name="bottomUp"/> is set (how Texture2D.ReadPixels stores them). The image is
    /// shrunk by the integer <paramref name="downscale"/> factor with a box filter.
    /// </summary>
    /// <returns>Number of bytes written into the returned buffer.</returns>
    public int Encode(byte[] rgba, int srcWidth, int srcHeight, bool bottomUp, int downscale, int quality, out byte[] result)
    {
        if (downscale < 1) downscale = 1;
        int width = srcWidth / downscale;
        int height = srcHeight / downscale;
        if (width < 16 || height < 16 || width > 65535 || height > 65535)
            throw new ArgumentException($"Unsupported frame size {width}x{height}.");
        if (rgba.Length < srcWidth * srcHeight * 4)
            throw new ArgumentException("Pixel buffer is smaller than the frame.");
        SetQuality(quality);

        int mcusPerRow = (width + 15) / 16;
        int mcuRows = (height + 15) / 16;
        int stripCount = Math.Min(_maxThreads, mcuRows);
        int rowsPerStrip = (mcuRows + stripCount - 1) / stripCount;
        stripCount = (mcuRows + rowsPerStrip - 1) / rowsPerStrip;
        int restartInterval = rowsPerStrip * mcusPerRow;
        bool useRestarts = stripCount > 1 && restartInterval <= 65535;
        if (!useRestarts)
        {
            stripCount = 1;
            rowsPerStrip = mcuRows;
        }

        if (_strips.Length < stripCount)
        {
            var grown = new StripState[stripCount];
            Array.Copy(_strips, grown, _strips.Length);
            for (int i = _strips.Length; i < stripCount; i++) grown[i] = new StripState();
            _strips = grown;
        }

        var job = new FrameJob(rgba, srcWidth, srcHeight, bottomUp, downscale, width, height, mcusPerRow, mcuRows, rowsPerStrip);
        if (stripCount == 1)
        {
            EncodeStrip(job, 0, _strips[0]);
        }
        else
        {
            StripState[] strips = _strips;
            Parallel.For(0, stripCount, new ParallelOptions { MaxDegreeOfParallelism = _maxThreads }, i => EncodeStrip(job, i, strips[i]));
        }

        // Assemble: headers, strips separated by RSTn markers, EOI.
        int size = 700;
        for (int i = 0; i < stripCount; i++) size += _strips[i].Length + 2;
        if (_output.Length < size) _output = new byte[Math.Max(size, _output.Length * 2)];
        int pos = WriteHeaders(_output, width, height, useRestarts ? restartInterval : 0);
        for (int i = 0; i < stripCount; i++)
        {
            Buffer.BlockCopy(_strips[i].Buffer, 0, _output, pos, _strips[i].Length);
            pos += _strips[i].Length;
            if (i < stripCount - 1)
            {
                _output[pos++] = 0xFF;
                _output[pos++] = (byte)(0xD0 + (i & 7));
            }
        }
        _output[pos++] = 0xFF;
        _output[pos++] = 0xD9;
        result = _output;
        return pos;
    }

    private void SetQuality(int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        if (quality == _quality) return;
        _quality = quality;
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        for (int i = 0; i < 64; i++)
        {
            int luma = (LumaQuant[i] * scale + 50) / 100;
            int chroma = (ChromaQuant[i] * scale + 50) / 100;
            _lumaTable[ZigZag[i]] = (byte)Math.Clamp(luma, 1, 255);
            _chromaTable[ZigZag[i]] = (byte)Math.Clamp(chroma, 1, 255);
        }
        for (int row = 0, k = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++, k++)
            {
                _lumaDivisors[k] = 1f / (_lumaTable[ZigZag[k]] * AanScale[row] * AanScale[col]);
                _chromaDivisors[k] = 1f / (_chromaTable[ZigZag[k]] * AanScale[row] * AanScale[col]);
            }
        }
    }

    private int WriteHeaders(byte[] o, int width, int height, int restartInterval)
    {
        int p = 0;
        void Put(byte b) => o[p++] = b;
        void Put16(int v) { o[p++] = (byte)(v >> 8); o[p++] = (byte)v; }

        Put(0xFF); Put(0xD8); // SOI
        Put(0xFF); Put(0xE0); Put16(16); // APP0 JFIF
        Put((byte)'J'); Put((byte)'F'); Put((byte)'I'); Put((byte)'F'); Put(0); Put(1); Put(1); Put(0); Put16(1); Put16(1); Put(0); Put(0);

        Put(0xFF); Put(0xDB); Put16(2 + 65 * 2); // DQT, both tables
        Put(0); Buffer.BlockCopy(_lumaTable, 0, o, p, 64); p += 64;
        Put(1); Buffer.BlockCopy(_chromaTable, 0, o, p, 64); p += 64;

        Put(0xFF); Put(0xC0); Put16(17); Put(8); Put16(height); Put16(width); Put(3); // SOF0
        Put(1); Put(0x22); Put(0); // Y: 2x2 sampling, table 0
        Put(2); Put(0x11); Put(1); // Cb
        Put(3); Put(0x11); Put(1); // Cr

        Put(0xFF); Put(0xC4); // DHT, all four tables
        Put16(2 + 4 + 16 * 4 + DcLumaSymbols.Length + AcLumaSymbols.Length + DcChromaSymbols.Length + AcChromaSymbols.Length);
        void Table(byte id, byte[] counts, byte[] symbols)
        {
            Put(id);
            Buffer.BlockCopy(counts, 0, o, p, 16); p += 16;
            Buffer.BlockCopy(symbols, 0, o, p, symbols.Length); p += symbols.Length;
        }
        Table(0x00, DcLumaCounts, DcLumaSymbols);
        Table(0x10, AcLumaCounts, AcLumaSymbols);
        Table(0x01, DcChromaCounts, DcChromaSymbols);
        Table(0x11, AcChromaCounts, AcChromaSymbols);

        if (restartInterval > 0)
        {
            Put(0xFF); Put(0xDD); Put16(4); Put16(restartInterval); // DRI
        }

        Put(0xFF); Put(0xDA); Put16(12); Put(3); // SOS
        Put(1); Put(0x00);
        Put(2); Put(0x11);
        Put(3); Put(0x11);
        Put(0); Put(63); Put(0);
        return p;
    }

    private sealed class FrameJob
    {
        public readonly byte[] Rgba;
        public readonly int SrcWidth, SrcHeight, Downscale, Width, Height, McusPerRow, McuRows, RowsPerStrip;
        public readonly bool BottomUp;

        public FrameJob(byte[] rgba, int srcWidth, int srcHeight, bool bottomUp, int downscale, int width, int height, int mcusPerRow, int mcuRows, int rowsPerStrip)
        {
            Rgba = rgba; SrcWidth = srcWidth; SrcHeight = srcHeight; BottomUp = bottomUp; Downscale = downscale;
            Width = width; Height = height; McusPerRow = mcusPerRow; McuRows = mcuRows; RowsPerStrip = rowsPerStrip;
        }
    }

    private sealed class StripState
    {
        public byte[] Buffer = new byte[64 * 1024];
        public int Length;
        public byte[] Rgb = Array.Empty<byte>();
        public int BitBuf, BitCnt;
        public readonly float[] Y = new float[256], U = new float[256], V = new float[256];
        public readonly float[] SubU = new float[64], SubV = new float[64];
        public readonly int[] Du = new int[64];

        public void EnsureSpace(int extra)
        {
            if (Length + extra > Buffer.Length)
            {
                var grown = new byte[Math.Max(Buffer.Length * 2, Length + extra)];
                System.Buffer.BlockCopy(Buffer, 0, grown, 0, Length);
                Buffer = grown;
            }
        }
    }

    private void EncodeStrip(FrameJob job, int stripIndex, StripState s)
    {
        int firstRow = stripIndex * job.RowsPerStrip;
        int lastRow = Math.Min(firstRow + job.RowsPerStrip, job.McuRows);
        int y0 = firstRow * 16;
        int rows = Math.Min(lastRow * 16, job.Height) - y0;
        int stride = job.Width * 3;
        int needed = stride * (lastRow - firstRow) * 16;
        if (s.Rgb.Length < needed) s.Rgb = new byte[needed];
        FillRgb(job, y0, rows, s.Rgb);

        s.Length = 0;
        s.BitBuf = 0;
        s.BitCnt = 0;
        int dcY = 0, dcU = 0, dcV = 0;
        float[] Y = s.Y, U = s.U, V = s.V;
        byte[] rgb = s.Rgb;
        for (int mcuRow = firstRow; mcuRow < lastRow; mcuRow++)
        {
            int localY = mcuRow * 16 - y0;
            s.EnsureSpace(job.McusPerRow * 3200);
            for (int x = 0; x < job.Width; x += 16)
            {
                int pos = 0;
                for (int row = 0; row < 16; row++)
                {
                    int r = localY + row;
                    if (r >= rows) r = rows - 1;
                    int rowBase = r * stride;
                    for (int col = 0; col < 16; col++, pos++)
                    {
                        int c = x + col;
                        if (c >= job.Width) c = job.Width - 1;
                        int p = rowBase + c * 3;
                        float red = rgb[p], green = rgb[p + 1], blue = rgb[p + 2];
                        Y[pos] = 0.29900f * red + 0.58700f * green + 0.11400f * blue - 128f;
                        U[pos] = -0.16874f * red - 0.33126f * green + 0.50000f * blue;
                        V[pos] = 0.50000f * red - 0.41869f * green - 0.08131f * blue;
                    }
                }
                dcY = ProcessBlock(s, Y, 0, 16, _lumaDivisors, dcY, DcLuma, AcLuma);
                dcY = ProcessBlock(s, Y, 8, 16, _lumaDivisors, dcY, DcLuma, AcLuma);
                dcY = ProcessBlock(s, Y, 128, 16, _lumaDivisors, dcY, DcLuma, AcLuma);
                dcY = ProcessBlock(s, Y, 136, 16, _lumaDivisors, dcY, DcLuma, AcLuma);
                float[] subU = s.SubU, subV = s.SubV;
                for (int yy = 0, p = 0; yy < 8; yy++)
                {
                    for (int xx = 0; xx < 8; xx++, p++)
                    {
                        int j = yy * 32 + xx * 2;
                        subU[p] = (U[j] + U[j + 1] + U[j + 16] + U[j + 17]) * 0.25f;
                        subV[p] = (V[j] + V[j + 1] + V[j + 16] + V[j + 17]) * 0.25f;
                    }
                }
                dcU = ProcessBlock(s, subU, 0, 8, _chromaDivisors, dcU, DcChroma, AcChroma);
                dcV = ProcessBlock(s, subV, 0, 8, _chromaDivisors, dcV, DcChroma, AcChroma);
            }
        }
        WriteBits(s, 0x7F, 7); // pad the last byte with 1-bits
    }

    /// <summary>Converts the output rows [y0, y0 + rows) into packed top-down RGB, shrinking as it goes.</summary>
    private static void FillRgb(FrameJob job, int y0, int rows, byte[] rgb)
    {
        byte[] src = job.Rgba;
        int f = job.Downscale;
        int srcStride = job.SrcWidth * 4;
        int stride = job.Width * 3;
        if (f == 1)
        {
            for (int y = 0; y < rows; y++)
            {
                int outY = y0 + y;
                int srcRow = job.BottomUp ? job.SrcHeight - 1 - outY : outY;
                int s = srcRow * srcStride;
                int d = y * stride;
                for (int x = 0; x < job.Width; x++, s += 4, d += 3)
                {
                    rgb[d] = src[s];
                    rgb[d + 1] = src[s + 1];
                    rgb[d + 2] = src[s + 2];
                }
            }
            return;
        }

        int area = f * f;
        int half = area / 2;
        for (int y = 0; y < rows; y++)
        {
            int outY = y0 + y;
            int d = y * stride;
            for (int x = 0; x < job.Width; x++, d += 3)
            {
                int r = 0, g = 0, b = 0;
                for (int j = 0; j < f; j++)
                {
                    int sy = outY * f + j;
                    int srcRow = job.BottomUp ? job.SrcHeight - 1 - sy : sy;
                    int s = srcRow * srcStride + x * f * 4;
                    for (int i = 0; i < f; i++, s += 4)
                    {
                        r += src[s];
                        g += src[s + 1];
                        b += src[s + 2];
                    }
                }
                rgb[d] = (byte)((r + half) / area);
                rgb[d + 1] = (byte)((g + half) / area);
                rgb[d + 2] = (byte)((b + half) / area);
            }
        }
    }

    private static int ProcessBlock(StripState s, float[] data, int offset, int stride, float[] divisors, int dc, HuffmanTable dcTable, HuffmanTable acTable)
    {
        for (int o = offset, end = offset + stride * 8; o < end; o += stride)
            Dct(data, o, 1);
        for (int o = offset; o < offset + 8; o++)
            Dct(data, o, stride);

        int[] du = s.Du;
        for (int y = 0, j = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++, j++)
            {
                float v = data[offset + y * stride + x] * divisors[j];
                du[ZigZag[j]] = (int)(v < 0 ? v - 0.5f : v + 0.5f);
            }
        }

        int diff = du[0] - dc;
        if (diff == 0)
        {
            WriteBits(s, dcTable.Codes[0], dcTable.Lengths[0]);
        }
        else
        {
            CalcBits(diff, out int bits, out int length);
            WriteBits(s, dcTable.Codes[length], dcTable.Lengths[length]);
            WriteBits(s, bits, length);
        }

        int end0 = 63;
        while (end0 > 0 && du[end0] == 0) end0--;
        if (end0 == 0)
        {
            WriteBits(s, acTable.Codes[0x00], acTable.Lengths[0x00]);
            return du[0];
        }
        for (int i = 1; i <= end0; i++)
        {
            int start = i;
            while (du[i] == 0 && i <= end0) i++;
            int zeroes = i - start;
            if (zeroes >= 16)
            {
                for (int m = zeroes >> 4; m > 0; m--)
                    WriteBits(s, acTable.Codes[0xF0], acTable.Lengths[0xF0]);
                zeroes &= 15;
            }
            CalcBits(du[i], out int bits, out int length);
            int symbol = (zeroes << 4) + length;
            WriteBits(s, acTable.Codes[symbol], acTable.Lengths[symbol]);
            WriteBits(s, bits, length);
        }
        if (end0 != 63)
            WriteBits(s, acTable.Codes[0x00], acTable.Lengths[0x00]);
        return du[0];
    }

    private static void CalcBits(int value, out int bits, out int length)
    {
        int magnitude = value < 0 ? -value : value;
        int v = value < 0 ? value - 1 : value;
        length = 1;
        while ((magnitude >>= 1) != 0) length++;
        bits = v & ((1 << length) - 1);
    }

    private static void WriteBits(StripState s, int code, int length)
    {
        int bitCnt = s.BitCnt + length;
        int bitBuf = s.BitBuf | (code << (24 - bitCnt));
        byte[] buffer = s.Buffer;
        while (bitCnt >= 8)
        {
            byte c = (byte)(bitBuf >> 16);
            buffer[s.Length++] = c;
            if (c == 0xFF) buffer[s.Length++] = 0;
            bitBuf <<= 8;
            bitCnt -= 8;
        }
        s.BitBuf = bitBuf;
        s.BitCnt = bitCnt;
    }

    private static void Dct(float[] d, int o, int step)
    {
        int i0 = o, i1 = o + step, i2 = o + step * 2, i3 = o + step * 3, i4 = o + step * 4, i5 = o + step * 5, i6 = o + step * 6, i7 = o + step * 7;
        float d0 = d[i0], d1 = d[i1], d2 = d[i2], d3 = d[i3], d4 = d[i4], d5 = d[i5], d6 = d[i6], d7 = d[i7];

        float tmp0 = d0 + d7, tmp7 = d0 - d7;
        float tmp1 = d1 + d6, tmp6 = d1 - d6;
        float tmp2 = d2 + d5, tmp5 = d2 - d5;
        float tmp3 = d3 + d4, tmp4 = d3 - d4;

        float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3;
        float tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
        d[i0] = tmp10 + tmp11;
        d[i4] = tmp10 - tmp11;
        float z1 = (tmp12 + tmp13) * 0.707106781f;
        d[i2] = tmp13 + z1;
        d[i6] = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;
        float z5 = (tmp10 - tmp12) * 0.382683433f;
        float z2 = tmp10 * 0.541196100f + z5;
        float z4 = tmp12 * 1.306562965f + z5;
        float z3 = tmp11 * 0.707106781f;
        float z11 = tmp7 + z3, z13 = tmp7 - z3;
        d[i5] = z13 + z2;
        d[i3] = z13 - z2;
        d[i1] = z11 + z4;
        d[i7] = z11 - z4;
    }

    private sealed class HuffmanTable
    {
        public readonly int[] Codes = new int[256];
        public readonly int[] Lengths = new int[256];

        public static HuffmanTable Build(byte[] counts, byte[] symbols)
        {
            var table = new HuffmanTable();
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < counts[length - 1]; i++, k++)
                {
                    table.Codes[symbols[k]] = code++;
                    table.Lengths[symbols[k]] = length;
                }
                code <<= 1;
            }
            return table;
        }
    }
}
