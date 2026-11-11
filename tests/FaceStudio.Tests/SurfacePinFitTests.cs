using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class SurfacePinFitTests
{
    [Fact]
    public void DraggedPinMovesExactlyWhileRestingPinNeckAndDistantPointsStayPut()
    {
        var moved=new Vector3(2,5,1);var held=new Vector3(3,5,1);
        var neck=new Vector3(2,0,1);var far=new Vector3(20,5,1);
        var input=new[]{new[]{moved,held,neck,far}};
        var target=moved+new Vector3(.3f,.2f,0);
        var result=SurfacePinFit.Apply(input,new[]{(moved,target),(held,held)},4,1);
        Assert.InRange(Vector3.Distance(target,result[0][0]),0,1e-5f);
        Assert.InRange(Vector3.Distance(held,result[0][1]),0,1e-5f);
        Assert.Equal(neck,result[0][2]);Assert.Equal(far,result[0][3]);Assert.Equal(moved,input[0][0]);
    }
    [Fact]
    public void MirroredPinsMoveSymmetricallyAndSharedSeamCopiesStayTogether()
    {
        var right=new Vector3(2,5,1);var left=new Vector3(-2,5,1);var center=new Vector3(0,5,1);
        var result=SurfacePinFit.Apply(new[]{new[]{right,left,center},new[]{right}},new[]{(right,right+Vector3.UnitX*.2f)},4,1,0);
        Assert.InRange(result[0][0].X,2.1999f,2.2001f);Assert.InRange(result[0][1].X,-2.2001f,-2.1999f);
        Assert.Equal(0,result[0][2].X);Assert.Equal(result[0][0],result[1][0]);
        var held=SurfacePinFit.Apply(new[]{new[]{right,left}},new[]{(right,right+Vector3.UnitX*.2f),(left,left)},4,1,0);
        Assert.InRange(Vector3.Distance(held[0][1],left),0,1e-5f);
        Assert.InRange(Vector3.Distance(held[0][0],right+Vector3.UnitX*.2f),0,1e-5f);
    }
}
