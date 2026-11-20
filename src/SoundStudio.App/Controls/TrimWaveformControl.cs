using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundStudio.App.Controls;

/// <summary>
/// Waveform with a keep-region: drag the two handles to choose what to keep; the rest is dimmed.
/// <see cref="SelectionStart"/>/<see cref="SelectionEnd"/> are fractions (0..1) and bind two-way.
/// </summary>
public sealed class TrimWaveformControl : FrameworkElement
{
    private const double HandleGrab = 10;
    private enum Drag { None, Start, End }
    private Drag _drag;

    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(TrimWaveformControl),
        new FrameworkPropertyMetadata(Array.Empty<float>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(double), typeof(TrimWaveformControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(double), typeof(TrimWaveformControl),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(TrimWaveformControl),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(TrimWaveformControl),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HandleBrushProperty = DependencyProperty.Register(
        nameof(HandleBrush), typeof(Brush), typeof(TrimWaveformControl),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[] Peaks { get => (float[])GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    /// <summary>Playhead as a fraction of the whole take; negative hides it.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
    public Brush HandleBrush { get => (Brush)GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var mid = height / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var startX = width * SelectionStart;
        var endX = width * SelectionEnd;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), null,
            new Rect(startX, 0, Math.Max(0, endX - startX), height));

        var peaks = Peaks;
        if (peaks is { Length: >= 2 } && width > 0)
        {
            var columns = peaks.Length / 2;
            const double pitch = 3, barWidth = 2;
            var bars = Math.Max(1, (int)(width / pitch));
            var dimmed = WaveBrush.Clone();
            dimmed.Opacity = 0.3;
            dimmed.Freeze();
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
                var x = b * pitch;
                var top = mid - Math.Max(1, max * mid * 0.95);
                var bottom = mid + Math.Max(1, -min * mid * 0.95);
                var inside = x + barWidth / 2 >= startX && x + barWidth / 2 <= endX;
                dc.DrawRoundedRectangle(inside ? WaveBrush : dimmed, null, new Rect(x, top, barWidth, bottom - top), 1, 1);
            }
        }

        DrawHandle(dc, startX, height);
        DrawHandle(dc, endX, height);

        if (Progress >= 0)
            dc.DrawRectangle(HandleBrush, null, new Rect(Math.Clamp(width * Progress, 0, width - 1.5), 0, 1.5, height));
    }

    private void DrawHandle(DrawingContext dc, double x, double height)
    {
        x = Math.Clamp(x, 1.5, ActualWidth - 1.5);
        dc.DrawRectangle(HandleBrush, null, new Rect(x - 1.5, 0, 3, height));
        dc.DrawRoundedRectangle(HandleBrush, null, new Rect(x - 5, height / 2 - 12, 10, 24), 3, 3);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        if (_drag != Drag.None && IsMouseCaptured)
        {
            var fraction = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
            const double minGap = 0.01;
            if (_drag == Drag.Start)
                SelectionStart = Math.Min(fraction, SelectionEnd - minGap);
            else
                SelectionEnd = Math.Max(fraction, SelectionStart + minGap);
            return;
        }
        Cursor = NearHandle(x) != Drag.None ? Cursors.SizeWE : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _drag = NearHandle(e.GetPosition(this).X);
        if (_drag == Drag.None)
        {
            // Clicking the waveform moves the closest handle there.
            var x = e.GetPosition(this).X;
            _drag = Math.Abs(x - ActualWidth * SelectionStart) <= Math.Abs(x - ActualWidth * SelectionEnd) ? Drag.Start : Drag.End;
        }
        CaptureMouse();
        OnMouseMove(e);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    private Drag NearHandle(double x)
    {
        var dStart = Math.Abs(x - ActualWidth * SelectionStart);
        var dEnd = Math.Abs(x - ActualWidth * SelectionEnd);
        if (dStart > HandleGrab && dEnd > HandleGrab)
            return Drag.None;
        return dStart <= dEnd ? Drag.Start : Drag.End;
    }
}
