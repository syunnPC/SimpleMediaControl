using System;
using System.Threading.Tasks;

namespace PlayerApp;

// UI-facing sessions contain only managed data; native GSMTC objects never cross IPC.
internal sealed class MediaSession(MediaClient owner, SessionDescriptor descriptor)
{
    public Guid Id { get; } = descriptor.Id;
    public string SourceAppUserModelId { get; } = descriptor.SourceAppUserModelId;
    public PlaybackData Playback { get; private set; } = descriptor.Playback;
    public TimelineData? Timeline { get; private set; }
    public MediaData Media { get; private set; } = MediaData.Empty;
    public event Action<MediaSession, object>? PlaybackInfoChanged;
    public event Action<MediaSession, object>? TimelinePropertiesChanged;
    public event Action<MediaSession, object>? MediaPropertiesChanged;

    internal void Apply(MediaMessage message)
    {
        switch (message.Kind)
        {
            case MediaMessageKind.Playback when message.Playback is { } playback:
                Playback = playback;
                PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case MediaMessageKind.Timeline:
                Timeline = message.Timeline;
                TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case MediaMessageKind.Media when message.Media is { } media:
                Media = media;
                MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public Task<bool> TryPlayAsync() => owner.SendAsync(new(0, MediaCommandKind.Play, Id));
    public Task<bool> TryPauseAsync() => owner.SendAsync(new(0, MediaCommandKind.Pause, Id));
    public Task<bool> TrySkipPreviousAsync() => owner.SendAsync(new(0, MediaCommandKind.Previous, Id));
    public Task<bool> TrySkipNextAsync() => owner.SendAsync(new(0, MediaCommandKind.Next, Id));
    public Task<bool> TryChangePlaybackPositionAsync(long ticks) => owner.SendAsync(new(0, MediaCommandKind.Seek, Id, ticks));
}
