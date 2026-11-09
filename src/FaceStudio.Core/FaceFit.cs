using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>A facial landmark: its spot in the shared face-atlas layout, which photo views show it, and its mirror partner.</summary>
public sealed record FaceLandmark(string Name, Vector2 Uv, bool Front, bool Side, int Mirror);

/// <summary>A point on the mesh: a triangle of one part and barycentric weights. Follows the part as it is sculpted.</summary>
public readonly record struct MeshAnchor(int Part, int A, int B, int C, float WA, float WB, float WC)
{
    public Vector3 Position(IReadOnlyList<Vector3[]> parts) => parts[Part][A] * WA + parts[Part][B] * WB + parts[Part][C] * WC;
}

/// <summary>
/// Likeness fitting: move a head so its facial landmarks match where a front and a side photo put them.
/// Photos are aligned to the head by scale and offset only, so the head keeps its size and position and only
/// proportions change. A smooth 3D field (biharmonic RBF) carries the landmark moves to every vertex of every
/// part, so eyeballs, teeth and hair follow; the neck and the back of the head are held in place.
/// Mesh coordinates: +X = viewer's right in the front view, +Y up, +Z toward the camera.
/// </summary>
public static class FaceFit
{
    // Measured on the stock atlas layout; jaw angles from the mesh (same UV on 10942, 21449 and 1188). Pairs: "left"/"right" as seen
    // in the front photo. The side view is taken from the right (+X), so right-hand landmarks carry the side flag.
    public static readonly FaceLandmark[] Landmarks =
    {
        new("Forehead",            new(0.500f, 0.213f), true,  true,  -1),
        new("Brow centre",         new(0.500f, 0.301f), true,  true,  -1),
        new("Nose bridge",         new(0.500f, 0.345f), false, true,  -1),
        new("Nose tip",            new(0.500f, 0.442f), true,  true,  -1),
        new("Under the nose",      new(0.500f, 0.475f), true,  true,  -1),
        new("Upper lip",           new(0.500f, 0.520f), true,  true,  -1),
        new("Lower lip",           new(0.500f, 0.555f), true,  true,  -1),
        new("Chin front",          new(0.500f, 0.594f), true,  true,  -1),
        new("Chin bottom",         new(0.500f, 0.657f), true,  true,  -1),
        new("Left eye, outer",     new(0.334f, 0.335f), true,  false, 10),
        new("Right eye, outer",    new(0.666f, 0.335f), true,  true,  9),
        new("Left eye, inner",     new(0.436f, 0.337f), true,  false, 12),
        new("Right eye, inner",    new(0.564f, 0.337f), true,  false, 11),
        new("Left brow, outer",    new(0.324f, 0.300f), true,  false, 14),
        new("Right brow, outer",   new(0.676f, 0.300f), true,  false, 13),
        new("Left nose wing",      new(0.427f, 0.462f), true,  false, 16),
        new("Right nose wing",     new(0.573f, 0.462f), true,  true,  15),
        new("Left mouth corner",   new(0.404f, 0.537f), true,  false, 18),
        new("Right mouth corner",  new(0.596f, 0.537f), true,  true,  17),
        new("Left cheekbone",      new(0.314f, 0.408f), true,  false, 20),
        new("Right cheekbone",     new(0.686f, 0.408f), true,  false, 19),
        new("Left jaw angle",      new(0.271f, 0.676f), true,  false, 22),
        new("Right jaw angle",     new(0.729f, 0.676f), true,  true,  21),
        new("Right ear",           new(0.803f, 0.359f), false, true,  -1),
    };

