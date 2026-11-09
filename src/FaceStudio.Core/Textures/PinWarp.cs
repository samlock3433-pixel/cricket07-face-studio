using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>
/// Puppet-style texture warp through pins: each pin says "the colour painted at From belongs at To". The displacement
/// is a compactly supported (Wendland C2) radial basis interpolation: exact at every pin, smooth, and exactly zero
/// farther than the reach from all pins, so a pin only moves its own neighbourhood. A pin with From == To holds that
/// spot still. The result is always sampled once (bicubic) from the unwarped texture, so editing pins never blurs.
/// Positions and reach are in texture pixels. Alpha is never moved.
/// </summary>
public static class PinWarp
{
    public static byte[] Apply(byte[] bgra, int width, int height, IReadOnlyList<(Vector2 From, Vector2 To)> pins, float reach)
    {
        if (width < 1 || height < 1 || bgra.Length != checked(width * height * 4)) throw new ArgumentException("Pixel buffer does not match its dimensions.");
        var output = (byte[])bgra.Clone();
        if (pins.Count == 0) return output;
        // A pin dragged farther than about two thirds of its reach would fold the texture over itself; that pin (only)
        // gets a wider reach instead, so one long drag does not make every other pin pull a wider area.
        int n = pins.Count;
        var r = pins.Select(p => Math.Max(Math.Max(reach, 1), 1.5f * Vector2.Distance(p.From, p.To))).ToArray();
        var kernel = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) kernel[i, j] = Wendland(Vector2.Distance(pins[i].To, pins[j].To) / r[j]);
        var wx = LinearSystem.Solve((double[,])kernel.Clone(), pins.Select(p => (double)(p.From.X - p.To.X)).ToArray());
        var wy = LinearSystem.Solve(kernel, pins.Select(p => (double)(p.From.Y - p.To.Y)).ToArray());
        int x0 = Math.Max(0, (int)Enumerable.Range(0, n).Min(i => pins[i].To.X - r[i])), x1 = Math.Min(width - 1, (int)Enumerable.Range(0, n).Max(i => pins[i].To.X + r[i]) + 1);
        int y0 = Math.Max(0, (int)Enumerable.Range(0, n).Min(i => pins[i].To.Y - r[i])), y1 = Math.Min(height - 1, (int)Enumerable.Range(0, n).Max(i => pins[i].To.Y + r[i]) + 1);
        Vector3 Src(int u, int v) { int j = (Math.Clamp(v, 0, height - 1) * width + Math.Clamp(u, 0, width - 1)) * 4; return new Vector3(bgra[j + 2], bgra[j + 1], bgra[j]); }
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            double dx = 0, dy = 0;
            for (int i = 0; i < n; i++)
            {
                double k = Wendland(Vector2.Distance(p, pins[i].To) / r[i]);
                if (k != 0) { dx += wx[i] * k; dy += wy[i] * k; }
            }
            if (dx == 0 && dy == 0) continue;
            var c = Vector3.Clamp(TexturePaint.Bicubic(Src, new Vector2((float)(x + dx), (float)(y + dy))), Vector3.Zero, new Vector3(255));
            int o = (y * width + x) * 4;
            output[o + 2] = (byte)MathF.Round(c.X); output[o + 1] = (byte)MathF.Round(c.Y); output[o] = (byte)MathF.Round(c.Z);
        }
        return output;
    }

    private static double Wendland(double q) => q >= 1 ? 0 : Math.Pow(1 - q, 4) * (4 * q + 1);
}
