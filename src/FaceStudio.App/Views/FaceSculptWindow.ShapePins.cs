using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio.Views;

public partial class FaceSculptWindow
{
    private readonly List<MeshAnchor> _shapePins = new();
    private int _selectedShapePin = -1, _shapeDrag = -1;
    private Point _shapeDragStart;

    private void OnPhotoProportionsOpened(object sender, RoutedEventArgs e)
    {
        ShapePins.IsChecked = true;
        OnShapeMode(sender, e);
    }
    private void OnPhotoProportionsClosed(object sender, RoutedEventArgs e)
    {
        ShapePins.IsChecked = false;
        EndShapeDrag(); QueueRedrawPins();
    }
    private void OnShapeMode(object sender, RoutedEventArgs e)
    {
        if (ShapePins.IsChecked == true)
        {
            HeadTool.SelectedIndex = 0; VertexMode.IsChecked = false; ReferenceEdit.IsChecked = false;
            ShowWireframe.IsChecked = true; OnWireframe(sender, e);
            Status.Text = "Align the reference photo, then click the head to add a shape pin and drag it onto the matching photo feature. Undo restores each drag.";
        }
        else EndShapeDrag();
        QueueRedrawPins();
    }
    private void OnShapeReference(object sender, RoutedEventArgs e)
    {
        OnReference(sender, e);
        if ((ReferenceMode.SelectedIndex == 1 ? FrontReference.Source : SideReference.Source) is null) return;
        ShapePins.IsChecked=false;EndShapeDrag();QueueRedrawPins();
        ReferenceOverlay.IsChecked = true; ReferenceEdit.IsChecked = true;
        OnReferenceEdit(sender, e);
        Status.Text = "Move and scale the photo to align it with the head, then enable Sculpt with shape pins to match the proportions.";
    }

