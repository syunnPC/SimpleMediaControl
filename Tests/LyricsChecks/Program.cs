using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PlayerApp;

var checks = 0;
void Check(bool result, string message)
{
    if (!result)
    {
        throw new InvalidOperationException(message);
    }
    checks++;
}

// Only synthetic text is checked into the repository.
var document = LyricsDocument.Parse("[ar:Fixture]\n[00:03.50]Second\n[00:01.2][00:05.123]First\n[00:03.500]Translation\n[00:04.00]\n[99:99]Invalid", null, false);
Check(document.Kind == LyricsKind.Synced && document.Cues.Count == 4, "LRC sorting/multiple tags/grouping");
Check(document.Cues[0].Time == TimeSpan.FromMilliseconds(1200), "tenths precision");
Check(document.Cues[1].Text == "Second\nTranslation", "simultaneous lyrics grouped");
Check(document.Cues[3].Time == TimeSpan.FromMilliseconds(5123), "milliseconds precision");
Check(document.GetActiveIndex(TimeSpan.Zero) == -1, "before first cue");
Check(document.GetActiveIndex(TimeSpan.FromMilliseconds(1200)) == 0, "exact boundary");
Check(document.GetActiveIndex(TimeSpan.FromMilliseconds(3499)) == 0, "immediately before boundary");
Check(document.GetActiveIndex(TimeSpan.FromMilliseconds(3500)) == 1, "next cue");
Check(document.Cues[document.GetActiveIndex(TimeSpan.FromSeconds(4.5))].Text == "", "instrumental gap clears highlighting");
Check(document.GetActiveIndex(TimeSpan.FromSeconds(6)) == 3, "forward seek");
Check(document.GetActiveIndex(TimeSpan.FromSeconds(2)) == 0, "backward seek");
var follow = new LyricsFollowState();
Check(follow.GetIndex(document, TimeSpan.FromMilliseconds(2500), true, false) == 1, "follow advances one second before source cue");
Check(follow.GetIndex(document, TimeSpan.FromMilliseconds(2400), true, false) == 1, "minor native correction cannot flip previous cue");
Check(follow.GetIndex(document, TimeSpan.FromMilliseconds(2499), true, false) == 1, "repeated boundary jitter remains stable");
Check(follow.GetIndex(document, TimeSpan.FromMilliseconds(2400), true, true) == 0, "explicit short backward seek applies immediately");
follow.GetIndex(document, TimeSpan.FromSeconds(5), true, false);
Check(follow.GetIndex(document, TimeSpan.FromSeconds(2), true, false) == 0, "large external backward seek applies immediately");
follow.GetIndex(document, TimeSpan.FromMilliseconds(2500), true, false);
Check(follow.GetIndex(document, TimeSpan.FromMilliseconds(2400), false, false) == 0, "paused/reverse position is not smoothed");
follow.Reset();
Check(follow.GetIndex(document, TimeSpan.Zero, true, false) == -1, "follow resets with track/timeline");
Check(document.Cues[1].Time == TimeSpan.FromSeconds(3.5) && document.Cues[1].HighlightTime == TimeSpan.FromSeconds(2.5), "source timestamps preserved; display lead is one second");
Check(document.GetHighlightIndex(TimeSpan.FromMilliseconds(2499)) == 0 && document.GetHighlightIndex(TimeSpan.FromMilliseconds(2500)) == 1, "exact advanced highlight boundary");
var early = LyricsDocument.Parse("[00:00.00]Zero\n[00:00.70]Early\n[00:02.00]Next", null, false);
Check(early.Cues[0].HighlightTime == TimeSpan.Zero && early.Cues[1].HighlightTime == TimeSpan.Zero, "zero/sub-second timestamps clamp to zero");
Check(early.GetHighlightIndex(TimeSpan.FromTicks(-1)) == -1 && early.GetHighlightIndex(TimeSpan.Zero) == 1, "clamped cues do not start before zero; latest source cue wins a tie");
Check(early.GetHighlightIndex(TimeSpan.FromMilliseconds(999)) == 1 && early.GetHighlightIndex(TimeSpan.FromSeconds(1)) == 2, "next cue starts exactly one second early");
Check(document.GetHighlightIndex(TimeSpan.MaxValue) == document.Cues.Count - 1, "large positions do not overflow lookahead");
Check(document.GetSeekPosition(1, TimeSpan.FromSeconds(10)) == TimeSpan.FromSeconds(2.5), "lyric click seeks one second before source timestamp");
Check(early.GetSeekPosition(0, TimeSpan.FromSeconds(10)) == TimeSpan.Zero && early.GetSeekPosition(1, TimeSpan.FromSeconds(10)) == TimeSpan.Zero, "seek targets clamp to zero");
Check(document.GetSeekPosition(-1, TimeSpan.FromSeconds(10)) is null && document.GetSeekPosition(99, TimeSpan.FromSeconds(10)) is null, "invalid row cannot seek");
Check(document.GetSeekPosition(1, TimeSpan.Zero) is null && document.GetSeekPosition(1, TimeSpan.FromSeconds(2)) is null, "missing/out-of-range duration cannot seek");
Check(LyricsDocument.Parse(null, "Plain fixture", false).GetSeekPosition(0, TimeSpan.FromSeconds(10)) is null, "plain lyrics cannot seek");
var intro = LyricsDocument.Parse("[00:07]First", null, false).ForDisplay();
Check(intro.Cues.Count == 2 && intro.Cues[0].Text == "" && intro.Cues[1].Time == TimeSpan.FromSeconds(7), "display adds intro gap without changing source timestamps");
Check(ReferenceEquals(early, early.ForDisplay()), "existing zero-time cue does not get duplicate intro");
Check(intro.GetGap(0, TimeSpan.Zero, TimeSpan.FromSeconds(20)) is { Phase: 0 } && intro.GetSeekPosition(0, TimeSpan.FromSeconds(20)) == TimeSpan.Zero, "intro gap is active and seekable at zero");
var gaps = LyricsDocument.Parse("[00:00]First\n[00:04]\n[00:10]Next\n[00:13]", null, false);
Check(gaps.GetGap(1, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(18)) is { Start.TotalSeconds: 3, End.TotalSeconds: 9, Phase: 0 }, "gap uses advanced highlight interval");
Check(gaps.GetGap(1, TimeSpan.FromTicks(TimeSpan.FromSeconds(5).Ticks - 1), TimeSpan.FromSeconds(18))?.Phase == 0
    && gaps.GetGap(1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(18))?.Phase == 1, "second dot starts at one third exactly");
