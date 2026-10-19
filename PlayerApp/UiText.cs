using System;
using System.Globalization;
using System.Resources;

namespace PlayerApp;

// CurrentUICulture follows the Windows display language. ResourceManager handles
// regional variants (ja-JP -> ja) and the neutral English resource set.
internal static class UiText
{
    private static readonly ResourceManager Resources = new("PlayerApp.Resources.UiStrings", typeof(UiText).Assembly);

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new InvalidOperationException($"Missing UI resource: {key}");

    public static string Format(string template, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);

    public static string LanguageTag => Get(nameof(LanguageTag));
    public static string NoMediaPlaying => Get(nameof(NoMediaPlaying));
    public static string MediaStartHint => Get(nameof(MediaStartHint));
    public static string ShowLyrics => Get(nameof(ShowLyrics));
    public static string HideLyrics => Get(nameof(HideLyrics));
    public static string PinWindow => Get(nameof(PinWindow));
    public static string UnpinWindow => Get(nameof(UnpinWindow));
    public static string MinimizeWindow => Get(nameof(MinimizeWindow));
    public static string CloseWindow => Get(nameof(CloseWindow));
    public static string PreviousTrack => Get(nameof(PreviousTrack));
    public static string Play => Get(nameof(Play));
    public static string Pause => Get(nameof(Pause));
    public static string NextTrack => Get(nameof(NextTrack));
    public static string LyricsTitle => Get(nameof(LyricsTitle));
    public static string NoSongPlaying => Get(nameof(NoSongPlaying));
    public static string RetryLyrics => Get(nameof(RetryLyrics));
    public static string LyricsCredit => Get(nameof(LyricsCredit));
    public static string TrackMetadataRequired => Get(nameof(TrackMetadataRequired));
    public static string SearchingLyrics => Get(nameof(SearchingLyrics));
    public static string LyricsRetryLater => Get(nameof(LyricsRetryLater));
    public static string LyricsFetchFailed => Get(nameof(LyricsFetchFailed));
    public static string InstrumentalTrack => Get(nameof(InstrumentalTrack));
    public static string LyricsNotFound => Get(nameof(LyricsNotFound));
    public static string LyricsGap => Get(nameof(LyricsGap));
    public static string CurrentLyric => Get(nameof(CurrentLyric));
    public static string SeekFailed => Get(nameof(SeekFailed));
    public static string LyricsGapProgress => Get(nameof(LyricsGapProgress));
    public static string SeekTo => Get(nameof(SeekTo));
    public static string MediaUnavailable => Get(nameof(MediaUnavailable));
    public static string MediaReconnecting => Get(nameof(MediaReconnecting));
    public static string MediaConnectionFailed => Get(nameof(MediaConnectionFailed));
    public static string UnknownTitle => Get(nameof(UnknownTitle));
    public static string UnknownApp => Get(nameof(UnknownApp));
    public static string SystemVolume => Get(nameof(SystemVolume));
    public static string AppVolume => Get(nameof(AppVolume));
    public static string Mute => Get(nameof(Mute));
    public static string Unmute => Get(nameof(Unmute));
    public static string AudioSessionUnavailable => Get(nameof(AudioSessionUnavailable));
    public static string BatteryDescription => Get(nameof(BatteryDescription));
    public static string AudioActionName => Get(nameof(AudioActionName));
    public static string VolumePercent => Get(nameof(VolumePercent));
    public static string SwitchToApp => Get(nameof(SwitchToApp));
}
