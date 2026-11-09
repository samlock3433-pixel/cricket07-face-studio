using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Cricket07.FaceStudio.Core;

/// <summary>
/// Patches only declared geometry fields in the original ELF .data section. Locations
/// come from OTools' parser dump and must match every exported source vertex/normal.
/// Ambiguous mappings and morph data are rejected. Layers are matched by declared names.
/// </summary>
public sealed class NativeModelPatch
{
    private readonly byte[] _source;
    private readonly List<(int Offset,int Stride,int Position,int? Normal,Vector3[] Original)> _parts=new();
    private readonly int[] _bounds;
    private readonly Dictionary<int,int[]> _boundParts=new();
    private NativeModelPatch(byte[] source,int[] bounds){_source=source;_bounds=bounds;}

    public static NativeModelPatch Bind(byte[] source,string dump,ModelDocument document)
    {
        var(dataOffset,dataSize)=DataSection(source);
        if(dump.Contains("BlendTarget",StringComparison.Ordinal)||dump.Contains("BlendShape",StringComparison.Ordinal))throw new NotSupportedException("Morph-based models require a dedicated exporter.");
        var models=Regex.Matches(dump,@"(?m)^Model [^\r\n]+ @([0-9A-Fa-f]+) \{");
        var layerBounds=Regex.Matches(dump,@"(?m)^Array\[(\d+)\] of ModelLayerBounding @([0-9A-Fa-f]+) \{");
        if(models.Count!=1||layerBounds.Count!=1)throw new NotSupportedException("Native export requires one model with a declared layer bounds array.");
        int layerCount=int.Parse(layerBounds[0].Groups[1].Value,CultureInfo.InvariantCulture);
        if(layerCount<1||layerCount>64||!Regex.IsMatch(dump,@"NumModelLayers: "+layerCount+@"\s"))throw new NotSupportedException("Invalid native model layer count.");
        int model=Hex(models[0].Groups[1].Value),layer=Hex(layerBounds[0].Groups[2].Value);
        if(model<0||(long)model+156>dataSize||layer<0||(long)layer+48*layerCount>dataSize)throw new InvalidDataException("Model bounds exceed ELF data.");
        var result=new NativeModelPatch((byte[])source.Clone(),new[]{dataOffset+model+108}.Concat(Enumerable.Range(0,layerCount).Select(i=>dataOffset+layer+48*i)).ToArray());
        var buffers=Regex.Matches(dump,@"(?m)^VertexBuffer [^\r\n]+ @([0-9A-Fa-f]+) \{\s+(\d+) vertices \(vertex stride (\d+) bytes\)")
            .Select(m=>(Offset:Hex(m.Groups[1].Value),Count:int.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture),Stride:int.Parse(m.Groups[3].Value,CultureInfo.InvariantCulture))).ToArray();
        var occupied=new List<(int Start,int End)>();
        for(int p=0;p<document.Parts.Count;p++)
        {
            var part=document.Parts[p];var layout=document.VertexLayout(p);
            var matches=buffers.Where(b=>b.Count==part.Positions.Length&&b.Stride==layout.Stride&&b.Offset>=0&&(long)b.Offset+b.Count*b.Stride<=dataSize)
                .Where(b=>Enumerable.Range(0,b.Count).All(i=>Read(source,dataOffset+b.Offset+i*b.Stride+layout.PositionOffset)==part.Positions[i]
                    &&(layout.NormalOffset is not int n||Read(source,dataOffset+b.Offset+i*b.Stride+n)==part.Normals[i]))).ToArray();
            if(matches.Length!=1)throw new NotSupportedException($"Part {p+1} does not uniquely match a declared native vertex buffer.");
            var match=matches[0];int start=dataOffset+match.Offset,end=start+match.Count*match.Stride;
            if(occupied.Any(b=>start<b.End&&end>b.Start))throw new NotSupportedException("Overlapping/shared native vertex buffers are unsupported.");
            if(result._bounds.Any(b=>start<b+48&&end>b))throw new InvalidDataException("Vertex and bounding data overlap.");
            occupied.Add((start,end));
            result._parts.Add((start,layout.Stride,layout.PositionOffset,layout.NormalOffset,part.Positions.ToArray()));
        }
        var all=Enumerable.Range(0,document.Parts.Count).ToArray();
        var names=Regex.Matches(dump,"ModelLayerName\\.[0-9A-Fa-f]+\\[(\\d+)\\]: \"([^\"]+)\"");
        for(int i=0;i<result._bounds.Length;i++)
        {
            int b=result._bounds[i];
            if(i==0||layerCount==1)result._boundParts[b]=all;
            else
            {
                var name=names.Cast<Match>().SingleOrDefault(m=>int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)==i-1)?.Groups[2].Value;
                var groups=document.MeshNames.Select((n,index)=>(n,index)).Where(g=>g.n==name).ToArray();
                if(groups.Length!=1)throw new NotSupportedException("Native layer name does not uniquely match editable geometry.");
                result._boundParts[b]=document.MeshGroups[groups[0].index];
            }
            var min=Read(source,b);var max=Read(source,b+16);
            if(!float.IsFinite(min.X+min.Y+min.Z+max.X+max.Y+max.Z)||min.X>max.X||min.Y>max.Y||min.Z>max.Z)
                throw new InvalidDataException("Invalid native bounds.");
        }
        if(layerCount>1 && (document.MeshGroups.Count!=layerCount||result._bounds.Skip(1).SelectMany(b=>result._boundParts[b]).Distinct().Count()!=all.Length))throw new NotSupportedException("Native layer mapping is incomplete.");
        return result;
    }

    public byte[] Apply(ModelDocument edited)
    {
        if(edited.Parts.Count!=_parts.Count)throw new InvalidDataException("Mesh part count changed.");
        var output=(byte[])_source.Clone();bool changed=false;
        var normals=edited.EditedNormals();
        for(int p=0;p<_parts.Count;p++)
        {
            var binding=_parts[p];var part=edited.Parts[p];
            if(part.Positions.Length!=binding.Original.Length)throw new InvalidDataException("Vertex count changed.");
            // Bind proved part.Normals equal the native bytes, so untouched normals are left byte-identical.
            for(int i=0;i<part.Positions.Length;i++)
            {
                if(part.Positions[i]!=binding.Original[i]){changed=true;Write(output,binding.Offset+i*binding.Stride+binding.Position,part.Positions[i]);}
                if(binding.Normal is int n&&normals[p][i]!=part.Normals[i])Write(output,binding.Offset+i*binding.Stride+n,normals[p][i]);
            }
        }
        if(changed)
        {
            foreach(int b in _bounds)
            {
            var positions=_boundParts[b].SelectMany(p=>edited.Parts[p].Positions).ToArray();
            var min=positions.Aggregate(new Vector3(float.PositiveInfinity),Vector3.Min);var max=positions.Aggregate(new Vector3(float.NegativeInfinity),Vector3.Max);
            min=Vector3.Min(min,Read(_source,b));max=Vector3.Max(max,Read(_source,b+16));
            var center=(min+max)/2;float radius=positions.Max(p=>Vector3.Distance(p,center));
            Write(output,b,min);Write(output,b+16,max);Write(output,b+32,center);BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(b+44,4),Math.Max(radius,BinaryPrimitives.ReadSingleLittleEndian(_source.AsSpan(b+44,4))+Vector3.Distance(center,Read(_source,b+32))));
            }
        }
        return output;
    }

    public bool IsEditableByte(int offset)
    {
        if(_bounds.Any(b=>(offset>=b&&offset<b+12)||(offset>=b+16&&offset<b+28)||(offset>=b+32&&offset<b+48)))return true;
        foreach(var part in _parts)
        {
            int relative=offset-part.Offset;
            if(relative<0||relative>=part.Stride*part.Original.Length)continue;
            int field=relative%part.Stride;
            if(field>=part.Position&&field<part.Position+12||(part.Normal is int n&&field>=n&&field<n+12))return true;
        }
        return false;
    }
    private static int Hex(string text)=>int.Parse(text,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
    private static Vector3 Read(byte[] bytes,int offset)=>new(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset,4)),BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset+4,4)),BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset+8,4)));
    private static void Write(byte[] bytes,int offset,Vector3 value)
    {
        if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z))throw new InvalidDataException("Non-finite edited vertex.");
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset,4),value.X);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset+4,4),value.Y);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset+8,4),value.Z);
    }
    private static (int Offset,int Size) DataSection(byte[] bytes)
    {
        if(bytes.Length<52||!bytes.AsSpan(0,4).SequenceEqual(new byte[]{0x7F,69,76,70})||bytes[4]!=1||bytes[5]!=1)throw new InvalidDataException("Expected ELF32 little-endian .o.");
        int table=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32,4)));
        int stride=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(46,2)),count=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(48,2)),names=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(50,2));
        if(stride<40||count==0||names>=count||table<52||(long)table+(long)stride*count>bytes.Length)throw new InvalidDataException("Invalid ELF section table.");
        int namesOffset=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(table+names*stride+16,4)));
        int namesSize=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(table+names*stride+20,4)));
        if(namesOffset<0||namesSize<0||(long)namesOffset+namesSize>bytes.Length)throw new InvalidDataException("Invalid ELF names section.");
        var sections=new List<(int Offset,int Size)>();
        for(int i=0;i<count;i++)
        {
            int s=table+i*stride;uint name=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s,4));
            if(name>=namesSize)throw new InvalidDataException("Invalid ELF section name.");
            int start=namesOffset+(int)name,end=Array.IndexOf(bytes,(byte)0,start,namesSize-(int)name);
            if(end<0)throw new InvalidDataException("Unterminated ELF section name.");
            if(Encoding.ASCII.GetString(bytes,start,end-start)!=".data")continue;
            int offset=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s+16,4))),size=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s+20,4)));
            if(offset<52||size<0||(long)offset+size>bytes.Length)throw new InvalidDataException("Invalid ELF data bounds.");
            sections.Add((offset,size));
        }
        return sections.Count==1?sections[0]:throw new InvalidDataException("Expected one ELF .data section.");
    }
}


