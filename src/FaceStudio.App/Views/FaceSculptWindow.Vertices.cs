using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf;

namespace Cricket07.FaceStudio.Views;

/// <summary>
/// Wireframe overlay and single-vertex grab: on a ~700-vertex game head, likeness comes down to where individual
/// vertices sit (nostril wing, lip corner, eyelid), so seeing the edges and moving one point is the precise tool.
/// </summary>
public partial class FaceSculptWindow
{
    private LinesVisual3D? _wire;
    private int[][]? _edges;                 // per part: vertex index pairs of its unique edges (topology never changes)
    private bool _wireQueued;
    /// <summary>The vertex being moved (and its copies along texture seams, and its mirror partner), or null.</summary>
    private List<(int Part, int Index, bool Mirror)>? _grabVertices;

    private void OnWireframe(object sender, RoutedEventArgs e)
    {
        if (ShowWireframe.IsChecked == true)
        {
            if (_wire is not null) return;
            _edges ??= _parts.Select(p => Edges(((MeshGeometry3D)p.Visual.Geometry).TriangleIndices)).ToArray();
            // Light, slightly see-through lines nudged toward the camera so they sit on the surface without flicker.
            _wire = new LinesVisual3D { Color = Color.FromArgb(190, 120, 225, 255), Thickness = 1, DepthOffset = 1e-4 };
            Viewport.Children.Add(_wire); UpdateWireframe();
        }
        else if (_wire is not null) { Viewport.Children.Remove(_wire); _wire = null; }
    }
    private static int[] Edges(Int32Collection triangles)
    {
        var edges = new HashSet<(int, int)>();
        for (int k = 0; k + 2 < triangles.Count; k += 3)
            for (int e = 0; e < 3; e++) { int a = triangles[k + e], b = triangles[k + (e + 1) % 3]; edges.Add(a < b ? (a, b) : (b, a)); }
        return edges.SelectMany(e => new[] { e.Item1, e.Item2 }).ToArray();
    }
    private void UpdateWireframe()
    {
        if (_wire is null || _edges is null) return;
        var points = new Point3DCollection(_edges.Sum(e => e.Length));
        for (int p = 0; p < _parts.Count; p++)
        {
            var positions = ((MeshGeometry3D)_parts[p].Visual.Geometry).Positions;
            foreach (int i in _edges[p]) points.Add(positions[i]);
        }
        _wire.Points = points;
    }
    /// <summary>Rebuilds the wireframe once the current edit has been handled (sculpts move vertices).</summary>
    private void QueueWireframe()
    {
        if (_wire is null || _wireQueued) return;
        _wireQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { _wireQueued = false; UpdateWireframe(); });
    }

    private void OnVertexMode(object sender, RoutedEventArgs e)
    {
        bool on = VertexMode.IsChecked == true;
        if (on) { HeadTool.SelectedIndex = 0; ReferenceEdit.IsChecked = false; } // left drag must sculpt
        if (!on) HideVertexMarkers();
        Status.Text = on ? "Single vertex: the dot shows the point under the cursor; left drag moves just that point (its seam copies and, with Mirror, its partner follow). Show wireframe (W) helps."
                         : "Left drag sculpts with the brush radius again.";
    }

    /// <summary>
    /// The vertex nearest the cursor on the head (a corner of the triangle under it), every vertex at the same spot (the
    /// mesh repeats vertices along texture seams and between parts; they must move together or the surface tears), and,
    /// with Mirror on, the same for its partner on the other side of the face.
    /// </summary>
    internal List<(int Part, int Index, bool Mirror)>? PickVertices(Point viewportPoint)
    {
        if (HitHead(viewportPoint) is not { } hit) return null;
        var mesh = hit.MeshHit; var cursor = Viewport.Viewport.TranslatePoint(viewportPoint, Viewport);
        int best = new[] { hit.VertexIndex1, hit.VertexIndex2, hit.VertexIndex3 }.MinBy(i => Project(mesh.Positions[i]) is { } s ? (s - cursor).Length : double.MaxValue);
        var target = mesh.Positions[best];
        var picked = Coincident(target).Select(v => (v.Part, v.Index, Mirror: false)).ToList();
        if (Plane is float plane && Math.Abs(target.X - plane) > _size * 1e-3)
        {
            var mirror = new Point3D(2 * plane - target.X, target.Y, target.Z);
            var nearest = Enumerable.Range(0, _parts.Count).SelectMany(p => ((MeshGeometry3D)_parts[p].Visual.Geometry).Positions.Select((v, i) => (Part: p, Index: i, Distance: (v - mirror).Length)))
                .MinBy(v => v.Distance);
            if (nearest.Distance < _size * 0.02)
                picked.AddRange(Coincident(((MeshGeometry3D)_parts[nearest.Part].Visual.Geometry).Positions[nearest.Index]).Select(v => (v.Part, v.Index, Mirror: true)));
        }
        float? floor = Floor;
        picked.RemoveAll(v => Locked(v.Part) || (floor is float y && ((MeshGeometry3D)_parts[v.Part].Visual.Geometry).Positions[v.Index].Y <= y));
        return picked.Count > 0 ? picked : null;
    }
    private IEnumerable<(int Part, int Index)> Coincident(Point3D point)
    {
        double near = _size * 1e-4;
        for (int p = 0; p < _parts.Count; p++)
        {
            var positions = ((MeshGeometry3D)_parts[p].Visual.Geometry).Positions;
            for (int i = 0; i < positions.Count; i++) if ((positions[i] - point).Length < near) yield return (p, i);
        }
    }
    /// <summary>Moves the grabbed vertices by a world displacement from where they were when the drag began.</summary>
    private void MoveVertices(List<(int Part, int Index, bool Mirror)> vertices, Vector3 displacement)
    {
        float? plane = Plane;
        float? floor = Floor;
        foreach (int part in vertices.Select(v => v.Part).Distinct().Where(p => !Locked(p)))
        {
            var positions = _strokeSource![part].ToArray();
            foreach (var v in vertices.Where(v => v.Part == part))
            {
                if (floor is float y && positions[v.Index].Y <= y) continue;
                var d = v.Mirror ? new Vector3(-displacement.X, displacement.Y, displacement.Z) : displacement;
                if (plane is float x && Math.Abs(positions[v.Index].X - x) < _size * 1e-3) d.X = 0; // a centre-line point stays on the centre line
                positions[v.Index] += d;
            }
            SetPositions(part, positions.Select(v => new Point3D(v.X, v.Y, v.Z)));
        }
        ShowVertexMarkers(vertices);
    }

    /// <summary>Dot on the vertex a press would grab (and a fainter one on its mirror partner).</summary>
    private void UpdateVertexMarkers(Point viewportPoint)
    {
        if (VertexMode.IsChecked != true || HeadPaintTool is not null || HeadPins || _orbit || ReferenceEdit.IsChecked == true) { HideVertexMarkers(); return; }
        if (_grabVertices is not null) return; // dragging: MoveVertices keeps the dots on the moving point
        if (PickVertices(Viewport.TranslatePoint(viewportPoint, Viewport.Viewport)) is { } picked) ShowVertexMarkers(picked); else HideVertexMarkers();
    }
    private void ShowVertexMarkers(List<(int Part, int Index, bool Mirror)> picked)
    {
        void Place(FrameworkElement marker, (int Part, int Index, bool Mirror)? v)
        {
            if (v is { } vertex && Project(((MeshGeometry3D)_parts[vertex.Part].Visual.Geometry).Positions[vertex.Index]) is { } s)
            { Canvas.SetLeft(marker, s.X - marker.Width / 2); Canvas.SetTop(marker, s.Y - marker.Height / 2); marker.Visibility = Visibility.Visible; }
            else marker.Visibility = Visibility.Collapsed;
        }
        Place(VertexMarker, picked.FirstOrDefault(v => !v.Mirror));
        Place(VertexMirrorMarker, picked.Any(v => v.Mirror) ? picked.First(v => v.Mirror) : null);
    }
    private void HideVertexMarkers() { VertexMarker.Visibility = VertexMirrorMarker.Visibility = Visibility.Collapsed; }
}