    private (Point Screen, bool Visible)? ShapePinScreen(int index)
    {
        if (index < 0 || index >= _shapePins.Count) return null;
        var anchor = _shapePins[index];
        var vertices = ((MeshGeometry3D)_parts[anchor.Part].Visual.Geometry).Positions;
        var a = vertices[anchor.A]; var b = vertices[anchor.B]; var c = vertices[anchor.C];
        var point = new Point3D(a.X * anchor.WA + b.X * anchor.WB + c.X * anchor.WC,
            a.Y * anchor.WA + b.Y * anchor.WB + c.Y * anchor.WC, a.Z * anchor.WA + b.Z * anchor.WB + c.Z * anchor.WC);
        if (Project(point) is not { } screen) return null;
        var hit = HitHead(Viewport.TranslatePoint(screen, Viewport.Viewport));
        return (screen, hit is not null && (hit.PointHit - point).Length < _size * .02);
    }
    private int NearestShapePin(Point point)
    {
        int nearest = -1; double distance = 12;
        for (int i = 0; i < _shapePins.Count; i++)
            if (ShapePinScreen(i) is { Visible: true } pin && (pin.Screen - point).Length <= distance)
            { nearest = i; distance = (pin.Screen - point).Length; }
        return nearest;
    }
    private bool BeginShapeDrag(Point point)
    {
        int index = NearestShapePin(point);
        if (index < 0)
        {
            if (HitHead(Viewport.TranslatePoint(point, Viewport.Viewport)) is not { } hit) return false;
            int part = _parts.FindIndex(p => p.Visual.Geometry == hit.MeshHit);
            if (Locked(part) || (Floor is float floor && hit.PointHit.Y <= floor))
            { Status.Text = "That part or neck region is locked."; return false; }
            _shapePins.Add(new MeshAnchor(part, hit.VertexIndex1, hit.VertexIndex2, hit.VertexIndex3,
                (float)hit.VertexWeight1, (float)hit.VertexWeight2, (float)hit.VertexWeight3));
            index = _shapePins.Count - 1;
        }
        if (Locked(_shapePins[index].Part)) return false;
        _selectedShapePin = _shapeDrag = index;
        _shapeDragStart = point; _strokeSource = AllPositions(); _strokeRecorded = false;
        QueueRedrawPins();
        Status.Text = "Shape pin selected. Drag it onto the reference feature; other pins hold their positions.";
        return true;
    }
    private void DragShapePin(Point point)
    {
        if (_shapeDrag < 0 || _strokeSource is null) return;
        var movement = point - _shapeDragStart;
        if (movement.Length < 2 && !_strokeRecorded) return;
        var basis = CameraAxes();
        var world = (basis.Right * movement.X - basis.Up * movement.Y) * PixelScale;
        var delta = new Vector3((float)world.X, (float)world.Y, (float)world.Z);
        var controls = _shapePins.Select((anchor, i) =>
        {
            var from = anchor.Position(_strokeSource);
            return (From: from, To: i == _shapeDrag ? from + delta : from);
        }).ToArray();
        var result = SurfacePinFit.Apply(_strokeSource, controls, (float)(_size * ShapePinReach.Value / 100), Floor ?? float.NegativeInfinity, Plane);
        if (!_strokeRecorded) { _undo.Push(new UndoStep(Snapshot())); _redo.Clear(); _strokeRecorded = true; }
        for (int p = 0; p < _parts.Count; p++)
            if (!Locked(p)) SetPositions(p, result[p].Select(v => new Point3D(v.X, v.Y, v.Z)));
        Status.Text = "Matching photo proportions. Release to finish this shape edit; Undo restores it.";
    }
    private void EndShapeDrag()
    {
        _shapeDrag = -1;
        _strokeSource = null;
    }
    private void OnRemoveShapePin(object sender, RoutedEventArgs e)
    {
        if (_selectedShapePin < 0 || _selectedShapePin >= _shapePins.Count) return;
        _shapePins.RemoveAt(_selectedShapePin); _selectedShapePin = -1; EndShapeDrag(); QueueRedrawPins();
        Status.Text = "Shape pin handle removed. The current head shape is kept.";
    }
    private void OnClearShapePins(object sender, RoutedEventArgs e)
    {
        _shapePins.Clear(); _selectedShapePin = -1; EndShapeDrag(); QueueRedrawPins();
    }
    private void RedrawShapePins()
    {
        ShapePinLayer.Children.Clear();
        if(PhotoProportions.IsExpanded&&ShowWireframe.IsChecked==true)DrawShapeWireframe();
        if (ShapePins.IsChecked != true) return;
        for (int i = 0; i < _shapePins.Count; i++)
        {
            if (ShapePinScreen(i) is not { Visible: true } pin) continue;
            var dot = new Ellipse { Width = 12, Height = 12, Fill = i == _selectedShapePin ? Brushes.OrangeRed : Brushes.Gold, Stroke = Brushes.Black, StrokeThickness = 1.5 };
            Canvas.SetLeft(dot, pin.Screen.X - 6); Canvas.SetTop(dot, pin.Screen.Y - 6); ShapePinLayer.Children.Add(dot);
        }
    }
    private void DrawShapeWireframe()
    {
        var wire=new StreamGeometry();
        using(var context=wire.Open())
        foreach(var part in _parts)
        {
            var mesh=(MeshGeometry3D)part.Visual.Geometry;
            var edges=new HashSet<(int,int)>();
            for(int t=0;t+2<mesh.TriangleIndices.Count;t+=3)
            {
                int a=mesh.TriangleIndices[t],b=mesh.TriangleIndices[t+1],c=mesh.TriangleIndices[t+2];
                var normal=Vector3D.CrossProduct(mesh.Positions[b]-mesh.Positions[a],mesh.Positions[c]-mesh.Positions[a]);
                if(Vector3D.DotProduct(normal,Viewport.Camera.LookDirection)>=0)continue;
                for(int e=0;e<3;e++)
                {
                    int from=mesh.TriangleIndices[t+e],to=mesh.TriangleIndices[t+(e+1)%3];
                    if(!edges.Add(from<to?(from,to):(to,from)))continue;
                    if(Project(mesh.Positions[from]) is not {} start||Project(mesh.Positions[to]) is not {} end)continue;
                    context.BeginFigure(start,false,false);context.LineTo(end,true,false);
                }
            }
        }
        wire.Freeze();
        ShapePinLayer.Children.Add(new System.Windows.Shapes.Path{Data=wire,Stroke=Brushes.SlateBlue,StrokeThickness=.65,Opacity=.85});
    }
}
