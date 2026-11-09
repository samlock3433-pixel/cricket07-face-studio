using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class FaceFitTests
{
    [Fact]
    public void AnchorFollowsTheTriangleDrawnAtThatUv()
    {
        var part = new ModelPart("head", new[] { Vector3.Zero, new Vector3(2, 0, 0), new Vector3(0, 2, 1) }, new Vector3[3],
            new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY }, new[] { 0, 1, 2 }, "face.png", "");
        var anchor = FaceFit.AnchorAtUv(new[] { part }, new[] { 0 }, new Vector2(0.25f, 0.5f))!.Value;
        var parts = new[] { part.Positions };
        Assert.True(Vector3.Distance(new Vector3(0.5f, 1, 0.5f), anchor.Position(parts)) < 1e-5f);
        parts[0] = parts[0].Select(v => v + Vector3.UnitZ).ToArray(); // sculpting moves the anchor with the surface
        Assert.True(Vector3.Distance(new Vector3(0.5f, 1, 1.5f), anchor.Position(parts)) < 1e-5f);
        var outside = FaceFit.AnchorAtUv(new[] { part }, new[] { 0 }, new Vector2(2, 2))!.Value; // nearest triangle, clamped onto it
        Assert.True(outside.WA >= 0 && outside.WB >= 0 && outside.WC >= 0);
    }

    [Fact]
    public void DeformationHitsTargetsAndHoldsFixedPoints()
    {
        var from = new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1) };
        var to = new[] { new Vector3(0, 0.3f, 1.2f), new Vector3(1.1f, 0, 1) };
        var held = new[] { new Vector3(0, -3, 0), new Vector3(2, -3, 0), new Vector3(0, 0, -3), new Vector3(1, 3, -3) };
        var field = FaceFit.Deformation(from, to, held);
        for (int i = 0; i < 2; i++) Assert.True(Vector3.Distance(to[i], field(from[i])) < 1e-4f);
        foreach (var h in held) Assert.True(Vector3.Distance(h, field(h)) < 1e-4f);
        var between = field(new Vector3(0.5f, 0, 1));
        Assert.True(between.Y > 0 && between.Y < 0.3f); // smooth in between
    }

    [Fact]
    public void TargetsChangeProportionsNotSizeAndHandleEitherSideFacing()
    {
        int n = FaceFit.Landmarks.Length;
        // A plausible head: landmark positions from the atlas layout, bulged into a face.
        var anchors = FaceFit.Landmarks.Select(l => new Vector3((l.Uv.X - 0.5f) * 20, (0.5f - l.Uv.Y) * 20, 6 - MathF.Abs(l.Uv.X - 0.5f) * 12)).ToArray();
        Vector2?[] FrontPhoto(Func<int, Vector3, Vector3> edit) => Enumerable.Range(0, n).Select(i => FaceFit.Landmarks[i].Front ? (Vector2?)(new Vector2(edit(i, anchors[i]).X, -edit(i, anchors[i]).Y) * 13 + new Vector2(400, 300)) : null).ToArray();
        Vector2?[] SidePhoto(Func<int, Vector3, Vector3> edit, float facing) => Enumerable.Range(0, n).Select(i => FaceFit.Landmarks[i].Side ? (Vector2?)(new Vector2(facing * edit(i, anchors[i]).Z, -edit(i, anchors[i]).Y) * 9 + new Vector2(250, 260)) : null).ToArray();

        // Photo of the same head, at a different scale and position: nothing should move.
        var same = FaceFit.Targets(anchors, FrontPhoto((_, a) => a), SidePhoto((_, a) => a, 1), false, 1);
        for (int i = 0; i < n; i++) Assert.True(Vector3.Distance(anchors[i], same[i]!.Value) < 1e-3f, FaceFit.Landmarks[i].Name);

        // Longer nose (tip further forward) in the side photo; side photo facing the other way.
        var longNose = FaceFit.Targets(anchors, FrontPhoto((_, a) => a), SidePhoto((i, a) => i == 3 ? a + new Vector3(0, 0, 2) : a, -1), true, 1);
        Assert.InRange(longNose[3]!.Value.Z - anchors[3].Z, 1.5f, 2.2f);
        Assert.True(Math.Abs(longNose[0]!.Value.Z - anchors[0].Z) < 0.3f); // forehead barely moves

        // Wider eyes only on one side of the photo: symmetry spreads it to both, midline stays on the midline.
        var wide = FaceFit.Targets(anchors, FrontPhoto((i, a) => i == 10 ? a + new Vector3(1, 0, 0) : a), SidePhoto((_, a) => a, 1), true, 1);
        Assert.True(wide[10]!.Value.X > anchors[10].X && wide[9]!.Value.X < anchors[9].X);
        Assert.Equal(0, wide[3]!.Value.X - anchors[3].X, 3);
    }

    [Fact]
    public void PartialSideMarkersNeverErrorAndFacingIsFoundFromAnyMarkers()
    {
        int n = FaceFit.Landmarks.Length;
        // A real profile is not flat: nose forward, then lips, chin front; forehead and chin bottom further back.
        var profile = new Dictionary<int, float> { [2] = 0.6f, [3] = 2.2f, [4] = 1.3f, [5] = 1.0f, [6] = 0.9f, [7] = 0.5f, [8] = -0.6f };
        var anchors = FaceFit.Landmarks.Select((l, i) => new Vector3((l.Uv.X - 0.5f) * 20, (0.5f - l.Uv.Y) * 20, 6 - MathF.Abs(l.Uv.X - 0.5f) * 12 + profile.GetValueOrDefault(i))).ToArray();
        Vector2?[] Side(float facing, Func<int, bool> placed, Func<int, Vector3, Vector3> edit) =>
            Enumerable.Range(0, n).Select(i => FaceFit.Landmarks[i].Side && placed(i) ? (Vector2?)(new Vector2(facing * edit(i, anchors[i]).Z, -edit(i, anchors[i]).Y) * 9 + new Vector2(250, 260)) : null).ToArray();
        var noFront = new Vector2?[n];

        // Only the lips and chin placed (no nose or ear), photo facing left: used, no mirror error.
        // Lips to chin bottom are too bunched to fix the scale (under a third of the profile), so add the forehead.
        foreach (float facing in new[] { -1f, 1f })
        {
            var notes = new List<string>();
            var chinForward = FaceFit.Targets(anchors, noFront, Side(facing, i => i is 0 or 5 or 6 or 7 or 8, (i, a) => i == 7 ? a + new Vector3(0, 0, 1) : a), true, 1, notes);
            Assert.Empty(notes);
            Assert.InRange(chinForward[7]!.Value.Z - anchors[7].Z, 0.6f, 1.3f); // chin front came forward whichever way the photo faces
        }

        // Only the lips placed: too bunched to fix the photo's scale, so the side photo is skipped with a note, not an error.
        var bunchedNotes = new List<string>();
        var bunched = FaceFit.Targets(anchors, noFront, Side(-1, i => i is 5 or 6 or 18, (_, a) => a), true, 1, bunchedNotes);
        Assert.All(bunched, t => Assert.Null(t));
        Assert.Single(bunchedNotes);
    }

    [Fact]
    public void FitMovesLandmarksAndLeavesTheNeck()
    {
        var head = new List<Vector3>();
        for (int y = -10; y <= 10; y++) for (int x = -6; x <= 6; x++) head.Add(new Vector3(x, y, 6 - 0.1f * x * x));
        for (int y = -10; y <= 10; y++) for (int x = -6; x <= 6; x++) head.Add(new Vector3(x, y, -6)); // back of the head
        var parts = new[] { head.ToArray() };
        int nose = head.FindIndex(v => v.X == 0 && v.Y == 0 && v.Z > 0);
        var anchors = new MeshAnchor?[FaceFit.Landmarks.Length]; anchors[3] = new MeshAnchor(0, nose, nose, nose, 1, 0, 0);
        var targets = new Vector3?[FaceFit.Landmarks.Length]; targets[3] = head[nose] + new Vector3(0, 0, 1.5f);
        var fitted = FaceFit.Fit(parts, anchors, targets, -8);
        Assert.True(Vector3.Distance(targets[3]!.Value, fitted[0][nose]) < 1e-3f);
        for (int i = 0; i < head.Count; i++) if (head[i].Y <= -8) Assert.Equal(head[i], fitted[0][i]);
        Assert.True(fitted[0].Where((v, i) => head[i].Z < -5).Max(v => Math.Abs(v.Z + 6)) < 0.2f); // back of the head stays
    }
}
