using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class ShapeTransferTests
{
    // A "head" sheet over the face-atlas UV square: position = (u, v) mapped to x/y, z = depth(u, v).
    private static (ModelPart Part, Vector3[] Positions) Sheet(int n, Func<float, float, float> depth)
    {
        var uvs = new List<Vector2>(); var pos = new List<Vector3>(); var idx = new List<int>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++)
        {
            float u = (float)i / n, v = (float)j / n;
            uvs.Add(new Vector2(u, v)); pos.Add(new Vector3((u - 0.5f) * 20, 108 - v * 20, depth(u, v)));
        }
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            idx.AddRange(new[] { a, b, c, b, d, c });
        }
        var p = pos.ToArray();
        return (new ModelPart("head", p, new Vector3[p.Length], uvs.ToArray(), idx.ToArray(), "face.png", ""), p);
    }

    [Fact]
    public void TemplateTakesTheSourceShapeAndHairFollowsTheScalp()
    {
        Func<float, float, float> sourceDepth = (u, v) => 4 + 2 * MathF.Sin(u * 3) + v;   // the player's head
        var (source, sourcePos) = Sheet(40, sourceDepth);
        var (target, targetPos) = Sheet(13, (_, _) => 4);                                   // a template with a flat face
        // Hair: a few points floating 1 unit in front of the template's upper head (top of the atlas = top of the head).
        var hairPos = new[] { new Vector3(0, 106, 5), new Vector3(-3, 105, 5), new Vector3(3, 105.5f, 5) };
        var hair = new ModelPart("hair", hairPos, new Vector3[3], new Vector2[3], new[] { 0, 1, 2 }, "hair.png", "");

        var result = ShapeTransfer.Transfer(new[] { source }, new[] { sourcePos }, new[] { 0 },
            new[] { target, hair }, new[] { targetPos, hairPos }, new[] { 0 }, neckY: 89, neckBlend: 2);

        for (int i = 0; i < targetPos.Length; i++)
        {
            var uv = target.Uvs[i]; var y = targetPos[i].Y;
            if (uv.Y > 0.88f && (uv.X < 0.32f || uv.X > 0.68f)) continue;             // eye/teeth pictures are not transferred directly
            if (y <= 91) continue;                                                      // neck blend zone
            Assert.True(MathF.Abs(result.Positions[0][i].Z - sourceDepth(uv.X, uv.Y)) < 0.05f, $"vertex {i} at {uv}");
        }
        for (int i = 0; i < 3; i++)                                                    // hair moved with the scalp under it
        {
            float scalpBefore = 4, scalpAfter = sourceDepth((hairPos[i].X / 20) + 0.5f, (108 - hairPos[i].Y) / 20);
            Assert.InRange(result.Positions[1][i].Z - hairPos[i].Z, scalpAfter - scalpBefore - 0.3f, scalpAfter - scalpBefore + 0.3f);
        }
        Assert.True(result.Transferred > 0 && result.Followed >= 3);
        for (int i = 0; i < targetPos.Length; i++) if (targetPos[i].Y <= 89) Assert.Equal(targetPos[i], result.Positions[0][i]); // neck kept
    }
}
