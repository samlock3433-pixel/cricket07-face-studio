using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cricket07.FaceStudio.Core;

public sealed record TextureAsset(string SourcePath, string ContainerPath, long Offset, int Length,
    string SourceHash, string ContentHash, int EntryIndex, string EntryName, int Width, int Height,
    byte Format, int MipCount, string WriteStatus)
{
    public string Key => $"{SourcePath}|{Offset}|{EntryIndex}";
    public string Label => $"{ContainerPath} / {EntryName} [{EntryIndex}]";
    public string Dimensions => $"{Width} × {Height}";
    public string Encoding => $"0x{Format:X2}";
}

public sealed class TextureCatalog
{
    public string Root { get; set; } = "";
    public List<TextureAsset> Textures { get; set; } = new();
    public List<string> Diagnostics { get; set; } = new();
    public Dictionary<string, string> Aliases { get; set; } = new();

    public IEnumerable<TextureAsset> Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Textures.Where(t => words.All(w =>
            $"{t.Label} {t.SourceHash} {t.ContentHash} {Aliases.GetValueOrDefault(t.Key)}"
                .Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static TextureCatalog Scan(string root, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new TextureCatalog { Root = Path.GetFullPath(root) };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 4) continue;
                byte[] magic = Read(stream, 0, 4);
                if (!IsContainer(magic))
                {
                    if (Path.GetExtension(file).ToLowerInvariant() is ".fsh" or ".big" or ".gob")
                    {
                        result.Diagnostics.Add($"{file}: unsupported signature (possibly compressed). Read-only.");
                        if (Path.GetExtension(file).Equals(".fsh", StringComparison.OrdinalIgnoreCase) && stream.Length <= int.MaxValue)
                        {
                            stream.Position = 0;
                            string opaqueHash = HashStream(stream, ct);
                            result.Textures.Add(new(file, Path.GetRelativePath(root, file), 0, (int)stream.Length,
                                opaqueHash, opaqueHash, -1, "compressed/unknown", 0, 0, 0, 0, "Read-only: unsupported container"));
                        }
                    }
                    continue;
                }
                progress?.Report(Path.GetRelativePath(root, file));
                stream.Position = 0;
                string hash = HashStream(stream, ct);
                Walk(stream, 0, stream.Length, Path.GetRelativePath(root, file), hash, 0);

                void Walk(Stream input, long start, long length, string chain, string sourceHash, int depth)
                {
                    ct.ThrowIfCancellationRequested();
                    if (depth > 12) throw new InvalidDataException("Archive nesting exceeds 12 levels.");
                    if (length < 16) return;
                    var header = Read(input, start, 16);
                    if (header.AsSpan(0, 4).SequenceEqual("SHPI"u8))
                    {
                        if (length > 128 * 1024 * 1024) throw new InvalidDataException("FSH exceeds 128 MiB safety limit.");
                        var bytes = Read(input, start, (int)length);
                        var entries = FshTextureEditor.Inspect(bytes);
                        string contentHash = Convert.ToHexString(SHA256.HashData(bytes));
                        foreach (var entry in entries)
                            result.Textures.Add(new(file, chain, start, bytes.Length, sourceHash, contentHash,
                                entry.Index, entry.Name, entry.Width, entry.Height, entry.Format, entry.MipCount, entry.WriteStatus));
                        return;
                    }
                    if (!header.AsSpan(0, 4).SequenceEqual("BIGF"u8) && !header.AsSpan(0, 4).SequenceEqual("BIG4"u8)) return;
                    int count = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
                    int end = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
                    if (count < 0 || count > 100000 || end < 16 || end > length || end > 32 * 1024 * 1024)
                        throw new InvalidDataException("Invalid BIG directory bounds.");
                    var table = Read(input, start, end);
                    int cursor = 16;
                    var members = new List<(string Name, int Offset, int Size)>();
                    for (int i = 0; i < count; i++)
                    {
                        if (cursor > end - 9) throw new InvalidDataException("Truncated BIG directory.");
                        int off = BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(cursor, 4));
                        int size = BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(cursor + 4, 4));
                        int nameStart = cursor + 8;
                        cursor = Array.IndexOf(table, (byte)0, nameStart);
                        if (cursor < 0 || off < end || size < 0 || (long)off + size > length)
                            throw new InvalidDataException("Invalid BIG member bounds/name.");
                        members.Add((Encoding.ASCII.GetString(table, nameStart, cursor - nameStart), off, size));
                        cursor++;
                    }
                    long previousEnd = end;
                    foreach (var m in members.OrderBy(m => m.Offset))
                    {
                        if (m.Offset < previousEnd) throw new InvalidDataException("Overlapping BIG members are unsupported.");
                        previousEnd = (long)m.Offset + m.Size;
                    }
                    foreach (var m in members)
                    {
                        if (m.Size < 4) continue;
                        var signature = Read(input, start + m.Offset, 4);
                        if (IsContainer(signature))
                        {
                            try { Walk(input, start + m.Offset, m.Size, chain + " > " + m.Name, sourceHash, depth + 1); }
                            catch (InvalidDataException ex) { result.Diagnostics.Add($"{chain} > {m.Name}: {ex.Message}"); }
                        }
                        else if (m.Name.EndsWith(".fsh", StringComparison.OrdinalIgnoreCase) || m.Name is "textures")
                        {
                            result.Diagnostics.Add($"{chain} > {m.Name}: unsupported/compressed texture payload.");
                            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            input.Position = start + m.Offset;
                            int remaining = m.Size;
                            byte[] buffer = new byte[Math.Min(1024 * 1024, remaining)];
                            while (remaining > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                int n = Math.Min(buffer.Length, remaining);
                                input.ReadExactly(buffer.AsSpan(0, n)); digest.AppendData(buffer, 0, n); remaining -= n;
                            }
                            result.Textures.Add(new(file, chain + " > " + m.Name, start + m.Offset, m.Size,
                                sourceHash, Convert.ToHexString(digest.GetHashAndReset()), -1, "compressed/unknown", 0, 0, 0, 0,
                                "Read-only: unsupported container"));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { result.Diagnostics.Add($"{file}: {ex.Message}"); }
        }
        return result;
    }

    private static bool IsContainer(byte[] magic) => magic.AsSpan().SequenceEqual("SHPI"u8)
        || magic.AsSpan().SequenceEqual("BIGF"u8) || magic.AsSpan().SequenceEqual("BIG4"u8);

    internal static byte[] Read(Stream input, long offset, int length)
    {
        input.Position = offset;
        var bytes = new byte[length];
        input.ReadExactly(bytes);
        return bytes;
    }

    public static string HashStream(Stream input, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
