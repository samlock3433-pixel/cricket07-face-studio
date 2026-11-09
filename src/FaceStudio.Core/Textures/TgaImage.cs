using System.Buffers.Binary;

namespace Cricket07.FaceStudio.Core;

public sealed record ImportedImage(int Width, int Height, byte[] Bgra);

public static class TgaImage
{
    public static ImportedImage Read(byte[] data)
    {
        if (data.Length < 18) throw new InvalidDataException("Truncated TGA header.");
        int type = data[2], bits = data[16], descriptor = data[17];
        if (data[1] != 0 || type is not (2 or 10) || bits is not (24 or 32) || (descriptor & 0xC0) != 0)
            throw new NotSupportedException("TGA must be non-interleaved true-color 24/32-bit (raw or RLE).");
        if ((descriptor & 15) != 0 && !(bits == 32 && (descriptor & 15) == 8))
            throw new NotSupportedException("Unsupported TGA alpha depth.");
        int w = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12, 2));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14, 2));
        if (w < 1 || h < 1 || w > 4096 || h > 4096) throw new InvalidDataException("TGA dimensions must be 1..4096.");
        var pixels = new byte[w * h * 4];
        int cursor = 18 + data[0], pixel = 0, stride = bits / 8;
        while (pixel < w * h)
        {
            int run = 1; bool repeat = false;
            if (type == 10)
            {
                if (cursor >= data.Length) throw new InvalidDataException("Truncated TGA RLE.");
                int packet = data[cursor++]; repeat = (packet & 128) != 0; run = (packet & 127) + 1;
            }
            if (run > w*h-pixel) throw new InvalidDataException("TGA RLE exceeds image bounds.");
            int source = cursor;
            for (int k = 0; k < run; k++)
            {
                int p = repeat ? source : cursor;
                if (p > data.Length-stride) throw new InvalidDataException("Truncated TGA pixels.");
                int x = pixel % w, y = pixel / w;
                if ((descriptor & 0x20) == 0) y = h-1-y;
                if ((descriptor & 0x10) != 0) x = w-1-x;
                int dest = (y*w+x)*4;
                Array.Copy(data, p, pixels, dest, 3);
                pixels[dest+3] = bits == 32 && (descriptor & 15) == 8 ? data[p+3] : (byte)255;
                if (!repeat) cursor += stride;
                pixel++;
            }
            if (repeat) cursor += stride;
        }
        return new(w, h, pixels);
    }
}
