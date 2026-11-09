using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio.Views;

/// <summary>
/// Pin warp on the 3D head: drag a painted feature (moustache edge, lip line, brow) to where it belongs on the mesh and a
/// pin stays there. All pins together define one smooth, local warp of the texture as it was before the first pin
/// (see <see cref="PinWarp"/>), so moving or removing pins never smears or softens anything.
/// </summary>
public partial class FaceSculptWindow
{
    /// <summary>Pins of one texture, normalised texture coordinates; Base = the texture before any pin, Result = with them.</summary>
    private sealed record PinState(StudioModel Model, int Part, string Uri, byte[] Base, int Width, int Height, (Vector2 From, Vector2 To)[] Pins, byte[] Result);
    private PinState? _pins;
    private int _selectedPin = -1;
    private bool _pinDragging;
    private PinState? _pinBefore;            // pin layout before the current change (for undo)
    private byte[]? _pinBeforePng;           // texture before the current change (for undo)
    private WriteableBitmap? _pinPreview;
    private DispatcherTimer? _pinReachTimer;
    private bool _pinRedrawQueued;

    private bool HeadPins => (HeadTool.SelectedItem as ComboBoxItem)?.Tag as string == "Pins";
    private float PinReachTexels(int width) => (float)(PinReach.Value / 100 * width);

    /// <summary>Press with the Pin warp tool: grab the pin under the cursor, or start a new one on the painted feature there.</summary>
    internal bool BeginPin(Point point)
    {
        int near = NearestPin(point, 12);
        if (near < 0)
        {
            if (HitUv(point) is not { } hit) return false;
            var (model, index, _) = _parts[hit.Part];
            string? uri = model.Document.Parts[index].ImageUri;
            if (uri is null) { Status.Text = "This part has no texture to warp."; return true; }
            if (_pins is null || _pins.Model != model || _pins.Uri != uri)
            {
                BitmapSource texture;
                try { texture = model.CurrentTexture(index); } catch (InvalidDataException ex) { Status.Text = ex.Message; return true; }
                var (pixels, width, height) = Ui.Pixels(texture);
                _pins = new PinState(model, index, uri, pixels, width, height, Array.Empty<(Vector2, Vector2)>(), pixels);
            }
            StartPinChange();
            _pins = _pins with { Pins = _pins.Pins.Append((hit.Uv, hit.Uv)).ToArray() };
            near = _pins.Pins.Length - 1;
        }
        else StartPinChange();
        _selectedPin = near; _pinDragging = true;
        ShowPinPreview();
        Status.Text = "Drag the painted feature to where it belongs on the head, then release. Pins without a drag hold their spot.";
        QueueRedrawPins();
        return true;
    }
    internal void DragPin(Point point)
    {
        if (!_pinDragging || _pins is not { } s || _selectedPin < 0) return;
        if (HitUv(point) is not { } hit || _parts[hit.Part].Model != s.Model || s.Model.Document.Parts[_parts[hit.Part].Index].ImageUri != s.Uri) return;
        var pins = (((Vector2 From, Vector2 To)[])s.Pins.Clone()); pins[_selectedPin] = (pins[_selectedPin].From, hit.Uv);
        _pins = s with { Pins = pins };
        Recompute();
        QueueRedrawPins();
    }
    internal void EndPin()
    {
        if (!_pinDragging) return;
        _pinDragging = false;
        CommitPins("Pin placed. Drag it again to adjust; Delete removes the selected pin; Undo steps back.");
    }

