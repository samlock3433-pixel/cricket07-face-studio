using System.Numerics;

namespace Cricket07.FaceStudio.Core;

public static class SculptBrush
{
    public static Vector3[] Proportion(IReadOnlyList<Vector3> vertices,Vector3 center,Vector3 scale,
        float radius,float? symmetryPlane=null,float? lockBelowY=null)
    {
        _=Move(vertices,center,Vector3.Zero,radius,symmetryPlane,lockBelowY);
        if(!Finite(scale)||scale.X<=0||scale.Y<=0||scale.Z<=0)throw new ArgumentException("Scale must be positive and finite.");
        var mirror=symmetryPlane is float x?new Vector3(2*x-center.X,center.Y,center.Z):center;
        return vertices.Select(p=>{
            if(lockBelowY is float floor&&p.Y<=floor)return p;
            var c=symmetryPlane is not null&&Vector3.DistanceSquared(p,mirror)<Vector3.DistanceSquared(p,center)?mirror:center;
            var delta=(p-c)*(scale-Vector3.One)*Weight(Vector3.Distance(p,c),radius);
            if(symmetryPlane is float plane&&Math.Abs(p.X-plane)<radius*0.0001f)delta.X=0;
            return p+delta;
        }).ToArray();
    }
    /// <summary>Local displacement with smooth falloff. Never adds or reorders vertices.</summary>
    public static Vector3[] Move(IReadOnlyList<Vector3> vertices, Vector3 center, Vector3 displacement,
        float radius, float? symmetryPlane = null, float? lockBelowY = null)
    {
        if (!float.IsFinite(radius) || radius <= 0 || !Finite(center) || !Finite(displacement))
            throw new ArgumentException("Brush radius and coordinates must be finite; radius must be positive.");
        var output = vertices.ToArray();
        var mirrored = symmetryPlane is float plane ? new Vector3(2*plane-center.X, center.Y, center.Z) : center;
        for (int i = 0; i < output.Length; i++)
        {
            var p = vertices[i];
            if (!Finite(p)) throw new InvalidDataException("Non-finite mesh vertex.");
            if (lockBelowY is float floor && p.Y <= floor) continue;
            float a = Weight(Vector3.Distance(p, center), radius);
            float b = symmetryPlane is null ? 0 : Weight(Vector3.Distance(p, mirrored), radius);
            var delta = a >= b ? displacement * a : new Vector3(-displacement.X, displacement.Y, displacement.Z) * b;
            if (symmetryPlane is float x && Math.Abs(p.X-x) < radius * 0.0001f) delta.X = 0;
            output[i] += delta;
        }
        return output;
    }

    public static Vector3[] Smooth(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles,
        Vector3 center, float radius, float strength, float? symmetryPlane = null, float? lockBelowY = null)
    {
        if (triangles.Count % 3 != 0 || triangles.Any(i => i < 0 || i >= vertices.Count))
            throw new InvalidDataException("Invalid triangle indices.");
        if (!float.IsFinite(strength) || strength < 0 || strength > 1) throw new ArgumentOutOfRangeException(nameof(strength));
        _ = Move(vertices, center, Vector3.Zero, radius, symmetryPlane, lockBelowY);
        // Weld by position: UV seams duplicate vertices, and smoothing each copy separately would open cracks.
        var weld = new Dictionary<Vector3, int>(); var points = new List<Vector3>();
        var key = vertices.Select(v => { if (!weld.TryGetValue(v, out int k)) { weld[v] = k = points.Count; points.Add(v); } return k; }).ToArray();
        var neighbors = points.Select(_ => new HashSet<int>()).ToArray();
        for (int t = 0; t < triangles.Count; t += 3)
            for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++)
                if (key[triangles[t+a]] != key[triangles[t+b]]) neighbors[key[triangles[t+a]]].Add(key[triangles[t+b]]);
        var result = vertices.ToArray();
        var mirrored = symmetryPlane is float plane ? new Vector3(2*plane-center.X, center.Y, center.Z) : center;
        for (int i = 0; i < result.Length; i++)
        {
            var n = neighbors[key[i]];
            if (n.Count == 0 || (lockBelowY is float floor && vertices[i].Y <= floor)) continue;
            float weight = Math.Max(Weight(Vector3.Distance(vertices[i], center), radius),
                symmetryPlane is null ? 0 : Weight(Vector3.Distance(vertices[i], mirrored), radius));
            var mean = n.Aggregate(Vector3.Zero, (sum, k) => sum + points[k]) / n.Count;
            result[i] = Vector3.Lerp(vertices[i], mean, weight * strength);
        }
        return result;
    }

    /// <summary>
    /// Volume brush (hair): pushes vertices along their surface normals, outward for a positive amount, inward for
    /// a negative one. Normals are welded by position so UV-seam duplicates move together, and oriented away from
    /// the mesh centre so "outward" means more volume whatever the triangle winding. <paramref name="whole"/> moves
    /// every vertex fully (neck lock still applies); otherwise the brush falloff around <paramref name="center"/>.
    /// </summary>
    public static Vector3[] Inflate(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles, Vector3 center, float amount,
        float radius, float? symmetryPlane = null, float? lockBelowY = null, bool whole = false)
    {
        if (triangles.Count % 3 != 0 || triangles.Any(i => i < 0 || i >= vertices.Count)) throw new InvalidDataException("Invalid triangle indices.");
        if (!float.IsFinite(amount)) throw new ArgumentException("Amount must be finite.");
        if (!whole) _ = Move(vertices, center, Vector3.Zero, radius, symmetryPlane, lockBelowY); // validates radius and centre
        var weld = new Dictionary<Vector3, int>(); var points = new List<Vector3>();
        var key = vertices.Select(v => { if (!weld.TryGetValue(v, out int k)) { weld[v] = k = points.Count; points.Add(v); } return k; }).ToArray();
        var normals = new Vector3[points.Count];
        for (int t = 0; t < triangles.Count; t += 3)
        {
            Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            var n = Vector3.Cross(b - a, c - a); // area-weighted
            normals[key[triangles[t]]] += n; normals[key[triangles[t + 1]]] += n; normals[key[triangles[t + 2]]] += n;
        }
        var centroid = points.Aggregate(Vector3.Zero, (s, p) => s + p) / Math.Max(1, points.Count);
        for (int k = 0; k < normals.Length; k++)
        {
            if (normals[k].LengthSquared() < 1e-20f) continue;
            normals[k] = Vector3.Normalize(normals[k]);
            if (Vector3.Dot(normals[k], points[k] - centroid) < 0) normals[k] = -normals[k];
        }
        var mirrored = symmetryPlane is float plane ? new Vector3(2 * plane - center.X, center.Y, center.Z) : center;
        var result = vertices.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            if (lockBelowY is float floor && vertices[i].Y <= floor) continue;
            float weight = whole ? 1 : Math.Max(Weight(Vector3.Distance(vertices[i], center), radius),
                symmetryPlane is null ? 0 : Weight(Vector3.Distance(vertices[i], mirrored), radius));
            var delta = normals[key[i]] * amount * weight;
            if (symmetryPlane is float x && Math.Abs(vertices[i].X - x) < (whole ? 1e-4f : radius * 0.0001f)) delta.X = 0;
            result[i] += delta;
        }
        return result;
    }

    private static float Weight(float distance, float radius)
    {
        float t = Math.Clamp(1-distance/radius, 0, 1);
        return t*t*(3-2*t);
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
