using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>
/// Face atlas layout. Every Cricket 07 head atlas shares one layout: unwrapped face in the centre, eyeball pictures
/// bottom-left, mouth/teeth pictures bottom-right. Positions are normalised (0..1).
/// </summary>
public static class FaceTexture
{
    // Eyeball and mouth/teeth pictures along the bottom of the atlas.
    private static readonly (Vector2 Min, Vector2 Max)[] Protected = { (new(0, 0.88f), new(0.32f, 1)), (new(0.68f, 0.88f), new(1, 1)) };
    /// <summary>True inside the eyeball or teeth picture along the bottom of the atlas (normalised UV).</summary>
    public static bool InPictureStrip(Vector2 uv) => Protected.Any(r => uv.X >= r.Min.X && uv.X <= r.Max.X && uv.Y >= r.Min.Y && uv.Y <= r.Max.Y);
}

/// <summary>Dense linear solve with partial pivoting (small spline systems).</summary>
internal static class LinearSystem
{
    public static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int c = 0; c < n; c++)
        {
            int pivot = Enumerable.Range(c, n - c).MaxBy(r => Math.Abs(a[r, c]));
            if (Math.Abs(a[pivot, c]) < 1e-12) throw new ArgumentException("Landmarks are degenerate (overlapping or in a line).");
            for (int k = 0; k < n; k++) (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
            (b[c], b[pivot]) = (b[pivot], b[c]);
            for (int r = c + 1; r < n; r++)
            {
                double f = a[r, c] / a[c, c];
                for (int k = c; k < n; k++) a[r, k] -= f * a[c, k];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
