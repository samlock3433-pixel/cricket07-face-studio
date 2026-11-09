using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class SculptBrushTests
{
    [Fact]
    public void ProportionsPreserveMirrorPlaneNeckAndOutsideVertices()
    {
        var input=new[]{new Vector3(-0.5f,1,0),new Vector3(0.5f,1,0),new Vector3(0,1,0),new Vector3(0,-2,0),new Vector3(5,1,0)};
        var output=SculptBrush.Proportion(input,new Vector3(0,1,0),new Vector3(1.5f,1,1),2,0,0);
        Assert.True(output[1].X>input[1].X);Assert.Equal(-output[1].X,output[0].X);
        Assert.Equal(input[2],output[2]);Assert.Equal(input[3],output[3]);Assert.Equal(input[4],output[4]);
        Assert.Throws<ArgumentException>(()=>SculptBrush.Proportion(input,Vector3.Zero,new Vector3(float.NaN),1));
    }
    [Fact]
    public void LocalMovementLeavesOutsideAndLockedVerticesUntouched()
    {
        var input = new[] { Vector3.Zero, new Vector3(10, 0, 0), new Vector3(0, -1, 0) };
        var result = SculptBrush.Move(input, Vector3.Zero, Vector3.UnitZ, 2, lockBelowY: -0.5f);
        Assert.Equal(Vector3.UnitZ, result[0]); Assert.Equal(input[1], result[1]); Assert.Equal(input[2], result[2]);
        Assert.Equal(Vector3.Zero, input[0]); Assert.Equal(input.Length, result.Length);
    }

    [Fact]
    public void MirroredBrushMovesOppositeSideInOppositeXDirection()
    {
        var input = new[] { new Vector3(-1, 0, 0), new Vector3(1, 0, 0), Vector3.Zero };
        var result = SculptBrush.Move(input, input[1], Vector3.UnitX, 2, symmetryPlane: 0);
        Assert.Equal(-2, result[0].X); Assert.Equal(2, result[1].X); Assert.Equal(0, result[2].X);
    }

    [Fact]
    public void SmoothingUsesSnapshotAndRejectsInvalidTopology()
    {
        var input = new[] { new Vector3(0, 1, 0), new Vector3(-1, 0, 0), new Vector3(1, 0, 0) };
        int[] triangles = { 0, 1, 2 };
        var output = SculptBrush.Smooth(input, triangles, input[0], 1, 0.5f);
        Assert.Equal(0.5f, output[0].Y); Assert.Equal(input[1], output[1]);
        Assert.Throws<InvalidDataException>(() => SculptBrush.Smooth(input, new[] { 0, 1, 9 }, input[0], 1, 0.5f));
    }

    [Fact]
    public void RejectsNonFiniteBrushInput()
    {
        Assert.Throws<ArgumentException>(() => SculptBrush.Move(new[] { Vector3.Zero }, Vector3.Zero, Vector3.One, float.NaN));
    }

    [Fact]
    public void SmoothKeepsUvSeamDuplicatesTogether()
    {
        // Two triangles share edge (1,0,0)-(0,1,0) through duplicated seam vertices 1=3 and 2=5.
        var v=new[]{Vector3.Zero,Vector3.UnitX,Vector3.UnitY,Vector3.UnitX,new Vector3(1,1,.8f),Vector3.UnitY};
        var smoothed=SculptBrush.Smooth(v,new[]{0,1,2,3,4,5},new Vector3(.5f,.5f,0),10,1);
        Assert.Equal(smoothed[1],smoothed[3]);
        Assert.Equal(smoothed[2],smoothed[5]);
        Assert.NotEqual(v[1],smoothed[1]); // the seam vertex sees neighbours from both sides
    }
}
