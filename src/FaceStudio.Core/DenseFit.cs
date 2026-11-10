using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>
/// Dense likeness fit from one front photo. The face-mesh detector is run on the photo and on a front render of the
/// head itself; both give the same 468 points in 3D. The photo's points are rotated, scaled and moved onto the render's
/// (so a turned or tilted head is straightened and only proportions remain), and each point's remaining difference is
/// how far that spot of the head must move. Because both sides come from the same detector, its systematic errors
/// cancel. Points are in "face space": x right, y up, z toward the camera, in pixels of their own image.
/// </summary>
public static class DenseFit
{
    /// <summary>Face points used (the detector's last ten are the irises, which say nothing about shape).</summary>
    public const int PointCount = 468;

    /// <summary>Least-squares rotation, uniform scale and offset taking <paramref name="from"/> onto <paramref name="to"/> (Horn's quaternion method).</summary>
    public static Func<Vector3, Vector3> Similarity(IReadOnlyList<Vector3> from, IReadOnlyList<Vector3> to)
    {
        if (from.Count != to.Count || from.Count < 3) throw new ArgumentException("At least three matching points are required.");
        var cf = from.Aggregate(Vector3.Zero, (s, v) => s + v) / from.Count; var ct = to.Aggregate(Vector3.Zero, (s, v) => s + v) / to.Count;
        double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
        for (int i = 0; i < from.Count; i++)
        {
            var a = from[i] - cf; var b = to[i] - ct;
            sxx += a.X * b.X; sxy += a.X * b.Y; sxz += a.X * b.Z; syx += a.Y * b.X; syy += a.Y * b.Y; syz += a.Y * b.Z; szx += a.Z * b.X; szy += a.Z * b.Y; szz += a.Z * b.Z;
        }
        var n = new double[4, 4]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
        };
        // Largest eigenvector by power iteration; the shift makes every eigenvalue positive so the largest one wins.
        double shift = 0; foreach (double v in n) shift += Math.Abs(v);
        var q = new double[] { 1, 0, 0, 0 };
        for (int iteration = 0; iteration < 400; iteration++)
        {
            var next = new double[4];
            for (int r = 0; r < 4; r++) { next[r] = shift * q[r]; for (int c = 0; c < 4; c++) next[r] += n[r, c] * q[c]; }
            double length = Math.Sqrt(next.Sum(v => v * v));
            if (length < 1e-30) break;
            for (int r = 0; r < 4; r++) q[r] = next[r] / length;
        }
        var rotation = new Quaternion((float)q[1], (float)q[2], (float)q[3], (float)q[0]);
        double num = 0, den = 0;
        for (int i = 0; i < from.Count; i++) { var a = Vector3.Transform(from[i] - cf, rotation); num += Vector3.Dot(a, to[i] - ct); den += a.LengthSquared(); }
        if (den < 1e-12 || !(num > 0)) throw new ArgumentException("The photo's face points do not match the head.");
        float scale = (float)(num / den);
        return p => Vector3.Transform(p - cf, rotation) * scale + ct;
    }

    /// <summary>
    /// Control points for the head's deformation: where chosen face points sit on the head now and where they should go.
    /// </summary>
    /// <param name="head">The head-surface point (mesh units) under each detector point of the render; null where it missed the head.</param>
    /// <param name="render">Detector points on the head's own front render (face space).</param>
    /// <param name="photo">Detector points on the photo (face space).</param>
    /// <param name="unitsPerPixel">Mesh units per render pixel.</param>
    /// <param name="mirrorX">The head's mirror plane (x), used by <paramref name="symmetric"/> to average left and right.</param>
    /// <param name="depth">0..1: how much of the detector's (rougher) depth difference to use.</param>
    /// <param name="count">How many well-spread points to keep.</param>
    public static (Vector3[] From, Vector3[] To) Controls(IReadOnlyList<Vector3?> head, IReadOnlyList<Vector3> render, IReadOnlyList<Vector3> photo,
        float unitsPerPixel, bool symmetric, float mirrorX, float strength, float depth, int count = 90)
    {
        if (head.Count != render.Count || render.Count != photo.Count) throw new ArgumentException("One value per face point is required.");
        var used = Enumerable.Range(0, head.Count).Where(i => head[i] is not null).ToArray();
        if (used.Length < 20) throw new ArgumentException("Too few face points landed on the head.");
        var align = Similarity(used.Select(i => photo[i]).ToArray(), used.Select(i => render[i]).ToArray());
        var move = new Vector3[head.Count];
        foreach (int i in used) move[i] = (align(photo[i]) - render[i]) * unitsPerPixel * new Vector3(1, 1, Math.Clamp(depth, 0, 1));
        if (symmetric)
        {
            var size = used.Select(i => head[i]!.Value).Aggregate((Min: new Vector3(float.MaxValue), Max: new Vector3(float.MinValue)), (b, v) => (Vector3.Min(b.Min, v), Vector3.Max(b.Max, v)));
            float near = Vector3.Distance(size.Min, size.Max) * 0.04f;
            var mirrored = (Vector3[])move.Clone();
            foreach (int i in used)
            {
                var p = head[i]!.Value; var reflected = new Vector3(2 * mirrorX - p.X, p.Y, p.Z);
                int partner = used.MinBy(j => Vector3.DistanceSquared(head[j]!.Value, reflected));
                if (Vector3.Distance(head[partner]!.Value, reflected) > near) continue; // no counterpart on the other side: keep its own move
                var other = move[partner] * new Vector3(-1, 1, 1);
                mirrored[i] = (move[i] + other) / 2;
                if (partner == i) mirrored[i].X = 0; // midline points stay on the midline
            }
            move = mirrored;
        }
        // Farthest-point sampling: an even spread, so crowded areas (lips, eyes) do not outvote the cheeks and jaw.
        var picked = new List<int> { used.MinBy(i => head[i]!.Value.Y) };
        var distance = used.ToDictionary(i => i, i => Vector3.DistanceSquared(head[i]!.Value, head[picked[0]]!.Value));
        while (picked.Count < Math.Min(count, used.Length))
        {
            int next = distance.MaxBy(d => d.Value).Key;
            if (distance[next] <= 1e-10f) break;
            picked.Add(next);
            foreach (int i in used) distance[i] = Math.Min(distance[i], Vector3.DistanceSquared(head[i]!.Value, head[next]!.Value));
        }
        float s = Math.Clamp(strength, 0, 5f);
        return (picked.Select(i => head[i]!.Value).ToArray(), picked.Select(i => head[i]!.Value + move[i] * s).ToArray());
    }
}