    private void StartPinChange() { _pinBefore = _pins is { Pins.Length: > 0 } ? _pins : null; _pinBeforePng = _pins!.Model.TextureOverrides.GetValueOrDefault(_pins.Uri); }
    private void Recompute()
    {
        var s = _pins!; var size = new Vector2(s.Width, s.Height);
        var result = PinWarp.Apply(s.Base, s.Width, s.Height, Effective(s).Select(p => (p.From * size, p.To * size)).ToArray(), PinReachTexels(s.Width));
        _pins = s with { Result = result };
        if (_pinPreview is { } bitmap && bitmap.PixelWidth == s.Width) bitmap.WritePixels(new Int32Rect(0, 0, s.Width, s.Height), result, s.Width * 4, 0);
    }
    private void ShowPinPreview()
    {
        var s = _pins!;
        _pinPreview = new WriteableBitmap(s.Width, s.Height, 96, 96, PixelFormats.Bgra32, null);
        _pinPreview.WritePixels(new Int32Rect(0, 0, s.Width, s.Height), s.Result, s.Width * 4, 0);
        s.Model.Preview(s.Part, _pinPreview);
    }
    /// <summary>Stores the warped texture in the model, with one undo step that also brings the previous pin layout back.</summary>
    private void CommitPins(string message)
    {
        if (_pins is not { } s) return;
        try
        {
            s.Model.Wrap(s.Part, BitmapSource.Create(s.Width, s.Height, 96, 96, PixelFormats.Bgra32, null, s.Result, s.Width * 4));
            _undo.Push(new UndoStep(null, s.Model, s.Part, _pinBeforePng, _pinBefore)); _redo.Clear(); _dirty = true;
            Status.Text = message;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        _pinPreview = null; QueueRedrawPins();
    }
    private void RemovePin(int index)
    {
        if (_pins is not { } s || index < 0 || index >= s.Pins.Length) return;
        StartPinChange();
        _pins = s with { Pins = s.Pins.Where((_, i) => i != index).ToArray() };
        _selectedPin = -1; Recompute();
        CommitPins(_pins.Pins.Length == 0 ? "All pins removed: the texture is back to how it was before the pins." : "Pin removed.");
    }
    private void OnRemovePin(object sender, RoutedEventArgs e) { if (_selectedPin < 0) { Status.Text = "Click a pin to select it first."; return; } RemovePin(_selectedPin); }
    private void OnClearPins(object sender, RoutedEventArgs e)
    {
        if (_pins is not { Pins.Length: > 0 } s) { Status.Text = "There are no pins."; return; }
        StartPinChange(); _pins = s with { Pins = Array.Empty<(Vector2, Vector2)>(), Result = s.Base }; _selectedPin = -1;
        CommitPins("All pins removed: the texture is back to how it was before the pins.");
    }
    private void OnPinReach(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_pins is not { Pins.Length: > 0 } || !IsLoaded) return;
        // Live while the slider moves; stored (one undo step) once it rests.
        if (_pinReachTimer is null)
        {
            _pinReachTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _pinReachTimer.Tick += (_, _) => { _pinReachTimer.Stop(); if (_pins is { Pins.Length: > 0 }) { Recompute(); CommitPins("Pin reach changed."); } };
            StartPinChange();
        }
        else if (!_pinReachTimer.IsEnabled) StartPinChange();
        if (_pinPreview is null) ShowPinPreview();
        Recompute(); _pinReachTimer.Stop(); _pinReachTimer.Start();
    }

    /// <summary>
    /// The pins the warp uses: the user's pins plus, with Mirror pins on a face texture, each one reflected across the
    /// middle of the face (the face atlas is left/right symmetric about u = 0.5). A reflection is left out where the user
    /// already placed a pin of their own, and pins on the centre line are their own reflection.
    /// </summary>
    private (Vector2 From, Vector2 To)[] Effective(PinState s)
    {
        if (MirrorPins.IsChecked != true || !string.Equals(System.IO.Path.GetFileNameWithoutExtension(s.Uri), "face", StringComparison.OrdinalIgnoreCase)) return s.Pins;
        static Vector2 M(Vector2 v) => new(1 - v.X, v.Y);
        float near = (float)(PinReach.Value / 100 * 0.25);
        var mirrored = s.Pins.Where(p => Math.Abs(p.To.X - 0.5f) > 0.01f).Select(p => (From: M(p.From), To: M(p.To)))
            .Where(m => !s.Pins.Any(q => Vector2.Distance(q.To, m.To) < near));
        return s.Pins.Concat(mirrored).ToArray();
    }
    private void OnMirrorPins(object sender, RoutedEventArgs e)
    {
        if (_pins is not { Pins.Length: > 0 }) return;
        StartPinChange(); Recompute(); CommitPins(MirrorPins.IsChecked == true ? "Pins mirrored to the other side of the face." : "Mirroring off: only the pins you placed act.");
    }

