using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Cricket07.FaceStudio.Views;

/// <summary>
/// Photoshop-style eyedropper feedback: a ring around the cursor, new colour on the top half and the previous colour on the
/// bottom half, edged in grey so it reads on any skin. It pops open (springy overshoot), holds briefly and fades away.
/// </summary>
internal static class PickFeedback
{
    public const double Diameter = 76, Thickness = 12;

    /// <param name="scale">Screen pixels per canvas unit (a zoomed texture canvas), so the ring is the same size on screen.</param>
    /// <returns>The ring element (removed from the canvas when its animation ends).</returns>
    public static FrameworkElement Show(Canvas layer, Point centre, Color picked, Color previous, double scale = 1)
    {
        double size = Diameter / scale, thickness = Thickness / scale, edge = 1.2 / scale;
        var ring = new Grid { Width = size, Height = size, IsHitTestVisible = false, Tag = "pick-feedback" };
        Ellipse Half(Color colour, bool top) => new()
        {
            Stroke = new SolidColorBrush(colour), StrokeThickness = thickness,
            Clip = new RectangleGeometry(new Rect(0, top ? 0 : size / 2, size, size / 2)),
        };
        ring.Children.Add(Half(previous, top: false));
        ring.Children.Add(Half(picked, top: true));
        var grey = new SolidColorBrush(Color.FromRgb(128, 128, 128));
        ring.Children.Add(new Ellipse { Stroke = grey, StrokeThickness = edge });                                     // outer edge
        ring.Children.Add(new Ellipse { Stroke = grey, StrokeThickness = edge, Margin = new Thickness(thickness - edge) }); // inner edge
        Canvas.SetLeft(ring, centre.X - size / 2); Canvas.SetTop(ring, centre.Y - size / 2);
        var pop = new ScaleTransform(0.55, 0.55);
        ring.RenderTransformOrigin = new Point(0.5, 0.5); ring.RenderTransform = pop;
        layer.Children.Add(ring);

        var grow = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut } };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        fade.Completed += (_, _) => layer.Children.Remove(ring);
        ring.BeginAnimation(UIElement.OpacityProperty, fade);
        return ring;
    }

    public static Color ToColor(System.Numerics.Vector3 c) =>
        Color.FromRgb((byte)Math.Round(Math.Clamp(c.X, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.Y, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.Z, 0, 1) * 255));
}
