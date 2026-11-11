using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>
/// Gives one head the shape of another. All Cricket 07 heads share the face-atlas layout, so a texture position
/// names the same spot on any head: each target head vertex moves to the source surface drawn at its UV. Eyeballs and teeth
/// go onto the source's own (found by their place in the eye/teeth picture), so the result has the source's eyes. Other
/// parts that are not head surface (hair, anything the source does not cover) follow a smooth field built from those moves. The neck blends back to the target's own neck so it still joins the body.
/// Used to put a hair template's hair on a head that has none: the template takes the player's shape.
/// </summary>
public static class ShapeTransfer
{
    // Eyeball and mouth/teeth pictures along the bottom of the atlas are not head surface.
    private static bool InPictureStrip(Vector2 uv) => uv.Y > 0.88f && (uv.X < 0.32f || uv.X > 0.68f);

    public sealed record Result(Vector3[][] Positions, int Transferred, int Followed);

    /// <param name="neckY">Below this height the target keeps its own shape (the body joins there).</param>
    /// <param name="neckBlend">Height over which the transfer fades in above <paramref name="neckY"/>.</param>
    public static Result Transfer(IReadOnlyList<ModelPart> sourceParts, IReadOnlyList<Vector3[]> sourcePositions, IEnumerable<int> sourceFace,
        IReadOnlyList<ModelPart> targetParts, IReadOnlyList<Vector3[]> targetPositions, IEnumerable<int> targetFace, float neckY, float neckBlend)
    {
        var lookup = new UvLookup(sourceParts, sourcePositions, sourceFace); targetFace = targetFace.ToArray();
        float Fade(float y) { float t = Math.Clamp((y - neckY) / Math.Max(neckBlend, 1e-4f), 0, 1); return t * t * (3 - 2 * t); }
        var result = targetPositions.Select(p => p.ToArray()).ToArray();
        var moved = new bool[targetPositions.Count][];
        for (int p = 0; p < moved.Length; p++) moved[p] = new bool[targetPositions[p].Length];
        var controls = new Dictionary<Vector3, Vector3>(); // original target position -> new position (deduplicated: seams repeat positions)
        int transferred = 0;
        foreach (int p in targetFace)
            for (int i = 0; i < targetPositions[p].Length; i++)
            {
                var uv = targetParts[p].Uvs[i];
                if (InPictureStrip(uv) || lookup.Find(uv) is not { } onSource) continue;
                var original = targetPositions[p][i];
                result[p][i] = Vector3.Lerp(original, onSource, Fade(original.Y));
                moved[p][i] = true; controls[original] = result[p][i]; transferred++;
            }
        if (controls.Count == 0) throw new InvalidDataException("The heads share no face-texture area, so the shape cannot be transferred.");
        // ponytail: every k-th control point (at most ~300) keeps the solve small; dense enough for hair and eyeballs.
        var keys = controls.Keys.ToArray(); int step = Math.Max(1, keys.Length / 300);
        var sample = keys.Where((_, i) => i % step == 0).ToArray();
        var field = FaceFit.Deformation(sample, sample.Select(k => controls[k]).ToArray(), Array.Empty<Vector3>());
        // Eyeballs and teeth are the player's own: each of the target's goes onto the source's, found by its place in the
        // eye/teeth picture. Left and right eyes share one picture, so the copy nearest to where the vertex would go wins.
        var pictures = lookup.PictureTriangles;
        var bounds = targetPositions.SelectMany(v => v).Aggregate((Min: new Vector3(float.MaxValue), Max: new Vector3(float.MinValue)), (b, v) => (Vector3.Min(b.Min, v), Vector3.Max(b.Max, v)));
        float snapReach = Vector3.Distance(bounds.Min, bounds.Max) * 0.015f; // a few eyeball widths: further than that is not the same eye
        int followed = 0;
        for (int p = 0; p < result.Length; p++)
            for (int i = 0; i < result[p].Length; i++)
            {
                if (moved[p][i]) continue;
                var original = targetPositions[p][i];
                var goes = field(original);
                if (pictures.Count > 0 && targetFace.Contains(p) && InPictureStrip(targetParts[p].Uvs[i]))
                {
                    var uv = targetParts[p].Uvs[i];
                    var options = pictures.Select(t =>
                    {
                        float det = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
                        float wa = Math.Max(0, ((t.B.Y - t.C.Y) * (uv.X - t.C.X) + (t.C.X - t.B.X) * (uv.Y - t.C.Y)) / det);
                        float wb = Math.Max(0, ((t.C.Y - t.A.Y) * (uv.X - t.C.X) + (t.A.X - t.C.X) * (uv.Y - t.C.Y)) / det);
                        float wc = Math.Max(0, 1 - wa - wb), sum = wa + wb + wc; wa /= sum; wb /= sum; wc /= sum; // outside the triangle: its nearest edge, roughly
                        return (Miss: Vector2.Distance(t.A * wa + t.B * wb + t.C * wc, uv), Point: t.PA * wa + t.PB * wb + t.PC * wc);
                    }).ToArray();
                    // Only a true match counts: the picture really covers this UV and the spot is close to where the vertex was going
                    // anyway. Some neck and scalp vertices are drawn from the same corner of the atlas; they keep the smooth field.
                    var match = options.Where(o => o.Miss < 0.002f).Select(o => o.Point).DefaultIfEmpty(new Vector3(float.NaN)).MinBy(o => Vector3.DistanceSquared(o, goes));
                    if (Vector3.Distance(match, goes) < snapReach) goes = match;
                }
                result[p][i] = Vector3.Lerp(original, goes, Fade(original.Y)); followed++;
            }
        return new Result(result, transferred, followed);
    }

