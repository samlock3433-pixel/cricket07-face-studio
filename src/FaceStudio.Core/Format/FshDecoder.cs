using System.Buffers.Binary;

namespace Cricket07.FaceStudio.Core.Format;

/// <summary>
/// Decoder for EA's FSH texture container. A single FSH file holds 1..N records inside a
/// SHPI directory. Type codes follow the canonical EA numbering documented by the
/// EA Graphics Manager project (https://github.com/bartlomiejduda/EA-Graphics-Manager).
///
/// Supported record types (subset focused on what Cricket 07 actually ships):
///   0x60 / 96  DXT1 (BC1)
///   0x61 / 97  DXT3 (BC2)
///   0x62 / 98  DXT5 (BC3)         ← Cricket 07 face textures live here
///   0x7D / 125 BGRA8888 (32-bit)
///   0x7F / 127 BGR888 (24-bit)
///   0x78 / 120 BGR565 (16-bit)
///   0x6D / 109 BGRA4444 (16-bit)
///   0x5B / 91  RGBA8888 (raw)
///   0x05 / 5   RGBA8888 (raw)
///   palette+indexed types 0x01, 0x02, 0x40, 0x41, 0x5C, 0x5D paired with 0x21+ palette records
///
/// For PC FSH files raw (uncompressed) pixel data is stored Morton-Z swizzled — we always
/// apply an unswizzle pass before laying out into BGRA32 output.
/// </summary>
public static class FshDecoder
{
    public static ReadOnlySpan<byte> Magic => "SHPI"u8;

    public static FshImage? TryDecodeFirstImage(ReadOnlySpan<byte> fsh)
    {
        if (fsh.Length < 16) return null;
        if (!fsh[..4].SequenceEqual(Magic)) return null;

        int numEntries = BinaryPrimitives.ReadInt32LittleEndian(fsh.Slice(8, 4));
        if (numEntries <= 0 || numEntries > 4096) return null;

        int dirStart = 16;
        if (fsh.Length < dirStart + numEntries * 8) return null;

        // First pass: locate any palette record (types 0x21..0x3B).
        int paletteOffset = LocatePaletteRecord(fsh, numEntries, dirStart);

        // Second pass: try to decode the first image record we can handle.
        for (int i = 0; i < numEntries; i++)
        {
            int entryOff = BinaryPrimitives.ReadInt32LittleEndian(fsh.Slice(dirStart + i * 8 + 4, 4));
            if (entryOff <= 0 || entryOff >= fsh.Length - 16) continue;
            var img = TryDecodeRecord(fsh, entryOff, paletteOffset);
            if (img is not null) return img;
        }
        return null;
    }

    private static int LocatePaletteRecord(ReadOnlySpan<byte> fsh, int numEntries, int dirStart)
    {
        for (int i = 0; i < numEntries; i++)
        {
            int entryOff = BinaryPrimitives.ReadInt32LittleEndian(fsh.Slice(dirStart + i * 8 + 4, 4));
            if (entryOff <= 0 || entryOff >= fsh.Length) continue;
            byte type = (byte)(fsh[entryOff] & 0x7F);
            if (type is 0x21 or 0x22 or 0x23 or 0x24 or 0x29 or 0x2A or 0x2C or 0x2D
                     or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x3A or 0x3B)
                return entryOff;
        }
        return -1;
    }

