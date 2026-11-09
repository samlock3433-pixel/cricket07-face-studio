using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class NativeModelTests : IDisposable
{
    private readonly string _folder=Path.Combine(Path.GetTempPath(),"native-head-test-"+Guid.NewGuid().ToString("N"));
    private const string Dump="VertexBuffer VB @0 {\n 3 vertices (vertex stride 24 bytes) [...]\n}\nArray[1] of ModelLayerBounding @80 {\n}\nModel head @B0 {\n NumModelLayers: 1\n}";
    public NativeModelTests()=>Directory.CreateDirectory(_folder);
    public void Dispose()=>Directory.Delete(_folder,true);
    private (byte[] Source,ModelDocument Document) Fixture()
    {
        byte[] source=new byte[800];new byte[]{127,69,76,70,1,1}.CopyTo(source,0);
        void U16(int offset,ushort n)=>BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(offset,2),n);
        void U32(int offset,uint n)=>BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(offset,4),n);
        void Vector(int offset,Vector3 v){BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(offset,4),v.X);BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(offset+4,4),v.Y);BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(offset+8,4),v.Z);}
        U32(32,640);U16(46,40);U16(48,3);U16(50,2);
        U32(680,1);U32(696,64);U32(700,512);U32(720,7);U32(736,576);U32(740,17);
        Encoding.ASCII.GetBytes("\0.data\0.shstrtab\0").CopyTo(source,576);
        var positions=new[]{Vector3.Zero,Vector3.UnitX,Vector3.UnitY};
        for(int i=0;i<3;i++){Vector(64+i*24,positions[i]);Vector(64+i*24+12,Vector3.UnitZ);}
        foreach(int b in new[]{64+128,64+176+108}){Vector(b,Vector3.Zero);Vector(b+16,new Vector3(1,1,0));Vector(b+32,new Vector3(.5f,.5f,0));BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(b+44,4),1);}
        var gltf=new{
            asset=new{version="2.0"},buffers=new[]{new{uri="data:application/octet-stream;base64,"+Convert.ToBase64String(source[64..136]),byteLength=72}},
            bufferViews=new[]{new{buffer=0,byteOffset=0,byteLength=72,byteStride=24}},
            accessors=new[]{new{bufferView=0,byteOffset=0,componentType=5126,count=3,type="VEC3"},new{bufferView=0,byteOffset=12,componentType=5126,count=3,type="VEC3"}},
            nodes=new[]{new{mesh=0}},meshes=new[]{new{name="head",primitives=new[]{new{attributes=new Dictionary<string,int>{{"POSITION",0},{"NORMAL",1}},mode=4}}}}
        };
        string path=Path.Combine(_folder,"model.gltf");File.WriteAllText(path,JsonSerializer.Serialize(gltf));
        return(source,ModelDocument.Load(path));
    }
    [Fact]
    public void NoEditNativeExportIsByteIdentical()
    {var f=Fixture();var patch=NativeModelPatch.Bind(f.Source,Dump,f.Document);Assert.Equal(f.Source,patch.Apply(f.Document));}
    [Fact]
    public void GeometryPatchPreservesEveryUnmappedByte()
    {
        var f=Fixture();var patch=NativeModelPatch.Bind(f.Source,Dump,f.Document);
        f.Document.Parts[0].Positions[2]=new Vector3(0,1.2f,0);
        var output=patch.Apply(f.Document);Assert.NotEqual(f.Source,output);Assert.Equal(f.Source.Length,output.Length);
        for(int i=0;i<output.Length;i++)if(!patch.IsEditableByte(i))Assert.Equal(f.Source[i],output[i]);
        Assert.Equal(1.2f,BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(64+48+4,4)));
        Assert.Equal(1.2f,BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(64+128+20,4)));
    }
    [Fact]
    public void IncorrectDeclaredBufferAndMorphModelsAreRejected()
    {
        var f=Fixture();
        Assert.Throws<NotSupportedException>(()=>NativeModelPatch.Bind(f.Source,Dump.Replace("VB @0","VB @20"),f.Document));
        Assert.Throws<NotSupportedException>(()=>NativeModelPatch.Bind(f.Source,Dump+"\nBlendTarget",f.Document));
    }
    [Fact]
    public void CorruptElfSectionTableIsRejected()
    {
        var f=Fixture();BinaryPrimitives.WriteUInt32LittleEndian(f.Source.AsSpan(32,4),790);
        Assert.Throws<InvalidDataException>(()=>NativeModelPatch.Bind(f.Source,Dump,f.Document));
    }
    [Fact]
    public void EditedNormalsKeepAuthoredNormalsAndWeldSeams()
    {
        // Triangle A (0,1,2) and B (3,4,5) share an edge through duplicated seam vertices 1=3, 2=5.
        // Triangle C (6,7,8) is a separate island. Authored normals are deliberately not geometric.
        var positions=new[]{Vector3.Zero,Vector3.UnitX,Vector3.UnitY, Vector3.UnitX,new Vector3(1,1,0),Vector3.UnitY,
            new Vector3(5,5,0),new Vector3(6,5,0),new Vector3(5,6,0)};
        var authored=Vector3.Normalize(new Vector3(.2f,.1f,1));
        var bytes=new byte[positions.Length*24];
        for(int i=0;i<positions.Length;i++)foreach(var(v,o)in new[]{(positions[i],0),(authored,12)})
        {BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*24+o,4),v.X);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*24+o+4,4),v.Y);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*24+o+8,4),v.Z);}
        var gltf=new{
            asset=new{version="2.0"},buffers=new[]{new{uri="data:application/octet-stream;base64,"+Convert.ToBase64String(bytes),byteLength=bytes.Length}},
            bufferViews=new[]{new{buffer=0,byteOffset=0,byteLength=bytes.Length,byteStride=24}},
            accessors=new[]{new{bufferView=0,byteOffset=0,componentType=5126,count=positions.Length,type="VEC3"},new{bufferView=0,byteOffset=12,componentType=5126,count=positions.Length,type="VEC3"}},
            nodes=new[]{new{mesh=0}},meshes=new[]{new{name="head",primitives=new[]{new{attributes=new Dictionary<string,int>{{"POSITION",0},{"NORMAL",1}},mode=4}}}}
        };
        string path=Path.Combine(_folder,"seam.gltf");File.WriteAllText(path,JsonSerializer.Serialize(gltf));
        var document=ModelDocument.Load(path);
        Assert.All(document.EditedNormals()[0],n=>Assert.Equal(authored,n));
        document.Parts[0].Positions[4]=new Vector3(1,1,.5f);
        var normals=document.EditedNormals()[0];
        Assert.Equal(authored,normals[0]);
        Assert.Equal(authored,normals[6]);Assert.Equal(authored,normals[7]);Assert.Equal(authored,normals[8]);
        Assert.Equal(normals[1],normals[3]);Assert.Equal(normals[2],normals[5]);
        Assert.NotEqual(authored,normals[4]);
        Assert.All(normals,n=>Assert.InRange(n.Length(),.999f,1.001f));
    }
    [Fact]
    public void FacePackageUsesStockLayoutAndReopens()
    {
        var model=new byte[1000];new byte[]{0x7f,69,76,70}.CopyTo(model,0);model[999]=7;
        var texture=new byte[300];"SHPI"u8.CopyTo(texture);texture[299]=9;
        var big=FaceArchive.Build(model,texture);
        Assert.Equal("BIGF"u8.ToArray(),big[..4]);
        Assert.Equal(big.Length,BinaryPrimitives.ReadInt32LittleEndian(big.AsSpan(4)));
        Assert.Equal(0x3D,BinaryPrimitives.ReadInt32BigEndian(big.AsSpan(12)));
        Assert.Equal(0x100,BinaryPrimitives.ReadInt32BigEndian(big.AsSpan(16)));   // model.o
        Assert.Equal(0x500,BinaryPrimitives.ReadInt32BigEndian(big.AsSpan(32)));   // textures.fsh, 256-aligned
        var reopened=new FaceArchive(big);
        Assert.Equal(model,reopened.Model);Assert.Equal(texture,reopened.Texture);
        Assert.Throws<InvalidDataException>(()=>FaceArchive.Build(texture,texture));
    }
    [SkippableFact]
    public void FacePackageIsByteIdenticalToStockHeads()
    {
        string heads=Environment.GetEnvironmentVariable("C07_STOCK_HEADS")??@"D:\C07 MODDING\Crickt_07_Raw_Files\players\heads";
        Skip.IfNot(Directory.Exists(heads),"Stock players\\heads folder not available.");
        int checkedCount=0;
        foreach(var path in Directory.EnumerateFiles(heads,"*.big"))
        {
            if(Path.GetFileName(path) is "lhead.big" or "mhead.big")continue; // shared LOD heads: model only, no textures
            var stock=File.ReadAllBytes(path);var archive=new FaceArchive(stock);
            Assert.Equal(stock,FaceArchive.Build(archive.Model,archive.Texture));checkedCount++;
        }
        Assert.True(checkedCount>0);
    }
    [Fact]
    public void IntermediateSaveKeepsUvAndIndicesWhileUpdatingPositions()
    {
        var f=Fixture();f.Document.Parts[0].Positions[0]=new Vector3(.2f,0,0);
        string path=Path.Combine(_folder,"edited.gltf");f.Document.Save(path);
        var reopened=ModelDocument.Load(path);
        Assert.Equal(f.Document.Parts[0].Positions,reopened.Parts[0].Positions);
        Assert.Equal(f.Document.Parts[0].Indices,reopened.Parts[0].Indices);
        Assert.Equal(f.Document.Parts[0].Uvs,reopened.Parts[0].Uvs);
    }
}
