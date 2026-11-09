using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Cricket07.FaceStudio.Views;

/// <summary>
/// Picks the part of a reference photo to show. Non-destructive: only a rectangle (fractions of the photo) is
/// returned; the photo itself is never changed, so the crop can be redone or reset later.
/// </summary>
public partial class CropWindow : Window
{
    private enum Drag { None, Draw, Move, Resize }
    private readonly int _width, _height;
    private readonly Rectangle[] _handles = new Rectangle[4]; // corners: top-left, top-right, bottom-right, bottom-left
    private Rect _crop;          // photo pixels
    private Drag _drag; private Point _from; private Rect _startCrop; private int _corner;

    /// <summary>The chosen area as fractions of the photo (0..1), set when the user applies.</summary>
    public Rect? Result { get; private set; }

    /// <summary>The area being edited, as fractions of the photo (0..1).</summary>
    public Rect Crop
    {
        get => new(_crop.X / _width, _crop.Y / _height, _crop.Width / _width, _crop.Height / _height);
        set { _crop = Clamp(new Rect(value.X * _width, value.Y * _height, value.Width * _width, value.Height * _height)); Redraw(); }
    }

    public CropWindow(BitmapSource image, Rect crop)
    {
        InitializeComponent();
        Ui.FitToScreen(this);
        var (pixels, width, height) = Ui.Pixels(image);
        (_width, _height) = (width, height);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze(); // 1 image pixel = 1 unit
        Photo.Source = bitmap; Overlay.Width = width; Overlay.Height = height;
        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Rectangle { Width = 12, Height = 12, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(210, 150, 30)), StrokeThickness = 2, Tag = i, Cursor = i % 2 == 0 ? Cursors.SizeNWSE : Cursors.SizeNESW };
            Overlay.Children.Add(_handles[i]); ZoomPanel.SetKeepScreenSize(_handles[i], true);
        }
        Zoom.MagnificationChanged += (_, _) => Frame.StrokeThickness = 2 / Math.Max(1e-3, Zoom.Scale);
        Crop = crop;
    }

    private Rect Clamp(Rect r)
    {
        double x = Math.Clamp(r.X, 0, _width - 8), y = Math.Clamp(r.Y, 0, _height - 8);
        return new Rect(x, y, Math.Clamp(r.Width, 8, _width - x), Math.Clamp(r.Height, 8, _height - y));
    }

    private void Redraw()
    {
        Canvas.SetLeft(Frame, _crop.X); Canvas.SetTop(Frame, _crop.Y); Frame.Width = _crop.Width; Frame.Height = _crop.Height;
        Shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, _width, _height)), new RectangleGeometry(_crop));
        var corners = new[] { _crop.TopLeft, _crop.TopRight, _crop.BottomRight, _crop.BottomLeft };
        for (int i = 0; i < 4; i++) { Canvas.SetLeft(_handles[i], corners[i].X - 6); Canvas.SetTop(_handles[i], corners[i].Y - 6); }
        bool whole = _crop.Width >= _width - 0.5 && _crop.Height >= _height - 0.5;
        Info.Text = whole ? $"Showing the whole photo ({_width} × {_height})" : $"Keeping {_crop.Width:F0} × {_crop.Height:F0} of {_width} × {_height} px";
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _from = e.GetPosition(Overlay); _startCrop = _crop;
        if (e.OriginalSource is Rectangle { Tag: int corner }) { _drag = Drag.Resize; _corner = corner; }
        else if (_crop.Contains(_from) && !(_crop.Width >= _width - 0.5 && _crop.Height >= _height - 0.5)) _drag = Drag.Move;
        else _drag = Drag.Draw;
        Overlay.CaptureMouse(); e.Handled = true; // keep the panel from panning on left drags
    }
    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        var p = e.GetPosition(Overlay); p = new Point(Math.Clamp(p.X, 0, _width), Math.Clamp(p.Y, 0, _height));
        switch (_drag)
        {
            case Drag.Draw:
                if ((p - _from).Length < 3) return;
                _crop = Clamp(new Rect(_from, p)); break;
            case Drag.Move:
                var d = p - _from;
                _crop = new Rect(Math.Clamp(_startCrop.X + d.X, 0, _width - _startCrop.Width), Math.Clamp(_startCrop.Y + d.Y, 0, _height - _startCrop.Height), _startCrop.Width, _startCrop.Height); break;
            case Drag.Resize:
                var opposite = new[] { _startCrop.BottomRight, _startCrop.BottomLeft, _startCrop.TopLeft, _startCrop.TopRight }[_corner];
                _crop = Clamp(new Rect(opposite, p)); break;
        }
        Redraw();
    }
    private void OnUp(object sender, MouseButtonEventArgs e) { if (_drag != Drag.None) { _drag = Drag.None; Overlay.ReleaseMouseCapture(); } }
    private void OnLost(object sender, MouseEventArgs e) => _drag = Drag.None;
    private void OnReset(object sender, RoutedEventArgs e) => Crop = new Rect(0, 0, 1, 1);
    private void OnApply(object sender, RoutedEventArgs e) { Result = Crop; DialogResult = true; }
}
