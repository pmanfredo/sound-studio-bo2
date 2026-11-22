using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoundStudio.App.ViewModels;

namespace SoundStudio.App.Controls;

/// <summary>
/// One layer's lane, Audacity style. Drag the clip to move it in time; drag its left or right edge to trim it; drag
/// the round handles at its top corners to fade in or out. Moves and trims snap to the start, the original's end
/// and other clips' edges (hold Shift to move freely). Double-click plays the layer on its own.
/// </summary>
public sealed class TrackLane : TimelineElement
{
    private enum Mode { None, Move, TrimStart, TrimEnd, FadeIn, FadeOut }

    private const double SnapPixels = 8;
    private const double EdgePixels = 6;
    private const double HandleRadius = 5;

    private Mode _drag;
    private Mode _hover;
    private double _grabX;
    private LayerState? _before;
    private double? _snappedTo;

    public TrackLane()
    {
        ClipToBounds = true;
        MouseLeave += (_, _) => SetHover(Mode.None);
    }

    private LayerViewModel? Layer => DataContext as LayerViewModel;
    protected override LayersViewModel? Editor => Layer?.Owner;

    private Rect ClipRect()
    {
        var l = Layer!;
        return new Rect(X(l.OffsetSeconds), 5, Math.Max(3, l.ClipSeconds * PixelsPerSecond), Math.Max(4, ActualHeight - 10));
    }

    private Point FadeInHandle(Rect r) => new(r.Left + Math.Min(Layer!.FadeIn * PixelsPerSecond, r.Width), r.Top + HandleRadius + 1);
    private Point FadeOutHandle(Rect r) => new(r.Right - Math.Min(Layer!.FadeOut * PixelsPerSecond, r.Width), r.Top + HandleRadius + 1);

    private Mode HitTest(Point p)
    {
        if (Layer == null)
            return Mode.None;
        var r = ClipRect();
        if (r.Width >= 16)
        {
            var fi = FadeInHandle(r);
            var fo = FadeOutHandle(r);
            const double reach = HandleRadius + 3;
            var dIn = (p - fi).Length;
            var dOut = (p - fo).Length;
            if (dIn <= reach || dOut <= reach)
                return dIn < dOut ? Mode.FadeIn : Mode.FadeOut;
        }
        if (p.Y < r.Top - 2 || p.Y > r.Bottom + 2)
            return Mode.None;
        var edge = Math.Min(EdgePixels, r.Width / 4);
        if (Math.Abs(p.X - r.Left) <= edge) return Mode.TrimStart;
        if (Math.Abs(p.X - r.Right) <= edge) return Mode.TrimEnd;
        return r.Contains(p) ? Mode.Move : Mode.None;
    }

    private void SetHover(Mode mode)
    {
        if (mode == _hover)
            return;
        _hover = mode;
        Cursor = mode switch
        {
            Mode.Move => Cursors.SizeAll,
            Mode.TrimStart or Mode.TrimEnd => Cursors.SizeWE,
            Mode.FadeIn or Mode.FadeOut => Cursors.Hand,
            _ => null,
        };
        ToolTip = mode switch
        {
            Mode.FadeIn => "Fade in",
            Mode.FadeOut => "Fade out",
            _ => null,
        };
        InvalidateVisual();
    }

    // ---- drawing -------------------------------------------------------------------------------------------

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
        var active = _drag != Mode.None;
        var focus = active ? _drag : _hover;
        var hot = focus != Mode.None;

        // While trimming: a faint outline of the whole source, so you can see what's cut off.
        if (_drag is Mode.TrimStart or Mode.TrimEnd)
        {
            var full = new Rect(X(l.OffsetSeconds - l.TrimStart), rect.Top, l.DurationSeconds * pps, rect.Height);
            dc.DrawRoundedRectangle(TimelineStyle.WithOpacity(faint, 0.08),
                new Pen(TimelineStyle.WithOpacity(faint, 0.6), 1) { DashStyle = DashStyles.Dash }, full, 4, 4);
        }

        var fill = muted ? TimelineStyle.WithOpacity(faint, 0.15) : TimelineStyle.WithOpacity(accent, active ? 0.42 : hot ? 0.34 : 0.26);
        dc.DrawRoundedRectangle(fill, new Pen(muted ? faint : accent, hot ? 2 : 1), rect, 4, 4);

        dc.PushClip(new RectangleGeometry(rect, 4, 4));
        DrawWaveform(dc, l, rect, primary, muted);
        DrawFades(dc, l, rect, primary);
        var name = TimelineStyle.Text(this, l.Name, 11, TimelineStyle.WithOpacity(primary, muted ? 0.5 : 0.95), bold: true);
        name.MaxTextWidth = Math.Max(1, rect.Width - 28);
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        if (rect.Width > 44)
            dc.DrawText(name, new Point(Math.Max(rect.Left, 0) + 14, rect.Top + 2));
        dc.Pop();

        // The trim edge under the mouse.
        if (focus is Mode.TrimStart or Mode.TrimEnd)
        {
            var ex = focus == Mode.TrimStart ? rect.Left : rect.Right - 3;
            dc.DrawRectangle(primary, null, new Rect(ex, rect.Top, 3, rect.Height));
        }

