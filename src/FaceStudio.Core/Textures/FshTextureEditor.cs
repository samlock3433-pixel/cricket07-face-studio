using System.Buffers.Binary;
using System.Text;

namespace Cricket07.FaceStudio.Core;

public sealed record FshTextureEntry(int Index, string Name, int Offset, int Width, int Height,
    byte Format, int MipCount, int PixelLength, string WriteStatus);

/// <summary>Conservative same-layout editor: never changes directory offsets, format, or dimensions.</summary>
public static class FshTextureEditor
{
    public static Format.FshImage? Preview(byte[] data, int index)
    {
        var entry = Inspect(data).Single(e => e.Index == index);
        return entry.WriteStatus == "Ready" ? Format.FshDecoder.TryDecodeRecord(data, entry.Offset, -1) : null;
    }

    public static IReadOnlyList<FshTextureEntry> Inspect(byte[] data)
    {
        if (data.Length < 16 || !data.AsSpan(0, 4).SequenceEqual("SHPI"u8))
            throw new InvalidDataException("Not an uncompressed SHPI container.");
        int declared = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
        if (declared < 16 || declared > data.Length || count < 0 || count > 4096 || 16L + count * 8 > declared)
            throw new InvalidDataException("Invalid SHPI directory bounds.");
        var offsets = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20 + i * 8, 4))).ToArray();
        if (offsets.Any(o => o < 16 + count * 8 || o > declared - 16) || offsets.Distinct().Count() != count)
            throw new InvalidDataException("Invalid/aliased SHPI entry offsets.");
        var result = new List<FshTextureEntry>();
        for (int i = 0; i < count; i++)
        {
            int off = offsets[i];
            int end = offsets.Where(o => o > off).DefaultIfEmpty(declared).Min();
            int blockSize = (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off, 4)) >> 8);
            if (blockSize != 0 && (blockSize < 16 || blockSize > end - off))
                throw new InvalidDataException("Invalid FSH section length.");
            if (blockSize != 0) end = off + blockSize;
            byte format = data[off];
            int w = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 4, 2));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 6, 2));
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12, 4));
            int levels = (int)(packed >> 28) + 1;
            string status = "Ready";
            int length = 0;
            if (format is not (0x60 or 0x61 or 0x62 or 0x7D or 0x7F)) status = "Read-only: unsupported encoding";
            else if (w < 1 || h < 1 || w > 4096 || h > 4096) status = "Read-only: unsupported dimensions";
            else if (((packed >> 12) & 15) != 0) status = "Read-only: nonstandard pixel flags";
            else if (levels > 1 && ((w & (w - 1)) != 0 || (h & (h - 1)) != 0 || levels > 1 + Math.Log2(Math.Min(w, h))))
                status = "Read-only: unsupported mip layout";
            else
            {
                int mw = w, mh = h;
                for (int l = 0; l < levels; l++) { length = checked(length + PixelSize(mw, mh, format)); mw = Math.Max(1, mw / 2); mh = Math.Max(1, mh / 2); }
                if (length > end - off - 16) status = "Read-only: truncated pixel payload";
            }
            result.Add(new(i, Encoding.ASCII.GetString(data, 16 + i * 8, 4), off, w, h, format, levels, length, status));
        }
        return result;
    }

    public static byte[] Replace(byte[] original, int index, int width, int height, byte[] bgra)
    {
        var entry = Inspect(original).Single(e => e.Index == index);
        if (entry.WriteStatus != "Ready") throw new NotSupportedException(entry.WriteStatus);
        if (width != entry.Width || height != entry.Height || bgra.Length != checked(width * height * 4))
            throw new InvalidDataException($"Import must be exactly {entry.Width} × {entry.Height} pixels.");
        for (int p = 3; p < bgra.Length; p += 4)
        {
            if (entry.Format == 0x7F && bgra[p] != 255) throw new InvalidDataException("24-bit target cannot preserve transparency.");
            if (entry.Format == 0x60 && bgra[p] is not (0 or 255)) throw new InvalidDataException("DXT1 accepts only opaque or fully transparent pixels. Use a binary alpha mask.");
        }
        var output = (byte[])original.Clone();
        int cursor = entry.Offset + 16;
        byte[] pixels = bgra;
        for (int l = 0; l < entry.MipCount; l++)
        {
            byte[] encoded = Encode(pixels, width, height, entry.Format);
            encoded.CopyTo(output, cursor);
            cursor += encoded.Length;
            if (l + 1 < entry.MipCount)
            {
                pixels = Downsample(pixels, width, height, entry.Format == 0x60);
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
        }
        _ = Inspect(output);
        return output;
    }

    /// <summary>
    /// Re-encodes one entry at a new size and rebuilds the container around it: same format and mip count,
    /// the entry's attachments (EAGL "metal bin" 0x69, name 0x70) and every other entry kept byte for byte,
    /// later offsets and the declared size shifted. Same size falls through to <see cref="Replace"/>.
    /// Matches the layout of community 512×512 faces (gill.big) built on stock 256×256 heads.
    /// </summary>
    public static byte[] Rebuild(byte[] original, int index, int width, int height, byte[] bgra)
    {
        var entries = Inspect(original);
        var entry = entries.Single(e => e.Index == index);
        if (width == entry.Width && height == entry.Height) return Replace(original, index, width, height, bgra);
        if (entry.WriteStatus != "Ready") throw new NotSupportedException(entry.WriteStatus);
        static bool PowerOfTwo(int v) => v >= 4 && (v & (v - 1)) == 0;
        if (!PowerOfTwo(width) || !PowerOfTwo(height) || width > 2048 || height > 2048)
            throw new InvalidDataException("Texture sizes must be powers of two from 4 to 2048 (for example 256, 512, 1024).");
        if (bgra.Length != checked(width * height * 4)) throw new InvalidDataException("Pixel buffer does not match its dimensions.");
        int declared = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(4, 4));
        int count = entries.Count, directoryEnd = 16 + count * 8;
        int End(FshTextureEntry e) => entries.Where(o => o.Offset > e.Offset).Select(o => o.Offset).DefaultIfEmpty(declared).Min();

        // New section: header with the new size, re-encoded pixels, then whatever followed the old pixels.
        int block = (int)(BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(entry.Offset, 4)) >> 8);
        int pixelEnd = entry.Offset + 16 + entry.PixelLength;
        int padding = block == 0 ? 0 : entry.Offset + block - pixelEnd; // bytes between pixels and the next section
        if (padding < 0) throw new InvalidDataException("FSH section is shorter than its pixels.");
        int levels = Math.Min(entry.MipCount, 1 + (int)Math.Log2(Math.Min(width, height)));
        var pixels = new List<byte>(); byte[] level = bgra; int lw = width, lh = height;
        for (int l = 0; l < levels; l++)
        {
            pixels.AddRange(Encode(level, lw, lh, entry.Format));
            if (l + 1 < levels) { level = Downsample(level, lw, lh, entry.Format == 0x60); lw = Math.Max(1, lw / 2); lh = Math.Max(1, lh / 2); }
        }
        var header = original.AsSpan(entry.Offset, 16).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)height);
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (packed & 0x0FFFFFFF) | (uint)(levels - 1) << 28);
        if (block != 0) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), (uint)(16 + pixels.Count + padding) << 8 | header[0]);
        var section = header.Concat(pixels).Concat(original.AsSpan(pixelEnd, End(entry) - pixelEnd).ToArray()).ToArray();

        // Container: header, directory, original bytes up to the first entry ("Buy ERTS" padding), entries in file order.
        int first = entries.Min(e => e.Offset);
        var output = new List<byte>(original.AsSpan(0, first).ToArray());
        var offsets = new int[count];
        foreach (var e in entries.OrderBy(e => e.Offset))
        {
            offsets[e.Index] = output.Count;
            output.AddRange(e.Index == index ? section : original.AsSpan(e.Offset, End(e) - e.Offset).ToArray());
        }
        int total = output.Count;
        output.AddRange(original.AsSpan(declared).ToArray()); // anything after the declared size stays as it was
        var result = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), total);
        for (int i = 0; i < count; i++) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20 + i * 8), offsets[i]);
        var check = Inspect(result).Single(e => e.Index == index);
        if (check.Width != width || check.Height != height || check.WriteStatus != "Ready" || directoryEnd > first)
            throw new InvalidDataException("Rebuilt FSH failed verification.");
        return result;
    }

    private static int PixelSize(int w, int h, byte f) => f switch
    {
        0x60 => ((w + 3) / 4) * ((h + 3) / 4) * 8,
        0x61 or 0x62 => ((w + 3) / 4) * ((h + 3) / 4) * 16,
        0x7D => w * h * 4, 0x7F => w * h * 3,
        _ => throw new NotSupportedException()
    };

    private static byte[] Encode(byte[] pixels, int w, int h, byte format)
    {
        if (format == 0x7D) return (byte[])pixels.Clone();
        var output = new byte[PixelSize(w, h, format)];
        if (format == 0x7F)
        {
            for (int p = 0; p < w * h; p++) Array.Copy(pixels, p * 4, output, p * 3, 3);
            return output;
        }
        int cursor = 0;
        var block = new byte[64];
        for (int by = 0; by < h; by += 4)
        for (int bx = 0; bx < w; bx += 4)
        {
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
                Array.Copy(pixels, (Math.Min(h - 1, by + y) * w + Math.Min(w - 1, bx + x)) * 4, block, (y * 4 + x) * 4, 4);
            if (format == 0x61)
            {
                ulong alpha = 0;
                for (int p = 0; p < 16; p++) alpha |= (ulong)((block[p * 4 + 3] + 8) / 17) << (p * 4);
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(cursor, 8), alpha); cursor += 8;
            }
            else if (format == 0x62)
            {
                var values = Enumerable.Range(0, 16).Select(p => (int)block[p * 4 + 3]).ToArray();
                int hi = values.Max(), lo = values.Min();
                output[cursor] = (byte)hi; output[cursor + 1] = (byte)lo;
                int[] palette = hi > lo ? new[] { hi, lo, (6*hi+lo)/7, (5*hi+2*lo)/7, (4*hi+3*lo)/7, (3*hi+4*lo)/7, (2*hi+5*lo)/7, (hi+6*lo)/7 }
                    : new[] { hi, lo, (4*hi+lo)/5, (3*hi+2*lo)/5, (2*hi+3*lo)/5, (hi+4*lo)/5, 0, 255 };
                ulong bits = 0;
                for (int p = 0; p < 16; p++) bits |= (ulong)Enumerable.Range(0, 8).MinBy(i => Math.Abs(values[p] - palette[i])) << (p * 3);
                for (int b = 0; b < 6; b++) output[cursor + 2 + b] = (byte)(bits >> (b * 8));
                cursor += 8;
            }
            EncodeColor(block, output.AsSpan(cursor, 8), format == 0x60);
            cursor += 8;
        }
        return output;
    }

    private static void EncodeColor(byte[] block, Span<byte> output, bool dxt1)
    {
        bool transparent = dxt1 && Enumerable.Range(0, 16).Any(p => block[p * 4 + 3] < 128);
        var opaque = Enumerable.Range(0, 16).Where(p => !transparent || block[p * 4 + 3] >= 128).ToArray();
        if (opaque.Length == 0) { output.Clear(); BinaryPrimitives.WriteUInt32LittleEndian(output[4..], uint.MaxValue); return; }
        ushort Pack(int p) => (ushort)((block[p*4+2] >> 3) << 11 | (block[p*4+1] >> 2) << 5 | block[p*4] >> 3);
        ushort a = opaque.Select(Pack).Max(), b = opaque.Select(Pack).Min();
        if (transparent) (a, b) = (b, a);
        else if (a == b) { if (a < ushort.MaxValue) a++; else b--; }
        BinaryPrimitives.WriteUInt16LittleEndian(output, a); BinaryPrimitives.WriteUInt16LittleEndian(output[2..], b);
        int[] Color(ushort v) => new[] { (v & 31) * 255 / 31, ((v >> 5) & 63) * 255 / 63, (v >> 11) * 255 / 31 };
        var colors = new[] { Color(a), Color(b), new int[3], new int[3] };
        for (int c = 0; c < 3; c++)
        {
            colors[2][c] = transparent ? (colors[0][c] + colors[1][c]) / 2 : (2*colors[0][c] + colors[1][c]) / 3;
            colors[3][c] = (colors[0][c] + 2*colors[1][c]) / 3;
        }
        uint indices = 0;
        for (int p = 0; p < 16; p++)
        {
            int index = transparent && block[p*4+3] < 128 ? 3 : Enumerable.Range(0, transparent ? 3 : 4)
                .MinBy(i => Enumerable.Range(0, 3).Sum(c => (block[p*4+c]-colors[i][c]) * (block[p*4+c]-colors[i][c])));
            indices |= (uint)index << (p * 2);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], indices);
    }

    private static byte[] Downsample(byte[] source, int w, int h, bool binaryAlpha)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var output = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
        {
            var samples = new int[4];
            for (int k = 0; k < 4; k++) samples[k] = (Math.Min(h-1, y*2+k/2)*w + Math.Min(w-1, x*2+k%2))*4;
            int alpha = samples.Sum(p => (int)source[p+3]);
            for (int c = 0; c < 3; c++) output[(y*nw+x)*4+c] = alpha == 0 ? (byte)0 : (byte)(samples.Sum(p => source[p+c]*source[p+3]) / alpha);
            output[(y*nw+x)*4+3] = binaryAlpha ? (byte)(alpha >= 510 ? 255 : 0) : (byte)(alpha/4);
        }
        return output;
    }
}
