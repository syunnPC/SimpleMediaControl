using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PlayerApp;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

internal static class ClientChecks
{
    public static async Task RunAsync(string executable)
    {
        var controller = Windows.System.DispatcherQueueController.CreateOnDedicatedThread();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.DispatcherQueue.TryEnqueue(async () =>
        {
            SynchronizationContext.SetSynchronizationContext(new MediaSynchronizationContext(action => controller.DispatcherQueue.TryEnqueue(() => action())));
            try
            {
                await RunCoreAsync(executable);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        try
        {
            await completion.Task;
        }
        finally
        {
            await controller.ShutdownQueueAsync();
        }
    }

    private static async Task RunCoreAsync(string executable)
    {
        var sourceThread = Environment.CurrentManagedThreadId;
        void CheckContext()
        {
            if (Environment.CurrentManagedThreadId != sourceThread || SynchronizationContext.Current is not MediaSynchronizationContext)
            {
                throw new InvalidOperationException("Async work escaped the STA source dispatcher.");
            }
        }
        await CheckFramingAsync();
        using var window = new SourceWindow();
        var controls = SystemMediaTransportControlsInterop.GetForWindow(window.Handle);
        var buttonEvents = 0;
        controls.ButtonPressed += (_, args) =>
        {
            Interlocked.Increment(ref buttonEvents);
            controls.PlaybackStatus = args.Button == SystemMediaTransportControlsButton.Pause
                ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
        };
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsNextEnabled = true;
        controls.IsPreviousEnabled = true;
        controls.PlaybackPositionChangeRequested += (_, args) => SetTimeline(controls, args.RequestedPlaybackPosition.TotalSeconds);
        controls.PlaybackStatus = MediaPlaybackStatus.Playing;
        controls.DisplayUpdater.Type = MediaPlaybackType.Music;
        controls.DisplayUpdater.MusicProperties.Title = "PlayerApp IPC contract test";
        controls.DisplayUpdater.MusicProperties.Artist = "Independent silent source";
        using var artwork = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, artwork);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 1, 1, 96, 96, [32, 96, 192, 255]);
        await encoder.FlushAsync();
        CheckContext();
        artwork.Seek(0);
        var artworkPath = Path.Combine(Path.GetTempPath(), $"PlayerApp-test-{Guid.NewGuid():N}.png");
        using (var destination = File.Create(artworkPath))
        {
            await artwork.AsStreamForRead().CopyToAsync(destination);
        }
        controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(artworkPath));
        controls.DisplayUpdater.Update();
        SetTimeline(controls, 12);

        var gate = new object();
        var connections = 0;
        using var client = new MediaClient(Path.GetFullPath(executable), action => { lock (gate) { action(); } }, Console.WriteLine);
        client.ConnectionChanged += (sender, _) => { if (sender.IsConnected) { connections++; } };
        client.Start();
        var source = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
        MediaSession? FindSession() => client.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains(source, StringComparison.OrdinalIgnoreCase));
        async Task WaitAsync(Func<bool> condition, string name)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(25))
            {
                lock (gate)
                {
                    if (condition())
                    {
                        return;
                    }
                }
                await Task.Delay(25);
            }
            lock (gate)
            {
                throw new InvalidOperationException("Timed out: " + name + "; sessions: " + string.Join("; ", client.GetSessions()
                    .Select(s => $"{s.SourceAppUserModelId} title={s.Media.Title} artwork={s.Media.Thumbnail?.Length}")));
            }
        }

        await WaitAsync(() => client.IsConnected && FindSession()?.Media.Thumbnail is { Length: > 0 }, "initial snapshot + artwork");
        await WaitAsync(() => FindSession()?.Timeline?.EndTime == TimeSpan.FromMinutes(4), "timeline over IPC");
        CheckContext();
        if (!await client.SendAsync(new(0, MediaCommandKind.Ping)))
        {
            throw new InvalidOperationException("Ping failed.");
        }
        MediaSession current;
        lock (gate) { current = FindSession()!; }
        if (!await current.TryPauseAsync())
        {
            throw new InvalidOperationException("Pause command was rejected.");
        }
        await WaitAsync(() => FindSession()?.Playback.PlaybackStatus == SessionPlaybackState.Paused && buttonEvents > 0, "pause round trip");

        for (var crash = 0; crash < 3; crash++)
        {
            var previousWorker = client.WorkerProcessId ?? throw new InvalidOperationException("Missing worker PID.");
            using (var process = Process.GetProcessById(previousWorker))
            {
                if (!string.Equals(process.MainModule?.FileName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Refusing to kill an unexpected process.");
                }
                process.Kill();
                await process.WaitForExitAsync();
            }
            await WaitAsync(() => !client.IsConnected, "disconnect notification");
            try
            {
                await current.TrySkipNextAsync();
                throw new InvalidOperationException("Disconnected command was accepted.");
            }
            catch (IOException)
            {
            }
            await WaitAsync(() => client.IsConnected && client.WorkerProcessId != previousWorker
                && FindSession()?.Media.Thumbnail is { Length: > 0 }, "recovery after worker termination");
            lock (gate) { current = FindSession()!; }
            controls.PlaybackStatus = MediaPlaybackStatus.Paused;
            await WaitAsync(() => FindSession()?.Playback.PlaybackStatus == SessionPlaybackState.Paused, "paused state before command");
            var before = buttonEvents;
            if (!await current.TryPlayAsync())
            {
                throw new InvalidOperationException("Play command after reconnect was rejected.");
            }
            await WaitAsync(() => FindSession()?.Playback.PlaybackStatus == SessionPlaybackState.Playing && buttonEvents > before, "play after recovery");
            if (!await current.TryChangePlaybackPositionAsync((30 + crash) * TimeSpan.TicksPerSecond))
            {
                throw new InvalidOperationException("Seek command after reconnect was rejected.");
            }
            await WaitAsync(() => FindSession()?.Timeline?.Position == TimeSpan.FromSeconds(30 + crash), "timeline after recovery");
            CheckContext();
            Console.WriteLine($"PASS: worker termination/recovery {crash + 1}");
        }
        var finalWorker = client.WorkerProcessId!.Value;
        client.Dispose();
        await WaitAsync(() => client.WorkerProcessId is null, "worker shutdown");
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)).Any(p => { using (p) { return p.Id == finalWorker && !p.HasExited; } }))
        {
            throw new InvalidOperationException("Orphan worker process after shutdown.");
        }
        controls.IsEnabled = false;
        File.Delete(artworkPath);
        Console.WriteLine($"PASS: IPC metadata/artwork/playback/timeline + 3 worker failures + shutdown; connections={connections}");
    }

    private static void SetTimeline(SystemMediaTransportControls controls, double seconds)
    {
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromMinutes(4),
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.FromMinutes(4),
            Position = TimeSpan.FromSeconds(seconds)
        });
    }

    private sealed class SourceWindow : IDisposable
    {
        public nint Handle { get; } = CreateWindowExW(0, "STATIC", "PlayerApp media test", 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public SourceWindow()
        {
            if (Handle == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public void Dispose() => DestroyWindow(Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(uint styleEx, string className, string name, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(nint hwnd);
    }

    private static async Task CheckFramingAsync()
    {
        foreach (var length in new[] { 0, -1, MediaPipe.MaxMessageBytes + 1 })
        {
            using var data = new MemoryStream(BitConverter.GetBytes(length));
            using var pipe = new MediaPipe(data);
            try
            {
                await pipe.ReadAsync<MediaMessage>(CancellationToken.None);
                throw new InvalidOperationException("Invalid IPC frame was accepted.");
            }
            catch (InvalidDataException)
            {
            }
        }
        using var stream = new MemoryStream();
        using var roundTrip = new MediaPipe(stream);
        var message = new MediaMessage(MediaMessageKind.Timeline, Timeline: new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(90), DateTimeOffset.UtcNow));
        await roundTrip.SendAsync(message, CancellationToken.None);
        stream.Position = 0;
        if (await roundTrip.ReadAsync<MediaMessage>(CancellationToken.None) != message)
        {
            throw new InvalidOperationException("IPC serialization changed the timeline.");
        }
    }
}
