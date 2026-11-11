using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio.Views;

public partial class LikenessWindow
{
    private PhotoTextureView? RegisteredPhoto(PhotoView view, Vector3[][] positions, bool includeSurfacePins = false)
    {
        if (!view.Loaded || view.Pixels is null) return null;
        var pins = view.Markers.Where(m => m.Value.Placed && _anchors[m.Key] is not null)
            .Select(m => (_anchors[m.Key]!.Value.Position(positions), m.Value.Position)).ToList();
        if(includeSurfacePins) pins.AddRange(_surfacePins.Where(p=>p.Side==view.Side).Select(p=>(p.Anchor.Position(positions),p.Marker.Position)));
        return pins.Count >= 4 ? new PhotoTextureView(view.Pixels, view.Width, view.Height, view.Side, pins) : null;
    }

    /// <summary>Builds from the current markers and a fitted mesh, also used by the Apply action.</summary>
    public BitmapSource BuildPhotoTexture(Vector3[][]? positions = null)
    {
        if (_baseTexture is null) throw new ArgumentException("Open a head with a face texture first.");
        positions ??= Compute();
        var photos = new[] { _frontPhoto, _sidePhoto }.Select(v => RegisteredPhoto(v, positions, includeSurfacePins:true)).OfType<PhotoTextureView>().ToArray();
        if (photos.Length == 0) throw new ArgumentException("Confirm at least four markers spread across a photo, then build its texture.");
        var (pixels, width, height) = Ui.Pixels(_baseTexture);
        var baked = PhotoTexture.Bake(pixels, width, height, _parts, positions, _faceParts, photos, (float)TextureStrength.Value, EqualizePhotos.IsChecked == true);
        _photoTexture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, baked.Pixels, width * 4);
        _photoTexture.Freeze();
        PreviewPhotoTexture(_photoTexture);
        Status.Text = $"Photo texture preview uses {photos.Length} view(s), covering {baked.PaintedPixels:N0} atlas pixels. Check the eyes, lips and beard alignment before Apply.";
        return _photoTexture;
    }

    private void OnBuildTexture(object sender, RoutedEventArgs e)
    {
        try
        {
            _fitted = Compute(); ShowResult.IsChecked = true; ShowHeads();
            BuildPhotoTexture(_fitted); UsePhotoTexture.IsChecked = true;
        }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
    }
    private void OnTextureOption(object sender, RoutedEventArgs e)
    {
        InvalidatePhotoTexture();
        Status.Text = "Build / preview photo texture updates the head views with these settings.";
    }
    private void OnTextureSetting(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) InvalidatePhotoTexture();
    }
    private void InvalidatePhotoTexture()
    {
        if (_photoTexture is null) return;
        _photoTexture = null;
        if (_baseTexture is not null) PreviewPhotoTexture(_baseTexture);
    }
    private void PreviewPhotoTexture(BitmapSource texture)
    {
        var material = new DiffuseMaterial(new ImageBrush(texture) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill });
        foreach (var view in new[] { _frontHead, _sideHead }) foreach (int p in _faceParts) view.Visuals[p].Material = material;
    }

    private void UpdatePhotoOverlays(Vector3[][] positions)
    {
        foreach (var view in new[] { _frontPhoto, _sidePhoto })
        {
            foreach (var old in view.Canvas.Children.OfType<Path>().Where(p => Equals(p.Tag, "PhotoWire")).ToArray()) view.Canvas.Children.Remove(old);
            if (PhotoOverlay.IsChecked != true || RegisteredPhoto(view, positions) is not { } photo) continue;
            try
            {
                var project = ShowResult.IsChecked == true && _surfaceCameras.TryGetValue(view.Side,out var camera)?camera:PhotoTexture.Projection(photo, warp: false);
                var wire = new StreamGeometry();
                using (var context = wire.Open())
                {
                    foreach (int p in _faceParts)
                    {
                        var part = _parts[p]; var vertices = positions[p];
                        var edges = new HashSet<(int,int)>();
                        for (int t = 0; t + 2 < part.Indices.Length; t += 3)
                        {
                            var a = vertices[part.Indices[t]]; var b = vertices[part.Indices[t + 1]]; var c = vertices[part.Indices[t + 2]];
                            var normal = Vector3.Cross(b - a, c - a);
                            if ((view.Side ? normal.X : normal.Z) <= 0) continue;
                            for(int edge=0;edge<3;edge++)
                            {
                                int from=part.Indices[t+edge],to=part.Indices[t+(edge+1)%3];
                                if(!edges.Add(from<to?(from,to):(to,from)))continue;
                                var start=project(vertices[from]);var end=project(vertices[to]);
                                context.BeginFigure(new Point(start.X,start.Y),false,false);
                                context.LineTo(new Point(end.X,end.Y),true,false);
                            }
                        }
                    }
                }
                wire.Freeze();
                view.Canvas.Children.Insert(0, new Path { Tag = "PhotoWire", Data = wire, Stroke = Brushes.SlateBlue, StrokeThickness = 0.65, Opacity = 0.75, IsHitTestVisible = false });
                UpdateWireThickness(view,view.Side?SidePhotoZoom:FrontPhotoZoom);
            }
            catch (ArgumentException) { } // more widely spread markers are needed for an affine camera
        }
    }
}
