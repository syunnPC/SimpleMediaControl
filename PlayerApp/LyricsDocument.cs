using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayerApp;

internal sealed record LyricsTrack(string Title, string Artist, string Album, int? DurationSeconds)
{
    public bool IsSameSong(LyricsTrack? other)
    {
        var current = Canonicalize();
        var next = other?.Canonicalize();
        return next is not null && string.Equals(current.Title, next.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Artist, next.Artist, StringComparison.OrdinalIgnoreCase);
    }

    public LyricsTrack Canonicalize() => this with
    {
        Title = Title.Normalize(NormalizationForm.FormKC).Trim(),
        // Some sources put "artist — album" in the artist field itself.
        Artist = Artist.Normalize(NormalizationForm.FormKC).Split(" — ", 2, StringSplitOptions.None)[0].Trim(),
        Album = Album.Normalize(NormalizationForm.FormKC).Trim()
    };

    public static LyricsTrack? Create(MediaData? media, TimeSpan duration)
    {
        if (media is null || string.IsNullOrWhiteSpace(media.Title))
        {
            return null;
        }
        var artist = string.IsNullOrWhiteSpace(media.Artist) ? media.AlbumArtist : media.Artist;
        if (string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }
        var seconds = Math.Round(duration.TotalSeconds);
        return new LyricsTrack(media.Title, artist, media.AlbumTitle, seconds is >= 1 and <= 3600 ? (int)seconds : null).Canonicalize();
    }
}

internal enum LyricsKind { Synced, Plain, Instrumental, NotFound }
internal sealed record LyricsCue(TimeSpan Time, string Text)
{
    public TimeSpan HighlightTime => Time > TimeSpan.FromSeconds(1) ? Time - TimeSpan.FromSeconds(1) : TimeSpan.Zero;
}
internal sealed record LyricsGap(TimeSpan Start, TimeSpan End, int Phase);

internal sealed class LyricsDocument(LyricsKind kind, IReadOnlyList<LyricsCue> cues, string plainText)
{
    private static readonly Regex Timestamp = new(@"\G\[([0-9]{1,6}):([0-5][0-9])(?:\.([0-9]{1,3}))?\]",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex WordTimestamp = new(@"<[0-9]{1,6}:[0-5][0-9](?:\.[0-9]{1,3})?>",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public LyricsKind Kind { get; } = kind;
    public IReadOnlyList<LyricsCue> Cues { get; } = cues;
    public string PlainText { get; } = plainText;

    public LyricsDocument ForDisplay() => Kind == LyricsKind.Synced && Cues.Count > 0 && Cues[0].HighlightTime > TimeSpan.Zero
        ? new(Kind, [new LyricsCue(TimeSpan.Zero, ""), .. Cues], PlainText) : this;

    public TimeSpan? GetSeekPosition(int index, TimeSpan duration)
    {
        if (Kind != LyricsKind.Synced || index < 0 || index >= Cues.Count || duration <= TimeSpan.Zero)
        {
            return null;
        }
        var position = Cues[index].HighlightTime;
        return position < duration ? position : null;
    }

    public LyricsGap? GetGap(int index, TimeSpan position, TimeSpan duration)
    {
        if (Kind != LyricsKind.Synced || index < 0 || index >= Cues.Count || !string.IsNullOrWhiteSpace(Cues[index].Text))
        {
            return null;
        }
        var start = Cues[index].HighlightTime;
        var end = index + 1 < Cues.Count && Cues[index + 1].HighlightTime < duration ? Cues[index + 1].HighlightTime : duration;
        if (end <= start || position < start || position >= end)
        {
            return null;
        }
        var phase = Math.Clamp((int)((double)(position - start).Ticks / (end - start).Ticks * 3), 0, 2);
        return new(start, end, phase);
    }

    public static LyricsDocument Parse(string? synced, string? plain, bool instrumental)
    {
        if (instrumental)
        {
            return new(LyricsKind.Instrumental, [], "");
        }
        var lines = new List<LyricsCue>();
        var offset = 0;
        foreach (var raw in (synced ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) && line.EndsWith(']'))
            {
                if (int.TryParse(line.AsSpan(8, line.Length - 9), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                {
                    offset = parsed;
                }
                continue;
            }
            var times = new List<TimeSpan>();
            var position = 0;
            while (Timestamp.Match(line, position) is { Success: true } match)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = match.Groups[3].Value;
                var milliseconds = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
                times.Add(TimeSpan.FromMilliseconds(((long)minutes * 60 + seconds) * 1000 + milliseconds));
                position += match.Length;
            }
            var text = WordTimestamp.Replace(line[position..].Trim(), "");
            foreach (var time in times)
            {
                lines.Add(new(time, text));
            }
        }
        if (lines.Count > 0)
        {
            // Positive LRC offset advances lyrics. Empty cues delimit instrumental gaps.
            var cues = lines.GroupBy(line => line.Time).OrderBy(group => group.Key)
                .Select(group => new LyricsCue(group.Key - TimeSpan.FromMilliseconds(offset),
                    string.Join('\n', group.Select(line => line.Text).Where(text => text.Length > 0)))).ToArray();
            return new(LyricsKind.Synced, cues, plain?.Trim() ?? "");
        }
        return string.IsNullOrWhiteSpace(plain)
            ? new(LyricsKind.NotFound, [], "")
            : new(LyricsKind.Plain, [], plain.Trim());
    }

    public int GetActiveIndex(TimeSpan position) => FindIndex(position, false);

    public int GetHighlightIndex(TimeSpan position) => FindIndex(position, true);

    private int FindIndex(TimeSpan position, bool highlighting)
    {
        var low = 0;
        var high = Cues.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var time = highlighting ? Cues[middle].HighlightTime : Cues[middle].Time;
            if (time <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low - 1;
    }
}