Check(gaps.GetGap(1, TimeSpan.FromTicks(TimeSpan.FromSeconds(7).Ticks - 1), TimeSpan.FromSeconds(18))?.Phase == 1
    && gaps.GetGap(1, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(18))?.Phase == 2, "third dot starts at two thirds exactly");
Check(gaps.GetGap(1, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(18)) is null && gaps.GetGap(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(18)) is null, "gap ends at next lyric; ordinary lyric is not inferred as gap");
Check(gaps.GetGap(3, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(18)) is { End.TotalSeconds: 18, Phase: 2 }, "last empty cue uses track end for remaining duration");
Check(gaps.GetGap(1, TimeSpan.FromSeconds(3), TimeSpan.Zero) is null, "unknown duration cannot yield gap progress");
Check(gaps.GetGap(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(18)) is null && gaps.GetGap(99, TimeSpan.Zero, TimeSpan.FromSeconds(18)) is null, "out-of-range gap positions rejected");
Check(gaps.GetGap(1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(18))?.Phase == 1
    && gaps.GetGap(1, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(18))?.Phase == 0, "gap phases follow backwards seeks");
Check(gaps.GetSeekPosition(1, TimeSpan.FromSeconds(18)) == TimeSpan.FromSeconds(3), "ellipsis click uses same one-second lead");
Check(LyricsFollowState.GetScrollTarget(90, 20, 200, 1000, 0) is null, "central cue needs no scrolling");
Check(LyricsFollowState.GetScrollTarget(190, 20, 200, 1000, 0) == 100, "off-center cue is centered once");
Check(LyricsFollowState.GetScrollTarget(190, 20, 200, 1000, 100) is null, "settled scroll does not bounce");
Check(LyricsFollowState.GetScrollTarget(1, 20, 200, 1000, 0) is null, "top clamp does not cause repeated scrolling");
Check(LyricsFollowState.GetScrollTarget(1100, 20, 200, 1000, 500) == 1000, "bottom target clamped");
Check(LyricsFollowState.GetScrollTarget(100, 20, 0, 1000, 0) is null, "unmeasured viewport does not scroll");
var offset = LyricsDocument.Parse("[00:00.20]Early\n[00:02.00]Later\n[offset:+500]", null, false);
Check(offset.Cues[0].Time == TimeSpan.FromMilliseconds(-300) && offset.GetActiveIndex(TimeSpan.Zero) == 0, "positive offset advances lyrics");
Check(LyricsDocument.Parse("[offset:-500]\n[00:01]Delayed", null, false).Cues[0].Time == TimeSpan.FromMilliseconds(1500), "negative offset delays lyrics");
Check(LyricsDocument.Parse("[offset:-500]\n[00:02]Delayed", null, false).Cues[0].HighlightTime == TimeSpan.FromMilliseconds(1500), "LRC offset applied before highlight lead");
Check(LyricsDocument.Parse("[00:01.00]<00:01.00>Word <00:01.20>timing", null, false).Cues[0].Text == "Word timing", "enhanced timestamps are not displayed");
Check(LyricsDocument.Parse("[00:60]Bad", "Plain fixture\n\nNext paragraph", false).Kind == LyricsKind.Plain, "plain text without valid time cues");
Check(LyricsDocument.Parse(null, null, true).Kind == LyricsKind.Instrumental, "instrumental response");
Check(LyricsDocument.Parse(null, null, false).Kind == LyricsKind.NotFound, "missing lyrics");
Check(LyricsDocument.Parse("[offset:bad]\n[00:01]Text", null, false).Cues.Count == 1, "invalid metadata ignored");
Check(LyricsDocument.Parse("[100:00.01]Long track", null, false).Cues[0].Time == TimeSpan.FromMilliseconds(6000010), "minutes beyond one hour");
var track = LyricsTrack.Create(new(" 曲 & +? ", " Artist/歌手 ", "", " Album #1 ", null), TimeSpan.FromSeconds(200.2))!;
Check(track.IsSameSong(track with { DurationSeconds = 203, Album = "Changed album" }), "duration and album updates preserve playing-song identity");
Check(new LyricsTrack("Title!", "Artist", "", null).IsSameSong(new("ＴＩＴＬＥ！", "ＡＲＴＩＳＴ — Album", "", 240)), "identity ignores case, width, and embedded album");
Check(!track.IsSameSong(track with { Title = "Next song" }) && !track.IsSameSong(track with { Artist = "Other artist" }) && !track.IsSameSong(null), "real song change invalidates lyrics");
Check(track.DurationSeconds == 200 && track.Title == "曲 & +?", "metadata normalization and duration");
var uri = LyricsSearch.BuildUri(track.Title, track.Artist);
Check(uri.Contains("track_name=%E6%9B%B2%20%26%20%2B%3F") && !uri.Contains("album_name") && uri.Contains("artist_name=Artist%2F"), "title query escaping; album is never a constraint");
Check(LyricsTrack.Create(new("Title", "", "Album artist", "", null), TimeSpan.FromHours(2)) is { Artist: "Album artist", DurationSeconds: null }, "album artist and unknown/long duration");
Check(LyricsTrack.Create(new("Title", "", "", "", null), TimeSpan.Zero) is null, "missing artist prevents unrelated lookup");
Check(new LyricsTrack(" Ｔｉｔｌｅ！？ ", " Ａｒｔｉｓｔ ", " Ａｌｂｕｍ　 ", 200).Canonicalize()
    == new LyricsTrack("Title!?", "Artist", "Album", 200), "NFKC metadata canonicalization");
