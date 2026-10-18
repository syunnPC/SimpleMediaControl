using System;
using PlayerApp;

var checks = 0;
var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
var timeline = new PlaybackTimeline();

void Equal(double seconds, DateTimeOffset at, string name)
{
    var actual = timeline.GetPosition(at).TotalSeconds;
    if (Math.Abs(seconds - actual) > 0.000001)
    {
        throw new InvalidOperationException($"{name}: expected {seconds}, got {actual}");
    }
    checks++;
}

Equal(0, start, "empty session");
timeline.SetPlayback(true, 1, start);
timeline.SetTimeline(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(180), start);
Equal(12, start.AddSeconds(2), "playing without native polling");
Equal(10, start.AddSeconds(-2), "future native timestamp");
Equal(180, start.AddDays(1), "duration limit");

timeline.SetPlayback(false, 1, start.AddSeconds(3));
Equal(13, start.AddSeconds(40), "pause freezes extrapolated position");
timeline.SetPlayback(true, 1, start.AddSeconds(50));
Equal(15, start.AddSeconds(52), "resume excludes paused interval");
timeline.SetPlayback(true, 2, start.AddSeconds(52));
Equal(21, start.AddSeconds(55), "rate change preserves position");
timeline.SetPlayback(true, 0, start.AddSeconds(55));
Equal(21, start.AddSeconds(70), "zero playback rate");
timeline.SetPlayback(true, -2, start.AddSeconds(70));
Equal(17, start.AddSeconds(72), "reverse playback");
Equal(0, start.AddSeconds(100), "reverse lower bound");

timeline.SetTimeline(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180), start.AddSeconds(101));
Equal(88, start.AddSeconds(102), "seek replaces cached snapshot");
timeline.SetPlayback(true, null, start.AddSeconds(102));
Equal(89, start.AddSeconds(103), "missing rate uses normal speed");
timeline.SetPlayback(true, double.NaN, start.AddSeconds(103));
Equal(90, start.AddSeconds(104), "invalid rate");
timeline.SetPlayback(true, double.PositiveInfinity, start.AddSeconds(104));
Equal(91, start.AddSeconds(105), "infinite rate");
timeline.SetPlayback(true, double.MaxValue, start.AddSeconds(105));
Equal(180, start.AddSeconds(106), "extreme rate cannot overflow");

timeline.SetTimeline(TimeSpan.FromSeconds(-10), TimeSpan.FromSeconds(180), start);
Equal(0, start, "negative native position");
timeline.SetTimeline(TimeSpan.MaxValue, TimeSpan.FromSeconds(180), start);
Equal(180, start, "native position beyond end");
timeline.SetTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(-1), start);
Equal(0, start, "unknown duration");
if (timeline.HasTimeline)
{
    throw new InvalidOperationException("Unknown duration must clear the display.");
}

timeline.Clear();
timeline.SetTimeline(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), start);
Equal(30, start.AddSeconds(3), "session switch resets playback state and rate");
timeline.SetPlayback(true, 1, start.AddSeconds(3));
timeline.SetTimeline(TimeSpan.Zero, TimeSpan.Zero, default);
timeline.SetTimeline(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90), start.AddSeconds(4));
Equal(21, start.AddSeconds(5), "timeline recovery preserves playback state");

// A full day's display ticks must work without any native object or new snapshot.
timeline.Clear();
timeline.SetPlayback(true, 1, start);
timeline.SetTimeline(TimeSpan.Zero, TimeSpan.FromDays(2), start);
for (var tick = 0; tick <= 172800; tick++)
{
    Equal(tick * 0.5, start.AddSeconds(tick * 0.5), "long running render timer");
}

Console.WriteLine($"PASS: {checks} playback timeline checks");
if (Array.IndexOf(args, "--stress") >= 0)
{
    await MediaStress.RunAsync();
}
var workerOption = Array.IndexOf(args, "--worker");
if (workerOption >= 0)
{
    await ClientChecks.RunAsync(args[workerOption + 1]);
}
var inspectOption = Array.IndexOf(args, "--inspect");
if (inspectOption >= 0)
{
    var gate = new object();
    using var client = new MediaClient(System.IO.Path.GetFullPath(args[inspectOption + 1]), action => { lock (gate) { action(); } }, Console.WriteLine);
    client.Start();
    var inspectDeadline = System.Diagnostics.Stopwatch.StartNew();
    while (true)
    {
        lock (gate)
        {
            if (client.IsConnected && System.Linq.Enumerable.Any(client.GetSessions(), session => session.Media.Title.Length > 0 && session.Timeline is not null))
            {
                break;
            }
        }
        if (inspectDeadline.Elapsed > TimeSpan.FromSeconds(25))
        {
            throw new InvalidOperationException("No media snapshot received before the inspection deadline.");
        }
        await System.Threading.Tasks.Task.Delay(50);
    }
    lock (gate)
    {
        foreach (var session in client.GetSessions())
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                session.SourceAppUserModelId,
                Status = session.Playback.PlaybackStatus.ToString(),
                session.Media.Title,
                session.Media.Artist,
                session.Media.AlbumArtist,
                session.Media.AlbumTitle,
                Duration = session.Timeline?.EndTime.TotalSeconds,
                Position = session.Timeline?.Position.TotalSeconds
            }));
        }
    }
}
