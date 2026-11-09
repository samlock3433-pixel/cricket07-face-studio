using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class HairTests
{
    // A hair "cap": a ring of quads around the Y axis, with a UV seam (vertices duplicated at angle 0 / 360).
    private static (Vector3[] Vertices, int[] Triangles) Cap(bool reversedWinding)
    {
        var v = new List<Vector3>(); var t = new List<int>();
        const int segments = 12;
        for (int ring = 0; ring < 4; ring++)
            for (int s = 0; s <= segments; s++) // s == segments duplicates s == 0 (seam)
            {
                double angle = 2 * Math.PI * (s % segments) / segments;
                v.Add(new Vector3((float)(Math.Cos(angle) * 5), ring * 2f, (float)(Math.Sin(angle) * 5)));
            }
        for (int ring = 0; ring < 3; ring++)
            for (int s = 0; s < segments; s++)
            {
                int a = ring * (segments + 1) + s, b = a + 1, c = a + segments + 1, d = c + 1;
                if (reversedWinding) t.AddRange(new[] { a, c, b, b, c, d }); else t.AddRange(new[] { a, b, c, b, d, c });
            }
        return (v.ToArray(), t.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VolumePushesHairOutwardKeepsSeamsAndRespectsRadiusAndNeckLock(bool reversedWinding)
    {
        var (v, t) = Cap(reversedWinding);
        float Radius(Vector3 p) => MathF.Sqrt(p.X * p.X + p.Z * p.Z);
        var whole = SculptBrush.Inflate(v, t, Vector3.Zero, 0.5f, 1, lockBelowY: 0.5f, whole: true);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i].Y <= 0.5f) Assert.Equal(v[i], whole[i]);                       // neck lock
            else Assert.True(Radius(whole[i]) > Radius(v[i]) + 0.3f, $"vertex {i}");  // more volume, whatever the winding
        }
        for (int ring = 0; ring < 4; ring++) Assert.Equal(whole[ring * 13], whole[ring * 13 + 12]); // seam duplicates move together
        var flatter = SculptBrush.Inflate(v, t, Vector3.Zero, -0.5f, 1, whole: true);
        Assert.True(Radius(flatter[20]) < Radius(v[20]));
        var local = SculptBrush.Inflate(v, t, v[20], 0.5f, 3);                       // brush around one vertex
        Assert.True(Radius(local[20]) > Radius(v[20]) + 0.3f);
        Assert.Equal(v[26], local[26]);                                              // opposite side, outside the radius
    }
}
