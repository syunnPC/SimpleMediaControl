using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;

namespace PlayerApp;

public sealed partial class MainWindow
{
    private readonly LyricsClient _lyricsClient = LyricsClient.Create();
    private sealed record LyricsRow(Button Row, TextBlock Text, Run[]? GapDots);
    private readonly List<LyricsRow> _lyricsRows = new();
    private readonly LyricsFollowState _lyricsFollow = new();
    private readonly SolidColorBrush _lyricsForeground = new();
    private readonly SolidColorBrush _lyricsSecondary = new();
    private readonly SolidColorBrush _lyricsAccent = new();
    private readonly SolidColorBrush _lyricsOnAccent = new();
    private DispatcherQueueTimer? _lyricsTimer;
    private CancellationTokenSource? _lyricsRequest;
    private LyricsTrack? _lyricsTrack;
    private LyricsDocument? _lyricsDocument;
    private bool _lyricsVisible;
    private int _lyricsGeneration;
    private int _lyricsActiveIndex = -1;
    private int _lyricsScrolledIndex = -1;
    private int _lyricsGapIndex = -1;
    private int _lyricsGapPhase = -1;
    private DateTimeOffset _lyricsSeekErrorUntil;
    private bool _lyricsPointerDown;
    private DateTimeOffset _lyricsFollowAfter;

