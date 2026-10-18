using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerApp;

internal sealed class LyricsRateLimitException : Exception { }

// Owns its HttpClient. No SDK or third-party parser is needed for LRCLIB's read API.
internal sealed class LyricsClient(HttpClient http, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<LyricsTrack, (LyricsDocument Document, DateTimeOffset Expires)> _cache = new();
    private DateTimeOffset _nextRequest;

    public static LyricsClient Create()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PlayerApp/1.0 (+https://github.com/syunnPC/SimpleMediaControl)");
        return new(http, TimeProvider.System);
    }

    public async Task<LyricsDocument> GetAsync(LyricsTrack track, bool refresh, CancellationToken cancellationToken)
    {
        track = track.Canonicalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _cache.TryGetValue(track, out var cached) && cached.Expires > clock.GetUtcNow())
            {
                return cached.Document;
            }
            var document = LyricsDocument.Parse(null, null, false);
            // Search returns all matching editions, unlike /get which may select
            // a plain-only edition despite an equivalent synced record existing.
            foreach (var query in LyricsSearch.GetQueries(track))
            {
                var payload = await GetPayloadAsync(query, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    continue;
                }
                var candidates = JsonSerializer.Deserialize<LyricsRecord[]>(payload, JsonOptions)
                    ?? throw new InvalidDataException("Empty LRCLIB search response.");
                if (LyricsSearch.Select(track, candidates) is { } match)
                {
                    var found = ParseDocument(match);
                    if (found.Kind == LyricsKind.Synced || document.Kind == LyricsKind.NotFound)
                    {
                        document = found;
                    }
                    if (document.Kind is LyricsKind.Synced or LyricsKind.Instrumental)
                    {
                        break;
                    }
                }
            }
            if (_cache.Count >= 50 && !_cache.ContainsKey(track))
            {
                _cache.Remove(_cache.MinBy(pair => pair.Value.Expires).Key);
            }
            _cache[track] = (document, clock.GetUtcNow() + (document.Kind == LyricsKind.NotFound ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24)));
            return document;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Each endpoint gets at most three attempts. Delays also apply to subsequent
    // searches/refreshes, so canceling a lookup cannot bypass Retry-After.
    private async Task<byte[]?> GetPayloadAsync(string uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var wait = _nextRequest - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            _nextRequest = clock.GetUtcNow().AddMilliseconds(500);
            var serverError = (int)response.StatusCode is >= 500 and <= 599;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || serverError)
            {
                var minimum = TimeSpan.FromSeconds(serverError ? Math.Pow(2, attempt) : 30);
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date - clock.GetUtcNow()) ?? minimum;
                delay = delay < minimum ? minimum : delay;
                _nextRequest = clock.GetUtcNow() + delay + TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Random.Shared.NextDouble() * 0.3);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new LyricsRateLimitException();
                }
                if (attempt < 2)
                {
                    // Dispose the response before waiting for the next attempt.
                    continue;
                }
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            return await ReadResponseAsync(response, timeout.Token).ConfigureAwait(false);
        }
    }

    private static LyricsDocument ParseDocument(LyricsRecord record)
    {
        var document = LyricsDocument.Parse(record.SyncedLyrics, record.PlainLyrics, record.Instrumental);
        if (document.Cues.Count > 2000 || document.PlainText.Split('\n').Length > 2000)
        {
            throw new InvalidDataException("Lyrics exceed the display limit.");
        }
        return document;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int limit = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException("LRCLIB response is too large.");
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > limit)
            {
                throw new InvalidDataException("LRCLIB response is too large.");
            }
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    public void Dispose() => http.Dispose();
}
