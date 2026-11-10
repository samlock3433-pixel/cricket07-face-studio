using System.Numerics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cricket07.FaceStudio.Core;

/// <summary>478 face-mesh points found in a photo (pixel coordinates) and the model's face-presence score.</summary>
public sealed record FaceDetection(Vector2[] Points, float Presence)
{
    /// <summary>The model's depth for each point, in photo pixels (smaller = nearer the camera). Rougher than x and y.</summary>
    public float[] Depth { get; init; } = Array.Empty<float>();
    /// <summary>The face points (without the irises) in face space: x right, y up, z toward the camera, photo pixels.</summary>
    public Vector3[] FaceSpace() => Enumerable.Range(0, DenseFit.PointCount).Select(i => new Vector3(Points[i].X, -Points[i].Y, -Depth[i])).ToArray();
}

/// <summary>
/// Finds facial landmarks in a front photo with Google's MediaPipe Face Mesh V2 landmark model (ONNX, Apache 2.0,
/// see vendor/facemesh/README.md). The model expects a square crop around one face with level eyes, so this tries
/// a few centre crops, keeps the most confident, then re-crops twice around the face it found, levelled by the
/// eye line. Works for straight-on and moderately turned faces; a true profile is out of its range.
/// </summary>
public sealed class FaceLandmarker : IDisposable
{
    public const string ModelSha256 = "F38C3321CEFFBC9E95103480AD38CC3F52E7E1BDE2BCEE7CD9355D0B9138AC0C";
    private const int Size = 256;
    private readonly InferenceSession _session;

    /// <summary>
    /// MediaPipe mesh index for each <see cref="FaceFit.Landmarks"/> entry seen in a front photo (-1 = side-only).
    /// Left/right as seen in the photo. Chosen as the mesh point nearest each landmark's head anchor on rendered
    /// stock and custom faces (10942, 10942 + 512 texture, gill + 512 texture), keeping MediaPipe's symmetric pairs.
    /// </summary>
    public static readonly int[] MeshIndex =
    {
        151, 9, -1, 4, 94, 0, 16, 18, 199, // forehead, brow centre, (nose bridge), nose tip, under nose, upper lip, lower lip, chin front, chin bottom
        247, 467, 173, 398,                 // eye outer L/R, eye inner L/R
        46, 276,                            // brow outer L/R
        64, 294,                            // nose wing L/R
        61, 291,                            // mouth corner L/R
        116, 345,                           // cheekbone L/R
        136, 365,                           // jaw angle L/R
        -1,                                 // (ear: side only)
    };

    public FaceLandmarker(string modelPath)
    {
        using (var stream = File.OpenRead(modelPath))
            if (Convert.ToHexString(SHA256.HashData(stream)) != ModelSha256)
                throw new InvalidDataException("The face landmark model does not match its recorded checksum (vendor/facemesh/README.md).");
        _session = new InferenceSession(modelPath);
    }

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// Detects one face, or null when the model is not confident there is a face. <paramref name="hint"/> (centre,
    /// size in pixels) is tried first, e.g. from markers the user dragged near the face.
    /// </summary>
    public FaceDetection? Detect(byte[] bgra, int width, int height, (Vector2 Centre, float Size)? hint = null)
    {
        if (bgra.Length != checked(width * height * 4)) throw new InvalidDataException("Pixel buffer does not match its dimensions.");
        float side = Math.Min(width, height);
        // Crops from the whole frame down to a fifth of it, overlapping by half, so small or off-centre faces are found too.
        var crops = new List<(Vector2, float)>();
        if (hint is { } h) crops.Add((h.Centre, h.Size));
        foreach (float f in new[] { 1f, 0.7f, 0.5f, 0.35f, 0.25f })
        {
            float size = side * f, step = size / 2;
            for (float y = size / 2; y <= height - size / 2 + 1; y += step)
            for (float x = size / 2; x <= width - size / 2 + 1; x += step) crops.Add((new Vector2(x, y), size));
        }
        FaceDetection? best = null;
        foreach (var (centre, size) in crops)
        {
            var d = Run(bgra, width, height, centre, size, 0);
            if (best is null || d.Presence > best.Presence) best = d;
            if (best.Presence > 0.95f) break; // confident enough; refinement follows
        }
        for (int pass = 0; pass < 2 && best!.Presence > 0.3f; pass++)
        {
            var p = best.Points;
            var min = p.Aggregate(new Vector2(float.MaxValue), Vector2.Min); var max = p.Aggregate(new Vector2(float.MinValue), Vector2.Max);
            var eyes = p[263] - p[33];
            var refined = Run(bgra, width, height, (min + max) / 2, Math.Max(max.X - min.X, max.Y - min.Y) * 1.5f, MathF.Atan2(eyes.Y, eyes.X));
            if (refined.Presence >= best.Presence * 0.9f) best = refined;
        }
        return best!.Presence >= 0.5f ? best : null;
    }

    /// <summary>One model pass on a square crop (centre, side length, roll in radians) of the photo.</summary>
    private FaceDetection Run(byte[] bgra, int width, int height, Vector2 centre, float side, float roll)
    {
        var tensor = new DenseTensor<float>(new[] { 1, Size, Size, 3 });
        float cos = MathF.Cos(roll), sin = MathF.Sin(roll), step = side / Size;
        Vector2 ToPhoto(float u, float v) { float dx = (u - Size / 2f) * step, dy = (v - Size / 2f) * step; return centre + new Vector2(dx * cos - dy * sin, dx * sin + dy * cos); }
        for (int v = 0; v < Size; v++)
        for (int u = 0; u < Size; u++)
        {
            var s = ToPhoto(u + 0.5f, v + 0.5f);
            int x = (int)MathF.Floor(s.X - 0.5f), y = (int)MathF.Floor(s.Y - 0.5f); float fx = s.X - 0.5f - x, fy = s.Y - 0.5f - y;
            for (int c = 0; c < 3; c++)
            {
                float Sample(int sx, int sy) => sx < 0 || sy < 0 || sx >= width || sy >= height ? 0 : bgra[(sy * width + sx) * 4 + 2 - c] / 255f; // BGRA → RGB
                tensor[0, v, u, c] = (Sample(x, y) * (1 - fx) + Sample(x + 1, y) * fx) * (1 - fy) + (Sample(x, y + 1) * (1 - fx) + Sample(x + 1, y + 1) * fx) * fy;
            }
        }
        string input = _session.InputMetadata.Keys.First();
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(input, tensor) });
        var outputs = results.ToDictionary(r => r.Name, r => r.AsEnumerable<float>().ToArray());
        // Output names from the model card: Identity = 478 × (x, y, z) in crop pixels, Identity_1 = face-presence logit.
        if (!outputs.TryGetValue("Identity", out var landmarks) || landmarks.Length != 478 * 3 || !outputs.TryGetValue("Identity_1", out var presence))
            throw new InvalidDataException("Unexpected face landmark model outputs.");
        float logit = presence[0];
        var points = Enumerable.Range(0, 478).Select(i => ToPhoto(landmarks[i * 3], landmarks[i * 3 + 1])).ToArray();
        return new FaceDetection(points, 1 / (1 + MathF.Exp(-logit))) { Depth = Enumerable.Range(0, 478).Select(i => landmarks[i * 3 + 2] * step).ToArray() };
    }

    /// <summary>Front-photo marker positions for <see cref="FaceFit.Landmarks"/> (null where the model has no matching point).</summary>
    public static Vector2?[] FrontMarkers(FaceDetection detection) =>
        MeshIndex.Select(i => i >= 0 ? detection.Points[i] : (Vector2?)null).ToArray();
}
