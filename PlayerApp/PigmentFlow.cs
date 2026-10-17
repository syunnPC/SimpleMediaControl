using System;
using Windows.UI;

namespace PlayerApp;

// A small dye field: semi-Lagrangian advection, diffusion and intermittent pigment drops.
// The analytic velocity is the curl of a stream function (divergence-free).
internal sealed class PigmentFlow
{
    public const int Width = 144;
    public const int Height = 52;
    private const int PixelCount = Width * Height;
    private const int PigmentCount = 4;
    private const double DropDurationSeconds = 1.6;
    private readonly float[][] _density = new float[PigmentCount][];
    private readonly float[][] _next = new float[PigmentCount][];
    private readonly double[,] _absorption = new double[PigmentCount, 3];
    private readonly double[,] _targetAbsorption = new double[PigmentCount, 3];
    private double _time;
    private double _nextDrop;
    private double _dropRemaining;
    private double _dropX;
    private double _dropY;
    private int _activePigment;
    private int _drop;

    public PigmentFlow(AlbumPalette palette)
    {
        for (var c = 0; c < PigmentCount; c++)
        {
            _density[c] = new float[PixelCount];
            _next[c] = new float[PixelCount];
            AddDrop(c, 0.17 + (c % 2) * 0.65, 0.18 + (c / 2) * 0.63, 0.60, 1.8);
        }
        SetPalette(palette, true);
    }

    public void SetPalette(AlbumPalette palette, bool immediately = false)
    {
        for (var c = 0; c < PigmentCount; c++)
        {
            // Alternate slots when artwork has two or three representative pigments.
            var color = palette.Pigments[c % palette.Pigments.Length];
            var reflectance = new[] { PaletteColor.Linear(color.R), PaletteColor.Linear(color.G), PaletteColor.Linear(color.B) };
            for (var channel = 0; channel < 3; channel++)
            {
                var r = Math.Max(0.001, reflectance[channel]);
                // Kubelka–Munk K/S: mixtures absorb light instead of adding RGB light.
                _targetAbsorption[c, channel] = (1 - r) * (1 - r) / (2 * r);
                if (immediately)
                {
                    _absorption[c, channel] = _targetAbsorption[c, channel];
                }
            }
        }
    }

