using System.Globalization;
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
/// Fits the head shape to a front and a side photo (see <see cref="FaceFit"/>). Photo markers say where each
/// landmark is on the person; head markers say where it is on the mesh (placed from the atlas layout, draggable).
/// </summary>
public partial class LikenessWindow : Window
{
    private const int ViewSize = 520;
    private sealed class Marker { public required FrameworkElement Visual; public Vector2 Position; public bool Placed = true; }
    private sealed class HeadView
    {
        public required Viewport3D Viewport; public required Canvas Canvas; public required OrthographicCamera Camera;
        public required List<GeometryModel3D> Visuals; public required bool Side;
        public readonly Dictionary<int, Marker> Markers = new();
    }
    private sealed class PhotoView
    {
        public required Image Image; public required Canvas Canvas; public required TextBlock Hint; public required bool Side;
        public int Width, Height; public bool Loaded; public byte[]? Pixels;
        public readonly Dictionary<int, Marker> Markers = new();
    }

    private readonly IReadOnlyList<ModelPart> _parts;
    private readonly int[] _faceParts;
    private readonly Vector3[][] _positions;
    private readonly float _neckY;
    private readonly MeshAnchor?[] _anchors;
    private readonly HeadView _frontHead, _sideHead;
    private readonly PhotoView _frontPhoto, _sidePhoto;
    private readonly DispatcherTimer _refit, _live;
    private Vector3[][]? _fitted;
    private Marker? _dragging; private Canvas? _dragCanvas;
    private FaceLandmarker? _landmarker;
    private FaceDetection? _frontDetection;          // face points found in the front photo
    private (Vector3[] Render, Vector3?[] Surface, float UnitsPerPixel)? _headFace; // the same points found on the head's own front render
    private bool _headFaceTried;
    private readonly List<string> _notes = new();
    /// <summary>The bundled MediaPipe face landmark model (vendor/facemesh).</summary>
    /// <summary>The detector under-reports how far a face differs from the head it is compared with; its differences are multiplied by this (measured on reshaped stock heads, see the smoke test).</summary>
    public static double DenseGain { get; set; } = 1;
    public static string ModelPath { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "facemesh", "face_landmarks.onnx");

    /// <summary>New positions for every part (same order as given), set when the user applies.</summary>
    public Vector3[][]? Result { get; private set; }

    public LikenessWindow(IReadOnlyList<GeometryModel3D> visuals, IReadOnlyList<ModelPart> parts, int[] faceParts, Vector3[][] positions, float neckY)
    {
        InitializeComponent();
        Ui.FitToScreen(this);
        (_parts, _faceParts, _positions, _neckY) = (parts, faceParts, positions, neckY);
        _anchors = new MeshAnchor?[FaceFit.Landmarks.Length];
        _refit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refit.Tick += (_, _) => { _refit.Stop(); Refit(); };
        // While a photo marker is dragged the fit is redone at most every 60 ms, so the head follows the drag.
        _live = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _live.Tick += (_, _) => { _live.Stop(); Refit(); };
        _frontHead = CreateHead(FrontHeadHost, FrontHeadCanvas, visuals, false);
        _sideHead = CreateHead(SideHeadHost, SideHeadCanvas, visuals, true);
        _frontPhoto = new PhotoView { Image = FrontPhoto, Canvas = FrontPhotoCanvas, Hint = FrontHint, Side = false };
        _sidePhoto = new PhotoView { Image = SidePhoto, Canvas = SidePhotoCanvas, Hint = SideHint, Side = true };
        Legend.Text = string.Join("\n", FaceFit.Landmarks.Select((l, i) => $"{i + 1}  {l.Name}" + (l.Front && l.Side ? "" : l.Side ? "  (side)" : "  (front)")));
        ResetAnchors();
        ShowHeads();
    }

