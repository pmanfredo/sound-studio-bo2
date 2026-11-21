using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoundStudio.App.ViewModels;

namespace SoundStudio.App.Controls;

/// <summary>Shared drawing helpers for the layer timeline (ruler, lanes, overlay).</summary>
internal static class TimelineStyle
{
    public static Brush Resource(FrameworkElement e, string key, Brush fallback) =>
        e.TryFindResource(key) as Brush ?? fallback;

    public static Brush WithOpacity(Brush brush, double opacity)
    {
        var b = brush.Clone();
        b.Opacity = opacity;
        b.Freeze();
        return b;
    }

    /// <summary>A "nice" tick step so labels are at least <paramref name="minPixels"/> apart.</summary>
    public static double TickStep(double pixelsPerSecond, double minPixels = 70)
    {
        foreach (var step in new[] { 0.01, 0.02, 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60 })
            if (step * pixelsPerSecond >= minPixels)
                return step;
        return 120;
    }

    public static string Label(double seconds, double step) =>
        step < 0.1 ? seconds.ToString("0.00", CultureInfo.InvariantCulture)
        : step < 1 ? seconds.ToString("0.0#", CultureInfo.InvariantCulture)
        : seconds.ToString("0", CultureInfo.InvariantCulture);

    public static FormattedText Text(FrameworkElement e, string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(e).PixelsPerDip);
}

/// <summary>Base: redraws whenever the bound view model (or the editor it belongs to) changes.</summary>
public abstract class TimelineElement : FrameworkElement
{
    private INotifyPropertyChanged? _watchedItem;
    private INotifyPropertyChanged? _watchedOwner;

    protected TimelineElement()
    {
        Focusable = false;
        DataContextChanged += (_, _) => Watch();
        Unloaded += (_, _) => Unwatch();
        Loaded += (_, _) => Watch();
    }

    protected abstract LayersViewModel? Editor { get; }

    private void Watch()
    {
        Unwatch();
        _watchedItem = DataContext as INotifyPropertyChanged;
        _watchedOwner = Editor is { } ed && !ReferenceEquals(ed, DataContext) ? ed : null;
        if (_watchedItem != null) _watchedItem.PropertyChanged += OnChanged;
        if (_watchedOwner != null) _watchedOwner.PropertyChanged += OnChanged;
        InvalidateVisual();
    }

    private void Unwatch()
    {
        if (_watchedItem != null) _watchedItem.PropertyChanged -= OnChanged;
        if (_watchedOwner != null) _watchedOwner.PropertyChanged -= OnChanged;
        _watchedItem = _watchedOwner = null;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected double PixelsPerSecond => Editor is { ViewSeconds: > 0 } ed ? ActualWidth / ed.ViewSeconds : 1;
    protected double ViewStart => Editor?.ViewStart ?? 0;

    /// <summary>Seconds in the mix → x in this element, and back.</summary>
    protected double X(double seconds) => (seconds - ViewStart) * PixelsPerSecond;
    protected double T(double x) => ViewStart + x / PixelsPerSecond;

    /// <summary>Ctrl+wheel zooms around the mouse; Shift+wheel scrolls through time.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var ed = Editor;
        if (ed == null)
            return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ed.Zoom(e.Delta > 0 ? 0.8 : 1.25, T(e.GetPosition(this).X));
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ed.Pan(e.Delta > 0 ? -0.15 : 0.15);
            e.Handled = true;
        }
    }
}

/// <summary>Time ruler above the tracks. Click or drag on it to play the mix from that point.</summary>
public sealed class TimelineRuler : TimelineElement
{
    public TimelineRuler()
    {
        Cursor = Cursors.Hand;
        Height = 30;
        ToolTip = "Click to play the mix from here";
    }

    protected override LayersViewModel? Editor => DataContext as LayersViewModel;

    protected override void OnRender(DrawingContext dc)
    {
        var ed = Editor;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ed == null || ActualWidth <= 0)
            return;
        var pps = PixelsPerSecond;
        var text = TimelineStyle.Resource(this, "TextFillColorSecondaryBrush", Brushes.Gray);
        var faint = TimelineStyle.Resource(this, "TextFillColorTertiaryBrush", Brushes.DimGray);
        var tickPen = new Pen(faint, 1);
        var step = TimelineStyle.TickStep(pps);
        var minor = step / 5;
        for (var i = (long)Math.Floor(ViewStart / minor); ; i++)
        {
            var t = i * minor;
            var x = Math.Round(X(t)) + 0.5;
            if (x > ActualWidth)
                break;
            if (x < 0)
                continue;
            var major = i % 5 == 0;
            dc.DrawLine(tickPen, new Point(x, ActualHeight - (major ? 10 : 4)), new Point(x, ActualHeight));
            if (major)
            {
                var label = TimelineStyle.Text(this, TimelineStyle.Label(t, step) + (i == 0 ? " s" : ""), 11, text);
                if (x + 3 + label.Width <= ActualWidth)
                    dc.DrawText(label, new Point(x + 3, 2));
            }
        }

