using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using SeekDownloader.Gui.ViewModels;

namespace SeekDownloader.Gui.Views;

/// <summary>
/// Fase 4: custom Skia-renderer voor de Galaxy. Eigen perspectief-projectie + depth-sort —
/// geen 3D-engine. Sleep = orbit, scroll = zoom, hover = tooltip, klik = open album.
/// </summary>
public class GalaxyControl : Control
{
    private double _yaw = 0.7, _pitch = 0.28, _zoom = 1.0;
    private bool _drag;
    private Point _lastPos, _pressPos, _pointer;
    private bool _hasPointer;
    private readonly DispatcherTimer _timer;

    public GalaxyControl()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            if (!IsEffectivelyVisible) return;
            if (!_drag) _yaw += 0.0012;          // trage idle-rotatie
            InvalidateVisual();
        };
        _timer.Start();
    }

    private GalaxyViewModel? Vm => DataContext as GalaxyViewModel;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _drag = true;
        _pressPos = _lastPos = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        _hasPointer = true;
        if (_drag)
        {
            _yaw += (p.X - _lastPos.X) * 0.008;
            _pitch = Math.Clamp(_pitch + (p.Y - _lastPos.Y) * 0.008, -1.4, 1.4);
            _lastPos = p;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        _drag = false;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPos.X) + Math.Abs(p.Y - _pressPos.Y) < 5) ClickAt(p);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.12, e.Delta.Y), 0.3, 6.0);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hasPointer = false;
    }

    private void ClickAt(Point p)
    {
        var vm = Vm;
        var pts = vm?.Points;
        if (vm == null || pts == null || pts.Length == 0) return;
        int idx = GalaxyMath.Nearest(pts, _yaw, _pitch, _zoom, Bounds.Width, Bounds.Height, p, 14);
        if (idx >= 0) vm.Open(pts[idx]);
    }

    public override void Render(DrawingContext context)
    {
        var vm = Vm;
        context.Custom(new GalaxyDrawOp(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            vm?.Points ?? Array.Empty<GalaxyPoint>(),
            vm?.ClusterLabels ?? Array.Empty<GalaxyLabel>(),
            _yaw, _pitch, _zoom,
            _hasPointer && !_drag ? _pointer : null));
    }
}

internal static class GalaxyMath
{
    public static void Project(GalaxyPoint p, double yaw, double pitch, double zoom,
        double w, double h, out float sx, out float sy, out float depth, out float persp)
    {
        double cy = Math.Cos(yaw), sy0 = Math.Sin(yaw);
        double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        double xr = p.X * cy - p.Z * sy0;
        double zr = p.X * sy0 + p.Z * cy;
        double yr = p.Y * cp - zr * sp;
        double zr2 = p.Y * sp + zr * cp;
        double pf = 2.2 / (2.2 + zr2);
        double scale = Math.Min(w, h) * 0.42 * zoom;
        sx = (float)(w / 2 + xr * pf * scale);
        sy = (float)(h / 2 + yr * pf * scale);
        depth = (float)zr2;
        persp = (float)pf;
    }

    public static int Nearest(GalaxyPoint[] pts, double yaw, double pitch, double zoom,
        double w, double h, Point at, double maxDist)
    {
        int best = -1;
        double bestD = maxDist * maxDist;
        float bestDepth = float.MaxValue;
        for (int i = 0; i < pts.Length; i++)
        {
            if (pts[i].Dim) continue;
            Project(pts[i], yaw, pitch, zoom, w, h, out var sx, out var sy, out var depth, out _);
            double dx = sx - at.X, dy = sy - at.Y;
            double d = dx * dx + dy * dy;
            if (d < bestD || (Math.Abs(d - bestD) < 1 && depth < bestDepth))
            {
                if (d <= maxDist * maxDist) { best = i; bestD = Math.Max(d, 0.01); bestDepth = depth; }
            }
        }
        return best;
    }
}

internal sealed class GalaxyDrawOp : ICustomDrawOperation
{
    private readonly GalaxyPoint[] _pts;
    private readonly GalaxyLabel[] _labels;
    private readonly double _yaw, _pitch, _zoom;
    private readonly Point? _pointer;

    public GalaxyDrawOp(Rect bounds, GalaxyPoint[] pts, GalaxyLabel[] labels,
        double yaw, double pitch, double zoom, Point? pointer)
    {
        Bounds = bounds;
        _pts = pts; _labels = labels;
        _yaw = yaw; _pitch = pitch; _zoom = zoom;
        _pointer = pointer;
    }

