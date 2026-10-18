using System;

namespace PlayerApp;

// Keep the render timer independent of WinRT objects. Native snapshots are read only
// on session changes/events, then copied here before they can be collected.
internal sealed class PlaybackTimeline
{
    private TimeSpan _position;
    private DateTimeOffset _updatedAt;
    private bool _playing;
    private double _rate = 1;

    public TimeSpan EndTime { get; private set; }
    public bool HasTimeline => EndTime > TimeSpan.Zero;

    public void Clear()
    {
        _position = TimeSpan.Zero;
        _updatedAt = default;
        EndTime = TimeSpan.Zero;
        _playing = false;
        _rate = 1;
    }

    public void SetTimeline(TimeSpan position, TimeSpan endTime, DateTimeOffset updatedAt)
    {
        EndTime = endTime > TimeSpan.Zero ? endTime : TimeSpan.Zero;
        _position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, EndTime.Ticks));
        _updatedAt = updatedAt;
    }

    public void SetPlayback(bool playing, double? rate, DateTimeOffset now)
    {
        var nextRate = rate is double value && double.IsFinite(value) ? value : 1;
        if (_playing == playing && _rate == nextRate)
        {
            return;
        }

        _position = GetPosition(now);
        _updatedAt = now;
        _playing = playing;
        _rate = nextRate;
    }

    public TimeSpan GetPosition(DateTimeOffset now)
    {
        if (!HasTimeline)
        {
            return TimeSpan.Zero;
        }

        var ticks = (double)_position.Ticks;
        if (_playing)
        {
            ticks += Math.Max(0, (now - _updatedAt).Ticks) * _rate;
        }

        // Clamp before converting back to ticks, including extreme native rates.
        return ticks <= 0 ? TimeSpan.Zero
            : ticks >= EndTime.Ticks ? EndTime
            : TimeSpan.FromTicks((long)ticks);
    }
}
