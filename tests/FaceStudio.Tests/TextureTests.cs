using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class FaceStudioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "c07-face-tests-" + Guid.NewGuid().ToString("N"));
    public FaceStudioTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private static byte[] Fsh(byte format = 0x7D, int mipCount = 1, int payload = 64)
    {
        var data = new byte[24 + 16 + payload];
        "SHPI"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 1);
        "GIMXface"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), 24);
        data[24] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(30), 4);
        data[39] = (byte)((mipCount-1) << 4);
        return data;
    }

    private static byte[] Big(byte[] inner)
    {
        byte[] name = Encoding.ASCII.GetBytes("textures\0");
        int offset = 24 + name.Length;
        byte[] data = new byte[offset + inner.Length];
        "BIGF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), offset);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), offset);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), inner.Length);
        name.CopyTo(data, 24); inner.CopyTo(data, offset);
        return data;
    }

    private static byte[] Pixels(byte alpha = 255) => Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 0, 0, 255, alpha }).ToArray();

    [Fact]
    public void RawReplacementPreservesHeaderAndOriginal()
    {
        var source = Fsh(); var snapshot = (byte[])source.Clone();
        var edited = FshTextureEditor.Replace(source, 0, 4, 4, Pixels());
        Assert.Equal(snapshot, source);
        Assert.Equal(source[..40], edited[..40]);
        Assert.Equal(Pixels(), edited[40..]);
    }

    [Theory]
    [InlineData(0x60, 8)] [InlineData(0x61, 16)] [InlineData(0x62, 16)]
    public void DxtEncodedSolidRedDecodesAsRed(int format, int size)
    {
        var source = Fsh((byte)format, payload: size);
        var edited = FshTextureEditor.Replace(source, 0, 4, 4, Pixels());
        var decoded = FshTextureEditor.Preview(edited, 0)!;
        for (int p = 0; p < 16; p++)
        {
            Assert.InRange(decoded.PixelsBgra32[p*4+2], 248, 255);
            Assert.InRange(decoded.PixelsBgra32[p*4], 0, 8);
            Assert.Equal(255, decoded.PixelsBgra32[p*4+3]);
        }
    }

    [Theory]
    [InlineData(0x60, 8, 0)] [InlineData(0x61, 16, 102)] [InlineData(0x62, 16, 119)]
    public void DxtPreservesSupportedAlpha(int format, int size, byte alpha)
    {
        var result = FshTextureEditor.Replace(Fsh((byte)format, payload: size), 0, 4, 4, Pixels(alpha));
        Assert.Equal(alpha, FshTextureEditor.Preview(result, 0)!.PixelsBgra32[3]);
    }

    [Fact]
    public void RegeneratesMipChainAndPreservesTrailer()
    {
        var source = Fsh(0x7D, mipCount: 3, payload: 88);
        source[^1] = 123;
        var result = FshTextureEditor.Replace(source, 0, 4, 4, Pixels());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, result[120..124]);
        Assert.Equal(123, result[^1]);
    }

    [Fact]
    public void RefusesWrongDimensionsTruncationCompressionAndFractionalDxt1Alpha()
    {
        Assert.Throws<InvalidDataException>(() => FshTextureEditor.Replace(Fsh(), 0, 2, 8, Pixels()));
        Assert.StartsWith("Read-only", FshTextureEditor.Inspect(Fsh(payload: 10))[0].WriteStatus);
        Assert.StartsWith("Read-only", FshTextureEditor.Inspect(Fsh(0xE0))[0].WriteStatus);
        Assert.Throws<InvalidDataException>(() => FshTextureEditor.Replace(Fsh(0x60, payload: 8), 0, 4, 4, Pixels(100)));
    }

    [Fact]
    public void NestedArchiveExportsOnlySelectedPixelsAndDoesNotAlterSource()
    {
        string root = Path.Combine(_root, "game"); Directory.CreateDirectory(root);
        byte[] source = Big(Big(Fsh()));
        string path = Path.Combine(root, "face.big"); File.WriteAllBytes(path, source);
        var catalog = TextureCatalog.Scan(root);
        var asset = Assert.Single(catalog.Textures);
        Assert.Contains("textures > textures", asset.Label);
        catalog.Aliases[asset.Key] = "india_face";
        Assert.Single(catalog.Search("india_face"));
        var replacement = FshTextureEditor.Replace(TexturePackage.ReadVerified(asset), 0, 4, 4, Pixels());
        string output = Path.Combine(_root, "output.big");
        TexturePackage.Export(asset, replacement, output, root);
        Assert.Equal(source, File.ReadAllBytes(path));
        var exported = File.ReadAllBytes(output);
        Assert.Equal(source.Length, exported.Length);
        for (int i = 0; i < source.Length; i++)
            if (i < asset.Offset+40 || i >= asset.Offset+104) Assert.Equal(source[i], exported[i]);
        Assert.True(File.Exists(output + ".manifest.json"));
        Assert.Throws<IOException>(() => TexturePackage.Export(asset, replacement, path, root));
        source[^1] ^= 1; File.WriteAllBytes(path, source);
        Assert.Throws<IOException>(() => TexturePackage.Export(asset, replacement, Path.Combine(_root, "stale.big"), root));
    }

    [Fact]
    public void BadDirectoryReportedWithoutDiscardingOtherFiles()
    {
        var broken = Big(Fsh()); BinaryPrimitives.WriteInt32BigEndian(broken.AsSpan(16), 0);
        File.WriteAllBytes(Path.Combine(_root, "bad.big"), broken);
        File.WriteAllBytes(Path.Combine(_root, "good.fsh"), Fsh());
        var catalog = TextureCatalog.Scan(_root);
        Assert.Single(catalog.Textures); Assert.Single(catalog.Diagnostics);
    }

    [Theory]
    [InlineData(0)] [InlineData(0x10)] [InlineData(0x20)] [InlineData(0x30)]
    public void TgaHonorsBothOriginBits(int origin)
    {
        byte[] data = new byte[18+12]; data[2] = 2; data[12] = 2; data[14] = 2; data[16] = 24; data[17] = (byte)origin;
        for (int p = 0; p < 4; p++) data[18+p*3] = (byte)(p+1);
        var image = TgaImage.Read(data);
        int sourceX = (origin & 16) != 0 ? 1 : 0;
        int sourceY = (origin & 32) != 0 ? 0 : 1;
        Assert.Equal(1+sourceY*2+sourceX, image.Bgra[0]);
    }

    [Fact]
    public void TgaRleBoundsAreChecked()
    {
        byte[] data = new byte[22]; data[2] = 10; data[12] = 1; data[14] = 1; data[16] = 24; data[18] = 0x81;
        Assert.Throws<InvalidDataException>(() => TgaImage.Read(data));
        data[18] = 0x80; data[19] = 77;
        Assert.Equal(77, TgaImage.Read(data).Bgra[0]);
    }

    [SkippableFact]
    public void RealInstallationSamplesEncodeWithoutChangingOtherBytes()
    {
        string? path = Environment.GetEnvironmentVariable("C07_FACE_STUDIO_CATALOG");
        Skip.If(string.IsNullOrEmpty(path), "Set C07_FACE_STUDIO_CATALOG to opt into read-only installation samples.");
        var catalog = JsonSerializer.Deserialize<TextureCatalog>(File.ReadAllText(path!))!;
        // A scan is a snapshot: mods get removed or changed. Sample only records that still verify.
        bool Current(TextureAsset a) { try { TexturePackage.ReadVerified(a); return true; } catch (IOException) { return false; } }
        var ready = catalog.Textures.Where(a => a.WriteStatus == "Ready").GroupBy(a => a.Format)
            .Select(g => g.OrderBy(a => a.Width * a.Height).FirstOrDefault(Current)).Where(a => a is not null).ToArray();
        Skip.If(ready.Length == 0, "No catalog sample still matches the installation. Rescan.");
        foreach (var asset in ready.Select(a => a!))
        {
            var original = TexturePackage.ReadVerified(asset);
            var entry = FshTextureEditor.Inspect(original).Single(e => e.Index == asset.EntryIndex);
            var pixels = new byte[asset.Width * asset.Height * 4];
            for (int p = 0; p < pixels.Length; p += 4) { pixels[p+1] = 255; pixels[p+3] = 255; }
            var replacement = FshTextureEditor.Replace(original, asset.EntryIndex, asset.Width, asset.Height, pixels);
            Assert.NotNull(FshTextureEditor.Preview(replacement, asset.EntryIndex));
            Assert.Equal(original.Length, replacement.Length);
            for (int i = 0; i < original.Length; i++)
                if (i < entry.Offset+16 || i >= entry.Offset+16+entry.PixelLength) Assert.Equal(original[i], replacement[i]);
            // Source remains untouched, even for images embedded deep in data.gob.
            Assert.Equal(original, TexturePackage.ReadVerified(asset));
        }
        var loose = catalog.Textures.First(a => a.WriteStatus == "Ready" && a.Offset == 0 && a.Length < 1024*1024 && Current(a));
        var bytes = TexturePackage.ReadVerified(loose);
        TexturePackage.Export(loose, bytes, Path.Combine(_root, "real-export.fsh"), catalog.Root);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, "real-export.fsh")));
    }
}
