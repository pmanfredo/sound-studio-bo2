using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundStudio.App.Controls;

/// <summary>
/// Draws a min/max waveform envelope with the played part highlighted. Click or drag to seek
/// (executes <see cref="SeekCommand"/> with a 0..1 fraction).
/// </summary>
public sealed class WaveformControl : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveformControl),
        new FrameworkPropertyMetadata(Array.Empty<float>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WaveformControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformControl),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnplayedBrushProperty = DependencyProperty.Register(
        nameof(UnplayedBrush), typeof(Brush), typeof(WaveformControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(WaveformControl));

    public WaveformControl()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public float[] Peaks { get => (float[])GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush PlayedBrush { get => (Brush)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush UnplayedBrush { get => (Brush)GetValue(UnplayedBrushProperty); set => SetValue(UnplayedBrushProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height)); // hit-testable everywhere

        var peaks = Peaks;
        var mid = height / 2;
        if (peaks is not { Length: >= 2 } || width <= 0)
        {
            dc.DrawRectangle(UnplayedBrush, null, new Rect(0, mid - 0.5, width, 1));
            return;
        }

        // One bar per ~3 px, each bar the loudest of the peak columns it covers.
        var columns = peaks.Length / 2;
        const double barPitch = 3, barWidth = 2;
        var bars = Math.Max(1, (int)(width / barPitch));
        var playedX = width * Math.Clamp(Progress, 0, 1);
        var minHalf = Math.Max(1, height * 0.02);

        for (var b = 0; b < bars; b++)
        {
            var from = b * columns / bars;
            var to = Math.Max(from + 1, (b + 1) * columns / bars);
            float min = 0, max = 0;
            for (var c = from; c < to && c < columns; c++)
            {
                min = Math.Min(min, peaks[c * 2]);
                max = Math.Max(max, peaks[c * 2 + 1]);
            }
            var top = mid - Math.Max(minHalf, max * mid * 0.95);
            var bottom = mid + Math.Max(minHalf, -min * mid * 0.95);
            var x = b * barPitch;
            dc.DrawRoundedRectangle(x + barWidth / 2 <= playedX ? PlayedBrush : UnplayedBrush, null,
                new Rect(x, top, barWidth, bottom - top), 1, 1);
        }

        // Playhead.
        if (Progress > 0)
            dc.DrawRectangle(PlayedBrush, null, new Rect(Math.Min(playedX, width - 1.5), 0, 1.5, height));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        SeekTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
            SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void SeekTo(double x)
    {
        if (ActualWidth <= 0)
            return;
        var fraction = Math.Clamp(x / ActualWidth, 0, 1);
        if (SeekCommand?.CanExecute(fraction) == true)
            SeekCommand.Execute(fraction);
    }
}
