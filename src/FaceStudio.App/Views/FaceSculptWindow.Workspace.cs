using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Cricket07.FaceStudio.Views;

/// <summary>
/// Workspace layout: the tool rail (one active tool, shown with its own options in the panel) and smooth camera moves.
/// The rail drives the existing mode switches (VertexMode, ShapePins, ReferenceEdit, HeadTool), so every tool keeps its
/// own logic; <see cref="SyncTools"/> mirrors whatever mode is active back onto the rail and the panel.
/// </summary>
public partial class FaceSculptWindow
{
    private sealed record ToolInfo(string Key, string Icon, string Title, string Help, string Hint);
    private static readonly ToolInfo[] Tools =
    {
        new("sculpt","","Sculpt","Left drag on the head pulls the surface under the brush. Click a spot to use the nudge buttons below.","Left drag: pull  ·  V: vertex  ·  W: wireframe  ·  Ctrl+Z: undo"),
        new("vertex","","Single vertex","Left drag moves one point of the mesh (its seam copies, and with Mirror its partner, follow). The yellow dot shows the point under the cursor.","Left drag: move point  ·  V: back to sculpt  ·  W: wireframe"),
        new("shape","","Shape pins","Click the head to add a pin, then drag it onto the matching feature of the reference photo. Other pins hold their spots.","Click: add pin  ·  Drag: move pin  ·  Right-click pin: remove"),
        new("paint","","Paint","Left drag paints the colour and keeps the texture's own detail.","Left drag: paint  ·  Alt or I: pick colour  ·  [ ]: brush size"),
        new("blur","","Blur","Left drag softens the texture under the brush.","Left drag: blur  ·  [ ]: brush size"),
        new("clone","","Clone","Ctrl+click the head where to copy from, then paint where it should go.","Ctrl+click: source  ·  Left drag: copy  ·  [ ]: brush size"),
        new("pinwarp","","Pin warp","Drag a painted feature (beard edge, lip line, brow) to where it belongs on the head. Click without dragging to pin a spot in place.","Drag: move feature  ·  Click: pin a spot  ·  Delete: remove pin"),
        new("photo","","Move photo","Left drag moves the reference photo to line it up with the head. Size and opacity are below.","Left drag: move photo"),
    };

    /// <summary>The active tool, read from the mode switches.</summary>
    private string ActiveTool =>
        ReferenceEdit.IsChecked == true ? "photo"
        : ShapePins.IsChecked == true ? "shape"
        : VertexMode.IsChecked == true ? "vertex"
        : HeadTool.SelectedIndex switch { 1 => "paint", 2 => "blur", 3 => "clone", 4 => "pinwarp", _ => "sculpt" };

    private void OnToolRail(object sender, RoutedEventArgs e) => SelectTool((string)((RadioButton)sender).CommandParameter);

    /// <summary>Switches to a tool by its key (see <see cref="Tools"/>), turning the other modes off.</summary>
    public void SelectTool(string key)
    {
        if (key == "photo" && ReferenceMode.SelectedIndex <= 0)
        {
            int loaded = FrontReference.Source is not null ? 1 : SideReference.Source is not null ? 2 : 0;
            if (loaded == 0) { Status.Text = "Load a front or side photo under Reference photos first."; SyncTools(); return; }
            ReferenceMode.SelectedIndex = loaded;
        }
        // Leave the current mode cleanly, then enter the new one through its own handler.
        if (VertexMode.IsChecked == true && key != "vertex") { VertexMode.IsChecked = false; OnVertexMode(VertexMode, new RoutedEventArgs()); }
        if (ShapePins.IsChecked == true && key != "shape") { ShapePins.IsChecked = false; OnShapeMode(ShapePins, new RoutedEventArgs()); }
        if (ReferenceEdit.IsChecked == true && key != "photo") { ReferenceEdit.IsChecked = false; OnReferenceEdit(ReferenceEdit, new RoutedEventArgs()); }
        int paint = key switch { "paint" => 1, "blur" => 2, "clone" => 3, "pinwarp" => 4, _ => 0 };
        if (HeadTool.SelectedIndex != paint) HeadTool.SelectedIndex = paint; // OnHeadTool does the rest
        switch (key)
        {
            case "vertex": VertexMode.IsChecked = true; OnVertexMode(VertexMode, new RoutedEventArgs()); break;
            case "shape": ShapePins.IsChecked = true; OnShapeMode(ShapePins, new RoutedEventArgs()); break;
            case "photo": ReferenceEdit.IsChecked = true; OnReferenceEdit(ReferenceEdit, new RoutedEventArgs()); break;
        }
        SyncTools();
    }

