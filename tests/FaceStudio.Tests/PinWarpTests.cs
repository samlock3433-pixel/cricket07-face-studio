using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class PinWarpTests
{
    private const int W = 96;
    private static byte[] Image()
    {
        var d = new byte[W * W * 4];
        for (int y = 0; y < W; y++) for (int x = 0; x < W; x++)
        { int i = (y * W + x) * 4; byte v = (byte)((x * 7 + y * 13) % 200 + 20); d[i] = v; d[i + 1] = (byte)(255 - v); d[i + 2] = (byte)(x * 2); d[i + 3] = (byte)(100 + y); }
        return d;
    }
    private static byte[] Pixel(byte[] d, int x, int y) => d.Skip((y * W + x) * 4).Take(4).ToArray();

    [Fact]
    public void NoPinsChangesNothing() => Assert.Equal(Image(), PinWarp.Apply(Image(), W, W, Array.Empty<(Vector2, Vector2)>(), 20));

    [Fact]
    public void APinCarriesThePaintedPixelToItsTarget()
    {
        var src = Image();
        // Pixel centres: the colour painted at pixel (30,40) should appear at pixel (36,44).
        var output = PinWarp.Apply(src, W, W, new[] { (new Vector2(30.5f, 40.5f), new Vector2(36.5f, 44.5f)) }, 20);
        Assert.Equal(Pixel(src, 30, 40).Take(3), Pixel(output, 36, 44).Take(3));
        Assert.True(Enumerable.Range(0, W * W).All(p => output[p * 4 + 3] == src[p * 4 + 3])); // alpha stays
    }

    [Fact]
    public void ItIsLocalAndHoldPinsStayPut()
    {
        var src = Image();
        var pins = new[] { (new Vector2(30.5f, 40.5f), new Vector2(36.5f, 40.5f)), (new Vector2(60.5f, 40.5f), new Vector2(60.5f, 40.5f)) };
        var output = PinWarp.Apply(src, W, W, pins, 20);
        Assert.Equal(Pixel(src, 60, 40), Pixel(output, 60, 40));   // held
        Assert.Equal(Pixel(src, 36, 80), Pixel(output, 36, 80));   // beyond the reach of every pin: untouched
        Assert.Equal(Pixel(src, 5, 5), Pixel(output, 5, 5));
    }

    [Fact]
    public void ALongDragWidensTheReachInsteadOfFolding()
    {
        // Dragged 30 px with a 10 px reach: the warp must stay one-to-one (no folded, mirrored texture): along the drag
        // line the source position must keep increasing.
        var ramp = new byte[W * W * 4];
        for (int y = 0; y < W; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; ramp[i + 2] = (byte)(x * 2); ramp[i + 3] = 255; }
        var output = PinWarp.Apply(ramp, W, W, new[] { (new Vector2(30.5f, 48.5f), new Vector2(60.5f, 48.5f)) }, 10);
        var row = Enumerable.Range(0, W).Select(x => (int)output[(48 * W + x) * 4 + 2]).ToArray();
        for (int x = 1; x < W; x++) Assert.True(row[x] >= row[x - 1] - 2, $"folded at x={x}: {row[x - 1]} -> {row[x]}");
        Assert.InRange(row[60], 59, 63); // the colour from x=30 (value 60) arrived at x=60
    }
}
