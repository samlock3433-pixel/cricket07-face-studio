using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio.Views;

/// <summary>Painting the face or hair texture directly on the 3D head, and the undo history shared by sculpt and paint.</summary>
public partial class FaceSculptWindow
{
    /// <summary>One undo step: a mesh snapshot, or a texture state (Png null = the original texture).</summary>
    /// <remarks>Pins: the pin layout that goes with that texture state (pin warp), so undo also brings the pins back.</remarks>
    private sealed record UndoStep(Point3D[][]? Mesh, StudioModel? Model = null, int Part = 0, byte[]? Png = null, PinState? Pins = null);
    private sealed class PaintSession
    {
        public required StudioModel Model; public required int Part; public required string Uri;
        public required byte[] Pixels; public required int Width, Height; public required WriteableBitmap Bitmap;
        public byte[]? Source; public byte[]? Before; public Vector2? Last; public Vector2 CloneOffset; public float Radius;
    }
    private PaintSession? _paint;
    private Vector3? _altSampled; // colour before the last Alt sample, put back if Alt turns out to start an orbit
    /// <summary>Slider moved: show the new brush circle (at the cursor, or in the middle of the view as a size preview).</summary>
    private void OnBrushSize(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_paintReady || HeadPaintTool is null) return;
        UpdateBrushRing(Viewport.IsMouseOver ? Mouse.GetPosition(Viewport) : new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    }
    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) { _altSampled = null; e.Handled = true; } // no window-menu focus on Alt
    }
    private (StudioModel Model, string Uri, Vector2 Texel)? _cloneSource;
    private Vector3 _headColour = new(0.20f, 0.13f, 0.08f);
    private bool _paintReady;

    private PaintTool? HeadPaintTool => HeadTool.SelectedItem is ComboBoxItem { Tag: string tag } && tag != "Pins" ? Enum.Parse<PaintTool>(tag) : null;

    private void InitPaint()
    {
        Ui.FillSwatches(HeadSwatches, c => { SetHeadColour(new Vector3(c.R, c.G, c.B) / 255f); if (HeadPaintTool != PaintTool.Paint) HeadTool.SelectedIndex = 1; });
        SetHeadColour(_headColour); _paintReady = true;
    }
    private void SetHeadColour(Vector3 colour)
    {
        _headColour = colour;
        HeadSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(colour.X * 255), (byte)(colour.Y * 255), (byte)(colour.Z * 255)));
    }
    private void OnHeadSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (ColourDialog.Pick(this, _headColour) is not { } colour) return;
        SetHeadColour(colour); if (HeadPaintTool != PaintTool.Paint) HeadTool.SelectedIndex = 1;
    }
    /// <summary>I: takes the colour under the cursor on the head (eyedropper) and switches to Paint. True when the cursor was over the head.</summary>
    internal bool PickUnderCursor(Point viewportPoint)
    {
        if (HitUv(viewportPoint) is not { } hit || _parts[hit.Part].Visual.Material is not DiffuseMaterial { Brush: ImageBrush { ImageSource: BitmapSource texture } }) return false;
        int x = Math.Clamp((int)(hit.Uv.X * texture.PixelWidth), 0, texture.PixelWidth - 1), y = Math.Clamp((int)(hit.Uv.Y * texture.PixelHeight), 0, texture.PixelHeight - 1);
        var pixel = new byte[4];
        new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        var previous = _headColour;
        SetHeadColour(new Vector3(pixel[2], pixel[1], pixel[0]) / 255f);
        PickFeedback.Show(CursorLayer, Viewport.Viewport.TranslatePoint(viewportPoint, CursorLayer), PickFeedback.ToColor(_headColour), PickFeedback.ToColor(previous));
        if (HeadPaintTool != PaintTool.Paint) HeadTool.SelectedIndex = 1;
        Status.Text = $"Colour picked (#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}). Paint is selected.";
        return true;
    }
    private void OnHeadTool(object sender, SelectionChangedEventArgs e)
    {
        if (!_paintReady) return;
        bool painting = HeadPaintTool is not null || HeadPins;
        if(painting){ShapePins.IsChecked=false;EndShapeDrag();}
        QueueRedrawPins();
        if (painting && ReferenceEdit.IsChecked == true) ReferenceEdit.IsChecked = false;
        Viewport.Cursor = painting ? Cursors.Cross : null;
        if (HeadPaintTool is null) HideBrushRing();
        Status.Text = HeadPins ? "Pin warp: drag a painted feature (moustache edge, lip line, beard edge, brow) to where it belongs on the head. Click without dragging to pin a spot so it stays put. Pin reach sets how far each pin pulls; the ring shows it."
            : HeadPaintTool == PaintTool.Clone && _cloneSource is null ? "Ctrl+click the head where to copy from, then paint where it should go."
            : HeadPaintTool == PaintTool.Paint ? "Left drag paints the colour, keeping the texture's own detail. Alt (or I) takes the colour under the cursor; click the big colour square for any colour. Right drag still orbits; the wheel zooms."
            : painting ? "Left drag on the head works on its texture. Right drag still orbits; the wheel zooms." : "Left drag sculpts again.";
        SyncTools();
    }

    /// <summary>The closest head/hair triangle under a viewport point, or null.</summary>
    private RayMeshGeometry3DHitTestResult? HitHead(Point point)
    {
        RayMeshGeometry3DHitTestResult? closest = null;
        VisualTreeHelper.HitTest(Viewport.Viewport, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult hit && _parts.Any(p => p.Visual.Geometry == hit.MeshHit) && (closest is null || hit.DistanceToRayOrigin < closest.DistanceToRayOrigin)) closest = hit;
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        return closest;
    }
    /// <summary>Which part was hit and the texture coordinate (0..1) there, from the triangle's corner UVs and the hit's barycentric weights.</summary>
    private (int Part, Vector2 Uv)? HitUv(Point point)
    {
        if (HitHead(point) is not { } hit) return null;
        var uvs = hit.MeshHit.TextureCoordinates;
        if (uvs.Count == 0) return null;
        Vector2 At(int i) => new((float)uvs[i].X, (float)uvs[i].Y);
        var uv = At(hit.VertexIndex1) * (float)hit.VertexWeight1 + At(hit.VertexIndex2) * (float)hit.VertexWeight2 + At(hit.VertexIndex3) * (float)hit.VertexWeight3;
        return (_parts.FindIndex(p => p.Visual.Geometry == hit.MeshHit), Vector2.Clamp(uv, Vector2.Zero, Vector2.One));
    }

    /// <summary>
    /// Cursor overlay. Brushes: a plain circle exactly the Brush size slider in screen pixels, centred on the cursor (the
    /// brush size is a screen size; each dab is converted to texels where it lands, see <see cref="TexelRadius"/>). Pin
    /// warp: the pin reach outline drawn on the surface, since reach is a distance on the texture.
    /// </summary>
    private void UpdateBrushRing(Point viewportPoint)
    {
        if ((HeadPaintTool is null && !HeadPins) || _orbit) { HideBrushRing(); return; }
        Geometry outline;
        if (HeadPins)
        {
            // Over a pin: the press will grab it, so show a hand instead of the outline.
            if (!_pinDragging && NearestPin(viewportPoint, 12) >= 0) { HideBrushRing(); Viewport.Cursor = Cursors.Hand; return; }
            if (Viewport.Camera is not OrthographicCamera || HitUv(Viewport.TranslatePoint(viewportPoint, Viewport.Viewport)) is not { } hit
                || _parts[hit.Part].Visual.Material is not DiffuseMaterial { Brush: ImageBrush { ImageSource: BitmapSource texture } }
                || _parts[hit.Part].Model.Document.Parts[_parts[hit.Part].Index].ImageUri is not string uri
                || SurfaceCircle(_parts[hit.Part].Model, uri, hit.Uv, PinReachTexels(texture.PixelWidth), texture.PixelWidth, texture.PixelHeight) is not { } reach)
            { HideBrushRing(); return; }
            outline = reach;
        }
        else
        {
            double r = HeadBrushSize.Value / 2;
            outline = new GeometryGroup { Children = {
                new EllipseGeometry(viewportPoint, r, r),
                new LineGeometry(viewportPoint - new System.Windows.Vector(3, 0), viewportPoint + new System.Windows.Vector(3, 0)),
                new LineGeometry(viewportPoint - new System.Windows.Vector(0, 3), viewportPoint + new System.Windows.Vector(0, 3)) } };
        }
        BrushRingShadow.Data = BrushRing.Data = outline;
        BrushRing.Visibility = BrushRingShadow.Visibility = Visibility.Visible;
        Viewport.Cursor = HeadPins ? Cursors.Cross : Cursors.None; // the circle is the brush cursor
    }
    /// <summary>
    /// The brush radius in texels where it lands: the texture distance covered by Brush size / 2 screen pixels from the
    /// cursor, averaged over four directions, so the painted area matches the circle on screen. Null at the silhouette
    /// edge or across a texture seam (the caller keeps the last value).
    /// </summary>
    private float? TexelRadius(Point point, Vector2 uv, StudioModel model, string uri, int width, int height)
    {
        double r = HeadBrushSize.Value / 2; var size = new Vector2(width, height); var found = new List<float>();
        foreach (var dir in new[] { new System.Windows.Vector(1, 0), new System.Windows.Vector(-1, 0), new System.Windows.Vector(0, 1), new System.Windows.Vector(0, -1) })
            if (HitUv(point + dir * r) is { } h && _parts[h.Part].Model == model && model.Document.Parts[_parts[h.Part].Index].ImageUri == uri)
            {
                float d = Vector2.Distance(h.Uv * size, uv * size);
                if (d < width * 0.25f) found.Add(d);
            }
        return found.Count > 0 ? Math.Max(0.5f, found.Average()) : null;
    }
    /// <summary>Screen outline of a circle of <paramref name="radiusTexels"/> around <paramref name="centreUv"/> on the texture, drawn on the surface; null when none of it is on the head.</summary>
    private Geometry? SurfaceCircle(StudioModel model, string uri, Vector2 centreUv, float radiusTexels, int width, int height)
    {
        const int steps = 48;
        var geometry = new StreamGeometry(); bool any = false;
        using (var context = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i <= steps; i++)
            {
                double a = 2 * Math.PI * i / steps;
                var uv = centreUv + new Vector2((float)Math.Cos(a) * radiusTexels / width, (float)Math.Sin(a) * radiusTexels / height);
                Point? screen = uv.X is >= 0 and <= 1 && uv.Y is >= 0 and <= 1 && SurfacePoint(model, uri, uv) is { } p ? Project(p) : null;
                if (screen is not { } s) { open = false; continue; } // off the texture or across a seam: break the line
                if (!open) { context.BeginFigure(s, false, false); open = true; } else context.LineTo(s, true, false);
                any = true;
            }
        }
        geometry.Freeze();
        return any ? geometry : null;
    }
    private void HideBrushRing()
    {
        BrushRing.Visibility = BrushRingShadow.Visibility = Visibility.Collapsed;
        Viewport.Cursor = HeadPaintTool is not null || HeadPins ? Cursors.Cross : null;
    }
    private void OnViewportLeave(object sender, MouseEventArgs e) { if (_paint is null) HideBrushRing(); }

    /// <summary>Starts a paint stroke (or picks a colour / sets the clone source) at a viewport point. True when the press was used.</summary>
    internal bool BeginPaint(Point point, bool control)
    {
        if (HitUv(point) is not { } hit) return false;
        var (model, index, _) = _parts[hit.Part];
        string? uri = model.Document.Parts[index].ImageUri;
        if (uri is null) { Status.Text = "This part has no texture to paint."; return true; }
        BitmapSource texture;
        try { texture = model.CurrentTexture(index); } catch (InvalidDataException ex) { Status.Text = ex.Message; return true; }
        var (pixels, width, height) = Ui.Pixels(texture);
        var texel = hit.Uv * new Vector2(width, height);
        var tool = HeadPaintTool!.Value;
        if (tool == PaintTool.Clone && control) { _cloneSource = (model, uri, texel); Status.Text = "Clone source set. Now paint where the copy should go."; return true; }
        if (tool == PaintTool.Clone && (_cloneSource is not { } source || source.Model != model || source.Uri != uri))
        { Status.Text = "Ctrl+click the spot to copy from first (on the same texture)."; return true; }
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _paint = new PaintSession
        {
            Model = model, Part = index, Uri = uri, Pixels = pixels, Width = width, Height = height, Bitmap = bitmap,
            Source = tool is PaintTool.Clone or PaintTool.Paint ? (byte[])pixels.Clone() : null, // stroke start: Clone copies from it, Paint keeps its detail
            Before = model.TextureOverrides.GetValueOrDefault(uri),
            CloneOffset = tool == PaintTool.Clone ? _cloneSource!.Value.Texel - texel : Vector2.Zero,
            Radius = TexelRadius(point, hit.Uv, model, uri, width, height) ?? width * 0.02f,
        };
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        model.Preview(index, bitmap); // live: every part using this texture shows the stroke as it is painted
        PaintTo(point, texel);
        return true;
    }
    private void PaintTo(Point point, Vector2 texel)
    {
        var s = _paint!;
        s.Radius = TexelRadius(point, texel / new Vector2(s.Width, s.Height), s.Model, s.Uri, s.Width, s.Height) ?? s.Radius;
        // A jump across a UV seam (or a long gap) starts a new segment instead of drawing a line across the texture.
        var from = s.Last is { } last && Vector2.Distance(last, texel) < s.Width * 0.12f ? last : texel;
        float radius = s.Radius;
        var r = TexturePaint.Segment(s.Pixels, s.Width, s.Height, from, texel, radius, HeadPaintTool ?? PaintTool.Paint, _headColour, (float)HeadBrushStrength.Value, s.Source, s.CloneOffset);
        if (r.Width > 0 && r.Height > 0) s.Bitmap.WritePixels(new Int32Rect(r.X, r.Y, r.Width, r.Height), s.Pixels, s.Width * 4, r.X, r.Y);
        s.Last = texel;
    }
    private void ContinuePaint(Point point)
    {
        var s = _paint!;
        if (HitUv(point) is not { } hit || _parts[hit.Part].Model != s.Model || s.Model.Document.Parts[_parts[hit.Part].Index].ImageUri != s.Uri) { s.Last = null; return; }
        PaintTo(point, hit.Uv * new Vector2(s.Width, s.Height));
        Status.Text = "Painting: release to finish. Undo removes the whole stroke.";
    }
    /// <summary>Stores the finished stroke in the model (and the undo history).</summary>
    private void FinishPaint()
    {
        if (_paint is not { } s) return;
        _paint = null;
        try
        {
            s.Model.Wrap(s.Part, BitmapSource.Create(s.Width, s.Height, 96, 96, PixelFormats.Bgra32, null, s.Pixels, s.Width * 4));
            _undo.Push(new UndoStep(null, s.Model, s.Part, s.Before)); _redo.Clear(); _dirty = true;
            if (_pins?.Model == s.Model && _pins.Uri == s.Uri) _pins = null; // painting over the pinned texture keeps the result; the pins are done
            Status.Text = "Stroke painted. Undo (Ctrl+Z) removes it; Save as .big writes the painted texture.";
        }
        catch (Exception ex) { s.Model.RestoreTexture(s.Part, s.Before); Status.Text = ex.Message; }
    }

    /// <summary>Records the current texture of a part before it is replaced, so Undo can bring it back.</summary>
    private void RecordTexture(StudioModel model, int part)
    {
        _undo.Push(new UndoStep(null, model, part, model.TextureOverrides.GetValueOrDefault(model.Document.Parts[part].ImageUri ?? ""), PinsOf(model, part)));
        if (PinsOf(model, part) is not null) _pins = null; // a replaced texture ends the pin warp on it
        _redo.Clear();
    }
    /// <summary>The state that undoing (or redoing) <paramref name="step"/> replaces, of the same kind.</summary>
    private UndoStep Current(UndoStep step) => step.Model is { } m
        ? step with { Mesh = step.Mesh is null ? null : Snapshot(), Png = m.TextureOverrides.GetValueOrDefault(m.Document.Parts[step.Part].ImageUri ?? ""), Pins = PinsOf(m, step.Part) }
        : new UndoStep(Snapshot());
    private PinState? PinsOf(StudioModel model, int part) => _pins is { } p && p.Model == model && p.Uri == model.Document.Parts[part].ImageUri ? p : null;
    private void Apply(UndoStep step)
    {
        if (step.Model is { } m)
        {
            m.RestoreTexture(step.Part, step.Png); _dirty = true;
            if (step.Pins is not null || PinsOf(m, step.Part) is not null) { _pins = step.Pins; _selectedPin = -1; QueueRedrawPins(); }
        }
        if (step.Mesh is not null) Restore(step.Mesh);
    }
}