    /// <summary>Finds the mesh point drawn at an atlas UV: the triangle containing it, else the nearest one.</summary>
    public static MeshAnchor? AnchorAtUv(IReadOnlyList<ModelPart> parts, IEnumerable<int> candidateParts, Vector2 uv)
    {
        MeshAnchor? best = null; float bestDistance = float.MaxValue;
        foreach (int p in candidateParts)
        {
            var part = parts[p];
            for (int t = 0; t + 2 < part.Indices.Length; t += 3)
            {
                int a = part.Indices[t], b = part.Indices[t + 1], c = part.Indices[t + 2];
                var (wa, wb, wc) = Barycentric(uv, part.Uvs[a], part.Uvs[b], part.Uvs[c]);
                if (!float.IsFinite(wa)) continue;
                float outside = Math.Max(0, -Math.Min(wa, Math.Min(wb, wc)));
                float distance = outside > 0 ? outside : 0;
                if (distance < bestDistance)
                {
                    float ca = Math.Max(wa, 0), cb = Math.Max(wb, 0), cc = Math.Max(wc, 0), sum = ca + cb + cc; // clamp onto the triangle
                    best = new MeshAnchor(p, a, b, c, ca / sum, cb / sum, cc / sum); bestDistance = distance;
                    if (distance == 0) return best;
                }
            }
        }
        return best;
    }

