using System.Numerics;

namespace Cricket07.FaceStudio.Core;

/// <summary>Uniform scale + offset from content pixels to screen pixels (screen = content × Scale + Offset).</summary>
public readonly record struct ViewTransform(float Scale, Vector2 Offset)
{
    public Vector2 ToScreen(Vector2 content) => content * Scale + Offset;
    public Vector2 ToContent(Vector2 screen) => (screen - Offset) / Scale;
}

/// <summary>Zoom/pan maths for image panels: fit, and zoom about a point that stays under the cursor.</summary>
public static class ViewZoom
{
    /// <summary>Largest scale that shows all of the content (times <paramref name="margin"/>), centred.</summary>
    public static ViewTransform Fit(Vector2 content, Vector2 viewport, float margin = 0.98f)
    {
        if (content.X <= 0 || content.Y <= 0 || viewport.X <= 0 || viewport.Y <= 0) return new ViewTransform(1, Vector2.Zero);
        float scale = Math.Min(viewport.X / content.X, viewport.Y / content.Y) * margin;
        return new ViewTransform(scale, (viewport - content * scale) / 2);
    }

    /// <summary>Scales by <paramref name="factor"/> (clamped to the limits) keeping the content point under <paramref name="screenPoint"/> fixed.</summary>
    public static ViewTransform ZoomAt(ViewTransform view, Vector2 screenPoint, float factor, float minScale, float maxScale)
    {
        float scale = Math.Clamp(view.Scale * factor, minScale, maxScale);
        var content = view.ToContent(screenPoint);
        return new ViewTransform(scale, screenPoint - content * scale);
    }
}
