using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media.Control;

namespace PlayerApp;

// All GSMTC activation, callbacks, snapshots and finalizers belong to this process.
// Access violations in Windows.Media.MediaControl.dll cannot corrupt the UI process.
internal sealed class MediaWorker
{
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly Channel<MediaMessage> _outgoing = Channel.CreateUnbounded<MediaMessage>(new() { SingleReader = true });
    private readonly Dictionary<nint, Entry> _sessions = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private int _sessionsQueued;

    public static async Task RunAsync(string pipeName)
    {
        using var lifetime = new CancellationTokenSource();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10000, lifetime.Token).ConfigureAwait(false);
        using var pipe = new MediaPipe(client);
        var worker = new MediaWorker();
        var writer = Task.Run(async () =>
        {
            await foreach (var message in worker._outgoing.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                await pipe.SendAsync(message, lifetime.Token).ConfigureAwait(false);
            }
        });
        var commands = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                var command = await pipe.ReadAsync<MediaCommand>(lifetime.Token).ConfigureAwait(false);
                worker._work.Writer.TryWrite(() => worker.ExecuteAsync(command));
            }
        });
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var nativeWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.DispatcherQueue.TryEnqueue(async () =>
        {
            SynchronizationContext.SetSynchronizationContext(new MediaSynchronizationContext(action => controller.DispatcherQueue.TryEnqueue(() => action())));
            try
            {
                worker._manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                worker._manager.SessionsChanged += worker.OnSessionsChanged;
                await worker.RefreshSessionsAsync();
                worker.Publish(new(MediaMessageKind.Ready, Error: $"apartment={Thread.CurrentThread.GetApartmentState()}, context={SynchronizationContext.Current?.GetType().Name}"));
                await foreach (var action in worker._work.Reader.ReadAllAsync(lifetime.Token))
                {
                    try
                    {
                        if (!controller.DispatcherQueue.HasThreadAccess)
                        {
                            throw new InvalidOperationException("Native media work left its owning dispatcher.");
                        }
                        await action();
                    }
                    catch (Exception ex)
                    {
                        worker.Publish(new(MediaMessageKind.Error, Error: ex.ToString()));
                    }
                }
                nativeWork.TrySetResult();
            }
            catch (Exception ex)
            {
                nativeWork.TrySetException(ex);
            }
        });
        try
        {
            await await Task.WhenAny(commands, writer, nativeWork.Task).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            worker._work.Writer.TryComplete();
            worker._outgoing.Writer.TryComplete();
            // Closing the pipe cancels blocked IO. The process lifetime owns all native
            // references, including callbacks that may still be in flight at shutdown.
            pipe.Dispose();
            await controller.ShutdownQueueAsync();
        }
    }

    private void Publish(MediaMessage message) => _outgoing.Writer.TryWrite(message);

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        if (Interlocked.Exchange(ref _sessionsQueued, 1) == 0)
        {
            _work.Writer.TryWrite(async () =>
            {
                Interlocked.Exchange(ref _sessionsQueued, 0);
                await RefreshSessionsAsync();
            });
        }
    }

    private Task RefreshSessionsAsync()
    {
        if (_manager is null)
        {
            return Task.CompletedTask;
        }
        var present = new HashSet<nint>();
        var added = new List<Entry>();
        foreach (var native in _manager.GetSessions())
        {
            var identity = ((WinRT.IWinRTObject)native).NativeObject.ThisPtr;
            present.Add(identity);
            if (!_sessions.ContainsKey(identity))
            {
                var entry = new Entry(this, native);
                _sessions.Add(identity, entry);
                added.Add(entry);
            }
        }
        foreach (var identity in _sessions.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            _sessions[identity].Detach();
            _sessions.Remove(identity);
        }
        foreach (var entry in added)
        {
            try
            {
                entry.UpdatePlayback();
            }
            catch (Exception ex)
            {
                Publish(new(MediaMessageKind.Error, Error: ex.ToString()));
            }
        }
        Publish(new(MediaMessageKind.Sessions, Sessions: _sessions.Values.Select(entry => entry.Descriptor).ToArray()));
        foreach (var entry in added)
        {
            entry.Queue(MediaMessageKind.Playback);
            entry.Queue(MediaMessageKind.Timeline);
            entry.Queue(MediaMessageKind.Media);
        }
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(MediaCommand command)
    {
        var success = false;
        try
        {
            if (command.Kind == MediaCommandKind.Ping)
            {
                success = true;
            }
            else if (_sessions.Values.FirstOrDefault(entry => entry.Descriptor.Id == command.SessionId) is { } entry)
            {
                success = command.Kind switch
                {
                    MediaCommandKind.Play => await entry.Native.TryPlayAsync(),
                    MediaCommandKind.Pause => await entry.Native.TryPauseAsync(),
                    MediaCommandKind.Previous => await entry.Native.TrySkipPreviousAsync(),
                    MediaCommandKind.Next => await entry.Native.TrySkipNextAsync(),
                    MediaCommandKind.Seek => await entry.Native.TryChangePlaybackPositionAsync(command.PositionTicks),
                    _ => false
                };
            }
        }
        catch (Exception ex)
        {
            Publish(new(MediaMessageKind.Error, Error: ex.ToString()));
        }
        Publish(new(MediaMessageKind.Result, RequestId: command.RequestId, Success: success));
    }

    private sealed class Entry
    {
        private readonly MediaWorker _owner;
        private int _queued;
        private bool _detached;
        public GlobalSystemMediaTransportControlsSession Native { get; }
        public SessionDescriptor Descriptor { get; private set; }

        public Entry(MediaWorker owner, GlobalSystemMediaTransportControlsSession native)
        {
            _owner = owner;
            Native = native;
            Descriptor = new(Guid.NewGuid(), native.SourceAppUserModelId, PlaybackData.Empty);
            native.PlaybackInfoChanged += OnPlayback;
            native.TimelinePropertiesChanged += OnTimeline;
            native.MediaPropertiesChanged += OnMedia;
        }

        public void Detach()
        {
            _detached = true;
            Native.PlaybackInfoChanged -= OnPlayback;
            Native.TimelinePropertiesChanged -= OnTimeline;
            Native.MediaPropertiesChanged -= OnMedia;
        }

        public PlaybackData UpdatePlayback()
        {
            var playback = Native.GetPlaybackInfo();
            var controls = playback.Controls;
            var rate = playback.PlaybackRate;
            var data = new PlaybackData((SessionPlaybackState)(int)playback.PlaybackStatus,
                rate is double value && double.IsFinite(value) ? value : null,
                new(controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsPreviousEnabled,
                    controls.IsNextEnabled, controls.IsPlaybackPositionEnabled));
            Descriptor = Descriptor with { Playback = data };
            return data;
        }

        private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Queue(MediaMessageKind.Playback);
        private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Queue(MediaMessageKind.Timeline);
        private void OnMedia(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Queue(MediaMessageKind.Media);

        public void Queue(MediaMessageKind kind)
        {
            var flag = 1 << (int)kind;
            if ((Interlocked.Or(ref _queued, flag) & flag) != 0)
            {
                return;
            }
            _owner._work.Writer.TryWrite(async () =>
            {
                Interlocked.And(ref _queued, ~flag);
                if (!_detached)
                {
                    await ReadAsync(kind);
                }
            });
        }

        private async Task ReadAsync(MediaMessageKind kind)
        {
            switch (kind)
            {
                case MediaMessageKind.Playback:
                    _owner.Publish(new(kind, Descriptor.Id, Playback: UpdatePlayback()));
                    break;
                case MediaMessageKind.Timeline:
                    var timeline = Native.GetTimelineProperties();
                    _owner.Publish(new(kind, Descriptor.Id, Timeline: timeline is null ? null
                        : new(timeline.Position, timeline.EndTime, timeline.LastUpdatedTime)));
                    break;
                case MediaMessageKind.Media:
                    var media = await Native.TryGetMediaPropertiesAsync();
                    byte[]? artwork = null;
                    try
                    {
                        if (media.Thumbnail is { } thumbnail)
                        {
                            using var stream = await thumbnail.OpenReadAsync();
                            // Base64 plus metadata must fit within the bounded IPC frame.
                            if (stream.Size <= 16 * 1024 * 1024)
                            {
                                artwork = new byte[checked((int)stream.Size)];
                                using var source = stream.AsStreamForRead();
                                await source.ReadExactlyAsync(artwork);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        artwork = null;
                        _owner.Publish(new(MediaMessageKind.Error, Error: "Artwork: " + ex));
                    }
                    _owner.Publish(new(kind, Descriptor.Id, Media: new(media.Title, media.Artist, media.AlbumArtist, media.AlbumTitle, artwork)));
                    break;
            }
        }
    }
}
