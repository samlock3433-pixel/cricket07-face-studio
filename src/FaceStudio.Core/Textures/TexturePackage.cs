using System.Security.Cryptography;
using System.Text.Json;

namespace Cricket07.FaceStudio.Core;

public static class TexturePackage
{
    public static byte[] ReadVerified(TextureAsset asset)
    {
        if (!File.Exists(asset.SourcePath))
            throw new FileNotFoundException("Texture source no longer exists. Rescan the game folder.", asset.SourcePath);
        using var input = File.OpenRead(asset.SourcePath);
        var data = TextureCatalog.Read(input, asset.Offset, asset.Length);
        if (Convert.ToHexString(SHA256.HashData(data)) != asset.ContentHash)
            throw new IOException("Texture changed since scanning. Rescan before editing.");
        return data;
    }

    /// <summary>Exports a complete outer archive or loose FSH without writing into the installation.</summary>
    public static void Export(TextureAsset asset, byte[] replacement, string destination, string gameRoot, CancellationToken ct = default)
    {
        destination = Path.GetFullPath(destination);
        string root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || destination.Equals(Path.GetFullPath(asset.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose an output folder outside the scanned installation.");
        if (File.Exists(destination) || File.Exists(destination + ".manifest.json"))
            throw new IOException("Choose a new output filename; existing packages are never overwritten.");
        if (replacement.Length != asset.Length) throw new InvalidDataException("Replacement must preserve container size.");
        var original = ReadVerified(asset);
        var entry = FshTextureEditor.Inspect(original).Single(e => e.Index == asset.EntryIndex);
        if (entry.WriteStatus != "Ready") throw new InvalidDataException(entry.WriteStatus);
        for (int i = 0; i < original.Length; i++)
            if ((i < entry.Offset + 16 || i >= entry.Offset + 16 + entry.PixelLength) && original[i] != replacement[i])
                throw new InvalidDataException("Replacement changed data outside the selected texture's pixels.");
        _ = FshTextureEditor.Inspect(replacement);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string manifestTemp = temp + ".json";
        try
        {
            using (var input = new FileStream(asset.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (TextureCatalog.HashStream(input, ct) != asset.SourceHash)
                    throw new IOException("Source archive changed since scanning. Rescan first.");
                input.Position = 0;
                using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Position = asset.Offset;
                output.Write(replacement);
                output.Flush(true);
                var check = TextureCatalog.Read(output, asset.Offset, replacement.Length);
                if (!check.AsSpan().SequenceEqual(replacement)) throw new IOException("Package verification failed.");
            }
            using var final = File.OpenRead(temp);
            string finalHash = TextureCatalog.HashStream(final, ct);
            final.Close();
            File.WriteAllText(manifestTemp, JsonSerializer.Serialize(new {
                version = 1, source = asset.SourcePath, sourceSha256 = asset.SourceHash,
                outputSha256 = finalHash, texture = asset.Label, offset = asset.Offset,
                entry = asset.EntryIndex, format = asset.Encoding, dimensions = asset.Dimensions,
                mipCount = asset.MipCount, gameValidated = false,
                note = "Same-layout texture replacement. Does not change player assignments or loader precedence."
            }, new JsonSerializerOptions { WriteIndented = true }));
            ct.ThrowIfCancellationRequested();
            File.Move(temp, destination);
            try { File.Move(manifestTemp, destination + ".manifest.json"); }
            catch { File.Delete(destination); throw; }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if (File.Exists(manifestTemp)) File.Delete(manifestTemp); }
    }
}