Check(LyricsTrack.Create(new("My Telepathy！私の熱い光線", "櫻井優衣", "", "", null), TimeSpan.FromSeconds(222))?.Title
    == "My Telepathy!私の熱い光線", "source metadata canonicalized before track-change comparison");
Check(new LyricsTrack("Galaxy Anthem", "ディーヴァ(Vo.八木海莉) — Galaxy Anthem - Single", "", 273).Canonicalize().Artist
    == "ディーヴァ(Vo.八木海莉)", "embedded album removed from source artist field");

StringContent Response(LyricsTrack source, string? synced, string? plain = null) => new(System.Text.Json.JsonSerializer.Serialize(
    new[] { new LyricsRecord(source.Title, source.Artist, source.Album, source.DurationSeconds ?? 0, plain, synced, false) }), Encoding.UTF8, "application/json");

var clock = new TestClock();
var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = Response(track, "[00:01.00]Synthetic line", "Synthetic line")
}));
using (var client = new LyricsClient(new HttpClient(handler) { BaseAddress = new("https://lrclib.net/") }, clock))
{
    Check((await client.GetAsync(track, false, default)).Kind == LyricsKind.Synced, "HTTP synced response");
    await client.GetAsync(track, false, default);
    Check(handler.Calls == 1, "reopen/repeat uses cache");
    clock.Advance(TimeSpan.FromSeconds(1));
    await client.GetAsync(track, true, default);
    Check(handler.Calls == 2, "explicit refresh bypasses cache");
}