    private static (float, float, float) Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(det) < 1e-12f) return (float.NaN, 0, 0);
        float wa = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / det;
        float wb = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / det;
        return (wa, wb, 1 - wa - wb);
    }

    /// <summary>
    /// Target 3D position for every landmark (null = no information). Front photo points give X and Y, side
    /// photo points give Z and Y (Y averaged when both exist). Photo points are in pixels (y down); each photo
    /// is aligned to the head's own landmarks by a least-squares scale and offset, so only proportions change.
    /// The side photo may face either way. <paramref name="symmetric"/> mirrors left/right moves into one
    /// symmetric change (photos are rarely perfectly straight) and keeps midline points on the midline.
    /// </summary>
    /// <param name="notes">Receives a short reason for each photo view that was not used (too few or bunched markers).</param>
    public static Vector3?[] Targets(Vector3[] anchors, Vector2?[] front, Vector2?[] side, bool symmetric, float strength, ICollection<string>? notes = null)
    {
        int n = Landmarks.Length;
        if (anchors.Length != n || front.Length != n || side.Length != n) throw new ArgumentException("One value per landmark is required.");
        var x = new float?[n]; var yFront = new float?[n]; var ySide = new float?[n]; var z = new float?[n];
        var f = Enumerable.Range(0, n).Where(i => front[i] is not null && Landmarks[i].Front).ToArray();
        if (AlignView("front", f, i => new Vector2(front[i]!.Value.X, -front[i]!.Value.Y), i => new Vector2(anchors[i].X, anchors[i].Y),
                Enumerable.Range(0, n).Where(i => Landmarks[i].Front), mirrorAllowed: false, notes) is { } fv)
            foreach (int i in f) { var m = new Vector2(front[i]!.Value.X, -front[i]!.Value.Y) * fv.Scale + fv.Offset; x[i] = m.X; yFront[i] = m.Y; }
        var s = Enumerable.Range(0, n).Where(i => side[i] is not null && Landmarks[i].Side).ToArray();
        // A profile may face either way: both are tried and the better fit decides, so any placed markers work.
        if (AlignView("side", s, i => new Vector2(side[i]!.Value.X, -side[i]!.Value.Y), i => new Vector2(anchors[i].Z, anchors[i].Y),
                Enumerable.Range(0, n).Where(i => Landmarks[i].Side), mirrorAllowed: true, notes) is { } sv)
            foreach (int i in s) { var m = new Vector2(sv.Facing * side[i]!.Value.X, -side[i]!.Value.Y) * sv.Scale + sv.Offset; z[i] = m.X; ySide[i] = m.Y; }
        var move = new Vector3?[n];
        for (int i = 0; i < n; i++)
        {
            if (x[i] is null && z[i] is null) continue;
            float? y = yFront[i] is { } a && ySide[i] is { } b ? (a + b) / 2 : yFront[i] ?? ySide[i];
            move[i] = new Vector3(x[i] ?? anchors[i].X, y ?? anchors[i].Y, z[i] ?? anchors[i].Z) - anchors[i];
        }
        if (symmetric)
        {
            var mirrored = (Vector3?[])move.Clone();
            for (int i = 0; i < n; i++)
            {
                int m = Landmarks[i].Mirror;
                if (m < 0) { if (move[i] is { } d) mirrored[i] = d with { X = 0 }; continue; }
                var own = move[i]; var other = move[m] is { } o ? new Vector3(-o.X, o.Y, o.Z) : (Vector3?)null;
                // Side-only information (Z) exists for one side; the mirror shares it.
                if (own is { } a && other is { } b)
                    mirrored[i] = new Vector3((a.X + b.X) / 2, (a.Y + b.Y) / 2, z[i] is not null && z[m] is null ? a.Z : z[m] is not null && z[i] is null ? b.Z : (a.Z + b.Z) / 2);
                else mirrored[i] = own ?? other;
            }
            move = mirrored;
        }
        return Enumerable.Range(0, n).Select(i => move[i] is { } d ? anchors[i] + d * Math.Clamp(strength, 0, 1.5f) : (Vector3?)null).ToArray();
    }

    /// <summary>
    /// Aligns one photo view to the head, or returns null (with a note) when its placed markers cannot fix the
    /// photo's scale: fewer than three, or bunched together (spanning under a third of that view's landmarks on the
    /// head). Bunched markers, e.g. only the lips, would otherwise give a wild scale and warp the whole head.
    /// </summary>
    private static (float Scale, Vector2 Offset, float Facing)? AlignView(string view, int[] placed, Func<int, Vector2> photo, Func<int, Vector2> head,
        IEnumerable<int> viewLandmarks, bool mirrorAllowed, ICollection<string>? notes)
    {
        if (placed.Length == 0) return null;
        static float Extent(IEnumerable<Vector2> points) { var p = points.ToArray(); var min = p.Aggregate(new Vector2(float.MaxValue), Vector2.Min); var max = p.Aggregate(new Vector2(float.MinValue), Vector2.Max); return Math.Max(max.X - min.X, max.Y - min.Y); }
        if (placed.Length < 3 || Extent(placed.Select(head)) < Extent(viewLandmarks.Select(head)) / 3)
        {
            notes?.Add($"Place more {view} markers, spread from the forehead to the chin, before the {view} photo is used.");
            return null;
        }
        (float Scale, Vector2 Offset, float Facing, float Error)? best = null;
        foreach (float facing in mirrorAllowed ? new[] { 1f, -1f } : new[] { 1f })
        {
            var from = placed.Select(i => photo(i) * new Vector2(facing, 1)).ToArray(); var to = placed.Select(head).ToArray();
            if (TryAlign(from, to) is not { } a || a.Scale <= 0) continue;
            float error = from.Zip(to).Sum(z => Vector2.DistanceSquared(z.First * a.Scale + a.Offset, z.Second));
            if (best is null || error < best.Value.Error) best = (a.Scale, a.Offset, facing, error);
        }
        if (best is { } b) return (b.Scale, b.Offset, b.Facing);
        notes?.Add($"The {view} photo markers do not match the head's layout; check that each one sits on its own feature.");
        return null;
    }

    private static (float Scale, Vector2 Offset)? TryAlign(Vector2[] from, Vector2[] to)
    {
        var cf = from.Aggregate(Vector2.Zero, (s, v) => s + v) / from.Length;
        var ct = to.Aggregate(Vector2.Zero, (s, v) => s + v) / to.Length;
        float num = 0, den = 0;
        for (int i = 0; i < from.Length; i++) { num += Vector2.Dot(from[i] - cf, to[i] - ct); den += (from[i] - cf).LengthSquared(); }
        if (den < 1e-9f) return null;
        float scale = num / den;
        return (scale, ct - cf * scale);
    }

    /// <summary>Least-squares uniform scale and offset taking <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static (float Scale, Vector2 Offset) Align(Vector2[] from, Vector2[] to)
    {
        var cf = from.Aggregate(Vector2.Zero, (s, v) => s + v) / from.Length;
        var ct = to.Aggregate(Vector2.Zero, (s, v) => s + v) / to.Length;
        float num = 0, den = 0;
        for (int i = 0; i < from.Length; i++) { num += Vector2.Dot(from[i] - cf, to[i] - ct); den += (from[i] - cf).LengthSquared(); }
        if (den < 1e-9f) throw new ArgumentException("Photo landmarks must be spread out.");
        float scale = num / den;
        if (!(scale > 0)) throw new ArgumentException("Photo landmarks are mirrored or misplaced relative to the head.");
        return (scale, ct - cf * scale);
    }

    /// <summary>
    /// Smooth displacement field through the control points (biharmonic RBF, φ(r) = r, plus an affine term).
    /// Points in <paramref name="fixedPoints"/> are held still.
    /// </summary>
    public static Func<Vector3, Vector3> Deformation(IReadOnlyList<Vector3> from, IReadOnlyList<Vector3> to, IEnumerable<Vector3> fixedPoints)
    {
        var points = from.Concat(fixedPoints).ToArray();
        var moves = from.Zip(to, (a, b) => b - a).Concat(Enumerable.Repeat(Vector3.Zero, points.Length - from.Count)).ToArray();
        // Affine term (constant + x, y, z) when the points span 3D; points all in one plane make it singular, so then
        // only a constant offset is used.
        try { return Solve(points, moves, affine: true); }
        catch (ArgumentException) { return Solve(points, moves, affine: false); }
    }

    private static Func<Vector3, Vector3> Solve(Vector3[] points, Vector3[] moves, bool affine)
    {
        int n = points.Length, terms = affine ? 4 : 1, size = n + terms;
        var matrix = new double[size, size];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) matrix[i, j] = Vector3.Distance(points[i], points[j]);
            matrix[i, n] = matrix[n, i] = 1;
            if (affine) { matrix[i, n + 1] = matrix[n + 1, i] = points[i].X; matrix[i, n + 2] = matrix[n + 2, i] = points[i].Y; matrix[i, n + 3] = matrix[n + 3, i] = points[i].Z; }
        }
        double[] Column(Func<Vector3, float> axis) => LinearSystem.Solve((double[,])matrix.Clone(), moves.Select(m => (double)axis(m)).Concat(new double[terms]).ToArray());
        var wx = Column(m => m.X); var wy = Column(m => m.Y); var wz = Column(m => m.Z);
        double Affine(double[] w, Vector3 p) => affine ? w[n] + w[n + 1] * p.X + w[n + 2] * p.Y + w[n + 3] * p.Z : w[n];
        return p =>
        {
            double dx = Affine(wx, p), dy = Affine(wy, p), dz = Affine(wz, p);
            for (int i = 0; i < n; i++) { double r = Vector3.Distance(p, points[i]); dx += wx[i] * r; dy += wy[i] * r; dz += wz[i] * r; }
            return p + new Vector3((float)dx, (float)dy, (float)dz);
        };
    }

    /// <summary>
    /// Fits every part: landmarks move to their targets, the neck (below <paramref name="neckY"/>) and the back of
    /// the head stay put. Returns new positions per part; vertex order and counts are unchanged.
    /// </summary>
    public static Vector3[][] Fit(IReadOnlyList<Vector3[]> parts, MeshAnchor?[] anchors, Vector3?[] targets, float neckY)
    {
        var from = new List<Vector3>(); var to = new List<Vector3>();
        for (int i = 0; i < anchors.Length; i++)
            if (anchors[i] is { } a && targets[i] is { } t) { from.Add(a.Position(parts)); to.Add(t); }
        if (from.Count == 0) return parts.Select(p => p.ToArray()).ToArray();
        var all = parts.SelectMany(p => p).ToArray();
        var min = all.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var max = all.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        float backZ = min.Z + (max.Z - min.Z) * 0.35f;
        // ponytail: ~40 evenly picked hold points, not every vertex; enough to pin the neck and skull back.
        float clearance = Vector3.Distance(min, max) * 0.12f; // holds must not sit on a landmark (the ear is near the back third)
        var held = all.Where(v => (v.Y <= neckY || v.Z <= backZ) && from.All(f => Vector3.Distance(f, v) > clearance)).ToArray();
        var holds = held.Where((_, i) => i % Math.Max(1, held.Length / 40) == 0).Distinct().ToArray();
        var field = Deformation(from, to, holds);
        return parts.Select(p => p.Select(v => v.Y <= neckY ? v : field(v)).ToArray()).ToArray();
    }
}
