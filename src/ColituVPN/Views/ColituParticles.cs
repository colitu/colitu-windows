using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace v2rayN.Views;

public enum ColituParticleMode
{
    /// <summary>A wavy ring of dots that ripples in 3D; sits behind the connect button.</summary>
    Ring,

    /// <summary>A slowly turning globe of dots; hero of the sign-in page.</summary>
    Sphere
}

/// <summary>
/// Live 3D particle field, ported from the phone apps (colitu-ios
/// lib/colitu/theme/particles.dart): a few hundred projected points stamped
/// into a bitmap every frame. <see cref="Energy"/> (0..1) drives speed and
/// brightness so the same field can idle, work and glow. It only ticks while
/// it is visible, so a window in the tray costs nothing.
/// </summary>
public sealed class ColituParticles : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(ColituParticleMode), typeof(ColituParticles),
        new FrameworkPropertyMetadata(ColituParticleMode.Ring, (d, _) => ((ColituParticles)d)._particles = null));

    public static readonly DependencyProperty EnergyProperty = DependencyProperty.Register(
        nameof(Energy), typeof(double), typeof(ColituParticles), new FrameworkPropertyMetadata(0.35));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(ColituParticles), new FrameworkPropertyMetadata(Color.FromRgb(0x9F, 0x8C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Color2Property = DependencyProperty.Register(
        nameof(Color2), typeof(Color), typeof(ColituParticles), new FrameworkPropertyMetadata(Color.FromRgb(0xC4, 0xB5, 0xFD)));

    private const double Focal = 2.7;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 45);

    private Particle[]? _particles;
    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];
    private double _time;
    private double _energy = -1;
    private double _renderedGlow = -1;
    private TimeSpan _last;
    private bool _ticking;

    public ColituParticles()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateTicking();
        Loaded += (_, _) => UpdateTicking();
        Unloaded += (_, _) => StopTicking();
    }

    public ColituParticleMode Mode
    {
        get => (ColituParticleMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double Energy
    {
        get => (double)GetValue(EnergyProperty);
        set => SetValue(EnergyProperty, value);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public Color Color2
    {
        get => (Color)GetValue(Color2Property);
        set => SetValue(Color2Property, value);
    }

    private void UpdateTicking()
    {
        if (IsVisible && IsLoaded)
        {
            if (!_ticking)
            {
                _ticking = true;
                _last = TimeSpan.Zero;
                CompositionTarget.Rendering += OnRendering;
            }
        }
        else
        {
            StopTicking();
        }
    }

    private void StopTicking()
    {
        if (_ticking)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last != TimeSpan.Zero && now - _last < FrameInterval)
        {
            return;
        }
        var dt = _last == TimeSpan.Zero ? 0 : Math.Clamp((now - _last).TotalSeconds, 0, 0.05);
        _last = now;
        if (Window.GetWindow(this)?.WindowState == WindowState.Minimized)
        {
            return;
        }

        var target = Energy;
        // Energy eases toward the target so state changes ramp instead of snap.
        _energy = _energy < 0 ? target : _energy + (target - _energy) * Math.Min(1, dt * 3.2);
        _time += dt * (0.55 + _energy * 1.35);
        DrawFrame();
        if (Math.Abs(GlowAlpha() - _renderedGlow) > 0.004)
        {
            InvalidateVisual();
        }
    }

    private double GlowAlpha() => Mode == ColituParticleMode.Ring ? 0.10 + 0.30 * _energy : 0.12 + 0.2 * _energy;

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Mode == ColituParticleMode.Ring ? size / 2 * 0.95 : size / 2;
        _renderedGlow = GlowAlpha();
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(WithAlpha(Color, _renderedGlow), 0),
                new GradientStop(WithAlpha(Color, _renderedGlow * 0.35), 0.45),
                new GradientStop(WithAlpha(Color, 0), 1)
            }
        };
        dc.DrawEllipse(glow, null, center, radius, radius);
        EnsureBitmap();
        if (_bitmap != null)
        {
            dc.DrawImage(_bitmap, new Rect((ActualWidth - size) / 2, (ActualHeight - size) / 2, size, size));
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _bitmap = null;
        InvalidateVisual();
    }

    private void EnsureBitmap()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var px = (int)Math.Ceiling(size * dpi.DpiScaleX);
        if (_bitmap == null || _bitmap.PixelWidth != px)
        {
            _bitmap = new WriteableBitmap(px, px, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
            _pixels = new uint[px * px];
            if (_energy < 0)
            {
                _energy = Energy;
            }
            DrawFrame();
        }
    }

    // ── Particles ───────────────────────────────────────────────────────────
    private readonly record struct Particle(double A, double R, double Phase, double Size, double X, double Y, double Z);

    private Particle[] Particles()
    {
        if (_particles != null)
        {
            return _particles;
        }
        var random = new Random(7);
        var list = new List<Particle>();
        if (Mode == ColituParticleMode.Ring)
        {
            for (var i = 0; i < 1250; i++)
            {
                var a = random.NextDouble() * Math.PI * 2;
                var r = 0.6 + 0.4 * Math.Sqrt(random.NextDouble());
                list.Add(new Particle(a, r, random.NextDouble() * 6.283, 0.9 + random.NextDouble() * 1.5, 0, 0, 0));
            }
        }
        else
        {
            const int n = 900;
            var golden = Math.PI * (3 - Math.Sqrt(5));
            for (var i = 0; i < n; i++)
            {
                var y = 1 - i / (double)(n - 1) * 2;
                var radius = Math.Sqrt(1 - y * y);
                var theta = golden * i;
                list.Add(new Particle(0, 0, random.NextDouble() * 6.283, 0.8 + random.NextDouble() * 1.3, Math.Cos(theta) * radius, y, Math.Sin(theta) * radius));
            }
        }
        return _particles = [.. list];
    }

    private void DrawFrame()
    {
        if (_bitmap == null)
        {
            return;
        }
        Array.Clear(_pixels);
        var scale = _bitmap.PixelWidth / Math.Max(1, Math.Min(ActualWidth, ActualHeight));
        if (Mode == ColituParticleMode.Ring)
        {
            DrawRing(scale);
        }
        else
        {
            DrawSphere(scale);
        }
        var w = _bitmap.PixelWidth;
        _bitmap.WritePixels(new Int32Rect(0, 0, w, w), _pixels, w * 4, 0);
    }

    private void DrawRing(double scale)
    {
        var w = _bitmap!.PixelWidth;
        var center = w / 2.0;
        var radius = w / 2.0;
        const double tilt = 0.82; // radians: view the ring from above at an angle
        var cosT = Math.Cos(tilt);
        var sinT = Math.Sin(tilt);
        var spin = _time * 0.22;
        var amp = 0.16 + 0.26 * _energy;
        var widths = new[] { 1.6, 2.2, 2.9, 1.7, 2.4, 3.1, 1.9, 2.6, 3.4 };
        var b = 0.34 + 0.5 * _energy;
        var alphas = new[] { b * 0.28, b * 0.3, b * 0.34, b * 0.55, b * 0.6, b * 0.66, b * 0.95, b, b };
        foreach (var p in Particles())
        {
            var a = p.A + spin;
            var wave = Math.Sin(p.A * 3 + _time * 1.5 + p.Phase) * 0.5
                + Math.Sin(p.A * 6 - _time * 0.9) * 0.28
                + Math.Sin(p.R * 9 + _time * 1.15 + p.Phase) * 0.22;
            var r = p.R * (1 + 0.09 * wave * (0.5 + _energy));
            var x = r * Math.Cos(a);
            var y = r * Math.Sin(a);
            var z = amp * wave;
            var y2 = y * cosT - z * sinT;
            var z2 = y * sinT + z * cosT;
            var s = Focal / (Focal + z2);
            var px = center + x * s * radius * 0.92;
            var py = center + y2 * s * radius * 0.92;
            var depth = Math.Clamp((z2 + 0.6) / 1.2, 0, 1);
            var depthBucket = depth < 0.4 ? 0 : depth < 0.7 ? 1 : 2;
            var sizeBucket = p.Size < 1.4 ? 0 : p.Size < 1.9 ? 1 : 2;
            var bucket = depthBucket * 3 + sizeBucket;
            Stamp(px, py, widths[bucket] * scale / 2, Mix(bucket / 8.0), Math.Clamp(alphas[bucket], 0, 1));
        }
    }

    private void DrawSphere(double scale)
    {
        var w = _bitmap!.PixelWidth;
        var center = w / 2.0;
        var radius = w / 2.0 * 0.78;
        var ry = _time * 0.32;
        const double rx = 0.42;
        var cy = Math.Cos(ry);
        var sy = Math.Sin(ry);
        var cx = Math.Cos(rx);
        var sx = Math.Sin(rx);

        // Faint equator and meridian for structure.
        var ringColor = Color;
        var ringAlpha = 0.18 + 0.12 * _energy;
        for (var ring = 0; ring < 2; ring++)
        {
            for (var i = 0; i < 520; i++)
            {
                var t = i / 520.0 * Math.PI * 2;
                var (x, y, z) = ring == 0 ? (Math.Cos(t), 0.0, Math.Sin(t)) : (Math.Cos(t), Math.Sin(t), 0.0);
                var (ppx, ppy, _) = Project(x, y, z, cy, sy, cx, sx, center, radius);
                Stamp(ppx, ppy, 0.5 * scale, ringColor, ringAlpha);
            }
        }

        var widths = new[] { 1.3, 1.9, 1.6, 2.3, 2.0, 2.9 };
        var b = 0.3 + 0.5 * _energy;
        var alphas = new[] { b * 0.18, b * 0.22, b * 0.5, b * 0.58, b * 0.95, b };
        foreach (var p in Particles())
        {
            var breathe = 1 + 0.025 * Math.Sin(_time * 1.3 + p.Phase) * (0.4 + _energy);
            var x1 = p.X * cy + p.Z * sy;
            var z1 = -p.X * sy + p.Z * cy;
            var y2 = p.Y * cx - z1 * sx;
            var z2 = p.Y * sx + z1 * cx;
            var s = Focal / (Focal + z2 * 0.9);
            var px = center + x1 * s * radius * breathe;
            var py = center + y2 * s * radius * breathe;
            var depth = Math.Clamp((z2 + 1) / 2, 0, 1);
            var depthBucket = depth < 0.35 ? 0 : depth < 0.62 ? 1 : 2;
            var sizeBucket = p.Size < 1.45 ? 0 : 1;
            var bucket = depthBucket * 2 + sizeBucket;
            Stamp(px, py, widths[bucket] * scale / 2, Mix(bucket / 5.0), Math.Clamp(alphas[bucket], 0, 1));
        }
    }

    private static (double X, double Y, double Z) Project(double x, double y, double z, double cy, double sy, double cx, double sx, double center, double radius)
    {
        var x1 = x * cy + z * sy;
        var z1 = -x * sy + z * cy;
        var y2 = y * cx - z1 * sx;
        var z2 = y * sx + z1 * cx;
        var s = Focal / (Focal + z2 * 0.9);
        return (center + x1 * s * radius, center + y2 * s * radius, z2);
    }

    private Color Mix(double t)
    {
        var a = Color;
        var b = Color2;
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Blends an anti-aliased round dot into the premultiplied pixel buffer.</summary>
    private void Stamp(double cx, double cy, double r, Color color, double alpha)
    {
        var w = _bitmap!.PixelWidth;
        var x0 = Math.Max(0, (int)Math.Floor(cx - r - 1));
        var x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + r + 1));
        var y0 = Math.Max(0, (int)Math.Floor(cy - r - 1));
        var y1 = Math.Min(w - 1, (int)Math.Ceiling(cy + r + 1));
        for (var y = y0; y <= y1; y++)
        {
            var dy = y + 0.5 - cy;
            var row = y * w;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x + 0.5 - cx;
                var coverage = r + 0.5 - Math.Sqrt(dx * dx + dy * dy);
                if (coverage <= 0)
                {
                    continue;
                }
                var sa = alpha * Math.Min(1, coverage);
                var dst = _pixels[row + x];
                var inv = 1 - sa;
                var oa = sa * 255 + (dst >> 24) * inv;
                var or = color.R * sa + ((dst >> 16) & 0xFF) * inv;
                var og = color.G * sa + ((dst >> 8) & 0xFF) * inv;
                var ob = color.B * sa + (dst & 0xFF) * inv;
                _pixels[row + x] = ((uint)oa << 24) | ((uint)or << 16) | ((uint)og << 8) | (uint)ob;
            }
        }
    }

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);
}
