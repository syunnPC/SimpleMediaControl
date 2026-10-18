using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerApp;

internal enum SessionPlaybackState { Closed, Opened, Changing, Stopped, Playing, Paused }
internal enum MediaMessageKind { Ready, Sessions, Playback, Timeline, Media, Result, Error }
internal enum MediaCommandKind { Ping, Play, Pause, Previous, Next, Seek }

internal sealed record MediaControls(bool IsPlayEnabled, bool IsPauseEnabled, bool IsPreviousEnabled,
    bool IsNextEnabled, bool IsPlaybackPositionEnabled);
internal sealed record PlaybackData(SessionPlaybackState PlaybackStatus, double? PlaybackRate, MediaControls Controls)
{
    public static PlaybackData Empty { get; } = new(SessionPlaybackState.Closed, null, new(false, false, false, false, false));
}
internal sealed record TimelineData(TimeSpan Position, TimeSpan EndTime, DateTimeOffset LastUpdatedTime);
internal sealed record MediaData(string Title, string Artist, string AlbumArtist, string AlbumTitle, byte[]? Thumbnail)
{
    public static MediaData Empty { get; } = new("", "", "", "", null);
}
internal sealed record SessionDescriptor(Guid Id, string SourceAppUserModelId, PlaybackData Playback);
internal sealed record MediaMessage(MediaMessageKind Kind, Guid SessionId = default,
    SessionDescriptor[]? Sessions = null, PlaybackData? Playback = null, TimelineData? Timeline = null,
    MediaData? Media = null, long RequestId = 0, bool Success = false, string? Error = null);
internal sealed record MediaCommand(long RequestId, MediaCommandKind Kind, Guid SessionId = default, long PositionTicks = 0);

// One reader and serialized writers per connection. Framing also bounds allocations
// for external metadata/artwork before deserialization.
internal sealed class MediaPipe(Stream stream) : IDisposable
{
    public const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task SendAsync<T>(T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("Media message exceeds the IPC size limit.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<T> ReadAsync<T>(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException("Invalid media message length.");
        }
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty media message.");
    }

    public void Dispose() => stream.Dispose();
}
