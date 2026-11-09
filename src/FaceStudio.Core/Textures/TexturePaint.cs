using System.Numerics;

namespace Cricket07.FaceStudio.Core;

public enum PaintTool { Paint, Blur, Clone }

/// <summary>
/// Soft round brush on BGRA pixels, edited in place. Alpha is never changed (the head's DXT edge mask, hair strand cut-out).
/// Positions and radius are in texture pixels.
/// </summary>
public static class TexturePaint
{
    /// <summary>
    /// Dabs along the segment from <paramref name="from"/> to <paramref name="to"/> (the start point is not dabbed again,
    /// so consecutive segments of one stroke do not double up). Pass from == to for a single dab.
    /// <paramref name="source"/> is the texture as it was when the stroke began (Clone copies from it at <paramref name="cloneOffset"/>;
    /// Paint takes the detail it keeps from it). Without it the current pixels are used.
    /// Returns the touched pixel rectangle (x, y, width, height) for partial screen updates.
    /// </summary>
    public static (int X, int Y, int Width, int Height) Segment(byte[] bgra, int width, int height, Vector2 from, Vector2 to, float radius,
        PaintTool tool, Vector3 colour, float strength, byte[]? source = null, Vector2 cloneOffset = default)
    {
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("Pixel buffer does not match its dimensions.");
        if (!(radius > 0) || !float.IsFinite(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
        float length = Vector2.Distance(from, to), step = Math.Max(0.5f, radius * 0.25f);
        int dabs = Math.Max(1, (int)MathF.Ceiling(length / step));
        if (tool == PaintTool.Paint && source is null) source = (byte[])bgra.Clone(); // Paint must read detail from pixels it is not changing
        for (int i = 1; i <= dabs; i++) Dab(bgra, width, height, Vector2.Lerp(from, to, (float)i / dabs), radius, tool, colour, strength, source ?? bgra, cloneOffset);
        int x0 = Math.Max(0, (int)(Math.Min(from.X, to.X) - radius - 1)), y0 = Math.Max(0, (int)(Math.Min(from.Y, to.Y) - radius - 1));
        int x1 = Math.Min(width, (int)(Math.Max(from.X, to.X) + radius + 2)), y1 = Math.Min(height, (int)(Math.Max(from.Y, to.Y) + radius + 2));
        return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>
    /// Catmull-Rom (bicubic) sample: keeps hair strands and pores crisp at part-pixel moves, where bilinear visibly
    /// softens them. Exact at whole pixels.
    /// </summary>
    internal static Vector3 Bicubic(Func<int, int, Vector3> at, Vector2 s)
    {
        int sx = (int)MathF.Floor(s.X), sy = (int)MathF.Floor(s.Y);
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
        Weights(s.X - sx, wx); Weights(s.Y - sy, wy);
        var sum = Vector3.Zero;
        for (int j = 0; j < 4; j++)
        {
            var row = Vector3.Zero;
            for (int i = 0; i < 4; i++) row += at(sx - 1 + i, sy - 1 + j) * wx[i];
            sum += row * wy[j];
        }
        return sum;
        static void Weights(float t, Span<float> w)
        {
            float t2 = t * t, t3 = t2 * t;
            w[0] = -0.5f * t3 + t2 - 0.5f * t; w[1] = 1.5f * t3 - 2.5f * t2 + 1;
            w[2] = -1.5f * t3 + 2f * t2 + 0.5f * t; w[3] = 0.5f * t3 - 0.5f * t2;
        }
    }

    private static void Dab(byte[] bgra, int width, int height, Vector2 centre, float radius, PaintTool tool, Vector3 colour, float strength, byte[] source, Vector2 cloneOffset)
    {
        int x0 = Math.Max(0, (int)(centre.X - radius)), x1 = Math.Min(width - 1, (int)(centre.X + radius));
        int y0 = Math.Max(0, (int)(centre.Y - radius)), y1 = Math.Min(height - 1, (int)(centre.Y + radius));
        if (x0 > x1 || y0 > y1) return;
        // Blur uses the local average of the pixels as they were before this dab (so it does not smear in one direction).
        byte[]? before = null; int k = Math.Max(1, (int)MathF.Round(radius * 0.15f));
        int bx0 = Math.Max(0, x0 - k), by0 = Math.Max(0, y0 - k), bx1 = Math.Min(width - 1, x1 + k), by1 = Math.Min(height - 1, y1 + k), bw = bx1 - bx0 + 1;
        if (tool == PaintTool.Blur)
        {
            before = new byte[bw * (by1 - by0 + 1) * 4];
            for (int y = by0; y <= by1; y++) Buffer.BlockCopy(bgra, (y * width + bx0) * 4, before, (y - by0) * bw * 4, bw * 4);
        }
        float s = Math.Clamp(strength, 0, 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), centre) / radius;
            if (d >= 1) continue;
            float t = Math.Clamp((1 - d) / 0.5f, 0, 1), weight = s * t * t * (3 - 2 * t); // solid core, soft outer half
            int i = (y * width + x) * 4;
            var c = new Vector3(bgra[i + 2], bgra[i + 1], bgra[i]) / 255f;
            Vector3 target;
            if (tool == PaintTool.Clone)
            {
                int sx = Math.Clamp((int)(x + cloneOffset.X), 0, width - 1), sy = Math.Clamp((int)(y + cloneOffset.Y), 0, height - 1), j = (sy * width + sx) * 4;
                target = new Vector3(source[j + 2], source[j + 1], source[j]) / 255f;
            }
            else if (tool == PaintTool.Paint)
            {
                // Paint sets the colour but keeps the texture's own fine detail (pores, stubble, hair strands): each pixel's
                // brightness relative to its neighbourhood in the texture as it was when the stroke began scales the new
                // colour. Reading the stroke-start texture (not the pixels just painted) keeps the brush's own soft edge
                // from feeding back as fake detail (it streaked); relative, not added, so dark paint gets no light flecks.
                float sum = 0; int n = 0;
                for (int v = Math.Max(0, y - k); v <= Math.Min(height - 1, y + k); v++)
                for (int u = Math.Max(0, x - k); u <= Math.Min(width - 1, x + k); u++) { sum += Luma(source, (v * width + u) * 4); n++; }
                const float floor = 0.04f; // near-black: no meaningful ratio, and dark noise must not be amplified
                float ratio = Math.Clamp((Luma(source, i) + floor) / (sum / n + floor), 0.4f, 2.5f);
                target = colour * ratio;
            }
            else
            {
                var sum = Vector3.Zero; int n = 0;
                for (int v = Math.Max(by0, y - k); v <= Math.Min(by1, y + k); v++)
                for (int u = Math.Max(bx0, x - k); u <= Math.Min(bx1, x + k); u++)
                { int j = ((v - by0) * bw + u - bx0) * 4; sum += new Vector3(before![j + 2], before[j + 1], before[j]); n++; }
                target = sum / (n * 255f);
            }
            var o = Vector3.Clamp(Vector3.Lerp(c, target, weight), Vector3.Zero, Vector3.One) * 255f;
            bgra[i + 2] = (byte)MathF.Round(o.X); bgra[i + 1] = (byte)MathF.Round(o.Y); bgra[i] = (byte)MathF.Round(o.Z);
        }
    }

    private static float Luma(byte[] b, int i) => (0.299f * b[i + 2] + 0.587f * b[i + 1] + 0.114f * b[i]) / 255f;

    /// <summary>Colour (0..1 RGB) at a pixel position, clamped to the texture.</summary>
    public static Vector3 Pick(byte[] bgra, int width, int height, Vector2 at)
    {
        int x = Math.Clamp((int)at.X, 0, width - 1), y = Math.Clamp((int)at.Y, 0, height - 1), i = (y * width + x) * 4;
        return new Vector3(bgra[i + 2], bgra[i + 1], bgra[i]) / 255f;
    }
}
