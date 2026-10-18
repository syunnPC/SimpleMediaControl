using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PlayerApp;
using Windows.Media;
using Windows.Media.Control;
using Windows.Media.Playback;

internal static class MediaStress
{
    public static async Task RunAsync()
    {
        // Publish an independent, silent session; never control another application's media.
        using var player = new MediaPlayer();
        player.CommandManager.IsEnabled = false;
        var controls = player.SystemMediaTransportControls;
        controls.ButtonPressed += (_, _) => { };
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.PlaybackStatus = MediaPlaybackStatus.Playing;
        controls.DisplayUpdater.Type = MediaPlaybackType.Music;
        controls.DisplayUpdater.MusicProperties.Title = "PlayerApp lifetime test";
        controls.DisplayUpdater.Update();

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
        GlobalSystemMediaTransportControlsSession? session = null;
        for (var attempt = 0; attempt < 100 && session is null; attempt++)
        {
            session = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains(processName, StringComparison.OrdinalIgnoreCase));
            await Task.Delay(50);
        }
        if (session is null)
        {
            throw new InvalidOperationException("The test media session was not registered.");
        }

        var queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        var timeline = new PlaybackTimeline();
        var snapshots = 0;
        var playbackChanges = 0;
        var frames = 0;
        using var duration = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        void ReadTimeline()
        {
            var native = session.GetTimelineProperties();
            if (native is not null)
            {
                timeline.SetTimeline(native.Position, native.EndTime, native.LastUpdatedTime);
                snapshots++;
            }
        }
        void ReadPlayback()
        {
            var native = session.GetPlaybackInfo();
            var playing = native.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            timeline.SetPlayback(playing, native.PlaybackRate, DateTimeOffset.Now);
            playbackChanges++;
        }
        void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            queue.Writer.TryWrite(ReadTimeline);
        }
        void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            queue.Writer.TryWrite(ReadPlayback);
        }

        session.TimelinePropertiesChanged += OnTimelineChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        queue.Writer.TryWrite(ReadPlayback);
        queue.Writer.TryWrite(ReadTimeline);

        var consumer = Task.Run(async () =>
        {
            await foreach (var action in queue.Reader.ReadAllAsync())
            {
                action();
            }
        });
        var render = Task.Run(async () =>
        {
            while (!duration.IsCancellationRequested)
            {
                queue.Writer.TryWrite(() =>
                {
                    var position = timeline.GetPosition(DateTimeOffset.Now);
                    if (position < TimeSpan.Zero || position > timeline.EndTime)
                    {
                        throw new InvalidOperationException("Extrapolated position is outside the timeline.");
                    }
                    frames++;
                });
                await Task.Delay(1);
            }
        });
        try
        {
            for (var update = 0; !duration.IsCancellationRequested; update++)
            {
                controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = TimeSpan.FromMinutes(4),
                    MinSeekTime = TimeSpan.Zero,
                    MaxSeekTime = TimeSpan.FromMinutes(4),
                    Position = TimeSpan.FromMilliseconds(update % 240000)
                });
                if (update % 20 == 0)
                {
                    controls.PlaybackStatus = update % 40 == 0 ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                await Task.Delay(5);
            }
        }
        finally
        {
            duration.Cancel();
            session.TimelinePropertiesChanged -= OnTimelineChanged;
            session.PlaybackInfoChanged -= OnPlaybackChanged;
            await render;
            queue.Writer.Complete();
            await consumer;
            controls.IsEnabled = false;
            GC.KeepAlive(manager);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (snapshots < 100 || playbackChanges < 10 || frames < 1000)
        {
            throw new InvalidOperationException($"Insufficient test activity: snapshots={snapshots}, playback={playbackChanges}, frames={frames}");
        }
        Console.WriteLine($"PASS: native events + GC stress, snapshots={snapshots}, playback={playbackChanges}, frames={frames}");
    }
}