        // Playhead.
        if (ed.PlayheadSeconds >= 0)
        {
            var accent = TimelineStyle.Resource(this, "AccentFillColorDefaultBrush", Brushes.DeepSkyBlue);
            var px = X(ed.PlayheadSeconds);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(px - 6, ActualHeight - 10), true, true);
                c.LineTo(new Point(px + 6, ActualHeight - 10), true, false);
                c.LineTo(new Point(px, ActualHeight), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(accent, null, g);
        }
        // Where the original sound ends.
        var mx = X(ed.OriginalSeconds);
        if (mx >= 0 && mx <= ActualWidth)
        {
            var amber = TimelineStyle.Resource(this, "PendingBrush", Brushes.SkyBlue);
            var label = TimelineStyle.Text(this, "Original ends", 10, amber, bold: true);
            var lx = Math.Min(mx + 4, ActualWidth - label.Width - 2);
            dc.DrawRectangle(TimelineStyle.WithOpacity(Brushes.Black, 0.55), null, new Rect(lx - 2, 15, label.Width + 4, label.Height));
            dc.DrawText(label, new Point(lx, 15));
            dc.DrawLine(new Pen(amber, 2), new Point(mx, 12), new Point(mx, ActualHeight));
        }

    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Seek(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            Seek(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    private void Seek(MouseEventArgs e) =>
        Editor?.PlayFrom(Math.Max(0, T(e.GetPosition(this).X)));
}

/// <summary>Lines drawn across all tracks: the original's end (and what "cut" removes) and the playhead.</summary>
public sealed class TimelineOverlay : TimelineElement
{
    public TimelineOverlay() => IsHitTestVisible = false;

    protected override LayersViewModel? Editor => DataContext as LayersViewModel;

    protected override void OnRender(DrawingContext dc)
    {
        var ed = Editor;
        if (ed == null || ActualWidth <= 0)
            return;
        var mx = X(ed.OriginalSeconds);
        if (ed.CutToOriginal && mx < ActualWidth)
        {
            var from = Math.Max(0, mx);
            dc.DrawRectangle(TimelineStyle.WithOpacity(Brushes.Black, 0.45), null, new Rect(from, 0, ActualWidth - from, ActualHeight));
        }
        if (mx >= 0 && mx <= ActualWidth)
        {
            var marker = TimelineStyle.Resource(this, "PendingBrush", Brushes.SkyBlue);
            var pen = new Pen(TimelineStyle.WithOpacity(marker, 0.85), 1.5) { DashStyle = new DashStyle([4, 3], 0) };
            dc.DrawLine(pen, new Point(mx, 0), new Point(mx, ActualHeight));
        }
        if (ed.PlayheadSeconds >= 0)
        {
            var accent = TimelineStyle.Resource(this, "AccentFillColorDefaultBrush", Brushes.DeepSkyBlue);
            var px = Math.Round(X(ed.PlayheadSeconds)) + 0.5;
            dc.DrawLine(new Pen(accent, 2), new Point(px, 0), new Point(px, ActualHeight));
        }
    }
}

/// <summary>
/// One layer's lane: the clip drawn where it starts in the mix. Drag the clip to move it in time; it snaps to the
/// start, the original's end and other clips' edges (hold Shift to move freely).
/// </summary>
public sealed class TrackLane : TimelineElement
{
    private const double SnapPixels = 8;
    private bool _dragging;
    private bool _hover;
    private double _grabX;
    private double _startOffset;
    private double? _snappedTo;

    public TrackLane()
    {
        ClipToBounds = true;
        MouseLeave += (_, _) => { _hover = false; InvalidateVisual(); };
    }

    private LayerViewModel? Layer => DataContext as LayerViewModel;
    protected override LayersViewModel? Editor => Layer?.Owner;

    private Rect ClipRect()
    {
        var l = Layer!;
        var pps = PixelsPerSecond;
        var x = X(l.OffsetSeconds);
        return new Rect(x, 5, Math.Max(3, l.DurationSeconds * pps), Math.Max(4, ActualHeight - 10));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // whole lane hit-testable
        var l = Layer;
        if (l == null || ActualWidth <= 0 || Editor == null)
            return;

        var pps = PixelsPerSecond;
        var faint = TimelineStyle.Resource(this, "TextFillColorTertiaryBrush", Brushes.DimGray);
        var gridPen = new Pen(TimelineStyle.WithOpacity(faint, 0.18), 1);
        var step = TimelineStyle.TickStep(pps);
        for (var t = Math.Ceiling(ViewStart / step) * step; X(t) < ActualWidth; t += step)
        {
            var x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, ActualHeight));
        }

        var accent = TimelineStyle.Resource(this, "AccentFillColorDefaultBrush", Brushes.DeepSkyBlue);
        var primary = TimelineStyle.Resource(this, "TextFillColorPrimaryBrush", Brushes.White);
        var muted = l.Muted;
        var rect = ClipRect();
        var fill = muted ? TimelineStyle.WithOpacity(faint, 0.15) : TimelineStyle.WithOpacity(accent, _dragging ? 0.42 : _hover ? 0.34 : 0.26);
        var border = new Pen(muted ? faint : accent, _dragging || _hover ? 2 : 1);
        dc.DrawRoundedRectangle(fill, border, rect, 4, 4);

        // Waveform inside the clip.
        dc.PushClip(new RectangleGeometry(rect, 4, 4));
        var peaks = l.Peaks;
        var columns = peaks.Length / 2;
        if (columns > 0)
        {
            var mid = rect.Top + rect.Height / 2 + 6; // leave room for the name
            var half = (rect.Height - 16) / 2;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                var first = (int)Math.Max(0, Math.Floor(rect.Left));
                var last = (int)Math.Min(ActualWidth, Math.Ceiling(rect.Right));
                for (var px = first; px < last; px++)
                {
                    var a = (int)((px - rect.Left) / rect.Width * columns);
                    var b = Math.Max(a + 1, (int)((px + 1 - rect.Left) / rect.Width * columns));
                    a = Math.Clamp(a, 0, columns - 1);
                    b = Math.Clamp(b, a + 1, columns);
                    float min = 0, max = 0;
                    for (var i = a; i < b; i++)
                    {
                        min = Math.Min(min, peaks[i * 2]);
                        max = Math.Max(max, peaks[i * 2 + 1]);
                    }
                    c.BeginFigure(new Point(px + 0.5, mid - max * half - 0.5), false, false);
                    c.LineTo(new Point(px + 0.5, mid - min * half + 0.5), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(TimelineStyle.WithOpacity(primary, muted ? 0.3 : 0.8), 1), geo);
        }
        var name = TimelineStyle.Text(this, l.Name, 11, TimelineStyle.WithOpacity(primary, muted ? 0.5 : 0.95), bold: true);
        name.MaxTextWidth = Math.Max(1, rect.Width - 10);
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        if (rect.Width > 24)
            dc.DrawText(name, new Point(Math.Max(rect.Left, 0) + 6, rect.Top + 2));
        dc.Pop();

        // While dragging: where it starts now.
        if (_dragging)
        {
            var label = TimelineStyle.Text(this,
                (l.OffsetSeconds < 0.0005 ? "Starts at 0" : $"Starts at {l.OffsetSeconds:0.000} s") + (_snappedTo != null ? "  · snapped" : ""),
                11, primary, bold: true);
            var lx = Math.Clamp(rect.Left, 2, Math.Max(2, ActualWidth - label.Width - 10));
            var box = new Rect(lx, ActualHeight - label.Height - 8, label.Width + 8, label.Height + 2);
            dc.DrawRoundedRectangle(TimelineStyle.WithOpacity(Brushes.Black, 0.75), null, box, 3, 3);
            dc.DrawText(label, new Point(box.Left + 4, box.Top + 1));
        }
        if (_snappedTo is { } snap)
        {
            var sx = Math.Round(X(snap)) + 0.5;
            dc.DrawLine(new Pen(primary, 1) { DashStyle = DashStyles.Dot }, new Point(sx, 0), new Point(sx, ActualHeight));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var l = Layer;
        if (l == null || Editor == null)
            return;
        var pos = e.GetPosition(this);
        if (_dragging)
        {
            var pps = PixelsPerSecond;
            var offset = Math.Max(0, _startOffset + (pos.X - _grabX) / pps);
            _snappedTo = null;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                offset = Snap(l, offset, pps);
            l.OffsetSeconds = Math.Round(offset, 3);
            InvalidateVisual();
            return;
        }
        var over = ClipRect().Contains(pos);
        if (over != _hover)
        {
            _hover = over;
            Cursor = over ? Cursors.SizeWE : null;
            ToolTip = over ? "Drag to move this layer in time (hold Shift to turn off snapping)" : null;
            InvalidateVisual();
        }
    }

    private double Snap(LayerViewModel layer, double offset, double pps)
    {
        var ed = Editor!;
        var targets = new List<double> { 0, ed.OriginalSeconds };
        foreach (var other in ed.Layers.Where(o => o != layer))
        {
            targets.Add(other.OffsetSeconds);
            targets.Add(other.OffsetSeconds + other.DurationSeconds);
        }
        double best = SnapPixels / pps, result = offset;
        foreach (var t in targets)
        {
            if (Math.Abs(t - offset) < best) { best = Math.Abs(t - offset); result = t; _snappedTo = t; }
            var end = offset + layer.DurationSeconds;
            if (Math.Abs(t - end) < best && t - layer.DurationSeconds >= 0)
            {
                best = Math.Abs(t - end);
                result = t - layer.DurationSeconds;
                _snappedTo = t;
            }
        }
        return result;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var l = Layer;
        if (l == null || Editor == null)
            return;
        var pos = e.GetPosition(this);
        if (!ClipRect().Contains(pos))
            return;
        if (e.ClickCount == 2)
        {
            l.PlayCommand.Execute(null); // double-click: hear it
            e.Handled = true;
            return;
        }
        _dragging = true;
        _grabX = pos.X;
        _startOffset = l.OffsetSeconds;
        Editor.BeginDrag();
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        _snappedTo = null;
        ReleaseMouseCapture();
        Editor?.EndDrag();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _snappedTo = null;
            Editor?.EndDrag();
            InvalidateVisual();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
