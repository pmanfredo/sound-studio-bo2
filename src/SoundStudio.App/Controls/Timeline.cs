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

