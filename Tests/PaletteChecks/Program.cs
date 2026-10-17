using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PlayerApp;
using Windows.UI;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static AlbumPalette Extract(params Color[] colors)
{
    var pixels = new byte[64 * 64 * 4];
    for (var i = 0; i < 64 * 64; i++)
    {
        var c = colors[i % colors.Length];
        pixels[i * 4] = c.B; pixels[i * 4 + 1] = c.G;
        pixels[i * 4 + 2] = c.R; pixels[i * 4 + 3] = c.A;
    }
    return AlbumPalette.Extract(pixels, 64, 64)!;
}

var black = Color.FromArgb(255, 0, 0, 0);
var white = Color.FromArgb(255, 255, 255, 255);
var red = Color.FromArgb(255, 240, 45, 35);
var yellow = Color.FromArgb(255, 245, 209, 42);
var blue = Color.FromArgb(255, 25, 105, 231);
var green = Color.FromArgb(255, 30, 201, 90);
Check(AlbumPalette.Extract(new byte[64], 4, 4) is null, "Transparent image must have no palette.");
var mono = Extract(black);
Check(mono.Pigments.Length == 2 && mono.Pigments.All(c => c.R == c.G && c.G == c.B),
    "A black cover must stay neutral, with two distinct tones.");
var four = Extract(red, yellow, blue, green);
Check(four.Pigments.Length == 4, "A four-color cover should retain four distinct pigments.");
Check(four.Pigments.SequenceEqual(Extract(red, yellow, blue, green).Pigments), "Extraction must be deterministic.");
Check(Extract(red, Color.FromArgb(0, 0, 255, 0)).Pigments.All(c => c.R >= c.G),
    "Transparent RGB must not contaminate pigment selection.");
var gray = Color.FromArgb(255, 112, 110, 108);
var muddyBrown = Color.FromArgb(255, 105, 83, 57);
var colorfulCover = Extract([.. Enumerable.Repeat(gray, 65), .. Enumerable.Repeat(muddyBrown, 15), .. Enumerable.Repeat(red, 10), .. Enumerable.Repeat(blue, 10)]);
Check(colorfulCover.Pigments.Length == 2, "Gray and muddy brown must not take pigment slots on a colorful cover.");
Check(colorfulCover.Pigments.All(c => c.R > c.G * 1.3 || c.B > c.G * 1.3),
    "The selected red/blue palette must not contain neutral or brown contamination.");
var darkCover = Extract([.. Enumerable.Repeat(black, 70), .. Enumerable.Repeat(white, 20), .. Enumerable.Repeat(blue, 10)]);
Check(darkCover.Pigments.All(c => c.B > c.R * 1.5 && c.B > c.G * 1.3),
    "A colored accent must survive dominant black/white areas without gray dilution.");
Check(darkCover.Pigments.Select((c, i) => c == PaletteColor.WithLuminance(blue, 0.055 + 0.022 * i)).All(v => v),
    "A single selected pigment must retain its color without an added gray second pigment.");
var noisyMono = Extract([.. Enumerable.Repeat(gray, 4095), red]);
Check(noisyMono.Pigments.All(c => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 8),
    "A single colored noise pixel must not recolor a neutral cover.");
var sepia = Extract(muddyBrown, Color.FromArgb(255, 155, 124, 88));
Check(sepia.Pigments.All(c => c.R > c.B), "A sepia-only cover must keep its original warm color family.");
Check(colorfulCover.Pigments.Select((c, i) => c == PaletteColor.WithLuminance(red, 0.055 + 0.022 * i)
    || c == PaletteColor.WithLuminance(blue, 0.055 + 0.022 * i)).All(v => v),
    "Cluster representatives must come from the artwork rather than averages of unrelated hues.");
var pigmentMix = new PigmentFlow(four with { Pigments = [yellow, blue] }).Sample(0.5, 0.5);
Check(pigmentMix.G > pigmentMix.R && pigmentMix.G > pigmentMix.B,
    "Yellow and blue pigments must mix towards green, rather than additive gray.");
var additiveMix = PaletteColor.Mix(yellow, blue, 0.5);
Check(Math.Abs(pigmentMix.G / (double)pigmentMix.B - additiveMix.G / (double)additiveMix.B) > 0.3,
    "Pigment mixing must differ materially from RGB averaging.");

