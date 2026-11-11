using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio.Views;

public partial class LikenessWindow
{
    private sealed record SurfacePin(MeshAnchor Anchor, bool Side, Marker Marker);
    private readonly List<SurfacePin> _surfacePins = new();
    private readonly Dictionary<bool, Func<Vector3, Vector2>> _surfaceCameras = new();

    private void InitSurfacePins()
    {
        foreach (var view in new[] { _frontPhoto, _sidePhoto })
        {
            view.Canvas.Background = Brushes.Transparent;
            view.Canvas.MouseLeftButtonDown += (_, e) =>
            {
                if (SurfacePinsEnabled.IsChecked != true || !view.Loaded) return;
                var point = e.GetPosition(view.Canvas);
                try
                {
                    var pin = AddSurfacePin(view.Side, new Vector2((float)point.X, (float)point.Y));
                    if (pin is null) return;
                    BeginSurfaceDrag(pin); e.Handled = true;
                }
                catch (ArgumentException ex) { Status.Text = ex.Message; }
            };
        }
        FrontPhotoZoom.MagnificationChanged += (_, _) => UpdateWireThickness(_frontPhoto, FrontPhotoZoom);
        SidePhotoZoom.MagnificationChanged += (_, _) => UpdateWireThickness(_sidePhoto, SidePhotoZoom);
    }

    private static void UpdateWireThickness(PhotoView photo, ZoomPanel zoom)
    {
        foreach (var path in photo.Canvas.Children.OfType<Path>().Where(p => Equals(p.Tag, "PhotoWire")))
            path.StrokeThickness = 0.65 / Math.Max(zoom.Scale, .001);
    }

    private Vector3[][] FitSurfacePins(Vector3[][] shape)
    {
        _surfaceCameras.Clear();
        foreach (var view in new[] { _frontPhoto, _sidePhoto })
        {
            if (RegisteredPhoto(view, shape) is not { } photo) continue;
            try { _surfaceCameras[view.Side] = PhotoTexture.Projection(photo, warp: false); }
            catch (ArgumentException) { }
        }
        var controls = new List<(Vector3 From, Vector3 To)>();
        foreach (var pin in _surfacePins)
        {
            if (!_surfaceCameras.TryGetValue(pin.Side, out var project) || _locked[pin.Anchor.Part]) continue;
            var source = pin.Anchor.Position(shape);
            var horizontal = pin.Side ? Vector3.UnitZ : Vector3.UnitX;
            var origin = project(source);
            var x = project(source + horizontal) - origin;
            var y = project(source + Vector3.UnitY) - origin;
            float determinant = x.X * y.Y - y.X * x.Y;
            if (Math.Abs(determinant) < 1e-6f) continue;
            var delta = pin.Marker.Position - origin;
            float dx = (delta.X * y.Y - y.X * delta.Y) / determinant;
            float dy = (x.X * delta.Y - delta.X * x.Y) / determinant;
            controls.Add((source, source + horizontal * dx + Vector3.UnitY * dy));
        }
        var all = shape.SelectMany(p => p).ToArray();
        float size = all.Max(p => p.Y) - all.Min(p => p.Y);
        float? mirror = Symmetric.IsChecked == true ? (all.Min(p => p.X) + all.Max(p => p.X)) / 2 : null;
        return SurfacePinFit.Apply(shape, controls, size * .4f, _neckY, mirror);
    }

    private SurfacePin? AddSurfacePin(bool side, Vector2 pixel)
    {
        if (_fitted is null) Refit();
        if (_fitted is null || !_surfaceCameras.TryGetValue(side, out var project))
            throw new ArgumentException("Align at least four numbered markers before adding surface pins.");
        MeshAnchor? anchor = null; float frontmost = float.NegativeInfinity;
        foreach (int p in _faceParts)
        {
            if (_locked[p]) continue;
            var part = _parts[p]; var vertices = _fitted[p];
            for (int t = 0; t + 2 < part.Indices.Length; t += 3)
            {
                int ia = part.Indices[t], ib = part.Indices[t + 1], ic = part.Indices[t + 2];
                var a = vertices[ia]; var b = vertices[ib]; var c = vertices[ic];
                var normal = Vector3.Cross(b - a, c - a);
                if ((side ? normal.X : normal.Z) <= 0) continue;
                var pa = project(a); var pb = project(b); var pc = project(c);
                var u = pb - pa; var v = pc - pa; var delta = pixel - pa;
                float determinant = u.X * v.Y - u.Y * v.X;
                if (Math.Abs(determinant) < 1e-6f) continue;
                float wb = (delta.X * v.Y - delta.Y * v.X) / determinant;
                float wc = (u.X * delta.Y - u.Y * delta.X) / determinant, wa = 1 - wb - wc;
                if (wa < 0 || wb < 0 || wc < 0) continue;
                var point = a * wa + b * wb + c * wc;
                float depth = side ? point.X : point.Z;
                if (point.Y <= _neckY || depth <= frontmost) continue;
                anchor = new MeshAnchor(p, ia, ib, ic, wa, wb, wc); frontmost = depth;
            }
        }
        if (anchor is not { } attached) return null;
        var view = side ? _sidePhoto : _frontPhoto;
        var visual = new Grid { Width = 12, Height = 12, Cursor = Cursors.SizeAll, ToolTip = "Drag to fit this surface point. Right-click removes this pin." };
        visual.Children.Add(new Ellipse { Fill = Brushes.OrangeRed, Stroke = Brushes.White, StrokeThickness = 1.3 });
        var marker = new Marker { Visual = visual, Position = pixel };
        var pin = new SurfacePin(attached, side, marker);
        _surfacePins.Add(pin); view.Canvas.Children.Add(visual); Move(marker, pixel);
        ZoomPanel.SetScreenSize(visual, 12); ZoomPanel.SetKeepScreenSize(visual, true);
        visual.MouseLeftButtonDown += (_, e) => { BeginSurfaceDrag(pin); e.Handled = true; };
        visual.MouseMove += (_, e) =>
        {
            if (!visual.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(view.Canvas);
            Move(marker, new Vector2((float)Math.Clamp(point.X, 0, view.Width - 1), (float)Math.Clamp(point.Y, 0, view.Height - 1)));
            InvalidatePhotoTexture();
            if (!_live.IsEnabled) _live.Start();
            e.Handled = true;
        };
        visual.MouseLeftButtonUp += (_, e) => { visual.ReleaseMouseCapture(); _live.Stop(); Refit(); e.Handled = true; };
        visual.MouseRightButtonDown += (_, e) =>
        {
            visual.ReleaseMouseCapture(); _surfacePins.Remove(pin); view.Canvas.Children.Remove(visual);
            InvalidatePhotoTexture(); Refit(); e.Handled = true;
        };
        Status.Text = "Surface pin added. Drag it onto the photo feature; click another spot to hold it still.";
        return pin;
    }
    private void BeginSurfaceDrag(SurfacePin pin)
    {
        ShowResult.IsChecked = true; pin.Marker.Visual.CaptureMouse();
    }
    private void OnClearSurfacePins(object sender, RoutedEventArgs e)
    {
        ClearSurfacePins(false); ClearSurfacePins(true);
        InvalidatePhotoTexture(); Refit();
    }
    private void ClearSurfacePins(bool side)
    {
        var view = side ? _sidePhoto : _frontPhoto;
        foreach (var pin in _surfacePins.Where(p => p.Side == side).ToArray())
        {
            pin.Marker.Visual.ReleaseMouseCapture(); view.Canvas.Children.Remove(pin.Marker.Visual); _surfacePins.Remove(pin);
        }
    }
}