var widthStub = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = Response(new("Title!?", "Artist", "Album", 200), "[00:01]Width fixture")
}));
using (var client = new LyricsClient(new HttpClient(widthStub) { BaseAddress = new("https://lrclib.net/") }, new TestClock()))
{
    await client.GetAsync(new("Ｔｉｔｌｅ！？", "Ａｒｔｉｓｔ", "Ａｌｂｕｍ", 200), false, default);
    await client.GetAsync(new("Title!?", "Artist", "Album", 200), false, default);
    Check(widthStub.Calls == 1, "full/half-width metadata shares cache");
}

foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.OK })
{
    var stub = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(status)
    {
        Content = Response(track, null, "Plain fixture")
    }));
    using var client = new LyricsClient(new HttpClient(stub) { BaseAddress = new("https://lrclib.net/") }, new TestClock());
    var result = await client.GetAsync(track, false, default);
    Check(result.Kind == (status == HttpStatusCode.NotFound ? LyricsKind.NotFound : LyricsKind.Plain), "plain and 404 response");
    var calls = stub.Calls;
    await client.GetAsync(track, false, default);
    Check(stub.Calls == calls, "negative and positive cache include completed partial searches");
    Check(calls is >= 1 and <= 4, "partial search bounded to four queries");
}

var searchTrack = new LyricsTrack("Synthetic Sunrise (Official Audio) - Bonus", "Fixture Artist feat. Guest", "Album", 200);
var queries = LyricsSearch.GetQueries(searchTrack);
Check(queries.Count <= 4 && queries.Distinct().Count() == queries.Count, "bounded deduplicated queries");
Check(queries[1].Contains("track_name=Synthetic%20Sunrise&artist_name=Fixture%20Artist"), "suffix and featuring stripped after full search");
Check(queries.Any(q => q.Contains("track_name=Synthetic&")), "word prefix query");
Check(LyricsSearch.GetQueries(new("テストのうた", "歌手", "", 200)).Any(q => q.Contains(Uri.EscapeDataString("テスト"))), "Japanese prefix query");
Check(LyricsSearch.GetQueries(new("Hi", "A", "", null)).Count == 1, "short titles are not shortened");

LyricsRecord Candidate(string title = "Synthetic Sunrise", string artist = "Fixture Artist", double duration = 200,
    string album = "Album", bool synced = true) => new(title, artist, album, duration, "Fixture text", synced ? "[00:01]Fixture text" : null, false);
var correct = Candidate();
Check(LyricsSearch.Select(searchTrack, [Candidate(artist: "Different Artist"), correct]) == correct, "select matching artist, not first result");
Check(LyricsSearch.Select(searchTrack, [Candidate(duration: 215)]) is null, "wrong duration rejected");
Check(LyricsSearch.Select(searchTrack, [Candidate() with { Duration = null }, correct]) == correct, "unknown candidate duration skipped without discarding other results");
Check(LyricsSearch.Select(searchTrack, [Candidate(title: "Synthetic Sunrise Live")]) is null, "different recording version rejected");
Check(LyricsSearch.Select(searchTrack, [Candidate(title: "Synthetic Sunset")]) is null, "shared prefix does not qualify unrelated title");
Check(LyricsSearch.Select(searchTrack, [Candidate(title: "Synthetic Sunrize")]) is not null, "minor title spelling differences accepted");
Check(LyricsSearch.Select(searchTrack, [Candidate(artist: "Fixture Artst")]) is not null, "minor artist spelling differences accepted");
Check(LyricsSearch.Select(new("Ｃａｆé Sunrise", "Fixture Artist", "", 200), [Candidate(title: "Cafe Sunrise")]) is not null, "width and accent normalization");
Check(LyricsSearch.Select(new("曲名！？", "ｶｼｭ", "", 200), [Candidate(title: "曲名!?", artist: "カシュ")]) is not null, "punctuation and half-width kana equivalence");
Check(LyricsSearch.Select(new("My Telepathy!私の熱い光線", "櫻井優衣", "", 222),
    [Candidate(title: "My Telepathy！私の熱い光線", artist: "櫻井優衣", duration: 223)]) is not null, "user reference punctuation variant");