    public void Advance(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, 0.05);
        _time += seconds;
        var transition = 1 - Math.Exp(-seconds * 2.8);
        for (var c = 0; c < PigmentCount; c++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                _absorption[c, channel] += (_targetAbsorption[c, channel] - _absorption[c, channel]) * transition;
            }
        }

        if (_time >= _nextDrop)
        {
            _activePigment = _drop++ % PigmentCount;
            _dropX = 0.5 + 0.36 * Math.Sin(_drop * 2.39996);
            _dropY = 0.5 + 0.34 * Math.Cos(_drop * 1.61803);
            _dropRemaining = DropDurationSeconds;
            _nextDrop = _time + 2.8;
        }
        if (_dropRemaining > 0)
        {
            // Feed a drop gradually: its arrival should not flash or jump between frames.
            var amount = Math.Min(seconds, _dropRemaining);
            var envelope = Math.Sin(Math.PI * (1 - _dropRemaining / DropDurationSeconds));
            AddDrop(_activePigment, _dropX, _dropY, 0.14, amount * 1.8 * envelope);
            _dropRemaining -= amount;
        }

        var diffusion = seconds * 1.4;
        var decay = (float)Math.Exp(-seconds * 0.055);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var nx = x / (double)(Width - 1);
                var ny = y / (double)(Height - 1);
                var phase = _time * 0.065;
                // Broad circulation with smaller curls; no random flicker or periodic reset.
                var vx = 0.033 * Math.Sin(Math.PI * nx) * Math.Cos(Math.PI * ny)
                    + 0.025 * Math.Sin(3 * Math.PI * nx + phase) * Math.Cos(2 * Math.PI * ny - phase)
                    + 0.009 * Math.Sin(5 * Math.PI * nx - phase) * Math.Cos(3 * Math.PI * ny + phase);
                var vy = -0.033 * Math.Cos(Math.PI * nx) * Math.Sin(Math.PI * ny)
                    - 0.0375 * Math.Cos(3 * Math.PI * nx + phase) * Math.Sin(2 * Math.PI * ny - phase)
                    - 0.015 * Math.Cos(5 * Math.PI * nx - phase) * Math.Sin(3 * Math.PI * ny + phase);
                var sx = Math.Clamp(x - vx * seconds * Width, 0, Width - 1.001);
                var sy = Math.Clamp(y - vy * seconds * Height, 0, Height - 1.001);
                var x0 = (int)sx;
                var y0 = (int)sy;
                var fx = sx - x0;
                var fy = sy - y0;
                var i = y * Width + x;
                for (var c = 0; c < PigmentCount; c++)
                {
                    var d = _density[c];
                    var a = d[y0 * Width + x0] * (1 - fx) + d[y0 * Width + x0 + 1] * fx;
                    var b = d[(y0 + 1) * Width + x0] * (1 - fx) + d[(y0 + 1) * Width + x0 + 1] * fx;
                    var advected = a * (1 - fy) + b * fy;
                    var neighbors = d[y * Width + Math.Max(0, x - 1)] + d[y * Width + Math.Min(Width - 1, x + 1)]
                        + d[Math.Max(0, y - 1) * Width + x] + d[Math.Min(Height - 1, y + 1) * Width + x];
                    _next[c][i] = (float)Math.Max(0, (advected + diffusion * (neighbors - 4 * d[i])) * decay);
                }
            }
        }

        for (var c = 0; c < PigmentCount; c++)
        {
            (_density[c], _next[c]) = (_next[c], _density[c]);
        }
    }

    private void AddDrop(int pigment, double x, double y, double radius, double strength)
    {
        for (var row = 0; row < Height; row++)
        {
            for (var col = 0; col < Width; col++)
            {
                // Correct for the wide player: each drop spreads in physical space.
                var dx = (col / (double)(Width - 1) - x) * Width / Height;
                var dy = row / (double)(Height - 1) - y;
                var distance = (dx * dx + dy * dy) / (radius * radius);
                _density[pigment][row * Width + col] += (float)(strength * Math.Exp(-distance * 1.8));
            }
        }
    }

    public Color Sample(double x, double y) => ColorAt((int)Math.Clamp(y * (Height - 1), 0, Height - 1) * Width
        + (int)Math.Clamp(x * (Width - 1), 0, Width - 1));

    private Color ColorAt(int i)
    {
        var total = 0.0;
        double r = 0, g = 0, b = 0;
        for (var c = 0; c < PigmentCount; c++)
        {
            // A small suspended pigment concentration avoids empty/black regions.
            var weight = _density[c][i] + 0.0015;
            total += weight;
            r += weight * _absorption[c, 0];
            g += weight * _absorption[c, 1];
            b += weight * _absorption[c, 2];
        }
        static double Reflectance(double ks) => 1 / (1 + ks + Math.Sqrt(ks * ks + 2 * ks));
        r = Reflectance(r / total); g = Reflectance(g / total); b = Reflectance(b / total);
        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var scale = Math.Clamp(luminance, 0.04, 0.125) / luminance;
        return Color.FromArgb(255, PaletteColor.Srgb(r * scale), PaletteColor.Srgb(g * scale), PaletteColor.Srgb(b * scale));
    }

    public void Render(byte[] bgra)
    {
        for (var i = 0; i < PixelCount; i++)
        {
            var color = ColorAt(i);
            bgra[i * 4] = color.B;
            bgra[i * 4 + 1] = color.G;
            bgra[i * 4 + 2] = color.R;
            bgra[i * 4 + 3] = 255;
        }
    }
}