    /// <summary>Point on a mesh drawn at a UV, via a coarse grid of triangle UV bounds.</summary>
    private sealed class UvLookup
    {
        private const int Grid = 64;
        private readonly List<(Vector2 A, Vector2 B, Vector2 C, Vector3 PA, Vector3 PB, Vector3 PC)> _triangles = new();
        private readonly List<int>[] _cells = Enumerable.Range(0, Grid * Grid).Select(_ => new List<int>()).ToArray();
        /// <summary>The eyeball and teeth triangles (drawn from the picture strip), kept apart from the head surface.</summary>
        public List<(Vector2 A, Vector2 B, Vector2 C, Vector3 PA, Vector3 PB, Vector3 PC)> PictureTriangles { get; } = new();

        public UvLookup(IReadOnlyList<ModelPart> parts, IReadOnlyList<Vector3[]> positions, IEnumerable<int> faceParts)
        {
            foreach (int p in faceParts)
            {
                var part = parts[p];
                for (int t = 0; t + 2 < part.Indices.Length; t += 3)
                {
                    int a = part.Indices[t], b = part.Indices[t + 1], c = part.Indices[t + 2];
                    Vector2 ua = part.Uvs[a], ub = part.Uvs[b], uc = part.Uvs[c];
                    if (Math.Abs((ub.X - ua.X) * (uc.Y - ua.Y) - (uc.X - ua.X) * (ub.Y - ua.Y)) < 1e-12f) continue; // no UV area
                    if (InPictureStrip(ua) && InPictureStrip(ub) && InPictureStrip(uc)) { PictureTriangles.Add((ua, ub, uc, positions[p][a], positions[p][b], positions[p][c])); continue; }
                    int index = _triangles.Count;
                    _triangles.Add((ua, ub, uc, positions[p][a], positions[p][b], positions[p][c]));
                    var min = Vector2.Min(ua, Vector2.Min(ub, uc)); var max = Vector2.Max(ua, Vector2.Max(ub, uc));
                    for (int y = Cell(min.Y); y <= Cell(max.Y); y++)
                        for (int x = Cell(min.X); x <= Cell(max.X); x++) _cells[y * Grid + x].Add(index);
                }
            }
        }
        private static int Cell(float v) => Math.Clamp((int)(v * Grid), 0, Grid - 1);

        public Vector3? Find(Vector2 uv)
        {
            foreach (int index in _cells[Cell(uv.Y) * Grid + Cell(uv.X)])
            {
                var t = _triangles[index];
                float det = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
                float wa = ((t.B.Y - t.C.Y) * (uv.X - t.C.X) + (t.C.X - t.B.X) * (uv.Y - t.C.Y)) / det;
                float wb = ((t.C.Y - t.A.Y) * (uv.X - t.C.X) + (t.A.X - t.C.X) * (uv.Y - t.C.Y)) / det;
                float wc = 1 - wa - wb;
                const float eps = -1e-4f;
                if (wa >= eps && wb >= eps && wc >= eps) return t.PA * wa + t.PB * wb + t.PC * wc;
            }
            return null;
        }
    }
}
