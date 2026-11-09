using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class TexturePaintTests
{
    private const int W = 64;
    private static byte[] Solid(byte r, byte g, byte b, byte a = 200)
    {
        var d = new byte[W * W * 4];
        for (int i = 0; i < d.Length; i += 4) { d[i] = b; d[i + 1] = g; d[i + 2] = r; d[i + 3] = a; }
        return d;
    }
    private static Vector3 At(byte[] d, int x, int y) { int i = (y * W + x) * 4; return new Vector3(d[i + 2], d[i + 1], d[i]); }
    private static void Set(byte[] d, int x, int y, byte r, byte g, byte b) { int i = (y * W + x) * 4; d[i] = b; d[i + 1] = g; d[i + 2] = r; }
    private static bool AlphaUnchanged(byte[] a, byte[] b) => Enumerable.Range(0, a.Length / 4).All(p => a[p * 4 + 3] == b[p * 4 + 3]);

    [Fact]
    public void PaintChangesOnlyInsideTheBrush()
    {
        var before = Solid(120, 100, 80); var pixels = (byte[])before.Clone();
        TexturePaint.Segment(pixels, W, W, new Vector2(32, 32), new Vector2(32, 32), 8, PaintTool.Paint, new Vector3(1, 0, 0), 1);
        Assert.Equal(new Vector3(255, 0, 0), At(pixels, 32, 32));
        Assert.Equal(At(before, 32, 22), At(pixels, 32, 22));   // outside the 8 px radius
        Assert.Equal(At(before, 41, 32), At(pixels, 41, 32));
        Assert.True(AlphaUnchanged(before, pixels));
    }

    [Fact]
    public void StrengthScalesTheChange()
    {
        var half = Solid(0, 0, 0);
        TexturePaint.Segment(half, W, W, new Vector2(32, 32), new Vector2(32, 32), 8, PaintTool.Paint, Vector3.One, 0.5f);
        Assert.InRange(At(half, 32, 32).X, 126, 129);
    }

    [Fact]
    public void SegmentPaintsTheWholeLine()
    {
        var pixels = Solid(0, 0, 0);
        TexturePaint.Segment(pixels, W, W, new Vector2(10, 20), new Vector2(50, 20), 4, PaintTool.Paint, Vector3.One, 1);
        for (int x = 10; x <= 50; x++) Assert.True(At(pixels, x, 20).X > 250, $"gap at x={x}");
        Assert.Equal(Vector3.Zero, At(pixels, 30, 30));
    }

    [Fact]
    public void CloneCopiesTheOffsetPatchFromTheStrokeStart()
    {
        var pixels = Solid(50, 50, 50);
        for (int y = 5; y < 15; y++) for (int x = 5; x < 15; x++) Set(pixels, x, y, 200, 10, 30);
        var source = (byte[])pixels.Clone();
        TexturePaint.Segment(pixels, W, W, new Vector2(40, 40), new Vector2(40, 40), 4, PaintTool.Clone, default, 1, source, new Vector2(-30, -30));
        Assert.Equal(new Vector3(200, 10, 30), At(pixels, 40, 40));
        Assert.Equal(new Vector3(50, 50, 50), At(pixels, 40, 50));
        Assert.True(AlphaUnchanged(source, pixels));
    }

    [Fact]
    public void BlurLowersLocalVariance()
    {
        var pixels = Solid(0, 0, 0);
        for (int y = 0; y < W; y++) for (int x = 0; x < W; x++) if ((x + y) % 2 == 0) Set(pixels, x, y, 255, 255, 255);
        float Variance(byte[] d) { var v = Enumerable.Range(28, 9).SelectMany(y => Enumerable.Range(28, 9).Select(x => At(d, x, y).X)).ToArray(); float m = v.Average(); return v.Average(a => (a - m) * (a - m)); }
        float before = Variance(pixels);
        TexturePaint.Segment(pixels, W, W, new Vector2(32, 32), new Vector2(32, 32), 12, PaintTool.Blur, default, 1);
        Assert.True(Variance(pixels) < before * 0.2f);
        Assert.True(At(pixels, 2, 2).X is 0 or 255); // far away: untouched checkerboard
    }

    [Fact]
    public void PaintSetsTheColourButKeepsTheTextureDetail()
    {
        // Fine stripes (like stubble or hair strands) on skin painted dark brown: the average becomes the paint colour and
        // the stripes still show, instead of a flat blob.
        var pixels = Solid(200, 160, 130);
        for (int y = 0; y < W; y++) for (int x = 0; x < W; x++) if (x % 2 == 0) Set(pixels, x, y, 230, 190, 160);
        var before = (byte[])pixels.Clone();
        TexturePaint.Segment(pixels, W, W, new Vector2(32, 32), new Vector2(32, 32), 12, PaintTool.Paint, new Vector3(0.25f, 0.15f, 0.1f), 1);
        float mean = (At(pixels, 32, 32).X + At(pixels, 33, 32).X) / 2;
        Assert.InRange(mean, 60, 70);                                            // 0.25 * 255 = 64
        Assert.InRange(Math.Abs(At(pixels, 32, 32).X - At(pixels, 33, 32).X), 5, 14);  // stripes kept, scaled to the darker colour (30 of 215 before)
        Assert.True(AlphaUnchanged(before, pixels));
    }
}
