using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace PlayerApp;

// Palette extraction and color calculations are independent of the window and rendering loop.
internal sealed record AlbumPalette(Color[] Pigments, Color Foreground, Color Secondary,
    Color Accent, Color OnAccent, Color Track, Color Hover)
{
    private const int SampleBudget = 6000;
    private const int MaximumColors = 4;
    private const int ClusteringIterations = 10;
    private const double MinimumCleanCoverage = 0.025;
    private const double MinimumClusterCoverage = 0.015;
    private const double DistinctColorDistance = 0.0016;

    public static AlbumPalette Neutral { get; } = Create(
    [
        Color.FromArgb(255, 80, 111, 135), Color.FromArgb(255, 115, 96, 134)
    ]);

    public static AlbumPalette? Extract(byte[] bgra, int width, int height)
    {
        // Keep real artwork colors in the histogram; transparent RGB is never a candidate.
        var bins = new Dictionary<int, (double R, double G, double B, double Weight)>();
        var step = Math.Max(1, (int)Math.Sqrt(width * height / (double)SampleBudget));
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var i = (y * width + x) * 4;
                var weight = bgra[i + 3] / 255.0;
                if (weight < 0.1)
                {
                    continue;
                }

                var r = bgra[i + 2];
                var g = bgra[i + 1];
                var b = bgra[i];
                var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                bins.TryGetValue(key, out var bin);
                bins[key] = (bin.R + r * weight, bin.G + g * weight,
                    bin.B + b * weight, bin.Weight + weight);
            }
        }

        if (bins.Count == 0)
        {
            return null;
        }

        var histogram = bins.OrderBy(b => b.Key).Select(b => new Sample(
            Color.FromArgb(255, (byte)(b.Value.R / b.Value.Weight),
                (byte)(b.Value.G / b.Value.Weight), (byte)(b.Value.B / b.Value.Weight)),
            b.Value.Weight)).ToArray();
        var totalWeight = histogram.Sum(s => s.Weight);
        var clean = histogram.Where(s => s.IsCleanPigment).ToArray();
        // Neutral areas, shadows and muddy ochres must not dilute a colorful cover.
        // Require visible coverage so a few colored compression/noise pixels cannot recolor it.
        var samples = clean.Sum(s => s.Weight) >= totalWeight * MinimumCleanCoverage ? clean : histogram;
        var centers = new List<Sample> { samples.MaxBy(s => s.Weight * s.Quality)! };
        while (centers.Count < MaximumColors)
        {
            var next = samples.MaxBy(s => centers.Min(c => Distance(s, c))
                * Math.Sqrt(s.Weight) * s.Quality)!;
            // Distinct perceptual colors only: a monochrome cover should remain monochrome.
            if (centers.Min(c => Distance(next, c)) < DistinctColorDistance)
            {
                break;
            }

            centers.Add(next);
        }
        for (var iteration = 0; iteration < ClusteringIterations; iteration++)
        {
            var groups = Enumerable.Range(0, centers.Count).Select(_ => new List<Sample>()).ToArray();
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                var best = Enumerable.Range(0, centers.Count).MinBy(c => Distance(sample, centers[c]));
                groups[best].Add(sample);
            }
            for (var c = 0; c < centers.Count; c++)
            {
                var group = groups[c];
                if (group.Count == 0)
                {
                    continue;
                }

                var weight = group.Sum(s => s.Weight * s.Quality);
                var mean = (L: group.Sum(s => s.Lab.L * s.Weight * s.Quality) / weight,
                    A: group.Sum(s => s.Lab.A * s.Weight * s.Quality) / weight,
                    B: group.Sum(s => s.Lab.B * s.Weight * s.Quality) / weight);
                // Use an actual color near the perceptual center, never an RGB average of
                // unrelated hues (which can manufacture gray/brown absent from the art).
                var representative = group.MinBy(s => LabDistance(s.Lab, mean) / s.Quality)!;
                centers[c] = new Sample(representative.Color, group.Sum(s => s.Weight));
            }
        }
        var total = samples.Sum(s => s.Weight);
        var colors = centers.Where(c => c.Weight >= total * MinimumClusterCoverage)
            .OrderByDescending(c => c.Weight * c.Quality).Select(c => c.Color).Distinct().ToList();
        if (colors.Count == 1)
        {
            // Create two luminance levels of this same pigment, without adding gray to it.
            colors.Add(colors[0]);
        }
        return Create([.. colors]);
    }

    private static AlbumPalette Create(Color[] source)
    {
        var pigments = source.Select((c, i) => PaletteColor.WithLuminance(c, 0.055 + 0.022 * i)).ToArray();
        var seed = source.MaxBy(c => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)));
        // Bounded background luminance (<= .13) keeps every animation frame readable.
        var accent = PaletteColor.WithLuminance(PaletteColor.Mix(seed,
            Color.FromArgb(255, 227, 218, 205), 0.65), 0.68);
        var foreground = PaletteColor.WithLuminance(PaletteColor.Mix(accent,
            Color.FromArgb(255, 235, 230, 222), 0.65), 0.79);
        var secondary = PaletteColor.WithLuminance(foreground, 0.79);
        var onAccent = PaletteColor.WithLuminance(seed, 0.065);
        var track = PaletteColor.WithLuminance(pigments[0], 0.17);
        var hover = Color.FromArgb(28, accent.R, accent.G, accent.B);
        return new(pigments, foreground, secondary, accent, onAccent, track, hover);
    }

    private sealed class Sample
    {
        public Color Color { get; }
        public double Weight { get; }
        public (double L, double A, double B) Lab { get; }
        public bool IsCleanPigment { get; }
        public double Quality { get; }
        public Sample(Color color, double weight)
        {
            Color = color;
            Weight = weight;
            var r = PaletteColor.Linear(color.R);
            var g = PaletteColor.Linear(color.G);
            var b = PaletteColor.Linear(color.B);
            var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            Lab = (0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s);
            var chroma = Math.Sqrt(Lab.A * Lab.A + Lab.B * Lab.B);
            var hue = (Math.Atan2(Lab.B, Lab.A) * 180 / Math.PI + 360) % 360;
            var muddyWarmTone = hue is >= 35 and <= 100 && Lab.L < 0.72 && chroma < 0.13;
            IsCleanPigment = Lab.L is >= 0.25 and <= 0.94 && chroma >= 0.035
                && chroma / Math.Max(0.1, Lab.L) >= 0.055 && !muddyWarmTone;
            Quality = 0.2 + Math.Clamp(chroma / 0.16, 0, 1.5);
            if (muddyWarmTone)
            {
                Quality *= 0.35;
            }

            if (Lab.L < 0.25 || Lab.L > 0.94)
            {
                Quality *= 0.25;
            }
        }
    }

    private static double Distance(Sample a, Sample b) => LabDistance(a.Lab, b.Lab);
    private static double LabDistance((double L, double A, double B) a, (double L, double A, double B) b) =>
        0.5 * Math.Pow(a.L - b.L, 2) + Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2);
}

