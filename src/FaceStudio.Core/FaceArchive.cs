using System.Buffers.Binary;
using System.Text;

namespace Cricket07.FaceStudio.Core;

/// <summary>Same-size face package editing; directory, padding and unknown members stay untouched.</summary>
public sealed class FaceArchive
{
    private readonly byte[] _source;
    private readonly (int Offset,int Size) _model, _texture;
    public byte[] Model => _source.AsSpan(_model.Offset,_model.Size).ToArray();
    public byte[] Texture => _source.AsSpan(_texture.Offset,_texture.Size).ToArray();
    public FaceArchive(byte[] source)
    {
        if(source.Length<16 || !(source.AsSpan(0,4).SequenceEqual("BIGF"u8)||source.AsSpan(0,4).SequenceEqual("BIG4"u8)))
            throw new InvalidDataException("Not a BIGF/BIG4 face package.");
        int count=Read(source,8),end=Read(source,12),cursor=16;
        if(count<1||count>100000||end<16||end>source.Length)throw new InvalidDataException("Invalid BIG directory.");
        var members=new Dictionary<string,(int Offset,int Size)>(StringComparer.OrdinalIgnoreCase);
        var ranges=new List<(int Offset,int Size)>();
        for(int i=0;i<count;i++)
        {
            if(cursor>end-9)throw new InvalidDataException("Truncated BIG directory.");
            int offset=Read(source,cursor),size=Read(source,cursor+4),start=cursor+8;
            int terminator=Array.IndexOf(source,(byte)0,start,end-start);
            if(terminator<0||offset<end||size<0||(long)offset+size>source.Length)throw new InvalidDataException("Invalid BIG member bounds.");
            string name=Encoding.ASCII.GetString(source,start,terminator-start);
            if(!members.TryAdd(name,(offset,size)))throw new InvalidDataException("Duplicate BIG member.");
            ranges.Add((offset,size));cursor=terminator+1;
        }
        var sorted=ranges.OrderBy(r=>r.Offset).ToArray();
        for(int i=1;i<sorted.Length;i++)if((long)sorted[i-1].Offset+sorted[i-1].Size>sorted[i].Offset)throw new InvalidDataException("Overlapping BIG members.");
        if(!members.TryGetValue("model.o",out _model))throw new InvalidDataException("Face package needs a root model.o member.");
        bool fsh=members.TryGetValue("textures.fsh",out var texture),legacy=members.TryGetValue("textures",out var alternate);
        if(fsh==legacy)throw new InvalidDataException("Face package needs exactly one textures.fsh or textures member.");
        _texture=fsh?texture:alternate;
        _source=(byte[])source.Clone();
        if(!Model.AsSpan().StartsWith(new byte[]{0x7f,69,76,70})||!Texture.AsSpan().StartsWith("SHPI"u8))
            throw new InvalidDataException("Compressed or unsupported face members. This importer requires ELF model.o and SHPI textures.");
    }
    /// <summary>
    /// Writes a face package in the stock players\heads\&lt;FaceID&gt;.big layout: BIGF, little-endian total size,
    /// big-endian directory (model.o, textures.fsh), "L234" tag, members aligned to 256 bytes, no tail padding.
    /// Byte-identical to stock packages when given their members.
    /// </summary>
    public static byte[] Build(byte[] model,byte[] texture)
    {
        if(!model.AsSpan().StartsWith(new byte[]{0x7f,69,76,70}))throw new InvalidDataException("Face model must be an uncompressed ELF .o.");
        if(!texture.AsSpan().StartsWith("SHPI"u8))throw new InvalidDataException("Face textures must be an uncompressed SHPI .fsh.");
        static int Align(int value)=>(value+255)&~255;
        var names=new[]{"model.o\0"u8.ToArray(),"textures.fsh\0"u8.ToArray()};
        int directoryEnd=16+names.Sum(n=>8+n.Length),headerSize=directoryEnd+8;
        int modelOffset=Align(headerSize),textureOffset=Align(checked(modelOffset+model.Length)),total=checked(textureOffset+texture.Length);
        var output=new byte[total];
        "BIGF"u8.CopyTo(output);BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4),total);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(8),2);BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(12),headerSize);
        int cursor=16;
        foreach(var(offset,size,name)in new[]{(modelOffset,model.Length,names[0]),(textureOffset,texture.Length,names[1])})
        {
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(cursor),offset);BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(cursor+4),size);
            name.CopyTo(output,cursor+8);cursor+=8+name.Length;
        }
        "L234"u8.CopyTo(output.AsSpan(cursor));
        model.CopyTo(output,modelOffset);texture.CopyTo(output,textureOffset);
        var check=new FaceArchive(output);
        if(!check.Model.AsSpan().SequenceEqual(model)||!check.Texture.AsSpan().SequenceEqual(texture))throw new InvalidDataException("Face package verification failed.");
        return output;
    }
    private static int Read(byte[] data,int offset)=>BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset,4));
}