    public Rect Bounds { get; }
    public bool HitTest(Point p) => Bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null) return;
        using var lease = leaseFeature.Lease();
        var c = lease.SkCanvas;

        float w = (float)Bounds.Width, h = (float)Bounds.Height;
        c.Save();
        var rect = new SKRect(0, 0, w, h);
        c.ClipRoundRect(new SKRoundRect(rect, 14, 14), SKClipOperation.Intersect, true);
        c.DrawRect(rect, new SKPaint { Color = new SKColor(0xFF0E0E13) });

        int n = _pts.Length;
        using var paint = new SKPaint { IsAntialias = true };
        if (n == 0)
        {
            paint.Color = new SKColor(0xFF737783);
            paint.TextSize = 14;
            c.DrawText("Nog geen punten — de index bouwt of de bieb is leeg.", 20, h / 2, paint);
            c.Restore();
            return;
        }

        var sx = new float[n];
        var sy = new float[n];
        var depth = new float[n];
        var persp = new float[n];
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            GalaxyMath.Project(_pts[i], _yaw, _pitch, _zoom, w, h, out sx[i], out sy[i], out depth[i], out persp[i]);
            order[i] = i;
        }
        var keys = (float[])depth.Clone();
        Array.Sort(keys, order);
        Array.Reverse(order);   // ver weg eerst

        bool glow = n <= 8000;
        for (int oi = 0; oi < n; oi++)
        {
            int i = order[oi];
            if (sx[i] < -20 || sx[i] > w + 20 || sy[i] < -20 || sy[i] > h + 20) continue;
            float t = Math.Clamp((depth[i] + 1.7f) / 3.4f, 0f, 1f);    // 0 = dichtbij
            byte alpha = (byte)(235 - t * 150);
            float r = Math.Max(0.8f, _pts[i].Size * persp[i] * (float)Math.Sqrt(_zoom));
            uint col = _pts[i].Color;
            if (_pts[i].Dim) { col = 0xFF3A3A42; alpha = (byte)(alpha / 4); }
            if (glow && !_pts[i].Dim)
            {
                paint.Color = new SKColor((byte)(col >> 16), (byte)(col >> 8), (byte)col, (byte)(alpha / 7));
                c.DrawCircle(sx[i], sy[i], r * 2.6f, paint);
            }
            paint.Color = new SKColor((byte)(col >> 16), (byte)(col >> 8), (byte)col, alpha);
            c.DrawCircle(sx[i], sy[i], r, paint);
        }

        // genre-labels
        paint.TextSize = 12.5f;
        foreach (var l in _labels)
        {
            var lp = new GalaxyPoint { X = l.X, Y = l.Y, Z = l.Z };
            GalaxyMath.Project(lp, _yaw, _pitch, _zoom, w, h, out var lx, out var ly, out var ld, out _);
            if (ld > 1.0f) continue;   // labels aan de achterkant verbergen
            paint.Color = new SKColor(0, 0, 0, 140);
            c.DrawText(l.Text, lx + 1, ly + 1, paint);
            paint.Color = new SKColor((byte)(l.Color >> 16), (byte)(l.Color >> 8), (byte)l.Color, 220);
            c.DrawText(l.Text, lx, ly, paint);
        }

        // hover-tooltip
        if (_pointer is { } mp)
        {
            int hi = -1;
            double bestD = 14 * 14;
            for (int i = 0; i < n; i++)
            {
                if (_pts[i].Dim) continue;
                double dx = sx[i] - mp.X, dy = sy[i] - mp.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; hi = i; }
            }
            if (hi >= 0)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1.6f;
                paint.Color = SKColors.White.WithAlpha(220);
                c.DrawCircle(sx[hi], sy[hi], Math.Max(4f, _pts[hi].Size * 2.0f), paint);
                paint.Style = SKPaintStyle.Fill;

                var line1 = _pts[hi].Label;
                var line2 = _pts[hi].Artist + (string.IsNullOrEmpty(_pts[hi].Album) ? "" : " — " + _pts[hi].Album);
                paint.TextSize = 12.5f;
                float w1 = paint.MeasureText(line1);
                float w2 = paint.MeasureText(line2);
                float bw = Math.Max(w1, w2) + 18;
                float bx = Math.Min((float)mp.X + 14, w - bw - 6);
                float by = Math.Max(6, (float)mp.Y - 46);
                paint.Color = new SKColor(20, 20, 26, 235);
                c.DrawRoundRect(new SKRect(bx, by, bx + bw, by + 40), 8, 8, paint);
                paint.Color = SKColors.White.WithAlpha(235);
                c.DrawText(line1, bx + 9, by + 16, paint);
                paint.Color = new SKColor(0xFFAFAFBC);
                c.DrawText(line2, bx + 9, by + 32, paint);
            }
        }
        c.Restore();
    }
}