    /// <summary>The 3D point drawn with texture coordinate <paramref name="uv"/> on the parts using this texture (picture strips skipped unless the point is in one).</summary>
    private Point3D? SurfacePoint(StudioModel model, string uri, Vector2 uv)
    {
        bool wantStrip = FaceTexture.InPictureStrip(uv);
        for (int part = 0; part < model.Document.Parts.Count; part++)
        {
            if (model.Document.Parts[part].ImageUri != uri) continue;
            var mesh = (MeshGeometry3D)model.Geometry[part].Geometry; var t = mesh.TextureCoordinates; var idx = mesh.TriangleIndices;
            for (int k = 0; k + 2 < idx.Count; k += 3)
            {
                Vector2 a = Uv(t[idx[k]]), b = Uv(t[idx[k + 1]]), c = Uv(t[idx[k + 2]]);
                if (FaceTexture.InPictureStrip((a + b + c) / 3) != wantStrip) continue;
                float det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (Math.Abs(det) < 1e-12f) continue;
                float l1 = ((b.Y - c.Y) * (uv.X - c.X) + (c.X - b.X) * (uv.Y - c.Y)) / det, l2 = ((c.Y - a.Y) * (uv.X - c.X) + (a.X - c.X) * (uv.Y - c.Y)) / det, l3 = 1 - l1 - l2;
                if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                Point3D pa = mesh.Positions[idx[k]], pb = mesh.Positions[idx[k + 1]], pc = mesh.Positions[idx[k + 2]];
                return new Point3D(pa.X * l1 + pb.X * l2 + pc.X * l3, pa.Y * l1 + pb.Y * l2 + pc.Y * l3, pa.Z * l1 + pb.Z * l2 + pc.Z * l3);
            }
        }
        return null;
        static Vector2 Uv(Point p) => new((float)p.X, (float)p.Y);
    }
    private Point? Project(Point3D point)
    {
        if (Viewport.Camera is not OrthographicCamera camera) return null;
        var axes = CameraAxes(); var d = point - camera.Position; double scale = PixelScale;
        return new Point(Viewport.ActualWidth / 2 + Vector3D.DotProduct(d, axes.Right) / scale, Viewport.ActualHeight / 2 - Vector3D.DotProduct(d, axes.Up) / scale);
    }
    /// <summary>Screen position of a pin end and whether it faces the camera (the head is not in front of it).</summary>
    private (Point Screen, bool Visible)? PinScreen(Vector2 uv)
    {
        if (_pins is not { } s || SurfacePoint(s.Model, s.Uri, uv) is not { } p || Project(p) is not { } screen) return null;
        var hit = HitHead(Viewport.TranslatePoint(screen, Viewport.Viewport));
        return (screen, hit is not null && (hit.PointHit - p).Length < _size * 0.03);
    }
    private int NearestPin(Point point, double within)
    {
        if (_pins is not { } s || !HeadPins) return -1;
        int best = -1; double bestDistance = within;
        for (int i = 0; i < s.Pins.Length; i++)
            if (PinScreen(s.Pins[i].To) is { Visible: true } end && (end.Screen - point).Length <= bestDistance) { best = i; bestDistance = (end.Screen - point).Length; }
        return best;
    }

    /// <summary>Redraws the pin handles once the current input has been handled (camera moves, sculpts, pin edits).</summary>
    private void QueueRedrawPins()
    {
        if (_pinRedrawQueued) return;
        _pinRedrawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { _pinRedrawQueued = false; RedrawPins(); });
    }
    private void RedrawPins()
    {
        PinLayer.Children.Clear();
        if (!HeadPins || _pins is not { } s) return;
        var yellow = new SolidColorBrush(Color.FromRgb(255, 210, 40)); var cyan = new SolidColorBrush(Color.FromRgb(60, 220, 255));
        var effective = Effective(s);
        for (int i = s.Pins.Length; i < effective.Length; i++) // reflections: faded, not grabbable
            if (PinScreen(effective[i].To) is { Visible: true } mirror)
            {
                var ghost = new Ellipse { Width = 9, Height = 9, Stroke = yellow, StrokeThickness = 1.5, Opacity = 0.6 };
                Canvas.SetLeft(ghost, mirror.Screen.X - 4.5); Canvas.SetTop(ghost, mirror.Screen.Y - 4.5); PinLayer.Children.Add(ghost);
            }
        if (_selectedPin >= 0 && _selectedPin < s.Pins.Length && SurfaceCircle(s.Model, s.Uri, s.Pins[_selectedPin].To, PinReachTexels(s.Width), s.Width, s.Height) is { } reachOutline)
            PinLayer.Children.Add(new System.Windows.Shapes.Path { Data = reachOutline, Stroke = cyan, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.8 });
        for (int i = 0; i < s.Pins.Length; i++)
        {
            if (PinScreen(s.Pins[i].To) is not { } to) continue;
            var colour = i == _selectedPin ? cyan : yellow; double opacity = to.Visible ? 1 : 0.35;
            if (PinScreen(s.Pins[i].From) is { } from && (from.Screen - to.Screen).Length > 2)
            {
                PinLayer.Children.Add(new Line { X1 = from.Screen.X, Y1 = from.Screen.Y, X2 = to.Screen.X, Y2 = to.Screen.Y, Stroke = colour, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 }, Opacity = opacity });
                var start = new Ellipse { Width = 7, Height = 7, Stroke = colour, StrokeThickness = 1.5, Opacity = opacity };
                Canvas.SetLeft(start, from.Screen.X - 3.5); Canvas.SetTop(start, from.Screen.Y - 3.5); PinLayer.Children.Add(start);
            }
            var pin = new Ellipse { Width = 12, Height = 12, Fill = colour, Stroke = Brushes.Black, StrokeThickness = 1.5, Opacity = opacity };
            Canvas.SetLeft(pin, to.Screen.X - 6); Canvas.SetTop(pin, to.Screen.Y - 6); PinLayer.Children.Add(pin);
        }
    }
}