    internal static FshImage? TryDecodeRecord(ReadOnlySpan<byte> fsh, int off, int paletteOff)
    {
        if (off + 16 > fsh.Length) return null;
        byte raw = fsh[off];
        byte type = (byte)(raw & 0x7F);   // low 7 bits = format
        // bit 7 (raw & 0x80) is the "compressed" marker; for decode purposes we only need the type.

        ushort width  = BinaryPrimitives.ReadUInt16LittleEndian(fsh.Slice(off + 4, 2));
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(fsh.Slice(off + 6, 2));
        if (width == 0 || height == 0 || width > 8192 || height > 8192) return null;

        int pixelStart = off + 16;
        return type switch
        {
            // ---- BC / DXT compressed (no swizzle, decode block-by-block) ----
            0x60 => DecodeDxt1(fsh, pixelStart, width, height),
            0x61 => DecodeDxt3(fsh, pixelStart, width, height),
            0x62 => DecodeDxt5(fsh, pixelStart, width, height),

            // ---- Raw pixel formats ----
            // Cricket 07 PC textures (SHPI) are stored LINEAR (row-major), not Morton-swizzled,
            // so we lay them out directly. Applying a Morton unswizzle here produced striped
            // noise on non-square textures like the 512x1024 bat sheets.
            0x7D => DecodeBgra8888(fsh, pixelStart, width, height, swizzled: false),
            0x05 => DecodeRgba8888(fsh, pixelStart, width, height),
            0x5B => DecodeRgba8888(fsh, pixelStart, width, height),
            0x16 => DecodeArgb8888(fsh, pixelStart, width, height),
            0x7F => DecodeBgr888(fsh, pixelStart, width, height, swizzled: false),
            0x04 => DecodeRgb888(fsh, pixelStart, width, height),
            0x78 => DecodeBgr565(fsh, pixelStart, width, height, swizzled: false),
            0x6D => DecodeBgra4444(fsh, pixelStart, width, height, swizzled: false),
            0x14 => DecodeRgb565(fsh, pixelStart, width, height),
            0x03 => DecodeRgba5551(fsh, pixelStart, width, height),

            // ---- Indexed (with optional shared palette) ----
            0x01 or 0x02 or 0x40 or 0x41 or 0x5C or 0x5D
                 => DecodePal8WithPalette(fsh, pixelStart, width, height, paletteOff),

            // ---- Metadata / non-image records (skip) ----
            0x69 or 0x6F or 0x70 or 0x7C => null,  // EAGL64 / comment / name / hot-spot

            // Unknown / unsupported — explicit null so the UI can show a placeholder.
            _ => null,
        };
    }

    // ============== RAW PIXEL DECODERS ==============

