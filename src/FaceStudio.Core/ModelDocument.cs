using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace Cricket07.FaceStudio.Core;

/// <summary>Edits OTools' glTF intermediates without flattening materials, weights, UVs or primitives.</summary>
public sealed class ModelDocument
{
    private readonly JsonObject _root;
    private readonly List<byte[]> _buffers = new();
    private readonly List<(int Position, int? Normal)> _accessors = new();
    private readonly List<Vector3[]> _originalPositions = new();
    public List<ModelPart> Parts { get; } = new();
    public List<int[]> MeshGroups { get; } = new();
    public List<string> MeshNames { get; } = new();
    public IReadOnlyList<string> ImageUris => _root["images"]?.AsArray().Select(i => i?["uri"]?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>();

    private ModelDocument(JsonObject root) => _root = root;

    public (int Stride,int PositionOffset,int? NormalOffset) VertexLayout(int part)
    {
        var position=_root["accessors"]![_accessors[part].Position]!;
        var view=_root["bufferViews"]![position["bufferView"]!.GetValue<int>()]!;
        int stride=view["byteStride"]?.GetValue<int>()??12;
        int posOffset=position["byteOffset"]?.GetValue<int>()??0;
        int? normalOffset=null;
        if(_accessors[part].Normal is int n)
        {
            var normal=_root["accessors"]![n]!;
            if(normal["bufferView"]!.GetValue<int>()!=position["bufferView"]!.GetValue<int>())throw new NotSupportedException("Split normal streams are not supported by native export.");
            normalOffset=normal["byteOffset"]?.GetValue<int>()??0;
        }
        if(posOffset<0||posOffset+12>stride||(normalOffset is int no&&(no<0||no+12>stride)))throw new NotSupportedException("Unsupported native vertex attribute layout.");
        return(stride,posOffset,normalOffset);
    }

    public static ModelDocument Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Invalid glTF.");
        var doc = new ModelDocument(root);
        foreach (var buffer in root["buffers"]!.AsArray())
        {
            string uri = buffer!["uri"]!.GetValue<string>();
            byte[] bytes;
            if (uri.StartsWith("data:", StringComparison.Ordinal)) bytes = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
            else bytes = File.ReadAllBytes(ContainedPath(Path.GetDirectoryName(Path.GetFullPath(path))!, uri));
            if (bytes.Length > 128 * 1024 * 1024) throw new InvalidDataException("Model buffer exceeds supported size.");
            doc._buffers.Add(bytes);
        }
        foreach (var node in root["nodes"]!.AsArray())
            if (node?["mesh"] is not null && (node["matrix"] is not null || node["translation"] is not null || node["rotation"] is not null || node["scale"] is not null))
                throw new NotSupportedException("Transformed mesh nodes need normalization before editing. Stock face templates use local identity transforms.");
        foreach (var mesh in root["meshes"]!.AsArray())
        {
        int firstPart=doc.Parts.Count;
        foreach (var primitive in mesh!["primitives"]!.AsArray())
        {
            if ((primitive!["mode"]?.GetValue<int>() ?? 4) != 4) throw new NotSupportedException("Only triangle-list OTools intermediates are supported.");
            var attrs = primitive["attributes"]!;
            int pos = attrs["POSITION"]!.GetValue<int>();
            int? normal = attrs["NORMAL"]?.GetValue<int>();
            var positions = doc.ReadVectors(pos, 3).Select(v => new Vector3(v[0], v[1], v[2])).ToArray();
            var normals = normal is int n ? doc.ReadVectors(n, 3).Select(v => new Vector3(v[0], v[1], v[2])).ToArray() : new Vector3[positions.Length];
            var uv = attrs["TEXCOORD_0"] is JsonNode uvNode ? doc.ReadVectors(uvNode.GetValue<int>(), 2).Select(v => new Vector2(v[0], v[1])).ToArray() : new Vector2[positions.Length];
            int[] indices = primitive["indices"] is JsonNode idx ? doc.ReadIndices(idx.GetValue<int>()) : Enumerable.Range(0, positions.Length).ToArray();
            if (indices.Length % 3 != 0 || indices.Any(i => i < 0 || i >= positions.Length) || uv.Length != positions.Length || normals.Length != positions.Length)
                throw new InvalidDataException("Invalid mesh topology or attributes.");
            int material = primitive["material"]?.GetValue<int>() ?? -1;
            string name = material >= 0 ? root["materials"]![material]?["name"]?.GetValue<string>() ?? $"Material {material}" : "Untextured";
            string? imageUri = null;
            var texture = material >= 0 ? root["materials"]![material]?["pbrMetallicRoughness"]?["baseColorTexture"]?["index"] : null;
            if (texture is not null)
            {
                int imageIndex = root["textures"]![texture.GetValue<int>()]!["source"]!.GetValue<int>();
                imageUri = root["images"]![imageIndex]?["uri"]?.GetValue<string>();
            }
            using var auxiliary = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var attribute in attrs.AsObject().OrderBy(a => a.Key))
            {
                if (attribute.Key is "POSITION" or "NORMAL" or "TEXCOORD_0") continue;
                int index = attribute.Value!.GetValue<int>();
                string type = root["accessors"]![index]!["type"]!.GetValue<string>();
                int dim = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new NotSupportedException("Unsupported vertex attribute type.") };
                var a = doc.Access(index, dim);
                int size = a.Component is 5126 or 5125 ? 4 : a.Component == 5123 ? 2 : 1;
                for (int v = 0; v < a.Count; v++) auxiliary.AppendData(a.Buffer, a.Start+v*a.Stride, size*dim);
            }
            doc.Parts.Add(new ModelPart($"{mesh["name"]?.GetValue<string>() ?? "Mesh"} / {name} / part {doc.Parts.Count+1}", positions, normals, uv, indices, imageUri,
                Convert.ToHexString(auxiliary.GetHashAndReset())));
            doc._accessors.Add((pos, normal));
            doc._originalPositions.Add(positions.ToArray());
        }
        doc.MeshGroups.Add(Enumerable.Range(firstPart,doc.Parts.Count-firstPart).ToArray());
        doc.MeshNames.Add(mesh!["name"]?.GetValue<string>()??"");
        }
        if (doc.Parts.Count == 0) throw new InvalidDataException("No editable mesh primitives found.");
        if (doc._accessors.Select(a => a.Position).Distinct().Count() != doc._accessors.Count)
            throw new NotSupportedException("Shared position accessors are not supported for independent part editing.");
        return doc;
    }

    public static string ContainedPath(string folder, string relative)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative)) throw new InvalidDataException("External model resource must remain inside its folder.");
        return path;
    }

    private (byte[] Buffer, int Start, int Stride, int Count, int Component) Access(int index, int dimensions)
    {
        var accessor = _root["accessors"]![index]!;
        if (accessor["sparse"] is not null) throw new NotSupportedException("Sparse accessors are unsupported.");
        int component = accessor["componentType"]!.GetValue<int>();
        int size = component switch { 5126 or 5125 => 4, 5123 => 2, 5121 => 1, _ => throw new NotSupportedException("Unsupported accessor component.") };
        string expectedType = dimensions switch { 1 => "SCALAR", 2 => "VEC2", 3 => "VEC3", 4 => "VEC4", _ => "" };
        if (accessor["type"]?.GetValue<string>() != expectedType) throw new InvalidDataException("Unexpected accessor dimensions.");
        var view = _root["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        int viewStart = view["byteOffset"]?.GetValue<int>() ?? 0;
        int local = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        int stride = view["byteStride"]?.GetValue<int>() ?? size*dimensions;
        int count = accessor["count"]!.GetValue<int>();
        int viewLength = view["byteLength"]!.GetValue<int>();
        var bytes = _buffers[view["buffer"]!.GetValue<int>()];
        long needed = count == 0 ? 0 : (long)(count-1)*stride + size*dimensions;
        if (count < 0 || count > 1000000 || local < 0 || stride < size*dimensions || viewStart < 0 || viewLength < 0
            || (long)viewStart + viewLength > bytes.Length || local + needed > viewLength) throw new InvalidDataException("Accessor exceeds buffer bounds.");
        return (bytes, viewStart+local, stride, count, component);
    }

    private float[][] ReadVectors(int index, int dim)
    {
        var a = Access(index, dim);
        if (a.Component != 5126) throw new NotSupportedException("Position, normal and UV accessors must use floats.");
        var result = new float[a.Count][];
        for (int i = 0; i < a.Count; i++)
        {
            result[i] = new float[dim];
            for (int c = 0; c < dim; c++)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(a.Buffer.AsSpan(a.Start+i*a.Stride+c*4, 4));
                if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite vertex attribute.");
                result[i][c] = value;
            }
        }
        return result;
    }

    private int[] ReadIndices(int index)
    {
        var a = Access(index, 1); var result = new int[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            var span = a.Buffer.AsSpan(a.Start + i*a.Stride);
            result[i] = a.Component switch { 5121 => span[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                5125 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span)), _ => throw new InvalidDataException("Invalid index encoding.") };
        }
        return result;
    }

    public void Save(string path)
    {
        var editedNormals = EditedNormals();
        for (int p = 0; p < Parts.Count; p++)
        {
            var part = Parts[p]; var a = Access(_accessors[p].Position, 3);
            if (part.Positions.Length != a.Count) throw new InvalidDataException("Vertex counts must not change.");
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            for (int i = 0; i < a.Count; i++)
            {
                var v = part.Positions[i];
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new InvalidDataException("Non-finite edited position.");
                WriteVector(a.Buffer, a.Start+i*a.Stride, v); min = Vector3.Min(min, v); max = Vector3.Max(max, v);
            }
            var accessor = _root["accessors"]![_accessors[p].Position]!;
            accessor["min"] = new JsonArray(min.X, min.Y, min.Z); accessor["max"] = new JsonArray(max.X, max.Y, max.Z);
            if (_accessors[p].Normal is int normal)
            {
                var n = Access(normal, 3);
                for (int i = 0; i < n.Count; i++) WriteVector(n.Buffer, n.Start+i*n.Stride, editedNormals[p][i]);
            }
        }
        for (int i = 0; i < _buffers.Count; i++) _root["buffers"]![i]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(_buffers[i]);
        File.WriteAllText(path, _root.ToJsonString());
    }

    private static void WriteVector(byte[] bytes, int offset, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset+4, 4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset+8, 4), value.Z);
    }

    /// <summary>
    /// Keeps the authored normals. A normal changes only where the surface around it changed, and then
    /// it is rotated by the same amount as the surface. Vertices duplicated at UV seams or part
    /// boundaries within one mesh are welded by original position, so both sides of a seam stay equal.
    /// </summary>
    public Vector3[][] EditedNormals()
    {
        var result = Parts.Select(p => p.Normals.ToArray()).ToArray();
        foreach (var group in MeshGroups)
        {
            var weld = new Dictionary<Vector3, int>();
            var keys = group.Select(p => _originalPositions[p].Select(v =>
            {
                if (!weld.TryGetValue(v, out int key)) weld[v] = key = weld.Count;
                return key;
            }).ToArray()).ToArray();
            var before = new Vector3[weld.Count]; var after = new Vector3[weld.Count];
            for (int g = 0; g < group.Length; g++)
            {
                var part = Parts[group[g]];
                Accumulate(before, _originalPositions[group[g]], part.Indices, keys[g]);
                Accumulate(after, part.Positions, part.Indices, keys[g]);
            }
            for (int g = 0; g < group.Length; g++)
                for (int i = 0; i < keys[g].Length; i++)
                {
                    int key = keys[g][i];
                    // Same triangles, same order: an untouched neighbourhood sums to identical bits.
                    if (before[key] != after[key]) result[group[g]][i] = Rotate(result[group[g]][i], before[key], after[key]);
                }
        }
        return result;
    }

    private static void Accumulate(Vector3[] sums, Vector3[] positions, int[] indices, int[] keys)
    {
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i+1], c = indices[i+2];
            var normal = Vector3.Cross(positions[b]-positions[a], positions[c]-positions[a]);
            sums[keys[a]] += normal; sums[keys[b]] += normal; sums[keys[c]] += normal;
        }
    }

    private static Vector3 Rotate(Vector3 normal, Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-20f || to.LengthSquared() < 1e-20f) return normal;
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float dot = Vector3.Dot(from, to);
        if (dot < -0.9999f) return to; // surface flipped inside out: no meaningful rotation, use the new geometry
        return Vector3.Transform(normal, Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + dot)));
    }

    // OTools removes triangle-strip connector degenerates during import. Compare the
    // remaining oriented triangles, allowing cyclic vertex rotation and triangle ordering.
    public static string[] SurfaceTriangles(ModelPart part)
    {
        var result=new List<string>();
        for(int i=0;i<part.Indices.Length;i+=3)
        {
            int a=part.Indices[i],b=part.Indices[i+1],c=part.Indices[i+2];
            if(a==b||b==c||c==a)continue;
            if(Vector3.Cross(part.Positions[b]-part.Positions[a],part.Positions[c]-part.Positions[a]).LengthSquared()<1e-20f)continue;
            if(b<a&&b<c)(a,b,c)=(b,c,a);else if(c<a&&c<b)(a,b,c)=(c,a,b);
            result.Add($"{a},{b},{c}");
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
}

public sealed record ModelPart(string Name, Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Indices, string? ImageUri, string AuxiliaryHash);
