using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class PhotoTextureTests
{
    private static ModelPart Plane(float z, float size = 1) => new("face",
        new[] { new Vector3(-size, size, z), new Vector3(-size, -size, z), new Vector3(size, -size, z), new Vector3(size, size, z) },
        Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), new[] { Vector2.Zero, Vector2.UnitY, Vector2.One, Vector2.UnitX },
        new[] { 0, 1, 2, 0, 2, 3 }, "face.png", "");

    private static PhotoTextureView Photo(ModelPart plane)
    {
        var pixels = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        { int i = (y * 64 + x) * 4; pixels[i] = (byte)(x * 4); pixels[i + 1] = (byte)(y * 4); pixels[i + 2] = 200; pixels[i + 3] = 255; }
        return new PhotoTextureView(pixels, 64, 64, false, plane.Positions.Select(p => (p, new Vector2(32 + p.X * 28, 32 - p.Y * 28))).ToArray());
    }

    [Fact]
    public void PhotoProjectsToAtlasAndKeepsAlphaAndEyeAndTeethPictures()
    {
        var plane = Plane(1); var photo = Photo(plane); var atlas = Enumerable.Repeat((byte)20, 64 * 64 * 4).ToArray();
        var result = PhotoTexture.Bake(atlas, 64, 64, new[] { plane }, new[] { plane.Positions }, new[] { 0 }, new[] { photo });
        Assert.True(result.PaintedPixels > 2000);
        int center = (32 * 64 + 32) * 4;
        Assert.InRange(result.Pixels[center], 124, 134); Assert.InRange(result.Pixels[center + 1], 124, 134); Assert.Equal(200, result.Pixels[center + 2]);
        for (int i = 0; i < 64 * 64; i++)
        {
            Assert.Equal(atlas[i * 4 + 3], result.Pixels[i * 4 + 3]);
            if (FaceTexture.InPictureStrip(new Vector2((i % 64 + .5f) / 64, (i / 64 + .5f) / 64)))
                Assert.Equal(atlas.AsSpan(i * 4, 4).ToArray(), result.Pixels.AsSpan(i * 4, 4).ToArray());
        }
        Assert.Equal(atlas, PhotoTexture.Bake(atlas, 64, 64, new[] { plane }, new[] { plane.Positions }, new[] { 0 }, new[] { photo }, strength: 0).Pixels);
    }

    [Fact]
    public void OccludingMeshPreventsPhotoPaintingBehindIt()
    {
        var face = Plane(1); var cover = Plane(2, .4f); var atlas = Enumerable.Repeat((byte)20, 64 * 64 * 4).ToArray();
        var result = PhotoTexture.Bake(atlas, 64, 64, new[] { face, cover }, new[] { face.Positions, cover.Positions }, new[] { 0 }, new[] { Photo(face) });
        int center = (32 * 64 + 32) * 4;
        Assert.Equal(20, result.Pixels[center + 2]); Assert.True(result.PaintedPixels > 500);
    }

    [Fact]
    public void SidePhotoSupportsEitherImageDirectionAndTransparency()
    {
        var plane = Plane(1); var photo = Photo(plane);
        var sidePositions = plane.Positions.Select(p => new Vector3(p.Z, p.Y, -p.X)).ToArray();
        var sidePart = plane with { Positions = sidePositions };
        var side = photo with { Side = true, Pins = photo.Pins.Select((pin, i) => (sidePositions[i], pin.Pixel)).ToArray() };
        var atlas = Enumerable.Repeat((byte)20, 64 * 64 * 4).ToArray();
        var result = PhotoTexture.Bake(atlas, 64, 64, new[] { sidePart }, new[] { sidePositions }, new[] { 0 }, new[] { side });
        Assert.Equal(200, result.Pixels[(32 * 64 + 32) * 4 + 2]);
        for (int y = 27; y <= 37; y++) for (int x = 27; x <= 37; x++) side.Pixels[(y * 64 + x) * 4 + 3] = 0;
        result = PhotoTexture.Bake(atlas, 64, 64, new[] { sidePart }, new[] { sidePositions }, new[] { 0 }, new[] { side });
        Assert.Equal(20, result.Pixels[(32 * 64 + 32) * 4 + 2]);
    }

    [Fact]
    public void FrontAndProfilePhotosBlendOnAVisibleCheek()
    {
        var plane = Plane(1);
        var positions = plane.Positions.Select(p => p with { Z = 2 - p.X }).ToArray();
        plane = plane with { Positions = positions };
        var front = Photo(plane);
        var sidePixels = (byte[])front.Pixels.Clone();
        for (int i = 0; i < front.Pixels.Length; i += 4)
        {
            front.Pixels[i] = 0; front.Pixels[i + 1] = 0; front.Pixels[i + 2] = 200;
            sidePixels[i] = 200; sidePixels[i + 1] = 0; sidePixels[i + 2] = 0;
        }
        var side = front with { Side = true, Pixels = sidePixels };
        var atlas = Enumerable.Repeat((byte)20, 64 * 64 * 4).ToArray();
        var result = PhotoTexture.Bake(atlas, 64, 64, new[] { plane }, new[] { positions }, new[] { 0 }, new[] { front, side }, equalize: false);
        int center = (32 * 64 + 32) * 4;
        Assert.InRange(result.Pixels[center], 99, 101);
        Assert.InRange(result.Pixels[center + 2], 99, 101);
    }

    [Fact]
    public void RegistrationTracksTiltAndHandAdjustedMarkersAndRejectsDegeneratePins()
    {
        var plane = Plane(1); var photo = Photo(plane);
        var moved = photo with { Pins = photo.Pins.Select((p, i) => (p.Surface, new Vector2(p.Pixel.X + .2f * p.Pixel.Y, p.Pixel.Y) + (i == 0 ? new Vector2(3, -2) : Vector2.Zero))).ToArray() };
        var project = PhotoTexture.Projection(moved);
        foreach (var pin in moved.Pins) Assert.InRange(Vector2.Distance(project(pin.Surface), pin.Pixel), 0, .01f);
        Assert.Throws<ArgumentException>(() => PhotoTexture.Projection(photo with { Pins = photo.Pins.Select(p => (Vector3.Zero, p.Pixel)).ToArray() }));
        Assert.Throws<ArgumentException>(() => PhotoTexture.Projection(photo with { Pins = photo.Pins.Take(3).ToArray() }));
    }
}