    /// <summary>Shows the active tool on the rail and only its options in the panel.</summary>
    private void SyncTools()
    {
        if (ToolTitle is null) return; // still loading
        string key = ActiveTool;
        var tool = Tools.First(t => t.Key == key);
        foreach (var rail in new[] { ToolSculpt, ToolVertex, ToolShapePins, ToolPaint, ToolBlur, ToolClone, ToolPinWarp, ToolPhoto })
            rail.IsChecked = (string)rail.CommandParameter == key;
        ToolIcon.Text = tool.Icon; ToolTitle.Text = tool.Title; ToolHelp.Text = tool.Help; ToolHint.Text = tool.Hint;
        void Show(FrameworkElement group, params string[] keys) => group.Visibility = keys.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
        Show(GroupBrush, "sculpt");
        Show(GroupShapeOptions, "sculpt", "vertex", "shape");
        Show(GroupNudge, "sculpt");
        Show(GroupShapePins, "shape");
        Show(GroupColour, "paint");
        Show(GroupTextureBrush, "paint", "blur", "clone");
        Show(GroupPinWarp, "pinwarp");
        Show(GroupPhoto, "photo");
    }

    // ---- smooth camera ----------------------------------------------------------------------------------
    private record struct CameraState(Point3D Position, Vector3D Look, Vector3D Up, double Width);
    private CameraState _cameraFrom, _cameraGoal;
    private DateTime _cameraStart;
    private double _cameraMs;
    private bool _cameraMoving;

    private static CameraState StateOf(OrthographicCamera c) => new(c.Position, c.LookDirection, c.UpDirection, c.Width);
    private static void Put(OrthographicCamera c, CameraState s) { c.Position = s.Position; c.LookDirection = s.Look; c.UpDirection = s.Up; c.Width = s.Width; }

    /// <summary>
    /// Runs a camera change (a view preset, zoom, orbit step) as a short eased move instead of a jump. Changes made while
    /// a move is running build on where the camera is heading, so repeated clicks or wheel notches stay smooth. Hidden
    /// windows (tests) change at once.
    /// </summary>
    private void Smoothly(Action change, double milliseconds = 240)
    {
        if (!IsVisible || Viewport.Camera is not OrthographicCamera camera) { change(); return; }
        var now = StateOf(camera);
        if (_cameraMoving) Put(camera, _cameraGoal);
        change();
        var goal = Viewport.Camera is OrthographicCamera moved ? StateOf(moved) : now;
        if (!ReferenceEquals(Viewport.Camera, camera)) Viewport.Camera = camera; // a preset builds a new camera; keep animating this one
        Put(camera, now);
        (_cameraFrom, _cameraGoal, _cameraStart, _cameraMs) = (now, goal, DateTime.UtcNow, milliseconds);
        if (!_cameraMoving) { _cameraMoving = true; CompositionTarget.Rendering += OnCameraFrame; }
    }
    /// <summary>Stops a running camera move where it is (a drag takes over).</summary>
    private void StopCamera()
    {
        if (!_cameraMoving) return;
        _cameraMoving = false; CompositionTarget.Rendering -= OnCameraFrame;
    }
    private void OnCameraFrame(object? sender, EventArgs e)
    {
        if (Viewport.Camera is not OrthographicCamera camera) { StopCamera(); return; }
        double t = Math.Min(1, (DateTime.UtcNow - _cameraStart).TotalMilliseconds / _cameraMs);
        double k = 1 - Math.Pow(1 - t, 3); // ease out
        var a = _cameraFrom; var b = _cameraGoal;
        // Turn around the point the camera looks at, so a view change swings round the head instead of cutting through it.
        var target = a.Position + a.Look + ((b.Position + b.Look) - (a.Position + a.Look)) * k;
        double distance = a.Look.Length + (b.Look.Length - a.Look.Length) * k;
        var direction = Slerp(a.Look, b.Look, k);
        var up = a.Up + (b.Up - a.Up) * k; if (up.LengthSquared < 1e-9) up = b.Up;
        Put(camera, new CameraState(target - direction * distance, direction * distance, up, a.Width + (b.Width - a.Width) * k));
        QueueRedrawPins();
        if (t >= 1) StopCamera();
    }
    /// <summary>Direction between two directions (unit result); opposite directions turn round the vertical axis.</summary>
    private static Vector3D Slerp(Vector3D from, Vector3D to, double k)
    {
        from.Normalize(); to.Normalize();
        double dot = Math.Clamp(Vector3D.DotProduct(from, to), -1, 1);
        if (dot > 0.9999) return to;
        var axis = Vector3D.CrossProduct(from, to);
        if (axis.LengthSquared < 1e-9) axis = new Vector3D(0, 1, 0); // front to back: swing round the head
        axis.Normalize();
        double angle = Math.Acos(dot) * k * 180 / Math.PI;
        var rotation = new Matrix3D(); rotation.Rotate(new Quaternion(axis, angle));
        var result = rotation.Transform(from); result.Normalize();
        return result;
    }
}