Check(LyricsSearch.Select(new("Song（Ｌｉｖｅ）", "Fixture Artist", "", 200), [Candidate(title: "Song (Live)")]) is not null, "version qualifiers normalize width too");
Check(LyricsSearch.Select(searchTrack with { DurationSeconds = null }, [Candidate(title: "Synthetic Sunrize")]) is null, "unknown duration needs canonical title match");
Check(LyricsSearch.Select(searchTrack, [correct, Candidate(title: "Synthetic Sunrise!", album: "Other", duration: 202)]) == correct, "closest duration wins regardless of album");
Check(LyricsSearch.Select(new("Synthetic Sunrize", "Fixture Artist", "", 200), [Candidate(), Candidate(title: "Synthetic Sunrige")]) is null, "ambiguous competing recordings rejected");
Check(LyricsSearch.Select(new("Galaxy Anthem", "ディーヴァ(Vo.八木海莉)", "", 273),
    [Candidate(title: "Galaxy Anthem", artist: "ディーヴァ（Vo.八木海莉）", duration: 274, synced: false),
     Candidate(title: "Galaxy Anthem", artist: "ディーヴァ(Vo.八木海莉)", duration: 272)])?.SyncedLyrics is not null,
    "rounded duration variants do not mask the synced edition as ambiguous");

var searchClock = new AutoAdvanceClock();
var lookupUris = new List<string>();
var searchStub = new StubHandler((request, _) =>
{
    var query = request.RequestUri!.PathAndQuery;
    lookupUris.Add(query);
    var match = query.StartsWith("/api/search?track_name=Synthetic%20Sunrise&", StringComparison.Ordinal);
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(match
            ? """[{"trackName":"Synthetic Sunrise","artistName":"Fixture Artist","albumName":"Album","duration":200,"syncedLyrics":"[00:01]Fixture text"}]""" : "[]")
    });
});
using (var client = new LyricsClient(new HttpClient(searchStub) { BaseAddress = new("https://lrclib.net/") }, searchClock))
{
    Check((await client.GetAsync(searchTrack, false, default)).Kind == LyricsKind.Synced, "empty full-name search progresses to stripped search");
    Check(searchStub.Calls == 2, "stop searching immediately on confident match");
    await client.GetAsync(searchTrack, false, default);
    Check(searchStub.Calls == 2, "partial match cached under original metadata");
    Check(searchClock.Delays.All(d => d >= TimeSpan.FromMilliseconds(500)), "searches keep request spacing");
}

foreach (var status in new[] { 500, 502, 503, 504, 599 })
{
    var retryClock = new AutoAdvanceClock();
    var attempts = 0;
    var stub = new StubHandler((_, _) => Task.FromResult(++attempts < 3 ? new HttpResponseMessage((HttpStatusCode)status)
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = Response(track, "[00:01]Retry fixture") }));
    using var client = new LyricsClient(new HttpClient(stub) { BaseAddress = new("https://lrclib.net/") }, retryClock);
    Check((await client.GetAsync(track, false, default)).Kind == LyricsKind.Synced && attempts == 3, $"{status} automatically retried");
    Check(retryClock.Delays.Count == 2 && retryClock.Delays[0] >= TimeSpan.FromSeconds(1)
        && retryClock.Delays[1] >= TimeSpan.FromSeconds(2), "exponential retry delay");
}

var upgradeStub = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("""[{"trackName":"Galaxy Anthem","artistName":"ディーヴァ(Vo.八木海莉)","duration":272,"plainLyrics":"Plain edition"},{"trackName":"Galaxy Anthem","artistName":"ディーヴァ(Vo.八木海莉)","duration":216,"syncedLyrics":"[00:01]Short edition"},{"trackName":"Galaxy Anthem","artistName":"ディーヴァ（Vo.八木海莉）","duration":272,"syncedLyrics":"[00:01]Full edition"}]""")
}));
using (var client = new LyricsClient(new HttpClient(upgradeStub) { BaseAddress = new("https://lrclib.net/") }, new AutoAdvanceClock()))
{
    var result = await client.GetAsync(new("Galaxy Anthem", "ディーヴァ(Vo.八木海莉)", "Unrelated album", 272), false, default);
    Check(result.Kind == LyricsKind.Synced && result.Cues[0].Text == "Full edition", "plain-only exact match upgraded to matching synced edition");
    Check(upgradeStub.Calls == 1, "wrong-duration synced record skipped; search stops at correct synced match");
}