var random = new Random(17017);
var palettes = new[] { mono, Extract(white), Extract(black, white), Extract(red), Extract(blue), four,
    colorfulCover, darkCover, noisyMono, sepia,
    Extract(Color.FromArgb(255, 255, 0, 255), Color.FromArgb(255, 0, 255, 255)), AlbumPalette.Neutral }
    .Concat(Enumerable.Range(0, 160).Select(_ => Extract([.. Enumerable.Range(0, 4).Select(_ => Color.FromArgb(255, (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)))]))).ToArray();
var pixelsOut = new byte[PigmentFlow.Width * PigmentFlow.Height * 4];
var minimumText = double.MaxValue;
var minimumAccent = double.MaxValue;
foreach (var palette in palettes)
{
    Check(palette.Pigments.Length is >= 2 and <= 4, "Palette must contain 2–4 pigments.");
    foreach (var control in new[] { palette.Foreground, palette.Secondary, palette.Accent })
    {
        Check(PaletteColor.Luminance(control) >= 0.55, "Controls must not become dark.");
        Check(Math.Min(control.R, Math.Min(control.G, control.B)) < 250, "Controls must not become white.");
    }
    Check(PaletteColor.Contrast(palette.OnAccent, palette.Accent) >= 4.5, "Play icon contrast.");
    Check(PaletteColor.Contrast(palette.Accent, palette.Track) >= 3, "Slider fill/track contrast.");
    var flow = new PigmentFlow(palette);
    for (var frame = 0; frame < 12; frame++)
    {
        flow.Advance(1.0 / 30);
        for (var y = 0; y <= 10; y++)
        {
            for (var x = 0; x <= 20; x++)
            {
                var bg = flow.Sample(x / 20.0, y / 10.0);
                minimumText = Math.Min(minimumText, PaletteColor.Contrast(palette.Secondary, bg));
                minimumAccent = Math.Min(minimumAccent, PaletteColor.Contrast(palette.Accent, bg));
                Check(PaletteColor.Luminance(bg) <= 0.13, "Background luminance bound.");
                Check(PaletteColor.Contrast(palette.Secondary, bg) >= 4.5, "Text contrast over moving pigments.");
                Check(PaletteColor.Contrast(palette.Accent, bg) >= 3, "Control contrast over moving pigments.");
            }
        }
    }
}

var animation = new PigmentFlow(four);
animation.Render(pixelsOut);
var before = pixelsOut.ToArray();
var timer = Stopwatch.StartNew();
var output = args.Length > 0 ? args[0] : null;
void SaveFrame(string name)
{
    if (output is null)
    {
        return;
    }

    Directory.CreateDirectory(output);
    // Portable pixmap preview, kept outside the source tree by the caller.
    using var file = File.Create(Path.Combine(output, name + ".ppm"));
    file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{PigmentFlow.Width} {PigmentFlow.Height}\n255\n"));
    for (var i = 0; i < pixelsOut.Length; i += 4)
    {
        file.Write([pixelsOut[i + 2], pixelsOut[i + 1], pixelsOut[i]]);
    }
}
SaveFrame("pigments-00");
for (var frame = 1; frame <= 1800; frame++)
{
    animation.Advance(1.0 / 30);
    animation.Render(pixelsOut);
    if (frame % 30 == 0)
    {
        var samples = Enumerable.Range(0, 60).Select(i => animation.Sample((i % 10) / 9.0, (i / 10) / 5.0)).ToArray();
        Check(samples.All(c => PaletteColor.Contrast(four.Secondary, c) >= 4.5), "Long-running text contrast.");
        Check(samples.Select(c => (int)c.R + c.G + c.B).Max()
            - samples.Select(c => (int)c.R + c.G + c.B).Min() > 20, "Flow must retain spatial pigment variation.");
    }
    if (frame is 180 or 540 or 1800)
    {
        SaveFrame($"pigments-{frame / 30:00}");
    }
}
timer.Stop();
Check(before.Where((b, i) => b != pixelsOut[i]).Count() > before.Length / 4, "Flow must move spatially over time.");
Check(pixelsOut.Where((_, i) => i % 4 == 3).All(a => a == 255), "Render must be fully opaque.");
animation.SetPalette(Extract(white));
for (var frame = 0; frame < 180; frame++)
{
    animation.Advance(1.0 / 30);
}

Check(PaletteColor.Luminance(animation.Sample(0.5, 0.5)) <= 0.13, "Palette transition must preserve contrast bounds.");
Console.WriteLine($"PASS: {palettes.Length} palettes; text contrast >= {minimumText:F2}:1; controls >= {minimumAccent:F2}:1.");
Console.WriteLine($"60-second advection/diffusion/render check: {timer.Elapsed.TotalMilliseconds / 1800:F2} ms/frame.");