        // Fade handles: always when there's a fade, otherwise while the mouse is over the clip.
        if (rect.Width >= 16 && (hot || l.HasFades))
        {
            DrawHandle(dc, FadeInHandle(rect), accent, primary, focus == Mode.FadeIn);
            DrawHandle(dc, FadeOutHandle(rect), accent, primary, focus == Mode.FadeOut);
        }

        if (active)
            DrawDragLabel(dc, l, rect, primary);
        if (_snappedTo is { } snap)
        {
            var sx = Math.Round(X(snap)) + 0.5;
            dc.DrawLine(new Pen(primary, 1) { DashStyle = DashStyles.Dot }, new Point(sx, 0), new Point(sx, ActualHeight));
        }
    }

    private void DrawWaveform(DrawingContext dc, LayerViewModel l, Rect rect, Brush primary, bool muted)
    {
        var peaks = l.Peaks;
        var columns = peaks.Length / 2;
        if (columns == 0 || l.DurationSeconds <= 0)
            return;
        var from = l.TrimStart / l.DurationSeconds;
        var span = l.ClipSeconds / l.DurationSeconds;
        var mid = rect.Top + rect.Height / 2 + 6; // leave room for the name
        var half = (rect.Height - 16) / 2;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            var first = (int)Math.Max(0, Math.Floor(rect.Left));
            var last = (int)Math.Min(ActualWidth, Math.Ceiling(rect.Right));
            for (var px = first; px < last; px++)
            {
                var a = (int)((from + (px - rect.Left) / rect.Width * span) * columns);
                var b = Math.Max(a + 1, (int)((from + (px + 1 - rect.Left) / rect.Width * span) * columns));
                a = Math.Clamp(a, 0, columns - 1);
                b = Math.Clamp(b, a + 1, columns);
                float min = 0, max = 0;
                for (var i = a; i < b; i++)
                {
                    min = Math.Min(min, peaks[i * 2]);
                    max = Math.Max(max, peaks[i * 2 + 1]);
                }
                var g = FadeGain(l, rect, px + 0.5); // draw it as it will sound
                c.BeginFigure(new Point(px + 0.5, mid - max * g * half - 0.5), false, false);
                c.LineTo(new Point(px + 0.5, mid - min * g * half + 0.5), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(TimelineStyle.WithOpacity(primary, muted ? 0.3 : 0.8), 1), geo);
    }

    private double FadeGain(LayerViewModel l, Rect rect, double x)
    {
        var pps = PixelsPerSecond;
        var g = 1.0;
        var fi = l.FadeIn * pps;
        var fo = l.FadeOut * pps;
        if (fi > 0 && x - rect.Left < fi) g *= Math.Max(0, (x - rect.Left) / fi);
        if (fo > 0 && rect.Right - x < fo) g *= Math.Max(0, (rect.Right - x) / fo);
        return g;
    }

    private void DrawFades(DrawingContext dc, LayerViewModel l, Rect rect, Brush primary)
    {
        var shade = TimelineStyle.WithOpacity(Brushes.Black, 0.35);
        var line = new Pen(TimelineStyle.WithOpacity(primary, 0.85), 1.5);
        var top = rect.Top + HandleRadius + 1;
        var pps = PixelsPerSecond;
        if (l.FadeIn > 0.0005)
        {
            var fx = rect.Left + Math.Min(l.FadeIn * pps, rect.Width);
            dc.DrawGeometry(shade, null, Triangle(new Point(rect.Left, rect.Top), new Point(fx, rect.Top), new Point(rect.Left, rect.Bottom)));
            dc.DrawLine(line, new Point(rect.Left, rect.Bottom), new Point(fx, top));
        }
        if (l.FadeOut > 0.0005)
        {
            var fx = rect.Right - Math.Min(l.FadeOut * pps, rect.Width);
            dc.DrawGeometry(shade, null, Triangle(new Point(rect.Right, rect.Top), new Point(fx, rect.Top), new Point(rect.Right, rect.Bottom)));
            dc.DrawLine(line, new Point(fx, top), new Point(rect.Right, rect.Bottom));
        }
    }

    private static Geometry Triangle(Point a, Point b, Point c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, true, true);
            ctx.LineTo(b, false, false);
            ctx.LineTo(c, false, false);
        }
        g.Freeze();
        return g;
    }

    private static void DrawHandle(DrawingContext dc, Point p, Brush accent, Brush primary, bool hot) =>
        dc.DrawEllipse(hot ? accent : primary, new Pen(hot ? primary : accent, 1.5), p, HandleRadius, HandleRadius);

    private void DrawDragLabel(DrawingContext dc, LayerViewModel l, Rect rect, Brush primary)
    {
        var text = _drag switch
        {
            Mode.Move => l.OffsetSeconds < 0.0005 ? "Starts at 0" : $"Starts at {l.OffsetSeconds:0.000} s",
            Mode.TrimStart or Mode.TrimEnd => $"{l.ClipSeconds:0.000} s",
            Mode.FadeIn => $"Fade {l.FadeIn:0.000} s",
            Mode.FadeOut => $"Fade {l.FadeOut:0.000} s",
            _ => "",
        };
        var label = TimelineStyle.Text(this, text, 11, primary, bold: true);
        var anchor = _drag is Mode.TrimEnd or Mode.FadeOut ? rect.Right - label.Width - 8 : rect.Left;
        var lx = Math.Clamp(anchor, 2, Math.Max(2, ActualWidth - label.Width - 10));
        var box = new Rect(lx, ActualHeight - label.Height - 8, label.Width + 8, label.Height + 2);
        dc.DrawRoundedRectangle(TimelineStyle.WithOpacity(Brushes.Black, 0.75), null, box, 3, 3);
        dc.DrawText(label, new Point(box.Left + 4, box.Top + 1));
    }

    // ---- mouse ---------------------------------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var l = Layer;
        if (l == null || Editor == null)
            return;
        var pos = e.GetPosition(this);
        if (_drag == Mode.None)
        {
            SetHover(HitTest(pos));
            return;
        }

        var b = _before!;
        var snap = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        var delta = (pos.X - _grabX) / PixelsPerSecond;
        var clip = l.DurationSeconds - b.TrimStart - b.TrimEnd;
        _snappedTo = null;
        switch (_drag)
        {
            case Mode.Move:
            {
                var offset = Math.Max(0, b.OffsetSeconds + delta);
                if (snap)
                    offset = SnapMove(l, offset, clip);
                l.OffsetSeconds = Math.Round(offset, 4);
                break;
            }
            case Mode.TrimStart:
            {
                var sourceStart = b.OffsetSeconds - b.TrimStart; // where the untrimmed audio would start
                var min = Math.Max(0, sourceStart);
                var max = b.OffsetSeconds + clip - LayerViewModel.MinClipSeconds;
                var start = Math.Clamp(b.OffsetSeconds + delta, min, max);
                if (snap)
                    start = Math.Clamp(SnapPoint(l, start), min, max);
                l.TrimStart = Math.Round(Math.Max(0, start - sourceStart), 4);
                l.OffsetSeconds = Math.Round(start, 4);
                l.ClampFades();
                break;
            }
            case Mode.TrimEnd:
            {
                var sourceEnd = b.OffsetSeconds - b.TrimStart + l.DurationSeconds;
                var min = b.OffsetSeconds + LayerViewModel.MinClipSeconds;
                var end = Math.Clamp(b.OffsetSeconds + clip + delta, min, sourceEnd);
                if (snap)
                    end = Math.Clamp(SnapPoint(l, end), min, sourceEnd);
                l.TrimEnd = Math.Round(Math.Max(0, sourceEnd - end), 4);
                l.ClampFades();
                break;
            }
            case Mode.FadeIn:
                l.FadeIn = Math.Round(Math.Clamp(b.FadeIn + delta, 0, l.ClipSeconds - l.FadeOut), 4);
                break;
            case Mode.FadeOut:
                l.FadeOut = Math.Round(Math.Clamp(b.FadeOut - delta, 0, l.ClipSeconds - l.FadeIn), 4);
                break;
        }
        InvalidateVisual();
    }

    private IEnumerable<double> SnapTargets(LayerViewModel layer)
    {
        var ed = Editor!;
        yield return 0;
        yield return ed.OriginalSeconds;
        foreach (var other in ed.Layers.Where(o => o != layer))
        {
            yield return other.OffsetSeconds;
            yield return other.EndSeconds;
        }
    }

    /// <summary>Snaps one time (a trimmed edge) to the nearest target within reach.</summary>
    private double SnapPoint(LayerViewModel layer, double t)
    {
        double best = SnapPixels / PixelsPerSecond, result = t;
        foreach (var target in SnapTargets(layer))
        {
            if (Math.Abs(target - t) < best)
            {
                best = Math.Abs(target - t);
                result = target;
                _snappedTo = target;
            }
        }
        return result;
    }

    /// <summary>Snaps a moved clip by its start or its end, whichever is closer to a target.</summary>
    private double SnapMove(LayerViewModel layer, double offset, double clip)
    {
        double best = SnapPixels / PixelsPerSecond, result = offset;
        foreach (var t in SnapTargets(layer))
        {
            if (Math.Abs(t - offset) < best)
            {
                best = Math.Abs(t - offset);
                result = t;
                _snappedTo = t;
            }
            if (Math.Abs(t - (offset + clip)) < best && t - clip >= 0)
            {
                best = Math.Abs(t - (offset + clip));
                result = t - clip;
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
        var mode = HitTest(pos);
        if (mode == Mode.None)
            return;
        if (e.ClickCount == 2 && mode == Mode.Move)
        {
            l.PlayCommand.Execute(null); // double-click: hear it
            e.Handled = true;
            return;
        }
        _drag = mode;
        _grabX = pos.X;
        _before = l.ToState();
        Editor.BeginDrag();
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Mode.None)
            return;
        EndDrag();
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_drag != Mode.None)
            EndDrag();
    }

    private void EndDrag()
    {
        _drag = Mode.None;
        _snappedTo = null;
        Editor?.EndDrag();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