foreach (var dateHeader in new[] { false, true })
{
    var retryClock = new AutoAdvanceClock();
    var attempts = 0;
    var stub = new StubHandler((_, _) =>
    {
        var response = new HttpResponseMessage(++attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
        {
            Content = Response(track, "[00:01]Retry fixture")
        };
        response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(retryClock.GetUtcNow().AddSeconds(10)) : new(TimeSpan.FromSeconds(10));
        return Task.FromResult(response);
    });
    using var client = new LyricsClient(new HttpClient(stub) { BaseAddress = new("https://lrclib.net/") }, retryClock);
    await client.GetAsync(track, false, default);
    Check(attempts == 2 && retryClock.Delays.Single() >= TimeSpan.FromSeconds(10), "5xx honors Retry-After delta/date");
}

var failingClock = new AutoAdvanceClock();
var failingStub = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
using (var client = new LyricsClient(new HttpClient(failingStub) { BaseAddress = new("https://lrclib.net/") }, failingClock))
{
    for (var lookup = 0; lookup < 2; lookup++)
    {
        try { await client.GetAsync(track, false, default); throw new InvalidOperationException("Expected exhausted retries."); }
        catch (HttpRequestException ex) { Check(ex.StatusCode == HttpStatusCode.InternalServerError, "original 5xx preserved after retry exhaustion"); }
        Check(failingStub.Calls == 3 * (lookup + 1), "three attempts only; failures not cached or treated as missing lyrics");
    }
}

using (var cancel = new CancellationTokenSource())
{
    var stub = new StubHandler((_, _) =>
    {
        cancel.Cancel();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    });
    using var client = new LyricsClient(new HttpClient(stub) { BaseAddress = new("https://lrclib.net/") }, new TestClock());
    try { await client.GetAsync(track, false, cancel.Token); throw new InvalidOperationException("Expected retry cancellation."); }
    catch (OperationCanceledException) { Check(stub.Calls == 1, "cancellation prevents scheduled retry"); }
}

var forbiddenStub = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
using (var client = new LyricsClient(new HttpClient(forbiddenStub) { BaseAddress = new("https://lrclib.net/") }, new TestClock()))
{
    try { await client.GetAsync(track, false, default); throw new InvalidOperationException("Expected forbidden response."); }
    catch (HttpRequestException) { Check(forbiddenStub.Calls == 1, "non-404 4xx neither retried nor searched"); }
}

foreach (var dateHeader in new[] { false, true })
{
    var rateClock = new TestClock();
    var stub = new StubHandler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(rateClock.GetUtcNow().AddSeconds(5)) : new(TimeSpan.FromSeconds(5));
        return Task.FromResult(response);
    });
    using var client = new LyricsClient(new HttpClient(stub) { BaseAddress = new("https://lrclib.net/") }, rateClock);
    try
    {
        await client.GetAsync(track, false, default);
        throw new InvalidOperationException("Expected rate limit response.");
    }
    catch (LyricsRateLimitException)
    {
        checks++;
    }
    rateClock.Advance(TimeSpan.FromSeconds(2));
    using var cancel = new CancellationTokenSource(30);
    try
    {
        await client.GetAsync(track, true, cancel.Token);
        throw new InvalidOperationException("Expected cancellation while cooling down.");
    }
    catch (OperationCanceledException)
    {
        Check(stub.Calls == 1, "Retry-After respected across explicit refresh");
    }
}

