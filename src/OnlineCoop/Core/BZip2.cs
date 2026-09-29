using System;
using System.IO;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// A small bzip2 decompressor (.NET has none built in). Cisco publishes OpenH264 as .bz2 files; the
/// result is always checked against a known SHA-256, so a mistake here can't slip a bad DLL through.
/// </summary>
public static class BZip2
{
    public static byte[] Decompress(byte[] input)
    {
        var bits = new BitReader(input);
        if (bits.Read(8) != 'B' || bits.Read(8) != 'Z' || bits.Read(8) != 'h') throw new InvalidDataException("not a bzip2 stream");
        int level = bits.Read(8) - '0';
        if (level < 1 || level > 9) throw new InvalidDataException("bad bzip2 block size");
        int maxBlock = level * 100_000;
        var output = new MemoryStream();
        var tt = new int[maxBlock];
        while (true)
        {
            long magic = ((long)bits.Read(24) << 24) | (uint)bits.Read(24);
            if (magic == 0x177245385090) break; // end of stream (combined CRC follows)
            if (magic != 0x314159265359) throw new InvalidDataException("bad bzip2 block header");
            bits.Read(16); // block CRC, two halves (reads are at most 24 bits)
            bits.Read(16);
            if (bits.Read(1) != 0) throw new InvalidDataException("randomised bzip2 blocks aren't supported");
            int origPtr = bits.Read(24);
            DecodeBlock(bits, tt, maxBlock, origPtr, output);
        }
        return output.ToArray();
    }