    // ---- head views ---------------------------------------------------------------------------------------
    private HeadView CreateHead(Grid host, Canvas canvas, IReadOnlyList<GeometryModel3D> visuals, bool side)
    {
        var all = _positions.SelectMany(p => p).ToArray();
        var min = all.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var max = all.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        float size = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
        var centre = (min + max) / 2;
        var look = side ? new Vector3D(-1, 0, 0) : new Vector3D(0, 0, -1);
        var camera = new OrthographicCamera(new Point3D(centre.X, centre.Y, centre.Z) - look * size * 3, look, new Vector3D(0, 1, 0), size * 1.15);
        var clones = visuals.Select(v => v.Clone()).ToList();
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(120, 120, 120)));
        group.Children.Add(new DirectionalLight(Colors.White, side ? new Vector3D(-1, -0.4, -0.3) : new Vector3D(-0.3, -0.4, -1)));
        foreach (var c in clones) group.Children.Add(c);
        var viewport = new Viewport3D { Camera = camera, Width = ViewSize, Height = ViewSize };
        viewport.Children.Add(new ModelVisual3D { Content = group });
        host.Children.Insert(0, viewport);
        canvas.Width = canvas.Height = ViewSize;
        return new HeadView { Viewport = viewport, Canvas = canvas, Camera = camera, Visuals = clones, Side = side };
    }
    private static Vector2 Project(HeadView view, Vector3 p)
    {
        var c = view.Camera; var look = c.LookDirection; look.Normalize();
        var right = Vector3D.CrossProduct(look, c.UpDirection); right.Normalize();
        var d = new Vector3D(p.X - c.Position.X, p.Y - c.Position.Y, p.Z - c.Position.Z);
        return new Vector2((float)(Vector3D.DotProduct(d, right) / c.Width * ViewSize + ViewSize / 2.0),
                           (float)(ViewSize / 2.0 - Vector3D.DotProduct(d, c.UpDirection) / c.Width * ViewSize));
    }
    private void ShowHeads()
    {
        var shown = ShowResult.IsChecked == true && _fitted is not null ? _fitted : _positions;
        foreach (var view in new[] { _frontHead, _sideHead })
        {
            for (int p = 0; p < view.Visuals.Count; p++)
            {
                var mesh = (MeshGeometry3D)view.Visuals[p].Geometry;
                mesh.Positions = new Point3DCollection(shown[p].Select(v => new Point3D(v.X, v.Y, v.Z))); mesh.Normals = new Vector3DCollection();
            }
            bool markers = ShowResult.IsChecked != true;
            foreach (var (i, marker) in view.Markers)
            {
                marker.Visual.Visibility = markers ? Visibility.Visible : Visibility.Collapsed;
                if (_anchors[i] is { } a) Move(marker, Project(view, a.Position(_positions)));
            }
        }
    }
    private void ResetAnchors()
    {
        for (int i = 0; i < _anchors.Length; i++) _anchors[i] = FaceFit.AnchorAtUv(_parts, _faceParts, FaceFit.Landmarks[i].Uv);
        foreach (var view in new[] { _frontHead, _sideHead })
        {
            view.Canvas.Children.Clear(); view.Markers.Clear();
            for (int i = 0; i < _anchors.Length; i++)
            {
                var l = FaceFit.Landmarks[i];
                if ((view.Side ? !l.Side : !l.Front) || _anchors[i] is not { } a) continue;
                int index = i; var v = view;
                view.Markers[i] = AddMarker(view.Canvas, index, Brushes.Gold, Project(view, a.Position(_positions)), 18, () => Reanchor(v, index));
            }
        }
    }
    /// <summary>A head marker was dropped: attach the landmark to the mesh point under it.</summary>
    private void Reanchor(HeadView view, int landmark)
    {
        var point = view.Markers[landmark].Position;
        RayMeshGeometry3DHitTestResult? hit = null;
        VisualTreeHelper.HitTest(view.Viewport, null, r =>
        {
            if (r is RayMeshGeometry3DHitTestResult h && view.Visuals.Any(v => v.Geometry == h.MeshHit) && (hit is null || h.DistanceToRayOrigin < hit.DistanceToRayOrigin)) hit = h;
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(new Point(point.X, point.Y)));
        if (hit is not null)
        {
            int part = view.Visuals.FindIndex(v => v.Geometry == hit.MeshHit);
            _anchors[landmark] = new MeshAnchor(part, hit.VertexIndex1, hit.VertexIndex2, hit.VertexIndex3, (float)hit.VertexWeight1, (float)hit.VertexWeight2, (float)hit.VertexWeight3);
            Status.Text = $"{FaceFit.Landmarks[landmark].Name} now sits on that spot of the head.";
        }
        else Status.Text = "Drop head markers on the head itself.";
        ShowHeads(); Queue();
    }

    // ---- photos -------------------------------------------------------------------------------------------
    private void OnLoadPhoto(object sender, RoutedEventArgs e)
    {
        var view = (string)((Button)sender).Tag == "side" ? _sidePhoto : _frontPhoto;
        try
        {
            var image = FaceSculptWindow.PickImage(this); if (image is null) return;
            LoadPhoto(view.Side, image);
            Status.Text = view.Side ? "Drag the numbered markers on the side photo onto the same features (side markers are always placed by hand)."
                : AutoPlaceFront() ? "Front markers placed automatically. Check each one and drag any that missed."
                : Status.Text;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    /// <summary>Shows a photo for one view and starts its markers from the head's layout.</summary>
    public void LoadPhoto(bool side, BitmapSource image)
    {
        var view = side ? _sidePhoto : _frontPhoto;
        var (pixels, width, height) = Ui.Pixels(image);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze();
        if (!side) _frontDetection = null;
        view.Image.Source = bitmap; view.Width = width; view.Height = height; view.Loaded = true; view.Pixels = pixels;
        view.Canvas.Width = width; view.Canvas.Height = height; view.Hint.Visibility = Visibility.Collapsed;
        PlacePhotoMarkers(view);
        Queue();
    }
    /// <summary>
    /// Places the front-photo markers from detected facial landmarks. False (with the reason in the status line)
    /// when there is no front photo, no model, or no confident face; the markers then stay where they were.
    /// </summary>
    public bool AutoPlaceFront()
    {
        if (!_frontPhoto.Loaded || _frontPhoto.Pixels is null) { Status.Text = "Load a front photo first."; return false; }
        try
        {
            _landmarker ??= new FaceLandmarker(ModelPath);
            // The current markers, if the user moved them near the face, are a good first crop.
            var placed = _frontPhoto.Markers.Values.Select(m => m.Position).ToArray();
            var min = placed.Aggregate(new Vector2(float.MaxValue), Vector2.Min); var max = placed.Aggregate(new Vector2(float.MinValue), Vector2.Max);
            var detection = _landmarker.Detect(_frontPhoto.Pixels, _frontPhoto.Width, _frontPhoto.Height, placed.Length > 0 ? ((min + max) / 2, Math.Max(max.X - min.X, max.Y - min.Y) * 1.6f) : null);
            _frontDetection = detection;
            if (detection is null) { Status.Text = "No face found automatically. Use a clear, straight-on photo, or place the markers by hand."; return false; }
            var markers = FaceLandmarker.FrontMarkers(detection);
            // Size markers to the face, not the photo, so they stay separate on a small face.
            double size = Math.Clamp(Vector2.Distance(detection.Points[33], detection.Points[263]) * 0.22, 6, Math.Max(_frontPhoto.Width, _frontPhoto.Height) / 34.0);
            foreach (var marker in _frontPhoto.Markers.Values) Resize(marker, size);
            for (int i = 0; i < markers.Length; i++) if (markers[i] is { } p) SetPhotoMarker(false, i, p);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            Status.Text = "Automatic markers unavailable: " + ex.Message; return false;
        }
    }
    private void OnAutoPlace(object sender, RoutedEventArgs e)
    {
        if (AutoPlaceFront()) Status.Text = "Front markers placed automatically. Check each one and drag any that missed.";
    }
    protected override void OnClosed(EventArgs e) { _landmarker?.Dispose(); base.OnClosed(e); }
    /// <summary>Places one photo marker (pixel coordinates), as dragging it would.</summary>
    public void SetPhotoMarker(bool side, int landmark, Vector2 position)
    {
        if ((side ? _sidePhoto : _frontPhoto).Markers.TryGetValue(landmark, out var marker)) { Move(marker, position); MarkPlaced(marker); Queue(); }
    }
    /// <summary>The fit for the current photos and markers.</summary>
    public Vector3[][] Fit() => Compute();
    /// <summary>Start markers from the head's own landmark layout, scaled into the middle of the photo.</summary>
    private void PlacePhotoMarkers(PhotoView view)
    {
        view.Canvas.Children.Clear(); view.Markers.Clear();
        var head = view.Side ? _sideHead : _frontHead;
        float scale = Math.Min(view.Width, view.Height) / (float)ViewSize * 0.9f;
        var centre = new Vector2(view.Width / 2f, view.Height / 2f);
        for (int i = 0; i < _anchors.Length; i++)
        {
            var l = FaceFit.Landmarks[i];
            if ((view.Side ? !l.Side : !l.Front) || _anchors[i] is not { } a) continue;
            var start = (Project(head, a.Position(_positions)) - new Vector2(ViewSize / 2f)) * scale + centre;
            // Starting spots are only a guess: a photo marker counts once it is dragged or auto-placed (faded until then).
            var marker = AddMarker(view.Canvas, i, new SolidColorBrush(Color.FromRgb(255, 140, 30)), start, Math.Max(view.Width, view.Height) / 34.0, Queue, live: true);
            marker.Placed = false; marker.Visual.Opacity = 0.45;
            view.Markers[i] = marker;
        }
    }

    // ---- markers ------------------------------------------------------------------------------------------
    /// <param name="live">Photo markers: count from the moment they are grabbed and refit the head while dragged.</param>
    private Marker AddMarker(Canvas canvas, int landmark, Brush colour, Vector2 position, double size, Action dropped, bool live = false)
    {
        var visual = new Grid { Width = size, Height = size, Cursor = Cursors.SizeAll, ToolTip = $"{landmark + 1}  {FaceFit.Landmarks[landmark].Name}" };
        visual.Children.Add(new Ellipse { Stroke = colour, StrokeThickness = Math.Max(1, size / 9), Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)) });
        visual.Children.Add(new TextBlock { Text = (landmark + 1).ToString(CultureInfo.InvariantCulture), Foreground = colour, FontWeight = FontWeights.Bold, FontSize = size * 0.48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var marker = new Marker { Visual = visual, Position = position };
        Move(marker, position);
        visual.MouseLeftButtonDown += (_, e) => { _dragging = marker; _dragCanvas = canvas; visual.CaptureMouse(); if (live) MarkPlaced(marker); Status.Text = $"{landmark + 1}  {FaceFit.Landmarks[landmark].Name}"; e.Handled = true; };
        visual.MouseMove += (_, e) =>
        {
            if (_dragging != marker || _dragCanvas is null) return;
            var p = e.GetPosition(_dragCanvas);
            Move(marker, new Vector2((float)Math.Clamp(p.X, 0, _dragCanvas.Width), (float)Math.Clamp(p.Y, 0, _dragCanvas.Height)));
            if (live && !_live.IsEnabled) _live.Start();
        };
        visual.MouseLeftButtonUp += (_, _) => { if (_dragging == marker) { _dragging = null; visual.ReleaseMouseCapture(); MarkPlaced(marker); dropped(); } };
        canvas.Children.Add(visual);
        ZoomPanel.SetKeepScreenSize(visual, true); // same size on screen at any zoom
        return marker;
    }
    private static void MarkPlaced(Marker marker) { marker.Placed = true; marker.Visual.Opacity = 1; }
    private static void Resize(Marker marker, double size)
    {
        var grid = (Grid)marker.Visual; grid.Width = grid.Height = size;
        foreach (var child in grid.Children) { if (child is Ellipse e) e.StrokeThickness = Math.Max(1, size / 9); else if (child is TextBlock t) t.FontSize = size * 0.48; }
        Move(marker, marker.Position);
    }
    private static void Move(Marker marker, Vector2 position)
    {
        marker.Position = position;
        Canvas.SetLeft(marker.Visual, position.X - marker.Visual.Width / 2); Canvas.SetTop(marker.Visual, position.Y - marker.Visual.Height / 2);
    }

    // ---- fit ----------------------------------------------------------------------------------------------
    private void Queue() { _refit.Stop(); _refit.Start(); }
    private void OnSetting(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) Queue(); }
    private void OnOption(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, ShowResult)) ShowHeads(); else Queue(); }

    /// <summary>
    /// The face points on the head itself: the detector run on a plain front render of the unfitted head, and the head
    /// surface under each point. Null (once, with a note) when no face is found on the render.
    /// </summary>
    private (Vector3[] Render, Vector3?[] Surface, float UnitsPerPixel)? HeadFace()
    {
        if (_headFaceTried) return _headFace;
        _headFaceTried = true;
        const int size = 640;
        var camera = _frontHead.Camera;
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(120, 120, 120)));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.3, -0.4, -1)));
        for (int p = 0; p < _frontHead.Visuals.Count; p++)
        {
            var clone = _frontHead.Visuals[p].Clone();
            var mesh = (MeshGeometry3D)clone.Geometry;
            mesh.Positions = new Point3DCollection(_positions[p].Select(v => new Point3D(v.X, v.Y, v.Z))); mesh.Normals = new Vector3DCollection();
            group.Children.Add(clone);
        }
        var viewport = new Viewport3D { Camera = camera.Clone(), Width = size, Height = size };
        viewport.Children.Add(new ModelVisual3D { Content = group });
        var root = new Grid { Width = size, Height = size, Background = new SolidColorBrush(Color.FromRgb(160, 170, 180)) };
        root.Children.Add(viewport);
        root.Measure(new Size(size, size)); root.Arrange(new Rect(0, 0, size, size)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var (pixels, width, height) = Ui.Pixels(bitmap);
        _landmarker ??= new FaceLandmarker(ModelPath);
        if (_landmarker.Detect(pixels, width, height) is not { } detection) return null;
        float units = (float)(camera.Width / size);
        var surface = detection.Points.Take(DenseFit.PointCount).Select(px => FaceFit.FrontSurface(_positions, _parts, _faceParts,
            new Vector2((float)camera.Position.X + (px.X - size / 2f) * units, (float)camera.Position.Y - (px.Y - size / 2f) * units))).ToArray();
        return _headFace = (detection.FaceSpace(), surface, units);
    }

    private Vector3[][] Compute()
    {
        int n = FaceFit.Landmarks.Length;
        Vector2?[] Points(PhotoView view) => Enumerable.Range(0, n).Select(i => view.Loaded && view.Markers.TryGetValue(i, out var m) && m.Placed ? (Vector2?)m.Position : null).ToArray();
        _notes.Clear();
        if (Dense.IsChecked == true && _frontPhoto.Loaded && _frontDetection is { } photo)
        {
            (Vector3[] Render, Vector3?[] Surface, float UnitsPerPixel)? head = null;
            try { head = HeadFace(); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException) { }
            if (head is { } h)
            {
                var all = _positions.SelectMany(p => p).ToArray();
                float mirrorX = (all.Min(v => v.X) + all.Max(v => v.X)) / 2;
                var (from, to) = DenseFit.Controls(h.Surface, h.Render, photo.FaceSpace(), h.UnitsPerPixel, Symmetric.IsChecked == true, mirrorX, (float)(Strength.Value * DenseGain), (float)DepthStrength.Value);
                var shaped = FaceFit.Fit(_positions, from, to, _neckY);
                // The side photo's markers then set the profile (depth only; the front photo already set width and height).
                var placed = _anchors.Select(a => a?.Position(shaped) ?? Vector3.Zero).ToArray();
                var profile = FaceFit.Targets(placed, new Vector2?[n], Points(_sidePhoto), Symmetric.IsChecked == true, (float)Strength.Value, _notes);
                for (int i = 0; i < n; i++) profile[i] = _anchors[i] is not null && profile[i] is { } t ? placed[i] with { Z = t.Z } : null;
                return profile.Any(t => t is not null) ? FaceFit.Fit(shaped, _anchors, profile, _neckY) : shaped;
            }
            _notes.Add("The automatic fit could not find the face on the head itself, so the numbered front markers are used.");
        }
        var anchorPositions = _anchors.Select(a => a?.Position(_positions) ?? Vector3.Zero).ToArray();
        var targets = FaceFit.Targets(anchorPositions, Points(_frontPhoto), Points(_sidePhoto), Symmetric.IsChecked == true, (float)Strength.Value, _notes);
        for (int i = 0; i < n; i++) if (_anchors[i] is null) targets[i] = null;
        return FaceFit.Fit(_positions, _anchors, targets, _neckY);
    }
    private void Refit()
    {
        if (!_frontPhoto.Loaded && !_sidePhoto.Loaded) { _fitted = null; ShowHeads(); return; }
        try
        {
            _fitted = Compute();
            float moved = _positions.Zip(_fitted).SelectMany(z => z.First.Zip(z.Second, Vector3.Distance)).Max();
            ShowResult.IsChecked = true; ShowHeads();
            Status.Text = (_notes.Count > 0 ? string.Join(" ", _notes) + " " : "")
                + $"Fitted: the largest move is {moved:F2} units. Turn off 'Show fitted shape' to adjust head markers; Apply makes it one undo step.";
        }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
    }
    private void OnReset(object sender, RoutedEventArgs e)
    {
        ResetAnchors();
        foreach (var view in new[] { _frontPhoto, _sidePhoto }) if (view.Loaded) PlacePhotoMarkers(view);
        ShowResult.IsChecked = false; _fitted = null; ShowHeads(); Queue();
    }
    private void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_frontPhoto.Loaded && !_sidePhoto.Loaded) { Status.Text = "Load at least one photo first."; return; }
            Result = Compute(); DialogResult = true;
        }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
    }
}
