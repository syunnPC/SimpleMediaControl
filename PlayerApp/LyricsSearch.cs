using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayerApp;

internal sealed record LyricsRecord(string? TrackName, string? ArtistName, string? AlbumName, double? Duration,
    string? PlainLyrics, string? SyncedLyrics, bool Instrumental);

internal static class LyricsSearch
{
    private static readonly Regex Suffix = new(@"\s*(?:\([^()]*\)|\[[^\[\]]*\]|（[^（）]*）|【[^【】]*】)\s*$|\s+[-–—|/~～〜]\s+.*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Featuring = new(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Versions = new(@"\b(live|remix|instrumental|karaoke|acoustic|demo)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<string> GetQueries(LyricsTrack track)
    {
        var queries = new List<string>();
        var title = Core(track.Title);
        var artist = ArtistCore(track.Artist);
        Add(track.Title, track.Artist);
        Add(title, artist);
        Add(Prefix(title, 0.75), artist);
        Add(Prefix(title, 0.5), artist);
        return queries;

        void Add(string name, string performer)
        {
            name = SearchTerms(name);
            performer = SearchTerms(performer);
            if (name.Length == 0 || performer.Length == 0)
            {
                return;
            }
            var query = BuildUri(name, performer);
            if (!queries.Contains(query, StringComparer.Ordinal))
            {
                queries.Add(query);
            }
        }
    }

    internal static string BuildUri(string title, string artist) =>
        "api/search?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);

    private static string Prefix(string value, double ratio)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            return string.Join(' ', words.Take(Math.Max(1, (int)Math.Floor(words.Length * ratio))));
        }
        // Count Unicode text elements, not UTF-16 code units, for Japanese/emoji titles.
        var starts = StringInfo.ParseCombiningCharacters(value);
        var count = Math.Max(3, (int)Math.Floor(starts.Length * ratio));
        return count < starts.Length ? value[..starts[count]] : value;
    }

    public static LyricsRecord? Select(LyricsTrack track, IEnumerable<LyricsRecord> candidates)
    {
        var ranked = candidates.Where(c => c.Instrumental || !string.IsNullOrWhiteSpace(c.SyncedLyrics) || !string.IsNullOrWhiteSpace(c.PlainLyrics))
            .Select(c => (Record: c, Score: Score(track, c), Difference: DurationDifference(track, c) ?? 0))
            .Where(c => c.Score > 0).OrderByDescending(c => c.Score).ToArray();
        if (ranked.Length == 0)
        {
            return null;
        }
        var best = ranked.OrderBy(c => c.Difference).ThenByDescending(c => c.Score).First();
        // Different recordings with almost equal evidence are not safe to auto-select.
        if (ranked.Skip(1).Any(c => Math.Abs(best.Difference - c.Difference) < 0.001
            && best.Score - c.Score < 0.25 && !SameRecording(best.Record, c.Record)))
        {
            return null;
        }
        return best.Record;
    }

    internal static bool IsBetterCandidate(LyricsTrack track, LyricsRecord candidate, LyricsRecord current) =>
        (DurationDifference(track, candidate) ?? 0) < (DurationDifference(track, current) ?? 0)
        || (Math.Abs((DurationDifference(track, candidate) ?? 0) - (DurationDifference(track, current) ?? 0)) < 0.001
            && Score(track, candidate) > Score(track, current));

    internal static bool HasExactDuration(LyricsTrack track, LyricsRecord candidate) =>
        DurationDifference(track, candidate) is 0;

    private static double Score(LyricsTrack track, LyricsRecord candidate)
    {
        if (candidate.TrackName is not { } title || candidate.ArtistName is not { } artist)
        {
            return 0;
        }
        var titleScore = Similarity(Core(track.Title), Core(title));
        var artistScore = Similarity(ArtistCore(track.Artist), ArtistCore(artist));
        if (titleScore < 0.82 || artistScore < 0.82 || Version(track.Title) != Version(title))
        {
            return 0;
        }
        var durationScore = 0.0;
        if (track.DurationSeconds is { } seconds)
        {
            var difference = DurationDifference(track, candidate);
            if (difference is not { } durationDifference)
            {
                return 0;
            }
            var tolerance = Math.Max(seconds * 0.01, 3);
            if (durationDifference > tolerance)
            {
                return 0;
            }
            durationScore = 1 - durationDifference / tolerance;
        }
        // Without a duration, only unambiguous canonical title/artist matches qualify.
        else if (titleScore < 1 || artistScore < 1)
        {
            return 0;
        }
        // After title/artist and the duration gate agree, prefer a synced edition
        // over a plain edition with a slightly closer rounded duration.
        return titleScore * 6 + artistScore * 4 + durationScore * 0.2
            + (!string.IsNullOrWhiteSpace(candidate.SyncedLyrics) ? 0.3 : 0);
    }

    private static double? DurationDifference(LyricsTrack track, LyricsRecord candidate)
    {
        if (track.DurationSeconds is not { } seconds)
        {
            return null;
        }
        return candidate.Duration is { } duration && double.IsFinite(duration) && duration > 0
            ? Math.Abs(seconds - duration)
            : null;
    }

    private static bool SameRecording(LyricsRecord a, LyricsRecord b) =>
        Normalize(Core(a.TrackName ?? "")) == Normalize(Core(b.TrackName ?? ""))
        && Normalize(ArtistCore(a.ArtistName ?? "")) == Normalize(ArtistCore(b.ArtistName ?? ""))
        && (a.Duration is not { } aDuration || b.Duration is not { } bDuration || Math.Abs(aDuration - bDuration) <= 5);

    private static string Version(string value) => string.Join(',', Versions.Matches(value.Normalize(NormalizationForm.FormKC))
        .Select(m => m.Value.ToLowerInvariant()).Distinct().Order());

    private static string ArtistCore(string value) => Featuring.Replace(Core(value), "").Trim();

    private static string Core(string value)
    {
        value = value.Normalize(NormalizationForm.FormKC).Trim();
        while (true)
        {
            var shortened = Suffix.Replace(value, "").Trim();
            if (shortened.Length == 0 || shortened == value)
            {
                return value;
            }
            value = shortened;
        }
    }

    private static string SearchTerms(string value) => string.Join(' ',
        string.Concat(value.Normalize(NormalizationForm.FormKC).EnumerateRunes()
            .Select(r => Rune.IsLetterOrDigit(r) ? r.ToString() : " ")).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // Preserve kana voicing marks while removing accents, then recompose them
    // so voiced, semi-voiced, and unvoiced kana remain distinct characters.
    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKD).EnumerateRunes()
        .Where(r => Rune.IsLetterOrDigit(r) || r.Value is 0x3099 or 0x309A).Take(256)
        .Select(r => Rune.ToLowerInvariant(r).ToString())).Normalize(NormalizationForm.FormKC);

    private static double Similarity(string a, string b)
    {
        a = Normalize(a);
        b = Normalize(b);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }
        if (a == b)
        {
            return 1;
        }
        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;
        if (shorter.Length >= 4 && shorter.Length >= longer.Length * 0.7 && longer.StartsWith(shorter, StringComparison.Ordinal))
        {
            return 0.88;
        }
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