    private static void DecodeBlock(BitReader bits, int[] tt, int maxBlock, int origPtr, MemoryStream output)
    {
        // Which byte values occur.
        var seqToUnseq = new byte[256];
        int inUse = 0;
        int used16 = bits.Read(16);
        for (int i = 0; i < 16; i++)
        {
            if ((used16 & (0x8000 >> i)) == 0) continue;
            int used = bits.Read(16);
            for (int j = 0; j < 16; j++)
                if ((used & (0x8000 >> j)) != 0) seqToUnseq[inUse++] = (byte)(i * 16 + j);
        }
        if (inUse == 0) throw new InvalidDataException("empty bzip2 block");
        int alphaSize = inUse + 2;

        int groups = bits.Read(3);
        if (groups < 2 || groups > 6) throw new InvalidDataException("bad bzip2 table count");
        int selectorCount = bits.Read(15);
        if (selectorCount < 1) throw new InvalidDataException("bad bzip2 selector count");
        var mtfGroups = new byte[6];
        for (int i = 0; i < groups; i++) mtfGroups[i] = (byte)i;
        var selectors = new byte[selectorCount];
        for (int i = 0; i < selectorCount; i++)
        {
            int j = 0;
            while (bits.Read(1) != 0)
            {
                if (++j >= groups) throw new InvalidDataException("bad bzip2 selector");
            }
            byte v = mtfGroups[j];
            for (; j > 0; j--) mtfGroups[j] = mtfGroups[j - 1];
            mtfGroups[0] = v;
            selectors[i] = v;
        }

        // Huffman tables, as code lengths coded as deltas.
        var limit = new int[groups][];
        var baseValue = new int[groups][];
        var perm = new int[groups][];
        var minLength = new int[groups];
        var lengths = new byte[alphaSize];
        for (int t = 0; t < groups; t++)
        {
            int current = bits.Read(5);
            for (int i = 0; i < alphaSize; i++)
            {
                while (true)
                {
                    if (current < 1 || current > 20) throw new InvalidDataException("bad bzip2 code length");
                    if (bits.Read(1) == 0) break;
                    current += bits.Read(1) == 0 ? 1 : -1;
                }
                lengths[i] = (byte)current;
            }
            BuildTable(lengths, alphaSize, out limit[t], out baseValue[t], out perm[t], out minLength[t]);
        }

        // Symbols -> move-to-front -> run lengths of zeros (RUNA/RUNB).
        var mtf = new byte[256];
        for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
        var counts = new int[256];
        int endOfBlock = inUse + 1;
        int groupIndex = -1, groupLeft = 0;
        int count = 0;
        int runLength = 0, runWeight = 1;
        int[] curLimit = limit[0], curBase = baseValue[0], curPerm = perm[0];
        int curMin = minLength[0];
        while (true)
        {
            if (groupLeft == 0)
            {
                if (++groupIndex >= selectorCount) throw new InvalidDataException("bzip2 selectors ran out");
                int g = selectors[groupIndex];
                curLimit = limit[g]; curBase = baseValue[g]; curPerm = perm[g]; curMin = minLength[g];
                groupLeft = 50;
            }
            groupLeft--;
            int length = curMin;
            int code = bits.Read(length);
            while (length <= 20 && code > curLimit[length])
            {
                length++;
                code = (code << 1) | bits.Read(1);
            }
            if (length > 20) throw new InvalidDataException("bad bzip2 Huffman code");
            int symbol = curPerm[code - curBase[length]];

            if (symbol <= 1)
            {
                runLength += (symbol + 1) * runWeight;
                runWeight <<= 1;
                if (runLength > maxBlock) throw new InvalidDataException("bzip2 run too long");
                continue;
            }
            if (runLength > 0)
            {
                byte b = seqToUnseq[mtf[0]];
                if (count + runLength > maxBlock) throw new InvalidDataException("bzip2 block too long");
                counts[b] += runLength;
                while (runLength-- > 0) tt[count++] = b;
                runLength = 0;
                runWeight = 1;
            }
            if (symbol == endOfBlock) break;
            int index = symbol - 1;
            byte value = mtf[index];
            Buffer.BlockCopy(mtf, 0, mtf, 1, index);
            mtf[0] = value;
            byte actual = seqToUnseq[value];
            if (count >= maxBlock) throw new InvalidDataException("bzip2 block too long");
            counts[actual]++;
            tt[count++] = actual;
        }
        if (origPtr < 0 || origPtr >= count) throw new InvalidDataException("bad bzip2 origin pointer");

        // Inverse Burrows-Wheeler: the upper bits of tt hold the next index.
        var start = new int[256];
        for (int i = 0, sum = 0; i < 256; i++)
        {
            start[i] = sum;
            sum += counts[i];
        }
        for (int i = 0; i < count; i++)
        {
            int b = tt[i] & 0xff;
            tt[start[b]++] |= i << 8;
        }

        // Undo the initial run-length encoding (four equal bytes, then a repeat count).
        int pos = tt[origPtr] >> 8;
        int last = -1, same = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = tt[pos];
            int b = entry & 0xff;
            pos = entry >> 8;
            if (same == 4)
            {
                for (int r = 0; r < b; r++) output.WriteByte((byte)last);
                same = 0;
                last = -1;
                continue;
            }
            output.WriteByte((byte)b);
            if (b == last) same++;
            else
            {
                last = b;
                same = 1;
            }
        }
    }

    private static void BuildTable(byte[] lengths, int alphaSize, out int[] limit, out int[] baseValue, out int[] perm, out int minLength)
    {
        int min = 32, max = 0;
        for (int i = 0; i < alphaSize; i++)
        {
            min = Math.Min(min, lengths[i]);
            max = Math.Max(max, lengths[i]);
        }
        perm = new int[alphaSize];
        int p = 0;
        for (int len = min; len <= max; len++)
            for (int i = 0; i < alphaSize; i++)
                if (lengths[i] == len) perm[p++] = i;

        var countPerLength = new int[22];
        for (int i = 0; i < alphaSize; i++) countPerLength[lengths[i]]++;
        limit = new int[22];
        baseValue = new int[22];
        int code = 0, index = 0;
        for (int len = 0; len <= 21; len++) limit[len] = -1;
        for (int len = min; len <= max; len++)
        {
            // Codes of this length run from `code` to `code + n - 1`; symbols from perm[index].
            baseValue[len] = code - index;
            code += countPerLength[len];
            index += countPerLength[len];
            limit[len] = code - 1;
            code <<= 1;
        }
        minLength = min;
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _pos;
        private uint _buffer;
        private int _count;

        public BitReader(byte[] data) => _data = data;

        public int Read(int n)
        {
            while (_count < n)
            {
                if (_pos >= _data.Length) throw new EndOfStreamException("bzip2 stream ended early");
                _buffer = (_buffer << 8) | _data[_pos++];
                _count += 8;
            }
            _count -= n;
            return (int)((_buffer >> _count) & ((1u << n) - 1));
        }
    }
}
