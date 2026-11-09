using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio.Views;

/// <summary>
/// Zoom and pan for an image panel. Wheel or touchpad pinch zooms at the cursor; drag empty space (left), or drag
/// anywhere with the right or middle button, to pan; double-click empty space or press 0 to fit; + and - zoom.
/// Elements marked <see cref="SetKeepScreenSize"/> (the markers) are drawn at a fixed size on screen
/// (<see cref="MarkerScreenSize"/>) whatever the photo resolution or zoom, which is what makes zooming useful.
/// </summary>
[ContentProperty(nameof(Content))]
public sealed class ZoomPanel : Grid
{
    private const float MinZoom = 0.5f, MaxZoom = 16f; // relative to fit
    /// <summary>On-screen size of markers, in device-independent pixels.</summary>
    public const double MarkerScreenSize = 22;
    private readonly Canvas _host = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly MatrixTransform _transform = new();
    private readonly TextBlock _label = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), FontSize = 11 };
    private UIElement? _content;
    private ViewTransform _view = new(1, Vector2.Zero), _fit = new(1, Vector2.Zero);
    private Size _contentSize, _viewportSize;
    private bool _fitted = true;
    private Point? _panFrom;

    public static readonly DependencyProperty KeepScreenSizeProperty = DependencyProperty.RegisterAttached(
        "KeepScreenSize", typeof(bool), typeof(ZoomPanel), new PropertyMetadata(false, (d, e) => { if (d is UIElement u && (bool)e.NewValue) Find(u)?.CounterScale(u); }));
    public static void SetKeepScreenSize(UIElement element, bool value) => element.SetValue(KeepScreenSizeProperty, value);
    public static bool GetKeepScreenSize(UIElement element) => (bool)element.GetValue(KeepScreenSizeProperty);

    /// <summary>Screen pixels per content pixel.</summary>
    public double Scale => _view.Scale;
    /// <summary>Zoom relative to "fit": 1 = whole image visible.</summary>
    public double Magnification => _view.Scale / _fit.Scale;
    public event EventHandler? MagnificationChanged;

    public UIElement? Content
    {
        get => _content;
        set { _host.Children.Clear(); _content = value; if (value is not null) { value.RenderTransform = _transform; _host.Children.Add(value); } _fitted = true; InvalidateArrange(); }
    }

    public ZoomPanel()
    {
        Focusable = true; ClipToBounds = true;
        Children.Add(_host);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4), Background = new SolidColorBrush(Color.FromArgb(150, 20, 24, 32)) };
        bar.Children.Add(_label);
        foreach (var (text, tip, action) in new (string, string, Action)[] { ("+", "Zoom in (+, wheel)", () => ZoomBy(1.5)), ("−", "Zoom out (-, wheel)", () => ZoomBy(1 / 1.5)), ("Fit", "Show the whole image (0, double-click)", Fit) })
        {
            var button = new Button { Content = text, ToolTip = tip, MinWidth = 26, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(1), Focusable = false };
            button.Click += (_, _) => action();
            bar.Children.Add(button);
        }
        Children.Add(bar);
        MouseWheel += OnWheel; MouseDown += OnDown; MouseMove += OnMove; MouseUp += OnUp; LostMouseCapture += (_, _) => _panFrom = null; KeyDown += OnKey;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var result = base.ArrangeOverride(arrangeSize);
        var content = _content?.DesiredSize ?? default;
        bool contentChanged = content != _contentSize, viewportChanged = arrangeSize != _viewportSize;
        _contentSize = content; _viewportSize = arrangeSize;
        if (contentChanged || (viewportChanged && _fitted)) Fit();
        return result;
    }

    /// <summary>Shows the whole content, centred.</summary>
    public void Fit()
    {
        _fit = ViewZoom.Fit(new Vector2((float)_contentSize.Width, (float)_contentSize.Height), new Vector2((float)_viewportSize.Width, (float)_viewportSize.Height));
        _fitted = true; Apply(_fit);
    }
    /// <summary>Zooms about a point in panel coordinates (the centre when omitted).</summary>
    public void ZoomBy(double factor, Point? at = null)
    {
        var p = at ?? new Point(_viewportSize.Width / 2, _viewportSize.Height / 2);
        _fitted = false;
        Apply(ViewZoom.ZoomAt(_view, new Vector2((float)p.X, (float)p.Y), (float)factor, _fit.Scale * MinZoom, _fit.Scale * MaxZoom));
    }

    private void Apply(ViewTransform view)
    {
        _view = view;
        _transform.Matrix = new Matrix(view.Scale, 0, 0, view.Scale, view.Offset.X, view.Offset.Y);
        _label.Text = $"{Magnification * 100:F0}%";
        if (_content is not null) foreach (var element in Descendants(_content)) if (GetKeepScreenSize(element)) CounterScale(element);
        MagnificationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void CounterScale(UIElement element)
    {
        double size = element is FrameworkElement { Width: > 0 } f ? f.Width : MarkerScreenSize;
        double factor = MarkerScreenSize / (size * _view.Scale);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new ScaleTransform(factor, factor);
    }
    private static IEnumerable<UIElement> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is UIElement u) yield return u;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
    private static ZoomPanel? Find(DependencyObject element)
    {
        for (var p = LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element); p is not null; p = LogicalTreeHelper.GetParent(p) ?? (p is Visual ? VisualTreeHelper.GetParent(p) : null))
            if (p is ZoomPanel panel) return panel;
        return null;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomBy(Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(this)); // touchpad pinch arrives as Ctrl+wheel: same thing
        e.Handled = true;
    }
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.Handled) return; // a marker took the press
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { Fit(); e.Handled = true; return; }
        if (e.ChangedButton is MouseButton.Left or MouseButton.Right or MouseButton.Middle)
        { _panFrom = e.GetPosition(this); CaptureMouse(); e.Handled = true; }
    }
    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is not { } from) return;
        var p = e.GetPosition(this); _panFrom = p; _fitted = false;
        Apply(_view with { Offset = _view.Offset + new Vector2((float)(p.X - from.X), (float)(p.Y - from.Y)) });
    }
    private void OnUp(object sender, MouseButtonEventArgs e) { if (_panFrom is not null) { _panFrom = null; ReleaseMouseCapture(); e.Handled = true; } }
    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus: ZoomBy(1.25); break;
            case Key.Subtract or Key.OemMinus: ZoomBy(0.8); break;
            case Key.D0 or Key.NumPad0: Fit(); break;
            default: return;
        }
        e.Handled = true;
    }
}
