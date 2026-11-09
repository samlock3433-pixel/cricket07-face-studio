using System.Buffers.Binary;
using System.Text;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class FshRebuildTests
{
    // Section = 16-byte header (format, chained block length, size) + pixels + attachments, as in stock face FSHs.
    private static byte[] Section(byte format, int w, int h, byte[] pixels, string name)
    {
        var metal = new byte[80]; metal[0] = 0x69; Encoding.ASCII.GetBytes("EAGL64 metal bin").CopyTo(metal, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(metal, 80u << 8 | 0x69);
        var label = new byte[16]; label[0] = 0x70; Encoding.ASCII.GetBytes(name).CopyTo(label, 4);
        var header = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + pixels.Length) << 8 | format);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)w); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)h);
        return header.Concat(pixels).Concat(metal).Concat(label).ToArray();
    }
    private static byte[] Container(params (string Name, byte[] Section)[] entries)
    {
        int first = 16 + entries.Length * 8; first = (first + 15) & ~15;
        var output = new byte[first]; "SHPI"u8.CopyTo(output); BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), entries.Length); "G359"u8.CopyTo(output.AsSpan(12));
        if (first > 16 + entries.Length * 8) "Buy ERTS"u8.CopyTo(output.AsSpan(16 + entries.Length * 8));
        var list = output.ToList();
        for (int i = 0; i < entries.Length; i++)
        {
            Encoding.ASCII.GetBytes(entries[i].Name).CopyTo(output, 16 + i * 8);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(20 + i * 8), list.Count);
            list.AddRange(entries[i].Section);
        }
        var result = output.Concat(list.Skip(output.Length)).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length);
        return result;
    }
    private static byte[] Bgra(int w, int h, byte g) { var p = new byte[w * h * 4]; for (int i = 0; i < p.Length; i += 4) { p[i + 1] = g; p[i + 3] = 255; } return p; }

    [Fact]
    public void RebuildGrowsOneEntryAndKeepsAttachmentsAndOtherEntries()
    {
        var face = Section(0x7D, 4, 4, Bgra(4, 4, 10), "face");
        var hair = Section(0x7D, 4, 4, Bgra(4, 4, 200), "hair");
        var fsh = Container(("face", face), ("hair", hair));
        var rebuilt = FshTextureEditor.Rebuild(fsh, 0, 8, 8, Bgra(8, 8, 99));
        var entries = FshTextureEditor.Inspect(rebuilt);
        Assert.Equal((8, 8), (entries[0].Width, entries[0].Height));
        Assert.Equal(rebuilt.Length, BinaryPrimitives.ReadInt32LittleEndian(rebuilt.AsSpan(4)));
        Assert.Equal(fsh.Length + (8 * 8 - 4 * 4) * 4, rebuilt.Length);
        Assert.Equal(hair, rebuilt.AsSpan(entries[1].Offset, hair.Length).ToArray());                   // other entry verbatim, shifted
        Assert.Equal(face[(16 + 64)..], rebuilt.AsSpan(entries[0].Offset + 16 + 256, face.Length - 80).ToArray()); // attachments verbatim
        Assert.Equal(99, FshTextureEditor.Preview(rebuilt, 0)!.PixelsBgra32[1]);
        Assert.Equal(fsh, FshTextureEditor.Rebuild(rebuilt, 0, 4, 4, Bgra(4, 4, 10)));                  // shrinking back restores the original bytes
        Assert.Throws<InvalidDataException>(() => FshTextureEditor.Rebuild(fsh, 0, 12, 12, Bgra(12, 12, 1))); // not a power of two
    }

    [SkippableFact]
    public void StockFaceRebuiltAt512HasCommunityFaceLayout()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "templates", "heads", "1188", "textures.fsh"))) dir = Path.GetDirectoryName(dir);
        Skip.If(dir is null, "Template library not found.");
        var stock = File.ReadAllBytes(Path.Combine(dir!, "templates", "heads", "1188", "textures.fsh"));
        var face = FshTextureEditor.Preview(stock, 0)!;
        var big = new byte[512 * 512 * 4];
        for (int y = 0; y < 512; y++) for (int x = 0; x < 512; x++) Array.Copy(face.PixelsBgra32, ((y / 2) * 256 + x / 2) * 4, big, (y * 512 + x) * 4, 4);
        var rebuilt = FshTextureEditor.Rebuild(stock, 0, 512, 512, big);
        var entry = FshTextureEditor.Inspect(rebuilt).Single();
        // Same shape as gill.big's 512 face (DXT1 512×512, single level, attachments after the pixels), but keeping
        // EA's 16 padding bytes after the pixels (stock block = 16+32768+16; gill's community tool drops them).
        Assert.Equal((0x60, 512, 512, 1), ((int)entry.Format, entry.Width, entry.Height, entry.MipCount));
        Assert.Equal(16 + 131072 + 16, (int)(BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(entry.Offset, 4)) >> 8));
        Assert.Equal(stock.Length + 3 * 32768, rebuilt.Length);
        Assert.Equal(stock[..32], rebuilt[..32].Select((b, i) => i is >= 4 and < 8 ? stock[i] : b).ToArray()); // header, directory, "Buy ERTS"
        Assert.Equal(stock[(32 + 16 + 32768)..], rebuilt[(32 + 16 + 131072)..]);                            // 0x69 + 0x70 attachments
    }
}
