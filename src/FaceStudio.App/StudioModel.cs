using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio;

public sealed class StudioModel
{
    public string SourcePath { get; }
    public string SourceHash { get; }
    public string WorkDirectory { get; }
    public ModelDocument Document { get; }
    public List<GeometryModel3D> Geometry { get; } = new();
    /// <summary>Preview-only inside of the mesh. The game supplies eyeballs separately, so open sockets must read as holes.</summary>
    public static readonly DiffuseMaterial InteriorMaterial = CreateInteriorMaterial();
    private static DiffuseMaterial CreateInteriorMaterial()
    { var material=new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(16,14,14)));material.Freeze();return material; }
    public Dictionary<string, byte[]> TextureOverrides { get; } = new();
    private readonly OtoolsConverter _converter;
    /// <summary>The converter this model was opened with (to open related models the same way).</summary>
    public OtoolsConverter Converter => _converter;
    /// <summary>Suggested file name (without .big) when saving; by default taken from the source file.</summary>
    public string? SaveName { get; set; }
    private readonly NativeModelPatch _native;

    private StudioModel(string source, string hash, string folder, ModelDocument document, OtoolsConverter converter,NativeModelPatch native)
    { SourcePath=source; SourceHash=hash; WorkDirectory=folder; Document=document; _converter=converter;_native=native; }

    public static async Task<StudioModel> OpenAsync(string path, OtoolsConverter converter, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        if(new FileInfo(path).Length>128*1024*1024)throw new InvalidDataException("Face source exceeds 128 MiB.");
        byte[] source = await File.ReadAllBytesAsync(path, ct);
        string hash = Convert.ToHexString(SHA256.HashData(source));
        FaceArchive? archive = source.AsSpan().StartsWith("BIGF"u8) || source.AsSpan().StartsWith("BIG4"u8) ? new FaceArchive(source) : null;
        if(archive is not null)source=archive.Model;
        if (source.Length < 4 || source[0] != 0x7F || source[1] != (byte)'E' || source[2] != (byte)'L' || source[3] != (byte)'F')
            throw new InvalidDataException("Choose an uncompressed Cricket 07 ELF .o mesh, not a morph archive or renamed image.");
        string folder = Path.Combine(WorkspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "model.o");
        await File.WriteAllBytesAsync(copy, source, ct);
        string textureSource = Path.Combine(Path.GetDirectoryName(path)!, "textures.fsh");
        if(archive is not null)await File.WriteAllBytesAsync(Path.Combine(folder,"textures.fsh"),archive.Texture,ct);
        else if (File.Exists(textureSource)) File.Copy(textureSource, Path.Combine(folder, "textures.fsh"));
        string gltf = Path.Combine(folder, "model.gltf");
        await converter.ConvertAsync("export", copy, gltf, ct);
        var document = ModelDocument.Load(gltf);
        string dump=Path.Combine(folder,"model.dump.txt");
        await converter.ConvertAsync("dump",copy,dump,ct);
        var native=NativeModelPatch.Bind(source,File.ReadAllText(dump),document);
        var model = new StudioModel(path, hash, folder, document, converter,native);
        string cachedTexture=Path.Combine(folder,"textures.fsh");
        byte[]? fsh = File.Exists(cachedTexture) ? File.ReadAllBytes(cachedTexture) : null;
        IReadOnlyList<FshTextureEntry> entries = Array.Empty<FshTextureEntry>();
        if (fsh is not null) { try { entries = FshTextureEditor.Inspect(fsh); } catch (InvalidDataException) { } }
        foreach (var part in document.Parts)
        {
            var mesh = new MeshGeometry3D {
                Positions = new Point3DCollection(part.Positions.Select(v => new Point3D(v.X,v.Y,v.Z))),
                Normals = new Vector3DCollection(part.Normals.Select(v => new Vector3D(v.X,v.Y,v.Z))),
                TextureCoordinates = new System.Windows.Media.PointCollection(part.Uvs.Select(v => new System.Windows.Point(v.X,v.Y))),
                TriangleIndices = new Int32Collection(part.Indices)
            };
            bool isHair = part.Name.Contains("hair", StringComparison.OrdinalIgnoreCase)
                || part.Name.Contains("hhr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(part.ImageUri), "hair", StringComparison.OrdinalIgnoreCase);
            Brush brush = new SolidColorBrush(isHair ? Color.FromRgb(85,67,54) : Color.FromRgb(204,164,137));
            if (part.ImageUri is string uri && !uri.StartsWith("data:"))
            {
                string imagePath = ModelDocument.ContainedPath(folder, uri);
                var entry = entries.FirstOrDefault(e => e.Name.TrimEnd('\0').Equals(Path.GetFileNameWithoutExtension(uri), StringComparison.OrdinalIgnoreCase));
                if (entry is not null && fsh is not null && FshTextureEditor.Preview(fsh, entry.Index) is { } image)
                {
                    var bitmap = BitmapSource.Create(image.Width,image.Height,96,96,PixelFormats.Bgra32,null,image.PixelsBgra32,image.Width*4);
                    SavePng(bitmap,imagePath); brush = UvBrush(bitmap);
                }
                else if (File.Exists(imagePath)) brush = UvBrush(ReadBitmap(imagePath));
                // Missing textures are previewed with a neutral material; export keeps texture references intact.
            }
            var material = new DiffuseMaterial(brush);
            model.Geometry.Add(new GeometryModel3D(mesh,material) { BackMaterial=InteriorMaterial });
        }
        return model;
    }

    public void ValidateTexture(int partIndex, BitmapSource image)
    {
        var uri = Document.Parts[partIndex].ImageUri ?? throw new InvalidDataException("This part has no texture binding.");
        if (uri.StartsWith("data:")) throw new NotSupportedException("Embedded glTF textures are not editable in this version.");
        string fshPath = Path.Combine(WorkDirectory,"textures.fsh");
        if (!File.Exists(fshPath)) throw new InvalidDataException("This template provides geometry only. Put its matching textures.fsh beside your source .o and reopen it to edit its texture.");
        byte[] fsh = File.ReadAllBytes(fshPath);
        var entry = FshTextureEditor.Inspect(fsh).SingleOrDefault(e => e.Name.TrimEnd('\0').Equals(Path.GetFileNameWithoutExtension(uri),StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The source FSH has no entry matching this mesh's texture binding.");
        var converted = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
        var pixels = new byte[checked(converted.PixelWidth*converted.PixelHeight*4)];
        converted.CopyPixels(pixels,converted.PixelWidth*4,0);
        _ = FshTextureEditor.Rebuild(fsh,entry.Index,converted.PixelWidth,converted.PixelHeight,pixels);
    }

    /// <summary>The part's texture as currently shown: the applied replacement, else the decoded FSH image.</summary>
    public BitmapSource CurrentTexture(int partIndex)
    {
        var uri = Document.Parts[partIndex].ImageUri ?? throw new InvalidDataException("This part has no texture binding.");
        if (TextureOverrides.TryGetValue(uri, out var png))
        { using var stream = new MemoryStream(png); return new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]; }
        string path = ModelDocument.ContainedPath(WorkDirectory, uri);
        if (!File.Exists(path)) throw new InvalidDataException("This part's texture could not be decoded, so it cannot be edited.");
        return ReadBitmap(path);
    }

    public void Wrap(int partIndex, BitmapSource image)
    {
        ValidateTexture(partIndex,image);
        var uri = Document.Parts[partIndex].ImageUri!;
        byte[] png;
        using (var stream = new MemoryStream()) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream); png=stream.ToArray(); }
        TextureOverrides[uri]=png;
        Preview(partIndex,image);
    }

    /// <summary>Shows an image on every part bound to this part's texture, without storing it (live painting).</summary>
    public void Preview(int partIndex, ImageSource image)
    {
        var uri = Document.Parts[partIndex].ImageUri;
        for (int i=0;i<Document.Parts.Count;i++) if (Document.Parts[i].ImageUri==uri) Geometry[i].Material=new DiffuseMaterial(UvBrush(image));
    }

    /// <summary>Puts back a texture state recorded for undo: an earlier replacement (PNG bytes), or null for the original texture.</summary>
    public void RestoreTexture(int partIndex, byte[]? png)
    {
        var uri = Document.Parts[partIndex].ImageUri ?? throw new InvalidDataException("This part has no texture binding.");
        if (png is null) TextureOverrides.Remove(uri); else TextureOverrides[uri]=png;
        Preview(partIndex,CurrentTexture(partIndex));
    }

    public async Task ExportAsync(string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        for (int p=0;p<Document.Parts.Count;p++)
        {
            var mesh = (MeshGeometry3D)Geometry[p].Geometry;
            for (int i=0;i<mesh.Positions.Count;i++) { var v=mesh.Positions[i]; Document.Parts[p].Positions[i]=new Vector3((float)v.X,(float)v.Y,(float)v.Z); }
        }
        string gltf = Path.Combine(folder,"edited.gltf");
        Document.Save(gltf);
        foreach (string uri in Document.ImageUris.Where(u => !string.IsNullOrEmpty(u) && !u.StartsWith("data:")))
        {
            string destination = ModelDocument.ContainedPath(folder,uri);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (TextureOverrides.TryGetValue(uri,out var bytes)) File.WriteAllBytes(destination,bytes);
            else
            {
                string source = ModelDocument.ContainedPath(WorkDirectory,uri);
                if(File.Exists(source)) File.Copy(source,destination);
            }
        }
        string originalTexture = Path.Combine(WorkDirectory,"textures.fsh");
        if (File.Exists(originalTexture))
        {
            var bytes=File.ReadAllBytes(originalTexture);
            foreach(var replacement in TextureOverrides)
            {
                var entry=FshTextureEditor.Inspect(bytes).SingleOrDefault(e=>e.Name.TrimEnd('\0').Equals(Path.GetFileNameWithoutExtension(replacement.Key),StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("Replacement texture has no matching FSH entry.");
                using var imageStream=new MemoryStream(replacement.Value);
                var bitmap=new PngBitmapDecoder(imageStream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                var image=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
                var pixels=new byte[image.PixelWidth*image.PixelHeight*4]; image.CopyPixels(pixels,image.PixelWidth*4,0);
                bytes=FshTextureEditor.Rebuild(bytes,entry.Index,image.PixelWidth,image.PixelHeight,pixels);
            }
            File.WriteAllBytes(Path.Combine(folder,"textures.fsh"),bytes);
        }
        else if(TextureOverrides.Count>0) throw new InvalidDataException("This loose model has no source textures.fsh. Add its matching FSH beside the source .o before importing if you want texture export.");
        string output=Path.Combine(folder,"model.o");
        var modelBytes=_native.Apply(Document);
        var originalModel=File.ReadAllBytes(Path.Combine(WorkDirectory,"model.o"));
        if(modelBytes.Length!=originalModel.Length)throw new InvalidDataException("Native model size changed.");
        for(int i=0;i<modelBytes.Length;i++)if(modelBytes[i]!=originalModel[i]&&!_native.IsEditableByte(i))throw new InvalidDataException("Native patch changed a protected model byte.");
        File.WriteAllBytes(output,modelBytes);
        string reopened=Path.Combine(folder,"verification.gltf");
        await _converter.ConvertAsync("export",output,reopened,ct);
        var check=ModelDocument.Load(reopened);
        if(check.Parts.Count!=Document.Parts.Count) throw new InvalidDataException("Re-export changed primitive count.");
        for(int p=0;p<check.Parts.Count;p++)
        {
            var a=Document.Parts[p];var b=check.Parts[p];
            if(a.Positions.Length!=b.Positions.Length)throw new InvalidDataException($"Re-export changed vertex count in part {p+1}.");
            if(a.AuxiliaryHash!=b.AuxiliaryHash)throw new InvalidDataException($"Re-export changed vertex colors or skin attributes in part {p+1}.");
            if(a.ImageUri!=b.ImageUri)throw new InvalidDataException($"Re-export changed texture binding in part {p+1}.");
            if(!a.Indices.SequenceEqual(b.Indices))throw new InvalidDataException($"Native export changed triangle indices in part {p+1}.");
            for(int i=0;i<a.Positions.Length;i++)
                if(Vector3.Distance(a.Positions[i],b.Positions[i])>0.001f || Vector2.Distance(a.Uvs[i],b.Uvs[i])>0.00001f)
                    throw new InvalidDataException("Re-export did not preserve the edited geometry/UVs.");
        }
        File.WriteAllText(Path.Combine(folder,"compatibility.json"),JsonSerializer.Serialize(new {
            source=SourcePath,sourceSha256=SourceHash,outputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))),
            primitiveCount=check.Parts.Count,vertices=check.Parts.Sum(p=>p.Positions.Length),nativeGeometryPatch=true,decoderReopen=true,gameValidated=false,
            note="Original ELF layout retained. Only mapped positions, normals and bounds may change. UVs, indices, weights and other bytes are preserved. Runtime validation remains required."
        },new JsonSerializerOptions { WriteIndented=true }));
    }

    /// <summary>Builds the face .big (stock layout: model.o + textures.fsh) from a verified export folder.</summary>
    public static byte[] BuildFacePackage(string exportFolder)
    {
        string texture=Path.Combine(exportFolder,"textures.fsh");
        if(!File.Exists(texture))throw new InvalidDataException("This head has no textures.fsh, so no .big can be built. Open a face .big or a head with its textures.");
        return FaceArchive.Build(File.ReadAllBytes(Path.Combine(exportFolder,"model.o")),File.ReadAllBytes(texture));
    }

    public static string WorkspaceRoot=>Path.Combine(AppContext.BaseDirectory,"workspace");
    /// <summary>Removes conversion folders left by crashed sessions; folders in use are younger than a day.</summary>
    public static void SweepWorkspaces()
    {
        if(!Directory.Exists(WorkspaceRoot))return;
        foreach(var folder in new DirectoryInfo(WorkspaceRoot).GetDirectories().Where(d=>d.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-1)))
            try{folder.Delete(true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public void DeleteWorkspace(){try{if(Directory.Exists(WorkDirectory))Directory.Delete(WorkDirectory,true);}catch(IOException){}catch(UnauthorizedAccessException){}}

    /// <summary>
    /// WPF maps texture coordinates relative to the mesh's own UV bounding box by default, which
    /// stretches the whole atlas over parts that use a small region (the eyeballs). Map 0..1 absolutely.
    /// </summary>
    private static ImageBrush UvBrush(ImageSource image) =>
        new(image) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new System.Windows.Rect(0,0,1,1), Stretch = Stretch.Fill };

    private static BitmapSource ReadBitmap(string path)
    { using var stream=File.OpenRead(path);return BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]; }
    private static void SavePng(BitmapSource image,string path)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var output=File.Create(path);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(output); }
}
