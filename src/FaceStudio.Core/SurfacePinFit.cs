using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>Local surface constraints with smooth falloff, solved together so resting pins hold their positions.</summary>
public static class SurfacePinFit
{
    public static Vector3[][] Apply(IReadOnlyList<Vector3[]> parts, IReadOnlyList<(Vector3 From, Vector3 To)> pins,
        float reach, float neckY, float? mirrorX = null)
    {
        if (!float.IsFinite(reach) || reach <= 0) throw new ArgumentException("Pin reach must be positive.");
        if(pins.Any(p=>!Finite(p.From)||!Finite(p.To)))throw new ArgumentException("Pin coordinates must be finite.");
        var controls = pins.ToList();
        if (mirrorX is float plane)
            controls.AddRange(pins.Select(p => (From:new Vector3(2 * plane - p.From.X, p.From.Y, p.From.Z), To:new Vector3(2 * plane - p.To.X, p.To.Y, p.To.Z)))
                .Where(p=>!pins.Any(authored=>Vector3.Distance(authored.From,p.From)<reach*1e-4f)));
        var groups = controls.GroupBy(p => p.From).ToArray();
        var from = groups.Select(g => g.Key).ToArray();
        var moves = groups.Select(g => g.Aggregate(Vector3.Zero, (sum, p) => sum + p.To - p.From) / g.Count()).ToArray();
        if (from.Length == 0) return parts.Select(p => p.ToArray()).ToArray();
        int n = from.Length;
        reach = Math.Max(reach, moves.Max(m => m.Length()) * 1.5f);
        double Kernel(Vector3 a, Vector3 b)
        {
            double q = Vector3.Distance(a, b) / reach;
            return q >= 1 ? 0 : Math.Pow(1 - q, 4) * (4 * q + 1);
        }
        var matrix = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) matrix[i, j] = Kernel(from[i], from[j]);
        var wx = LinearSystem.Solve((double[,])matrix.Clone(), moves.Select(m => (double)m.X).ToArray());
        var wy = LinearSystem.Solve((double[,])matrix.Clone(), moves.Select(m => (double)m.Y).ToArray());
        var wz = LinearSystem.Solve(matrix, moves.Select(m => (double)m.Z).ToArray());
        return parts.Select(part => part.Select(p =>
        {
            if (p.Y <= neckY) return p;
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < n; i++) { double weight = Kernel(p, from[i]); x += weight * wx[i]; y += weight * wy[i]; z += weight * wz[i]; }
            if (mirrorX is float plane && Math.Abs(p.X - plane) < 1e-5f) x = 0;
            return p + new Vector3((float)x, (float)y, (float)z);
        }).ToArray()).ToArray();
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
