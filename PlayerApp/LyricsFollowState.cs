using System;

namespace PlayerApp;

// Display-only smoothing: native timeline corrections can briefly cross a cue
// backwards. Explicit seeks and larger corrections still take effect immediately.
internal sealed class LyricsFollowState
{
    private LyricsDocument? _document;
    private TimeSpan _lastPosition;

    public int GetIndex(LyricsDocument document, TimeSpan position, bool forwardPlayback, bool seeking)
    {
        if (!ReferenceEquals(document, _document))
        {
            _document = document;
            _lastPosition = position;
        }
        if (forwardPlayback && !seeking && position < _lastPosition && _lastPosition - position <= TimeSpan.FromMilliseconds(350))
        {
            position = _lastPosition;
        }
        _lastPosition = position;
        return document.GetHighlightIndex(position);
    }

    public void Reset()
    {
        _document = null;
        _lastPosition = default;
    }

    public static double? GetScrollTarget(double top, double rowHeight, double viewportHeight, double scrollableHeight, double offset)
    {
        if (rowHeight <= 0 || viewportHeight <= 0)
        {
            return null;
        }
        var center = top + rowHeight / 2;
        var relative = center - offset;
        // Keep a central band rather than recentering for every tiny layout change.
        if (relative >= viewportHeight * 0.35 && relative <= viewportHeight * 0.65)
        {
            return null;
        }
        var target = Math.Clamp(center - viewportHeight / 2, 0, scrollableHeight);
        return Math.Abs(target - offset) >= 1 ? target : null;
    }
}