    private void InitializeLyrics()
    {
        _lyricsTimer = DispatcherQueue.CreateTimer();
        _lyricsTimer.Interval = TimeSpan.FromMilliseconds(100);
        _lyricsTimer.Tick += OnLyricsTick;
        LyricsScroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => DeferLyricsFollow()), true);
        LyricsScroller.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _lyricsPointerDown = true;
            DeferLyricsFollow();
        }), true);
        LyricsScroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            _lyricsPointerDown = false;
            DeferLyricsFollow();
        }), true);
        LyricsScroller.PointerCaptureLost += (_, _) => _lyricsPointerDown = false;
        LyricsScroller.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
            {
                DeferLyricsFollow();
            }
        };
        LyricsScroller.SizeChanged += (_, _) =>
        {
            _lyricsScrolledIndex = -1;
            UpdateLyricsPosition();
        };
    }

    private void DeferLyricsFollow()
    {
        _lyricsFollowAfter = DateTimeOffset.UtcNow.AddSeconds(5);
        _lyricsScrolledIndex = -1;
    }

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private void OnLyricsClick(object sender, RoutedEventArgs e)
    {
        _lyricsVisible = !_lyricsVisible;
        LyricsPanel.Visibility = _lyricsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateWindowSize();
        var label = _lyricsVisible ? UiText.HideLyrics : UiText.ShowLyrics;
        AutomationProperties.SetName(LyricsButton, label);
        ToolTipService.SetToolTip(LyricsButton, label);
        UpdateAuxiliaryButtonColors();
        if (_lyricsVisible)
        {
            _lyricsFollowAfter = default;
            _lyricsScrolledIndex = -1;
            if (_lyricsDocument is null)
            {
                _ = LoadLyricsAsync(false);
            }
            UpdateLyricsPosition();
        }
        else
        {
            CancelLyricsRequest();
            LyricsLoading.IsActive = false;
        }
        UpdateLyricsTimerState();
        OnUi(UpdateNonClientRegions);
    }

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private void OnLyricsRetryClick(object sender, RoutedEventArgs e) => _ = LoadLyricsAsync(true);

    private void RefreshLyricsTrack()
    {
        var track = LyricsTrack.Create(_session?.Media, _playbackTimeline.EndTime);
        // A reconnect can deliver an empty snapshot before the actual metadata.
        // Duration/album changes do not invalidate lyrics for the playing song.
        if ((track is null && _session is not null) || track == _lyricsTrack)
        {
            return;
        }
        if (track?.IsSameSong(_lyricsTrack) == true)
        {
            _lyricsTrack = track;
            return;
        }
        CancelLyricsRequest();
        _lyricsTrack = track;
        _lyricsDocument = null;
        _lyricsSeekErrorUntil = default;
        _lyricsFollow.Reset();
        _lyricsActiveIndex = -1;
        _lyricsScrolledIndex = -1;
        _lyricsFollowAfter = default;
        _lyricsPointerDown = false;
        LyricsLines.Children.Clear();
        _lyricsRows.Clear();
        ShowLyricsMessage(track is null ? UiText.TrackMetadataRequired : UiText.SearchingLyrics, false, false);
        UpdateLyricsTimerState();
        if (_lyricsVisible)
        {
            _ = LoadLyricsAsync(false);
        }
    }

    private async Task LoadLyricsAsync(bool refresh)
    {
        CancelLyricsRequest();
        if (_lyricsTrack is not { } track || !_lyricsVisible || _closed)
        {
            ShowLyricsMessage(_session is null ? UiText.NoSongPlaying : UiText.TrackMetadataRequired, false, false);
            return;
        }
        var request = new CancellationTokenSource();
        _lyricsRequest = request;
        var generation = _lyricsGeneration;
        ShowLyricsMessage(UiText.SearchingLyrics, true, false);
        try
        {
            // Metadata and duration may arrive in separate broker messages.
            await Task.Delay(300, request.Token);
            var lookupTrack = _lyricsTrack;
            if (lookupTrack is null || !track.IsSameSong(lookupTrack))
            {
                return;
            }
            var document = (await _lyricsClient.GetAsync(lookupTrack, refresh, request.Token)).ForDisplay();
            if (_closed || !_lyricsVisible || generation != _lyricsGeneration)
            {
                return;
            }
            _lyricsDocument = document;
            DisplayLyrics(document);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_closed)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
            or System.IO.InvalidDataException or System.Text.RegularExpressions.RegexMatchTimeoutException or LyricsRateLimitException)
        {
            if (!_closed && _lyricsVisible && generation == _lyricsGeneration)
            {
                ShowLyricsMessage(ex is LyricsRateLimitException
                    ? UiText.LyricsRetryLater : UiText.LyricsFetchFailed, false, true);
            }
        }
        finally
        {
            if (ReferenceEquals(_lyricsRequest, request))
            {
                _lyricsRequest = null;
            }
            request.Dispose();
        }
    }

    private void CancelLyricsRequest()
    {
        _lyricsGeneration++;
        _lyricsRequest?.Cancel();
        _lyricsRequest = null;
    }

    private void ShowLyricsMessage(string text, bool loading, bool retry)
    {
        LyricsMessage.Text = text;
        LyricsMessagePanel.Visibility = Visibility.Visible;
        LyricsScroller.Visibility = Visibility.Collapsed;
        LyricsLoading.IsActive = loading;
        LyricsLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LyricsRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        LyricsSeekStatus.Text = "";
        LyricsSeekStatus.Visibility = Visibility.Collapsed;
    }

    private void DisplayLyrics(LyricsDocument document)
    {
        LyricsLines.Children.Clear();
        _lyricsRows.Clear();
        _lyricsActiveIndex = -1;
        _lyricsScrolledIndex = -1;
        _lyricsGapIndex = -1;
        _lyricsGapPhase = -1;
        _lyricsFollow.Reset();
        if (document.Kind is LyricsKind.NotFound or LyricsKind.Instrumental)
        {
            ShowLyricsMessage(document.Kind == LyricsKind.Instrumental ? UiText.InstrumentalTrack : UiText.LyricsNotFound, false, document.Kind == LyricsKind.NotFound);
            UpdateLyricsTimerState();
            return;
        }
        var lines = document.Kind == LyricsKind.Synced
            ? System.Linq.Enumerable.Select(document.Cues, cue => cue.Text)
            : document.PlainText.Replace("\r", "").Split('\n');
        foreach (var line in lines)
        {
            var synced = document.Kind == LyricsKind.Synced;
            // Measure all synced rows at the largest size. Cue changes affect only
            // the render transform, never line wrapping, row height or scroll extent.
            var gap = synced && string.IsNullOrWhiteSpace(line);
            var text = new TextBlock
            {
                Text = line,
                FontSize = synced ? 20 : 16,
                FontWeight = synced ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _lyricsForeground,
                Scale = new Vector3(synced ? 0.8f : 1f, synced ? 0.8f : 1f, 1f)
            };
            Run[]? dots = null;
            if (gap)
            {
                text.HorizontalAlignment = HorizontalAlignment.Center;
                dots = [new Run { Text = "・" }, new Run { Text = "・" }, new Run { Text = "・" }];
                foreach (var dot in dots)
                {
                    dot.Foreground = _lyricsSecondary;
                    text.Inlines.Add(dot);
                }
            }
            if (synced)
            {
                text.SizeChanged += (_, _) => text.CenterPoint = new Vector3(gap ? (float)text.ActualWidth / 2 : 0, (float)text.ActualHeight / 2, 0);
            }
            var row = new Button
            {
                Content = text,
                Style = (Style)Application.Current.Resources["LyricsLineButton"],
                Tag = _lyricsRows.Count,
                FocusVisualPrimaryBrush = _lyricsForeground,
                FocusVisualSecondaryBrush = _lyricsOnAccent
            };
            AutomationProperties.SetName(row, gap ? UiText.LyricsGap : line);
            AutomationProperties.SetAutomationId(row, $"LyricLine{_lyricsRows.Count}");
            row.Click += OnLyricLineClick;
            _lyricsRows.Add(new(row, text, dots));
            LyricsLines.Children.Add(row);
        }
        LyricsMessagePanel.Visibility = Visibility.Collapsed;
        LyricsLoading.IsActive = false;
        LyricsScroller.Visibility = Visibility.Visible;
        LyricsScroller.ChangeView(null, 0, null, true);
        UpdateLyricsTransitions();
        UpdateLyricsSeekAvailability();
        UpdateLyricsTimerState();
        OnUi(UpdateLyricsPosition);
    }

    private void OnLyricsTick(DispatcherQueueTimer sender, object args) => UpdateLyricsPosition();

    private void UpdateLyricsTimerState()
    {
        if (_lyricsTimer is null)
        {
            return;
        }
        var minimized = AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized;
        if (_lyricsVisible && !_closed && !minimized && _lyricsDocument?.Kind == LyricsKind.Synced)
        {
            _lyricsTimer.Start();
        }
        else
        {
            _lyricsTimer.Stop();
        }
    }

    private void UpdateLyricsPosition()
    {
        if (!_lyricsVisible || _lyricsDocument is not { Kind: LyricsKind.Synced or LyricsKind.Plain } document || _closed)
        {
            return;
        }
        var seekFailed = DateTimeOffset.UtcNow < _lyricsSeekErrorUntil;
        LyricsSeekStatus.Text = seekFailed ? UiText.SeekFailed : "";
        LyricsSeekStatus.Visibility = seekFailed ? Visibility.Visible : Visibility.Collapsed;
        var position = _seeking ? TimeSpan.FromSeconds(_timelineValue) : _playbackTimeline.GetPosition(DateTimeOffset.Now);
        var forwardPlayback = _session?.Playback is { PlaybackStatus: SessionPlaybackState.Playing } playback && (playback.PlaybackRate ?? 1) > 0;
        var index = document.Kind == LyricsKind.Synced && _playbackTimeline.HasTimeline && position < _playbackTimeline.EndTime
            ? _lyricsFollow.GetIndex(document, position, forwardPlayback, _seeking) : -1;
        if (index != _lyricsActiveIndex)
        {
            var old = _lyricsActiveIndex;
            _lyricsActiveIndex = index;
            StyleLyricsRow(old);
            StyleLyricsRow(index);
        }
        UpdateLyricsGap(index, position, document);
        if (index < 0 || index == _lyricsScrolledIndex
            || _lyricsPointerDown || DateTimeOffset.UtcNow < _lyricsFollowAfter)
        {
            return;
        }
        var row = _lyricsRows[index].Row;
        if (row.ActualHeight <= 0 || LyricsScroller.ViewportHeight <= 0)
        {
            return;
        }
        var top = row.TransformToVisual(LyricsLines).TransformPoint(new(0, 0)).Y;
        var target = LyricsFollowState.GetScrollTarget(top, row.ActualHeight, LyricsScroller.ViewportHeight,
            LyricsScroller.ScrollableHeight, LyricsScroller.VerticalOffset);
        if (target is { } offset)
        {
            LyricsScroller.ChangeView(null, offset, null, !_uiSettings.AnimationsEnabled);
        }
        _lyricsScrolledIndex = index;
    }

    private void StyleLyricsRow(int index)
    {
        if (index < 0 || index >= _lyricsRows.Count)
        {
            return;
        }
        var (row, text, dots) = _lyricsRows[index];
        var active = index == _lyricsActiveIndex;
        var scale = active ? 1f : 0.8f;
        text.Scale = new Vector3(scale, scale, 1f);
        AutomationProperties.SetItemStatus(row, active ? (dots is null ? UiText.CurrentLyric : UiText.LyricsGap) : "");
        if (dots is not null)
        {
            foreach (var dot in dots)
            {
                dot.Foreground = _lyricsSecondary;
            }
            _lyricsGapIndex = _lyricsGapPhase = -1;
        }
    }

    private void UpdateLyricsGap(int index, TimeSpan position, LyricsDocument document)
    {
        if (index < 0 || _lyricsRows[index].GapDots is not { } dots || document.GetGap(index, position, _playbackTimeline.EndTime) is not { } gap
            || (_lyricsGapIndex == index && _lyricsGapPhase == gap.Phase))
        {
            return;
        }
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].Foreground = i <= gap.Phase ? _lyricsAccent : _lyricsSecondary;
        }
        _lyricsGapIndex = index;
        _lyricsGapPhase = gap.Phase;
        AutomationProperties.SetItemStatus(_lyricsRows[index].Row, UiText.Format(UiText.LyricsGapProgress, gap.Phase + 1));
    }

    private bool CanSeekLyrics => !_closed && _timelineSeekEnabled && _mediaClient?.IsConnected == true
        && _session?.Playback.Controls.IsPlaybackPositionEnabled == true && _playbackTimeline.HasTimeline;

    private void UpdateLyricsSeekAvailability()
    {
        for (var i = 0; i < _lyricsRows.Count; i++)
        {
            var row = _lyricsRows[i].Row;
            var position = CanSeekLyrics ? _lyricsDocument?.GetSeekPosition(i, _playbackTimeline.EndTime) : null;
            row.IsEnabled = row.IsTabStop = position is not null;
            var label = position is { } time ? UiText.Format(UiText.SeekTo, FormatTime(time)) : null;
            ToolTipService.SetToolTip(row, label);
            AutomationProperties.SetHelpText(row, label ?? "");
        }
    }

    private async void OnLyricLineClick(object sender, RoutedEventArgs e)
    {
        var session = _session;
        var document = _lyricsDocument;
        if (!CanSeekLyrics || session is null || document is null || sender is not Button { Tag: int index }
            || document.GetSeekPosition(index, _playbackTimeline.EndTime) is not { } position)
        {
            return;
        }
        DeferLyricsFollow();
        _lyricsSeekErrorUntil = default;
        var generation = _lyricsGeneration;
        bool success;
        try
        {
            success = await session.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or TimeoutException)
        {
            success = false;
        }
        if (_closed || generation != _lyricsGeneration || !ReferenceEquals(session, _session) || !ReferenceEquals(document, _lyricsDocument))
        {
            return;
        }
        _lyricsFollow.Reset();
        if (!success)
        {
            _lyricsSeekErrorUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        }
        UpdateLyricsPosition();
    }

    private void UpdateLyricsTransitions()
    {
        var animate = _uiSettings.AnimationsEnabled && _lyricsDocument?.Kind == LyricsKind.Synced;
        foreach (var (_, text, _) in _lyricsRows)
        {
            // Implicit transitions retarget from the displayed scale if the next
            // cue arrives mid-animation, without changing any layout dimensions.
            text.ScaleTransition = animate ? new Vector3Transition
            {
                Duration = TimeSpan.FromMilliseconds(240),
                Components = Vector3TransitionComponents.X | Vector3TransitionComponents.Y
            } : null;
        }
    }

    private void ApplyLyricsColors(Color foreground, Color secondary, Color accent, Color onAccent, Color hover)
    {
        _lyricsForeground.Color = foreground;
        _lyricsSecondary.Color = secondary;
        _lyricsAccent.Color = accent;
        _lyricsOnAccent.Color = onAccent;
        LyricsPanel.Background = new SolidColorBrush(_accessibilitySettings.HighContrast ? _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background) : Color.FromArgb(38, 0, 0, 0));
        LyricsPanel.BorderBrush = new SolidColorBrush(hover);
        LyricsHeading.Foreground = _lyricsForeground;
        LyricsSeekStatus.Foreground = _lyricsSecondary;
        LyricsMessage.Foreground = _lyricsForeground;
        LyricsCredit.Foreground = _lyricsSecondary;
        LyricsLoading.Foreground = _lyricsAccent;
        LyricsRetry.Foreground = _lyricsForeground;
        UpdateLyricsTransitions();
        UpdateAuxiliaryButtonColors();
    }

    private void UpdateAuxiliaryButtonColors()
    {
        LyricsButton.Background = _lyricsVisible ? _lyricsAccent : new SolidColorBrush(Colors.Transparent);
        LyricsButton.Foreground = _lyricsVisible ? _lyricsOnAccent : _lyricsForeground;
        PinButton.Background = _isPinned ? _lyricsAccent : new SolidColorBrush(Colors.Transparent);
        PinButton.Foreground = _isPinned ? _lyricsOnAccent : _lyricsForeground;
        PinIcon.Glyph = _isPinned ? "\uE77A" : "\uE718";
    }

    private void DisposeLyrics()
    {
        CancelLyricsRequest();
        _lyricsTimer?.Stop();
        if (_lyricsTimer is not null)
        {
            _lyricsTimer.Tick -= OnLyricsTick;
        }
        _lyricsClient.Dispose();
    }
}