var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var blocked = new StubHandler(async (_, token) =>
{
    entered.TrySetResult();
    await Task.Delay(Timeout.Infinite, token);
    return new HttpResponseMessage(HttpStatusCode.OK);
});
using (var client = new LyricsClient(new HttpClient(blocked) { BaseAddress = new("https://lrclib.net/") }, new TestClock()))
using (var first = new CancellationTokenSource())
using (var second = new CancellationTokenSource())
{
    var request = client.GetAsync(track, false, first.Token);
    await entered.Task;
    var queued = client.GetAsync(track with { Title = "Other track" }, false, second.Token);
    second.Cancel();
    try { await queued; } catch (OperationCanceledException) { checks++; }
    Check(blocked.Calls == 1, "requests serialized and obsolete queued lookup canceled");
    first.Cancel();
    try { await request; } catch (OperationCanceledException) { checks++; }
}

var oversized = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) }));
using (var client = new LyricsClient(new HttpClient(oversized) { BaseAddress = new("https://lrclib.net/") }, new TestClock()))
{
    try { await client.GetAsync(track, false, default); throw new InvalidOperationException("Oversized response accepted."); }
    catch (InvalidDataException) { checks++; }
}

if (args.Contains("--live-galaxy"))
{
    var http = new HttpClient(new LiveHandler()) { BaseAddress = new("https://lrclib.net/") };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("PlayerApp/1.0 (+https://github.com/syunnPC/SimpleMediaControl)");
    using var client = new LyricsClient(http, TimeProvider.System);
    var result = await client.GetAsync(new("Galaxy Anthem", "ディーヴァ(Vo.八木海莉) — Galaxy Anthem - Single", "", 273), false, default);
    Console.WriteLine($"Live Galaxy Anthem: {result.Kind}, {result.Cues.Count} cues");
    Check(result.Kind == LyricsKind.Synced && result.Cues.Count > 0, "live Galaxy Anthem reference");
}
else if (args.Contains("--live"))
{
    using var client = LyricsClient.Create();
    // The documented API example. Print only status/counts, never real lyric text.
    var result = await client.GetAsync(new("I Want to Live", "Borislav Slavov", "Baldur's Gate 3 (Original Game Soundtrack)", 233), false, default);
    Check(result.Kind == LyricsKind.Synced && result.Cues.Count > 0, "live LRCLIB contract");
    Console.WriteLine($"Live LRCLIB: {result.Kind}, {result.Cues.Count} cues");
    var current = await client.GetAsync(new("My Telepathy!私の熱い光線", "櫻井優衣", "My Telepathy!私の熱い光線 - Single", 222), false, default);
    Check(current.Kind == LyricsKind.Synced && current.Cues.Count > 0, "live user reference with punctuation and album differences");
    Console.WriteLine($"Live reference 38517550: {current.Kind}, {current.Cues.Count} cues");
    var galaxy = await client.GetAsync(new("Galaxy Anthem", "ディーヴァ(Vo.八木海莉) — Galaxy Anthem - Single", "", 273), false, default);
    Console.WriteLine($"Live Galaxy Anthem: {galaxy.Kind}, {galaxy.Cues.Count} cues");
    Check(galaxy.Kind == LyricsKind.Synced && galaxy.Cues.Count > 0, "live Galaxy Anthem reference");
    var spotify = await client.GetAsync(new("あの夢をなぞって", "YOASOBI", "THE BOOK", 241), false, default);
    Check(spotify.Kind == LyricsKind.Synced && spotify.Cues.Count > 0, "live Spotify metadata reference");
    Console.WriteLine($"Live Spotify reference: {spotify.Kind}, {spotify.Cues.Count} cues");
}
Console.WriteLine($"PASS: {checks} lyrics checks");

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return respond(request, cancellationToken);
    }
}

class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}

sealed class AutoAdvanceClock : TestClock
{
    public List<TimeSpan> Delays { get; } = [];
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException("Only one-shot delays are expected.");
        }
        Delays.Add(dueTime);
        Advance(dueTime);
        return new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
}

// Optional live diagnostics include request/status and metadata only, never lyric text.
sealed class LiveHandler() : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        Console.WriteLine($"LRCLIB {(int)response.StatusCode} {request.RequestUri!.PathAndQuery}");
        if (response.IsSuccessStatusCode)
        {
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var records = json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? json.RootElement.EnumerateArray().ToArray() : [json.RootElement];
            foreach (var record in records)
            {
                Console.WriteLine($"Candidate {record.GetProperty("id")}: {record.GetProperty("trackName")}; {record.GetProperty("artistName")}; {record.GetProperty("duration")}s; synced={!string.IsNullOrWhiteSpace(record.GetProperty("syncedLyrics").GetString())}");
            }
        }
        return response;
    }
}
