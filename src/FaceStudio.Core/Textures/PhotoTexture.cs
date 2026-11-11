using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>A front or right-side photograph, registered by surface points and image pixels.</summary>
public sealed record PhotoTextureView(byte[] Pixels, int Width, int Height, bool Side,
    IReadOnlyList<(Vector3 Surface, Vector2 Pixel)> Pins);

/// <summary>Projects registered photos onto the existing UV atlas, blending visible surfaces by viewing angle.</summary>
public static class PhotoTexture
{
    public sealed record Result(byte[] Pixels, int PaintedPixels);

    /// <summary>Least-squares affine camera, with an optional smooth landmark residual for texture registration.</summary>
    public static Func<Vector3, Vector2> Projection(PhotoTextureView view, bool warp = true)
    {
        if (view.Width < 2 || view.Height < 2 || view.Pixels.Length != checked(view.Width * view.Height * 4))
            throw new ArgumentException("Photo pixels do not match the image dimensions.");
        if (view.Pins.Count < 4) throw new ArgumentException("Place at least four photo markers spread across the face.");
        if (view.Pins.Any(p => !float.IsFinite(p.Surface.X) || !float.IsFinite(p.Surface.Y) || !float.IsFinite(p.Surface.Z) || !float.IsFinite(p.Pixel.X) || !float.IsFinite(p.Pixel.Y)))
            throw new ArgumentException("Photo markers must have finite coordinates.");
        Vector2 Plane(Vector3 p) => new(view.Side ? p.Z : p.X, -p.Y);
        var points = view.Pins.Select(p => Plane(p.Surface)).ToArray();
        var centre = points.Aggregate(Vector2.Zero, (a, b) => a + b) / points.Length;
        float scale = points.Max(p => Vector2.Distance(p, centre));
        if (!float.IsFinite(scale) || scale < 1e-5f) throw new ArgumentException("Photo markers must cover distinct facial features.");
        points = points.Select(p => (p - centre) / scale).ToArray();
        var normal = new double[3, 3]; var bx = new double[3]; var by = new double[3];
        for (int i = 0; i < points.Length; i++)
        {
            double[] row = { 1, points[i].X, points[i].Y };
            for (int a = 0; a < 3; a++)
            {
                bx[a] += row[a] * view.Pins[i].Pixel.X; by[a] += row[a] * view.Pins[i].Pixel.Y;
                for (int b = 0; b < 3; b++) normal[a, b] += row[a] * row[b];
            }
        }
        var ax = LinearSystem.Solve((double[,])normal.Clone(), bx);
        var ay = LinearSystem.Solve(normal, by);
        Vector2 Affine(Vector2 p) => new((float)(ax[0] + ax[1] * p.X + ax[2] * p.Y), (float)(ay[0] + ay[1] * p.X + ay[2] * p.Y));
        if (!warp) return p => Affine((Plane(p) - centre) / scale);
        int n = points.Length;
        var system = new double[n + 3, n + 3]; var rx = new double[n + 3]; var ry = new double[n + 3];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) system[i, j] = Kernel(Vector2.DistanceSquared(points[i], points[j]));
            system[i, i] += 1e-6; // coincident seam anchors and noisy marker positions
            system[i, n] = system[n, i] = 1;
            system[i, n + 1] = system[n + 1, i] = points[i].X;
            system[i, n + 2] = system[n + 2, i] = points[i].Y;
            var residual = view.Pins[i].Pixel - Affine(points[i]); rx[i] = residual.X; ry[i] = residual.Y;
        }
        var wx = LinearSystem.Solve((double[,])system.Clone(), rx); var wy = LinearSystem.Solve(system, ry);
        return position =>
        {
            var p = (Plane(position) - centre) / scale; var result = Affine(p);
            double x = wx[n] + wx[n + 1] * p.X + wx[n + 2] * p.Y, y = wy[n] + wy[n + 1] * p.X + wy[n + 2] * p.Y;
            for (int i = 0; i < n; i++) { double k = Kernel(Vector2.DistanceSquared(p, points[i])); x += wx[i] * k; y += wy[i] * k; }
            return result + new Vector2((float)x, (float)y);
        };
    }

    private static double Kernel(float squaredDistance) => squaredDistance < 1e-12f ? 0 : squaredDistance * Math.Log(squaredDistance);

    public static Result Bake(byte[] atlas, int width, int height, IReadOnlyList<ModelPart> parts,
        IReadOnlyList<Vector3[]> positions, IReadOnlyList<int> faceParts, IReadOnlyList<PhotoTextureView> photos,
        float strength = 1, bool equalize = true)
    {
        if (width < 2 || height < 2 || atlas.Length != checked(width * height * 4)) throw new ArgumentException("Atlas dimensions are invalid.");
        if (!float.IsFinite(strength) || strength < 0 || strength > 1) throw new ArgumentOutOfRangeException(nameof(strength));
        if (parts.Count != positions.Count || parts.Where((p, i) => p.Positions.Length != positions[i].Length).Any()) throw new ArgumentException("Mesh positions do not match the parts.");
        if (faceParts.Any(p => p < 0 || p >= parts.Count)) throw new ArgumentException("Face part index is invalid.");
        if (photos.Count == 0) throw new ArgumentException("Load and align a photo before building its texture.");
        var output = (byte[])atlas.Clone(); var colors = new Vector3[width * height]; var weights = new float[colors.Length]; var coverage = new float[colors.Length];
        Vector3? reference = null;
        foreach (var photo in photos)
        {
            var project = Projection(photo);
            var hull = Hull(photo.Pins.Select(p => new Vector2(photo.Side ? p.Surface.Z : p.Surface.X, -p.Surface.Y)));
            float span = hull.Max(p => p.Y) - hull.Min(p => p.Y), feather = Math.Max(span * 0.08f, 1e-4f);
            var mean = photo.Pins.Select(p => Sample(photo, p.Pixel)).Aggregate(Vector3.Zero, (a, b) => a + b) / photo.Pins.Count;
            reference ??= mean;
            var gain = equalize ? Vector3.Clamp((reference.Value + Vector3.One) / (mean + Vector3.One), new Vector3(0.7f), new Vector3(1.4f)) : Vector3.One;
            var visible = new Visibility(parts, positions, photo.Side);
            foreach (int partIndex in faceParts)
            {
                var part = parts[partIndex]; var vertices = positions[partIndex];
                for (int t = 0; t + 2 < part.Indices.Length; t += 3)
                {
                    int ia = part.Indices[t], ib = part.Indices[t + 1], ic = part.Indices[t + 2];
                    var a = vertices[ia]; var b = vertices[ib]; var c = vertices[ic];
                    var normal = Vector3.Cross(b - a, c - a);
                    if (normal.LengthSquared() < 1e-12f) continue;
                    normal = Vector3.Normalize(normal);
                    float facing = photo.Side ? normal.X : normal.Z;
                    if (facing <= 0.12f) continue;
                    Vector2 Texel(int i) => part.Uvs[i] * new Vector2(width, height);
                    var ua = Texel(ia); var ub = Texel(ib); var uc = Texel(ic);
                    Raster(ua, ub, uc, width, height, (x, y, bary) =>
                    {
                        var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                        if (FaceTexture.InPictureStrip(uv)) return;
                        var p = a * bary.X + b * bary.Y + c * bary.Z;
                        float edge = HullDistance(hull, new Vector2(photo.Side ? p.Z : p.X, -p.Y));
                        if (edge <= 0 || !visible.Contains(p)) return;
                        var pixel = project(p);
                        if (!float.IsFinite(pixel.X) || !float.IsFinite(pixel.Y) || pixel.X < 1 || pixel.Y < 1 || pixel.X >= photo.Width - 1 || pixel.Y >= photo.Height - 1) return;
                        float border = Math.Min(Math.Min(pixel.X, photo.Width - 1 - pixel.X), Math.Min(pixel.Y, photo.Height - 1 - pixel.Y));
                        float opacity = photo.Pixels[((int)pixel.Y * photo.Width + (int)pixel.X) * 4 + 3] / 255f;
                        float blend = Math.Clamp(edge / feather, 0, 1) * Math.Clamp(border / 8, 0, 1) * opacity;
                        float weight = facing * facing * blend; int index = y * width + x;
                        colors[index] += Sample(photo, pixel) * gain * weight; weights[index] += weight;
                        coverage[index] = Math.Max(coverage[index], blend * Math.Clamp((facing - 0.12f) / 0.4f, 0, 1));
                    });
                }
            }
        }
        int painted = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] <= 0) continue;
            var original = new Vector3(atlas[i * 4], atlas[i * 4 + 1], atlas[i * 4 + 2]);
            var color = Vector3.Clamp(Vector3.Lerp(original, colors[i] / weights[i], coverage[i] * strength), Vector3.Zero, new Vector3(255));
            output[i * 4] = (byte)MathF.Round(color.X); output[i * 4 + 1] = (byte)MathF.Round(color.Y); output[i * 4 + 2] = (byte)MathF.Round(color.Z); painted++;
        }
        if (painted == 0) throw new ArgumentException("The photos cover no visible face surface. Check the photo markers and head markers.");
        return new Result(output, painted);
    }

    private static Vector3 Sample(PhotoTextureView photo, Vector2 pixel)
    {
        float x = Math.Clamp(pixel.X, 0, photo.Width - 1), y = Math.Clamp(pixel.Y, 0, photo.Height - 1);
        int ix = (int)x, iy = (int)y;
        Vector3 At(int u, int v) { int k = (Math.Min(v, photo.Height - 1) * photo.Width + Math.Min(u, photo.Width - 1)) * 4; return new(photo.Pixels[k], photo.Pixels[k + 1], photo.Pixels[k + 2]); }
        return Vector3.Lerp(Vector3.Lerp(At(ix, iy), At(ix + 1, iy), x - ix), Vector3.Lerp(At(ix, iy + 1), At(ix + 1, iy + 1), x - ix), y - iy);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static Vector2[] Hull(IEnumerable<Vector2> source)
    {
        var points = source.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray(); var hull = new List<Vector2>();
        foreach (var point in points) { while (hull.Count >= 2 && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
        int lower = hull.Count;
        foreach (var point in points.Reverse().Skip(1)) { while (hull.Count > lower && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
        if (hull.Count < 4) throw new ArgumentException("Photo markers must surround an area of the face.");
        return hull.Take(hull.Count - 1).ToArray();
    }
    private static float HullDistance(Vector2[] hull, Vector2 point)
    {
        float distance = float.MaxValue;
        for (int i = 0; i < hull.Length; i++) { var edge = hull[(i + 1) % hull.Length] - hull[i]; distance = Math.Min(distance, Cross(edge, point - hull[i]) / edge.Length()); }
        return distance;
    }

    private static void Raster(Vector2 a, Vector2 b, Vector2 c, int width, int height, Action<int, int, Vector3> visit)
    {
        float det = Cross(b - a, c - a); if (Math.Abs(det) < 1e-8f) return;
        var min = Vector2.Max(Vector2.Zero, Vector2.Min(a, Vector2.Min(b, c))); var max = Vector2.Min(new Vector2(width - 1, height - 1), Vector2.Max(a, Vector2.Max(b, c)));
        for (int y = (int)min.Y; y <= max.Y; y++) for (int x = (int)min.X; x <= max.X; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f); float wb = Cross(p - a, c - a) / det, wc = Cross(b - a, p - a) / det, wa = 1 - wb - wc;
            if (wa >= -1e-5f && wb >= -1e-5f && wc >= -1e-5f) visit(x, y, new Vector3(wa, wb, wc));
        }
    }

    private sealed class Visibility
    {
        private const int Size = 512;
        private readonly float[] _depth = Enumerable.Repeat(float.NegativeInfinity, Size * Size).ToArray();
        private readonly Vector2 _min, _span;
        private readonly bool _side;
        private readonly float _tolerance;
        private Vector2 Plane(Vector3 p) => new(_side ? p.Z : p.X, -p.Y);
        private Vector2 Pixel(Vector3 p) => (Plane(p) - _min) / _span * (Size - 1);
        private float Depth(Vector3 p) => _side ? p.X : p.Z;
        public Visibility(IReadOnlyList<ModelPart> parts, IReadOnlyList<Vector3[]> positions, bool side)
        {
            _side = side; var all = positions.SelectMany(p => p).ToArray();
            _min = all.Select(Plane).Aggregate(new Vector2(float.MaxValue), Vector2.Min);
            _span = Vector2.Max(new Vector2(1e-4f), all.Select(Plane).Aggregate(new Vector2(float.MinValue), Vector2.Max) - _min);
            _tolerance = _span.Length() / Size * 2;
            for (int p = 0; p < parts.Count; p++) for (int t = 0; t + 2 < parts[p].Indices.Length; t += 3)
            {
                var a = positions[p][parts[p].Indices[t]]; var b = positions[p][parts[p].Indices[t + 1]]; var c = positions[p][parts[p].Indices[t + 2]];
                Raster(Pixel(a), Pixel(b), Pixel(c), Size, Size, (x, y, w) => _depth[y * Size + x] = Math.Max(_depth[y * Size + x], Depth(a) * w.X + Depth(b) * w.Y + Depth(c) * w.Z));
            }
        }
        public bool Contains(Vector3 p)
        {
            var pixel = Pixel(p); int x = Math.Clamp((int)pixel.X, 0, Size - 1), y = Math.Clamp((int)pixel.Y, 0, Size - 1);
            return Depth(p) >= _depth[y * Size + x] - _tolerance;
        }
    }
}