    private static FshImage DecodeBgra8888(ReadOnlySpan<byte> src, int p, int w, int h, bool swizzled)
    {
        int n = w * h * 4;
        if (p + n > src.Length) n = Math.Max(0, src.Length - p);
        var pix = new byte[w * h * 4];
        src.Slice(p, n).CopyTo(pix);
        if (swizzled) UnswizzleMorton(pix, w, h, bytesPerPixel: 4);
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeRgba8888(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        int n = w * h * 4;
        if (p + n > src.Length) return Empty(w, h);
        var pix = new byte[w * h * 4];
        // RGBA → BGRA swap
        for (int i = 0; i < w * h; i++)
        {
            int s = p + i * 4;
            int d = i * 4;
            pix[d + 0] = src[s + 2]; // B ← R
            pix[d + 1] = src[s + 1]; // G ← G
            pix[d + 2] = src[s + 0]; // R ← B
            pix[d + 3] = src[s + 3]; // A
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeArgb8888(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h && p + i * 4 + 3 < src.Length; i++)
        {
            int s = p + i * 4;
            int d = i * 4;
            // Source is ARGB (big-endian style): A R G B
            pix[d + 0] = src[s + 3]; // B
            pix[d + 1] = src[s + 2]; // G
            pix[d + 2] = src[s + 1]; // R
            pix[d + 3] = src[s + 0]; // A
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeBgr888(ReadOnlySpan<byte> src, int p, int w, int h, bool swizzled)
    {
        // First read 3-byte BGR into a temp BGR buffer (so Morton swizzle can operate on
        // the actual pixel stride), then expand to BGRA32.
        var bgr = new byte[w * h * 3];
        int n = Math.Min(bgr.Length, src.Length - p);
        src.Slice(p, n).CopyTo(bgr);
        if (swizzled) UnswizzleMorton(bgr, w, h, bytesPerPixel: 3);

        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int s = i * 3, d = i * 4;
            pix[d + 0] = bgr[s + 0]; pix[d + 1] = bgr[s + 1]; pix[d + 2] = bgr[s + 2]; pix[d + 3] = 0xFF;
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeRgb888(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h && p + i * 3 + 2 < src.Length; i++)
        {
            int s = p + i * 3, d = i * 4;
            pix[d + 0] = src[s + 2]; // B ← R
            pix[d + 1] = src[s + 1]; // G
            pix[d + 2] = src[s + 0]; // R ← B
            pix[d + 3] = 0xFF;
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeBgr565(ReadOnlySpan<byte> src, int p, int w, int h, bool swizzled)
    {
        var bytes16 = new byte[w * h * 2];
        int n = Math.Min(bytes16.Length, src.Length - p);
        src.Slice(p, n).CopyTo(bytes16);
        if (swizzled) UnswizzleMorton(bytes16, w, h, bytesPerPixel: 2);

        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            ushort v = (ushort)(bytes16[i * 2] | (bytes16[i * 2 + 1] << 8));
            // BGR565: bbbbbggggggrrrrr (B in high 5, R in low 5)
            int b = ((v >> 11) & 0x1F) * 255 / 31;
            int g = ((v >> 5)  & 0x3F) * 255 / 63;
            int r = (v         & 0x1F) * 255 / 31;
            int d = i * 4;
            pix[d + 0] = (byte)b; pix[d + 1] = (byte)g; pix[d + 2] = (byte)r; pix[d + 3] = 0xFF;
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeRgb565(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h && p + i * 2 + 1 < src.Length; i++)
        {
            ushort v = (ushort)(src[p + i * 2] | (src[p + i * 2 + 1] << 8));
            int r = ((v >> 11) & 0x1F) * 255 / 31;
            int g = ((v >> 5)  & 0x3F) * 255 / 63;
            int b = (v         & 0x1F) * 255 / 31;
            int d = i * 4;
            pix[d + 0] = (byte)b; pix[d + 1] = (byte)g; pix[d + 2] = (byte)r; pix[d + 3] = 0xFF;
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeBgra4444(ReadOnlySpan<byte> src, int p, int w, int h, bool swizzled)
    {
        var bytes16 = new byte[w * h * 2];
        int n = Math.Min(bytes16.Length, src.Length - p);
        src.Slice(p, n).CopyTo(bytes16);
        if (swizzled) UnswizzleMorton(bytes16, w, h, bytesPerPixel: 2);

        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            ushort v = (ushort)(bytes16[i * 2] | (bytes16[i * 2 + 1] << 8));
            int d = i * 4;
            pix[d + 0] = (byte)((v & 0x000F) << 4);   // B
            pix[d + 1] = (byte)(((v >> 4)  & 0x0F) << 4); // G
            pix[d + 2] = (byte)(((v >> 8)  & 0x0F) << 4); // R
            pix[d + 3] = (byte)(((v >> 12) & 0x0F) << 4); // A
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage DecodeRgba5551(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h && p + i * 2 + 1 < src.Length; i++)
        {
            ushort v = (ushort)(src[p + i * 2] | (src[p + i * 2 + 1] << 8));
            int d = i * 4;
            pix[d + 0] = (byte)((v >> 1) & 0x1F).Scale5To8();
            pix[d + 1] = (byte)((v >> 6) & 0x1F).Scale5To8();
            pix[d + 2] = (byte)((v >> 11) & 0x1F).Scale5To8();
            pix[d + 3] = (byte)((v & 0x0001) != 0 ? 0xFF : 0x00);
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage? DecodePal8WithPalette(ReadOnlySpan<byte> src, int p, int w, int h, int paletteOff)
    {
        if (paletteOff < 0) return null;
        // Palette record body starts at paletteOff + 16, BGRA quartets.
        int palPixStart = paletteOff + 16;
        int palBytes = Math.Min(256 * 4, src.Length - palPixStart);
        if (palBytes < 4) return null;

        var pix = new byte[w * h * 4];
        for (int i = 0; i < w * h && p + i < src.Length; i++)
        {
            byte idx = src[p + i];
            int palE = palPixStart + idx * 4;
            int d = i * 4;
            if (palE + 3 < src.Length)
            {
                pix[d + 0] = src[palE + 0];
                pix[d + 1] = src[palE + 1];
                pix[d + 2] = src[palE + 2];
                pix[d + 3] = src[palE + 3];
            }
            else
            {
                pix[d + 0] = pix[d + 1] = pix[d + 2] = idx; pix[d + 3] = 0xFF;
            }
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage Empty(int w, int h) => new(w, h, new byte[w * h * 4]);

    // ============== DXT / BC BLOCK DECODERS ==============

    private static FshImage? DecodeDxt1(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        if (w % 4 != 0 || h % 4 != 0) return null;
        int blocksX = w / 4, blocksY = h / 4;
        if (p + blocksX * blocksY * 8 > src.Length) return null;
        var pix = new byte[w * h * 4];
        Span<byte> palR = stackalloc byte[4];
        Span<byte> palG = stackalloc byte[4];
        Span<byte> palB = stackalloc byte[4];
        Span<byte> palA = stackalloc byte[4];
        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int b = p + (by * blocksX + bx) * 8;
            DecodeDxtColorBlock(src, b, palR, palG, palB, palA, dxt1Alpha: true);
            EmitBlock(pix, w, bx, by, palR, palG, palB, palA, indicesByte0: src[b + 4], indicesByte1: src[b + 5], indicesByte2: src[b + 6], indicesByte3: src[b + 7], alpha: null);
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage? DecodeDxt3(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        if (w % 4 != 0 || h % 4 != 0) return null;
        int blocksX = w / 4, blocksY = h / 4;
        if (p + blocksX * blocksY * 16 > src.Length) return null;
        var pix = new byte[w * h * 4];
        Span<byte> palR = stackalloc byte[4];
        Span<byte> palG = stackalloc byte[4];
        Span<byte> palB = stackalloc byte[4];
        Span<byte> palA = stackalloc byte[4];
        Span<byte> alpha = stackalloc byte[16];
        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int b = p + (by * blocksX + bx) * 16;
            // 8 bytes of explicit 4-bit-per-pixel alpha
            for (int i = 0; i < 8; i++)
            {
                byte v = src[b + i];
                alpha[i * 2 + 0] = (byte)((v & 0x0F) * 17);
                alpha[i * 2 + 1] = (byte)((v >> 4) * 17);
            }
            // 8 bytes of DXT1-style color (always treat as 4-color, no 1-bit alpha)
            DecodeDxtColorBlock(src, b + 8, palR, palG, palB, palA, dxt1Alpha: false);
            EmitBlock(pix, w, bx, by, palR, palG, palB, palA,
                      indicesByte0: src[b + 12], indicesByte1: src[b + 13],
                      indicesByte2: src[b + 14], indicesByte3: src[b + 15],
                      alpha: alpha);
        }
        return new FshImage(w, h, pix);
    }

    private static FshImage? DecodeDxt5(ReadOnlySpan<byte> src, int p, int w, int h)
    {
        if (w % 4 != 0 || h % 4 != 0) return null;
        int blocksX = w / 4, blocksY = h / 4;
        if (p + blocksX * blocksY * 16 > src.Length) return null;
        var pix = new byte[w * h * 4];
        Span<byte> palR = stackalloc byte[4];
        Span<byte> palG = stackalloc byte[4];
        Span<byte> palB = stackalloc byte[4];
        Span<byte> palA = stackalloc byte[4];
        Span<byte> alphaTable = stackalloc byte[8];
        Span<byte> alpha = stackalloc byte[16];
        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int b = p + (by * blocksX + bx) * 16;
            byte a0 = src[b], a1 = src[b + 1];
            alphaTable[0] = a0;
            alphaTable[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++) alphaTable[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++) alphaTable[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                alphaTable[6] = 0;
                alphaTable[7] = 255;
            }
            // 6 bytes = 48 bits, 16 pixels × 3 bits per pixel
            ulong bits = (ulong)src[b + 2]
                       | ((ulong)src[b + 3] << 8)
                       | ((ulong)src[b + 4] << 16)
                       | ((ulong)src[b + 5] << 24)
                       | ((ulong)src[b + 6] << 32)
                       | ((ulong)src[b + 7] << 40);
            for (int i = 0; i < 16; i++)
                alpha[i] = alphaTable[(int)((bits >> (3 * i)) & 0x7)];

            DecodeDxtColorBlock(src, b + 8, palR, palG, palB, palA, dxt1Alpha: false);
            EmitBlock(pix, w, bx, by, palR, palG, palB, palA,
                      indicesByte0: src[b + 12], indicesByte1: src[b + 13],
                      indicesByte2: src[b + 14], indicesByte3: src[b + 15],
                      alpha: alpha);
        }
        return new FshImage(w, h, pix);
    }

    private static void DecodeDxtColorBlock(
        ReadOnlySpan<byte> src, int b,
        Span<byte> palR, Span<byte> palG, Span<byte> palB, Span<byte> palA,
        bool dxt1Alpha)
    {
        ushort c0 = (ushort)(src[b]     | (src[b + 1] << 8));
        ushort c1 = (ushort)(src[b + 2] | (src[b + 3] << 8));
        R565(c0, out palR[0], out palG[0], out palB[0]);
        R565(c1, out palR[1], out palG[1], out palB[1]);
        palA[0] = palA[1] = palA[2] = palA[3] = 0xFF;
        if (!dxt1Alpha || c0 > c1)
        {
            palR[2] = (byte)((2 * palR[0] + palR[1]) / 3);
            palG[2] = (byte)((2 * palG[0] + palG[1]) / 3);
            palB[2] = (byte)((2 * palB[0] + palB[1]) / 3);
            palR[3] = (byte)((palR[0] + 2 * palR[1]) / 3);
            palG[3] = (byte)((palG[0] + 2 * palG[1]) / 3);
            palB[3] = (byte)((palB[0] + 2 * palB[1]) / 3);
        }
        else
        {
            palR[2] = (byte)((palR[0] + palR[1]) / 2);
            palG[2] = (byte)((palG[0] + palG[1]) / 2);
            palB[2] = (byte)((palB[0] + palB[1]) / 2);
            palR[3] = 0; palG[3] = 0; palB[3] = 0; palA[3] = 0;
        }
    }

    private static void EmitBlock(
        byte[] pix, int w, int bx, int by,
        Span<byte> palR, Span<byte> palG, Span<byte> palB, Span<byte> palA,
        byte indicesByte0, byte indicesByte1, byte indicesByte2, byte indicesByte3,
        Span<byte> alpha)
    {
        uint indices = (uint)(indicesByte0 | (indicesByte1 << 8) | (indicesByte2 << 16) | (indicesByte3 << 24));
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int local = y * 4 + x;
            int idx = (int)((indices >> (2 * local)) & 0x3);
            int px = (by * 4 + y) * w + (bx * 4 + x);
            int o = px * 4;
            pix[o + 0] = palB[idx];
            pix[o + 1] = palG[idx];
            pix[o + 2] = palR[idx];
            pix[o + 3] = alpha.IsEmpty ? palA[idx] : alpha[local];
        }
    }

    private static void R565(ushort v, out byte r, out byte g, out byte b)
    {
        r = (byte)(((v >> 11) & 0x1F) * 255 / 31);
        g = (byte)(((v >> 5)  & 0x3F) * 255 / 63);
        b = (byte)((v         & 0x1F) * 255 / 31);
    }

    // ============== MORTON-Z UNSWIZZLE ==============

    /// <summary>
    /// In-place Morton-Z (Z-order curve) unswizzle. EA's PC SHPI textures store raw pixels
    /// in Morton order; this rearranges them back to row-major. <paramref name="bpp"/> is
    /// bytes per pixel, supports 2 / 3 / 4. No-op if w or h is not a power of 2 (Morton
    /// addressing only well-defined then).
    /// </summary>
    public static void UnswizzleMorton(byte[] buffer, int width, int height, int bytesPerPixel)
    {
        if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height)) return;
        var tmp = new byte[buffer.Length];
        for (int i = 0; i < width * height; i++)
        {
            int x = Compact1(i);
            int y = Compact1(i >> 1);
            int dst = (y * width + x) * bytesPerPixel;
            int src = i * bytesPerPixel;
            if (src + bytesPerPixel > buffer.Length || dst + bytesPerPixel > buffer.Length) continue;
            for (int k = 0; k < bytesPerPixel; k++) tmp[dst + k] = buffer[src + k];
        }
        Array.Copy(tmp, buffer, buffer.Length);
    }

    private static int Compact1(int x)
    {
        x &= 0x55555555;
        x = (x ^ (x >> 1)) & 0x33333333;
        x = (x ^ (x >> 2)) & 0x0F0F0F0F;
        x = (x ^ (x >> 4)) & 0x00FF00FF;
        x = (x ^ (x >> 8)) & 0x0000FFFF;
        return x;
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;
}

file static class ScaleExt
{
    public static byte Scale5To8(this int v) => (byte)((v & 0x1F) * 255 / 31);
}

/// <summary>An RGBA32 decoded image. Pixel order is BGRA (matches WPF PixelFormats.Bgra32).</summary>
public sealed record FshImage(int Width, int Height, byte[] PixelsBgra32);

/// <summary>One record inside a multi-record FSH atlas — the (tag, offset) entry.</summary>
public sealed record FshRecord(string Tag, int Offset, byte Type, int Width, int Height);

public static class FshAtlas
{
    public static IReadOnlyList<FshRecord> EnumerateRecords(ReadOnlySpan<byte> fsh)
    {
        if (fsh.Length < 16) return Array.Empty<FshRecord>();
        if (!fsh[..4].SequenceEqual(FshDecoder.Magic)) return Array.Empty<FshRecord>();
        int n = BinaryPrimitives.ReadInt32LittleEndian(fsh.Slice(8, 4));
        if (n <= 0 || n > 4096) return Array.Empty<FshRecord>();
        int dirStart = 16;
        if (fsh.Length < dirStart + n * 8) return Array.Empty<FshRecord>();

        var result = new List<FshRecord>(n);
        for (int i = 0; i < n; i++)
        {
            string tag = System.Text.Encoding.ASCII.GetString(fsh.Slice(dirStart + i * 8, 4))
                .TrimEnd('\0').TrimEnd();
            int off = BinaryPrimitives.ReadInt32LittleEndian(fsh.Slice(dirStart + i * 8 + 4, 4));
            if (off < 0 || off + 16 > fsh.Length) continue;
            byte type = (byte)(fsh[off] & 0x7F);
            ushort w = BinaryPrimitives.ReadUInt16LittleEndian(fsh.Slice(off + 4, 2));
            ushort hgh = BinaryPrimitives.ReadUInt16LittleEndian(fsh.Slice(off + 6, 2));
            result.Add(new FshRecord(tag, off, type, w, hgh));
        }
        return result;
    }

    public static FshImage? DecodeRecordAt(ReadOnlySpan<byte> fsh, int offset)
    {
        if (offset <= 0 || offset >= fsh.Length - 16) return null;
        // Locate a palette record (if any) for indexed decoders.
        int palette = -1;
        foreach (var r in EnumerateRecords(fsh))
        {
            if (r.Type is 0x21 or 0x22 or 0x23 or 0x24 or 0x29 or 0x2A or 0x2C or 0x2D
                       or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x3A or 0x3B)
            { palette = r.Offset; break; }
        }
        return FshDecoder.TryDecodeRecord(fsh, offset, palette);
    }
}
