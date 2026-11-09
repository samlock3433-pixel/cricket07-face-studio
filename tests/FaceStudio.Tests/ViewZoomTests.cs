using System.Numerics;
using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class ViewZoomTests
{
    [Fact]
    public void FitCentresContentAndZoomKeepsTheCursorPoint()
    {
        var fit = ViewZoom.Fit(new Vector2(1000, 500), new Vector2(400, 400), 1);
        Assert.Equal(0.4f, fit.Scale, 4);
        Assert.Equal(new Vector2(0, 100), fit.Offset);                 // centred vertically
        var cursor = new Vector2(130, 270);
        var underCursor = fit.ToContent(cursor);
        var zoomed = ViewZoom.ZoomAt(fit, cursor, 3, 0.2f, 6.4f);
        Assert.Equal(1.2f, zoomed.Scale, 4);
        Assert.True(Vector2.Distance(cursor, zoomed.ToScreen(underCursor)) < 1e-3f); // same content point stays under the cursor
        Assert.Equal(6.4f, ViewZoom.ZoomAt(zoomed, cursor, 100, 0.2f, 6.4f).Scale); // clamped
        Assert.Equal(0.2f, ViewZoom.ZoomAt(zoomed, cursor, 0.001f, 0.2f, 6.4f).Scale);
    }
}
