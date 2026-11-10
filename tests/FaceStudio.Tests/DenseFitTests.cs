using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class DenseFitTests
{
    private static Vector3[] Cloud()
    {
        var random = new Random(7);
        return Enumerable.Range(0, 60).Select(_ => new Vector3((float)random.NextDouble() * 200, (float)random.NextDouble() * 260, (float)random.NextDouble() * 80)).ToArray();
    }
    private static Vector3 Turn(Vector3 p) => Vector3.Transform(p, Quaternion.CreateFromYawPitchRoll(0.25f, -0.15f, 0.1f)) * 1.7f + new Vector3(40, -25, 10);

    [Fact]
    public void SimilarityUndoesATurnScaleAndShift()
    {
        var render = Cloud(); var photo = render.Select(Turn).ToArray();
        var align = DenseFit.Similarity(photo, render);
        for (int i = 0; i < render.Length; i++) Assert.True(Vector3.Distance(align(photo[i]), render[i]) < 0.05f);
    }

    [Fact]
    public void ATurnedPhotoOfTheSameFaceMovesNothingAndARealDifferenceMovesThatSpot()
    {
        var render = Cloud(); var head = render.Select(p => (Vector3?)(p * 0.1f)).ToArray(); // 0.1 mesh units per pixel
        var same = DenseFit.Controls(head, render, render.Select(Turn).ToArray(), 0.1f, symmetric: false, mirrorX: 10, strength: 1, depth: 1, count: 60);
        Assert.True(same.From.Zip(same.To, Vector3.Distance).Max() < 0.02f);

        var changed = (Vector3[])render.Clone(); changed[5] += new Vector3(12, -8, 0); // one feature sits elsewhere on the person
        var fit = DenseFit.Controls(head, render, changed.Select(Turn).ToArray(), 0.1f, false, 10, 1, 1, 60);
        int moved = Array.FindIndex(fit.From, p => Vector3.Distance(p, head[5]!.Value) < 1e-4f);
        Assert.True(moved >= 0);
        var move = fit.To[moved] - fit.From[moved];
        Assert.InRange(move.X, 1.0f, 1.3f); Assert.InRange(move.Y, -0.9f, -0.65f); // ~ (12, -8) px x 0.1, less the small share the alignment absorbs
        Assert.True(fit.From.Zip(fit.To, Vector3.Distance).Where((_, i) => i != moved).Max() < 0.15f);
    }
}