internal static class PaletteColor
{
    private static readonly double[] LinearTable = [.. Enumerable.Range(0, 256).Select(i =>
        i / 255.0 <= 0.04045 ? i / 255.0 / 12.92 : Math.Pow((i / 255.0 + 0.055) / 1.055, 2.4))];
    private static readonly byte[] SrgbTable = [.. Enumerable.Range(0, 8193).Select(i =>
        (byte)Math.Clamp(Math.Round(255 * (i / 8192.0 <= 0.0031308
            ? 12.92 * i / 8192.0 : 1.055 * Math.Pow(i / 8192.0, 1 / 2.4) - 0.055)), 0, 255))];

    public static double Linear(byte c) => LinearTable[c];
    public static byte Srgb(double c) => SrgbTable[(int)Math.Clamp(Math.Round(c * 8192), 0, 8192)];
    public static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    public static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + 0.05)
        / (Math.Min(Luminance(a), Luminance(b)) + 0.05);
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color WithLuminance(Color c, double target)
    {
        var r = Linear(c.R);
        var g = Linear(c.G);
        var b = Linear(c.B);
        var l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        // Mix in linear light towards a neutral tone; preserve hue and avoid clipped channels.
        if (l < target)
        {
            var t = (target - l) / (1 - l);
            r += (1 - r) * t; g += (1 - g) * t; b += (1 - b) * t;
        }
        else
        {
            var scale = target / l;
            r *= scale; g *= scale; b *= scale;
        }
        return Color.FromArgb(255, Srgb(r), Srgb(g), Srgb(b));
    }
}
