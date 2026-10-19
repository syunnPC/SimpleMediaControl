using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PlayerApp;

public sealed partial class MainWindow : Window
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    private MediaClient? _mediaClient;
    private MediaSession? _session;
    private DispatcherQueueTimer? _timelineTimer;
    private string? _selectedAumid;
    private bool _seeking;
    private DateTimeOffset _seekingAt;
    private Guid _currentSessionId;
    private int _mediaGeneration;
    private bool _closed;
    private readonly PlaybackTimeline _playbackTimeline = new();

    private bool _timelineSeekEnabled;
    private double _timelineValue;
    private double _timelineMaxSeconds;

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _titleMarqueeStoryboard;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _artistMarqueeStoryboard;

    private bool _sessionPanActive;
    private double _sessionPanStartOffset;
    private Windows.Foundation.Point _sessionPanStartPos;
    private bool _sessionPanMoved;
    private uint _sessionPanPointerId;
    private bool _suppressSessionSwitchClick;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        InitializePaletteRenderer();
        InitializeLyrics();
        ShowEmpty();
        ResetMarquee();
        StartTimelineTimer();
        HookTimelinePointerEvents();
        HookVolumePointerEvents();
        HookSessionPanEvents();
        SessionSwitchScroller.ViewChanged += (sender, args) => OnSessionSwitchViewChanged();
        InitializeAudioVolumes();
        AppWindow.Changed += OnAppWindowChanged;
        RootGrid.Loaded += OnWindowContentLoaded;
        RootGrid.SizeChanged += (sender, args) =>
        {
            OnUi(UpdateNonClientRegions);
            OnUi(UpdateMarquees);
            OnUi(UpdateTimelineBar);
            OnUi(UpdateVolumeBar);
        };
        TitleText.SizeChanged += (sender, args) => OnUi(UpdateMarquees);
        ArtistText.SizeChanged += (sender, args) => OnUi(UpdateMarquees);
        SessionSwitchPanel.SizeChanged += (sender, args) => OnUi(UpdateSessionFades);
        Closed += (window, args) => OnWindowClosed();
        InitializeMediaClient();
    }

    private void StartTimelineTimer()
    {
        _timelineTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timelineTimer.Interval = TimeSpan.FromMilliseconds(500);
        _timelineTimer.IsRepeating = true;
        _timelineTimer.Tick += OnTimelineTick;
        _timelineTimer.Start();
    }

    private void ResetMarquee()
    {
        TitleMarqueeTransform.X = 0;
        ArtistMarqueeTransform.X = 0;
        DispatcherQueue.TryEnqueue(UpdateMarquees);
    }

    private void UpdateMarquees()
    {
        _titleMarqueeStoryboard = ConfigureMarqueeStoryboard(TitleText, TitleMarqueeTransform, _titleMarqueeStoryboard);
        _artistMarqueeStoryboard = ConfigureMarqueeStoryboard(ArtistText, ArtistMarqueeTransform, _artistMarqueeStoryboard);
    }

    private static Microsoft.UI.Xaml.Media.Animation.Storyboard? ConfigureMarqueeStoryboard(
        TextBlock text,
        TranslateTransform transform,
        Microsoft.UI.Xaml.Media.Animation.Storyboard? existing)
    {
        existing?.Stop();
        transform.X = 0;
        if (text.Parent is not ScrollViewer scroller)
        {
            return null;
        }
        var overflow = text.ActualWidth - scroller.ViewportWidth;
        if (overflow < 4)
        {
            return null;
        }
        const double HoldStartSeconds = 2.4;
        const double HoldEndSeconds = 1.8;
        const double PixelsPerSecond = 30.0;
        var scrollSeconds = overflow / PixelsPerSecond;
        var scrollEnd = HoldStartSeconds + scrollSeconds;
        var cycleEnd = scrollEnd + HoldEndSeconds;

        var frames = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            EnableDependentAnimation = true,
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever
        };
        frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.LinearDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.LinearDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(HoldStartSeconds)), Value = 0 });
        frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.LinearDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(scrollEnd)), Value = -overflow });
        frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.LinearDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycleEnd)), Value = -overflow });
        frames.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(cycleEnd * 1000 + 16)), Value = 0 });

        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(frames);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(frames, text);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(frames, "(TextBlock.RenderTransform).(TranslateTransform.X)");
        storyboard.Begin();
        return storyboard;
    }

    private void HookTimelinePointerEvents()
    {
        TimelineBar.PointerPressed += OnTimelinePointerPressed;
        TimelineBar.PointerMoved += OnTimelinePointerMoved;
        TimelineBar.PointerReleased += OnTimelinePointerReleased;
        TimelineBar.PointerCaptureLost += OnTimelinePointerCaptureLost;
    }

    private void HookSessionPanEvents()
    {
        SessionSwitchScroller.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSessionPanPointerPressed), true);
        SessionSwitchScroller.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnSessionPanPointerMoved), true);
        SessionSwitchScroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSessionPanPointerReleased), true);
        SessionSwitchScroller.PointerCaptureLost += OnSessionPanPointerCaptureLost;
    }

    private void OnSessionPanPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;
        }
        _sessionPanActive = true;
        _sessionPanPointerId = e.Pointer.PointerId;
        _sessionPanStartPos = e.GetCurrentPoint(SessionSwitchScroller).Position;
        _sessionPanStartOffset = SessionSwitchScroller.HorizontalOffset;
        _sessionPanMoved = false;
        _suppressSessionSwitchClick = false;
        SessionSwitchScroller.CapturePointer(e.Pointer);
    }

    private void OnSessionPanPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_sessionPanActive || e.Pointer.PointerId != _sessionPanPointerId)
        {
            return;
        }
        var position = e.GetCurrentPoint(SessionSwitchScroller).Position;
        var deltaX = position.X - _sessionPanStartPos.X;
        if (!_sessionPanMoved && Math.Abs(deltaX) > 6)
        {
            _sessionPanMoved = true;
            _suppressSessionSwitchClick = true;
        }
        if (_sessionPanMoved)
        {
            SessionSwitchScroller.ChangeView(_sessionPanStartOffset - deltaX, null, null, true);
        }
    }

    private void OnSessionPanPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _sessionPanPointerId)
        {
            return;
        }
        _sessionPanActive = false;
        SessionSwitchScroller.ReleasePointerCapture(e.Pointer);
    }

    private void OnSessionPanPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _sessionPanActive = false;
    }

    private void InitializeMediaClient()
    {
        try
        {
            _mediaClient = new MediaClient(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable."), OnUi, MediaLog.Write);
            _mediaClient.SessionsChanged += OnMediaSessionsChanged;
            _mediaClient.ConnectionChanged += OnMediaConnectionChanged;
            _mediaClient.Start();
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                TitleText.Text = "メディア情報を取得できません";
                ArtistText.Text = ex.Message;
            });
        }
    }

    private void OnUi(Action action)
    {
        if (_closed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                action();
            }
        });
    }

    private void OnWindowClosed()
    {
        _closed = true;
        _mediaGeneration++;
        DisposeLyrics();
        DisposePaletteRenderer();
        _timelineTimer?.Stop();
        if (_timelineTimer is not null)
        {
            _timelineTimer.Tick -= OnTimelineTick;
        }

        _titleMarqueeStoryboard?.Stop();
        _artistMarqueeStoryboard?.Stop();
        AppWindow.Changed -= OnAppWindowChanged;
        RootGrid.Loaded -= OnWindowContentLoaded;
        if (_windowXamlRoot is not null)
        {
            _windowXamlRoot.Changed -= OnWindowXamlRootChanged;
        }
        if (_mediaClient != null)
        {
            _mediaClient.SessionsChanged -= OnMediaSessionsChanged;
            _mediaClient.ConnectionChanged -= OnMediaConnectionChanged;
            _mediaClient.Dispose();
        }
        DetachSession();
    }

    private void OnMediaSessionsChanged(MediaClient sender, object args)
    {
        OnUi(RefreshSessions);
    }

    private void OnMediaConnectionChanged(MediaClient sender, object args)
    {
        if (sender.IsConnected)
        {
            RefreshSessions();
        }
        else
        {
            SetTransportEnabled(false);
            ArtistText.Text = "メディア情報を再接続しています…";
            UpdateTimelineDisplay();
        }
    }

    private void RefreshSessions()
    {
        if (_mediaClient is null)
        {
            return;
        }

        var sessions = _mediaClient.GetSessions();

        MediaSession? current = null;
        if (_currentSessionId != Guid.Empty)
        {
            current = sessions.FirstOrDefault(s => IsSameSession(s, _currentSessionId));
        }
        if (current is null && _selectedAumid is not null)
        {
            current = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _selectedAumid);
        }
        current ??= sessions.FirstOrDefault(s => s.Playback.PlaybackStatus == SessionPlaybackState.Playing);
        current ??= sessions.FirstOrDefault();

        UpdateSessionSwitchPanel(sessions);

        AttachSession(current);
    }

    private void AttachSession(MediaSession? session)
    {
        if (session is null)
        {
            DetachSession();
            ShowEmpty();
            return;
        }
        if (_session == session)
        {
            _selectedAumid = session.SourceAppUserModelId;
            _currentSessionId = session.Id;
            RefreshPlaybackInfo();
            HighlightSessionSwitchButtons();
            return;
        }
        DetachSession();
        _session = session;
        RefreshLyricsTrack();
        _selectedAumid = session.SourceAppUserModelId;
        _currentSessionId = session.Id;
        _mediaGeneration++;
        session.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnSessionTimelinePropertiesChanged;
        _ = RefreshMediaAsync();
        RefreshPlaybackInfo();
        RefreshTimeline();
        HighlightSessionSwitchButtons();
    }

    private void DetachSession()
    {
        _playbackTimeline.Clear();
        if (_session is null)
        {
            return;
        }
        _session.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnSessionTimelinePropertiesChanged;
        _session = null;
        _currentSessionId = Guid.Empty;
        _mediaGeneration++;
        _appVolume = null;
    }

    private static bool IsSameSession(MediaSession? session, MediaSession? other)
    {
        return session is not null && other is not null && session.Id == other.Id;
    }

    private static bool IsSameSession(MediaSession? session, Guid identity)
    {
        return session is not null && session.Id == identity;
    }

    private void OnSessionMediaPropertiesChanged(MediaSession sender, object args)
    {
        OnUi(() =>
        {
            if (IsSameSession(sender, _session))
            {
                _ = RefreshMediaAsync();
            }
        });
    }

    private void OnSessionPlaybackInfoChanged(MediaSession sender, object args)
    {
        OnUi(() =>
        {
            if (IsSameSession(sender, _session))
            {
                RefreshPlaybackInfo();
                UpdateTimelineDisplay();
            }
        });
    }

    private void OnSessionTimelinePropertiesChanged(MediaSession sender, object args)
    {
        OnUi(() =>
        {
            if (IsSameSession(sender, _session))
            {
                RefreshTimeline();
            }
        });
    }

    private void ShowEmpty()
    {
        ClearArtPalette();
        TitleText.Text = "再生中のメディアがありません";
        ArtistText.Text = "Spotifyなどのアプリで再生すると表示されます";
        AlbumArtImage.Source = null;
        AlbumArtImage.Visibility = Visibility.Collapsed;
        ArtPlaceholder.Visibility = Visibility.Visible;
        _timelineSeekEnabled = false;
        _timelineValue = 0;
        _timelineMaxSeconds = 0;
        TimelineBar.Opacity = 0.4;
        VolumeBar.Opacity = 0.4;
        PositionText.Text = "--:--";
        DurationText.Text = "--:--";
        PlayPauseIcon.Glyph = PlayGlyph;
        PreviousButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        RefreshLyricsTrack();
        ShowLyricsMessage("再生中の曲がありません", false, false);
    }

    private async Task RefreshMediaAsync()
    {
        var session = _session;
        var generation = ++_mediaGeneration;
        if (session is null)
        {
            ShowEmpty();
            return;
        }
        try
        {
            var props = session.Media;
            RefreshLyricsTrack();
            if (generation != _mediaGeneration)
            {
                return;
            }
            TitleText.Text = string.IsNullOrWhiteSpace(props.Title) ? "不明なタイトル" : props.Title;
            var artist = string.IsNullOrWhiteSpace(props.Artist)
                ? (string.IsNullOrWhiteSpace(props.AlbumArtist) ? "" : props.AlbumArtist)
                : props.Artist;
            var album = props.AlbumTitle?.Trim() ?? "";
            ArtistText.Text = string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(artist) || album.Contains(artist, StringComparison.OrdinalIgnoreCase)
                ? artist
                : $"{artist} — {album}";
            ResetMarquee();
            await UpdateAlbumArtAsync(props.Thumbnail, generation);
        }
        catch
        {
        }
    }

    private void UpdateSessionSwitchPanel(IReadOnlyList<MediaSession> sessions)
    {
        SessionSwitchPanel.Children.Clear();
        if (sessions.Count <= 1)
        {
            SessionSwitchPanel.Visibility = Visibility.Collapsed;
            SessionSwitchArea.Visibility = Visibility.Collapsed;
            return;
        }
        SessionSwitchPanel.Visibility = Visibility.Visible;
        SessionSwitchArea.Visibility = Visibility.Visible;
        foreach (var session in sessions)
        {
            var button = new Button
            {
                Width = 26,
                Height = 26,
                Tag = session,
                Style = (Style)Application.Current.Resources["GhostButton"],
                Foreground = new SolidColorBrush(_paletteForeground),
                FocusVisualPrimaryBrush = new SolidColorBrush(_paletteForeground)
            };
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, GetSessionDisplayLabel(session));
            var placeholder = new FontIcon
            {
                Glyph = "\uE8D6",
                FontSize = 12
            };
            button.Content = placeholder;
            button.Click += OnSessionSwitchClick;
            SessionSwitchPanel.Children.Add(button);
            _ = FillSessionButtonIconAsync(button, session);
        }
        HighlightSessionSwitchButtons(scrollIntoView: true);
        DispatcherQueue.TryEnqueue(UpdateSessionFades);
    }

    private void UpdateSessionFades()
    {
        try
        {
            if (_pigmentFlow is null || _accessibilitySettings.HighContrast)
            {
                SessionFadeLeft.Visibility = Visibility.Collapsed;
                SessionFadeRight.Visibility = Visibility.Collapsed;
                return;
            }
            var overflows = SessionSwitchScroller.ScrollableWidth > 2;
            var atStart = SessionSwitchScroller.HorizontalOffset <= 2;
            var atEnd = SessionSwitchScroller.HorizontalOffset >= SessionSwitchScroller.ScrollableWidth - 2;
            SessionFadeLeft.Visibility = overflows && !atStart ? Visibility.Visible : Visibility.Collapsed;
            SessionFadeRight.Visibility = overflows && !atEnd ? Visibility.Visible : Visibility.Collapsed;
            if (SessionFadeLeft.Visibility == Visibility.Visible)
            {
                UpdateEdgeFadeBrush(SessionFadeLeft, false);
            }
            if (SessionFadeRight.Visibility == Visibility.Visible)
            {
                UpdateEdgeFadeBrush(SessionFadeRight, true);
            }
        }
        catch
        {
        }
    }

    private void OnSessionSwitchViewChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateSessionFades);
    }

    private void HighlightSessionSwitchButtons(bool scrollIntoView = false)
    {
        Button? activeButton = null;
        foreach (var child in SessionSwitchPanel.Children.OfType<Button>())
        {
            var active = child.Tag is MediaSession session && IsSameSession(session, _session);
            if (active)
            {
                activeButton = child;
            }
            child.Opacity = active ? 1.0 : 0.55;
            child.BorderBrush = active ? new SolidColorBrush(_paletteForeground) : null;
            child.BorderThickness = active ? new Thickness(1) : new Thickness(0);
        }
        if (scrollIntoView && activeButton is not null)
        {
            DispatcherQueue.TryEnqueue(() => ScrollSessionButtonIntoView(activeButton));
        }
    }

    private void ScrollSessionButtonIntoView(Button button)
    {
        try
        {
            if (button.ActualWidth <= 0)
            {
                return;
            }
            var scroller = SessionSwitchScroller;
            var viewport = scroller.ViewportWidth;
            var bounds = button.TransformToVisual(scroller).TransformBounds(
                new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
            var offset = scroller.HorizontalOffset;
            const double MarginPx = 3.0;
            double target;
            if (bounds.Left < MarginPx)
            {
                target = offset + (bounds.Left - MarginPx);
            }
            else if (bounds.Right > viewport - MarginPx)
            {
                target = offset + (bounds.Right - (viewport - MarginPx));
            }
            else
            {
                return;
            }
            target = Math.Clamp(target, 0, scroller.ScrollableWidth);
            if (Math.Abs(target - offset) > 0.5)
            {
                scroller.ChangeView(target, null, null, false);
            }
        }
        catch
        {
        }
    }

    private void OnSessionSwitchClick(object sender, RoutedEventArgs e)
    {
        if (_suppressSessionSwitchClick)
        {
            _suppressSessionSwitchClick = false;
            return;
        }
        if (sender is not Button button || button.Tag is not MediaSession session)
        {
            return;
        }
        AttachSession(session);
        HighlightSessionSwitchButtons(scrollIntoView: true);
    }

    private void RefreshPlaybackInfo()
    {
        var session = _session;
        if (session is null || _mediaClient?.IsConnected != true)
        {
            SetTransportEnabled(false);
            return;
        }
        try
        {
            var info = session.Playback;
            var controls = info.Controls;
            var playing = info.PlaybackStatus == SessionPlaybackState.Playing;
            _playbackTimeline.SetPlayback(playing, info.PlaybackRate, DateTimeOffset.Now);
            PlayPauseIcon.Glyph = playing ? PauseGlyph : PlayGlyph;
            PlayPauseButton.IsEnabled = playing ? controls.IsPauseEnabled : controls.IsPlayEnabled;
            PreviousButton.IsEnabled = controls.IsPreviousEnabled;
            NextButton.IsEnabled = controls.IsNextEnabled;
            _timelineSeekEnabled = controls.IsPlaybackPositionEnabled;
            TimelineBar.Opacity = _timelineSeekEnabled ? 1.0 : 0.4;
            UpdateLyricsSeekAvailability();
        }
        catch
        {
            SetTransportEnabled(false);
        }
    }

    private void SetTransportEnabled(bool enabled)
    {
        _playbackTimeline.SetPlayback(false, null, DateTimeOffset.Now);
        PlayPauseIcon.Glyph = PlayGlyph;
        PlayPauseButton.IsEnabled = enabled;
        PreviousButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
        _timelineSeekEnabled = enabled;
        TimelineBar.Opacity = enabled ? 1.0 : 0.4;
        UpdateLyricsSeekAvailability();
    }

    private void OnTimelineTick(DispatcherQueueTimer sender, object args)
    {
        _frameClearCounter++;
        if (_frameClearCounter >= 4)
        {
            _frameClearCounter = 0;
            ClearFrameStyles();
            UpdateNonClientRegions();
        }
        UpdateSessionFades();
        UpdateTimelineDisplay();
        UpdateVolumeDisplay();
    }

    private void RefreshTimeline()
    {
        var timeline = _session?.Timeline;
        if (timeline is null)
        {
            _playbackTimeline.SetTimeline(TimeSpan.Zero, TimeSpan.Zero, default);
        }
        else
        {
            _playbackTimeline.SetTimeline(timeline.Position, timeline.EndTime, timeline.LastUpdatedTime);
        }
        RefreshLyricsTrack();
        UpdateTimelineDisplay();
        UpdateLyricsSeekAvailability();
    }

    private void UpdateTimelineDisplay()
    {
        if (_seeking && DateTimeOffset.Now - _seekingAt > TimeSpan.FromSeconds(4))
        {
            _seeking = false;
        }
        if (!_playbackTimeline.HasTimeline)
        {
            _timelineMaxSeconds = 0;
            _timelineValue = 0;
            UpdateTimelineBar();
            PositionText.Text = "--:--";
            DurationText.Text = "--:--";
            return;
        }
        var elapsed = _playbackTimeline.GetPosition(DateTimeOffset.Now);
        _timelineMaxSeconds = _playbackTimeline.EndTime.TotalSeconds;
        if (!_seeking)
        {
            _timelineValue = elapsed.TotalSeconds;
            UpdateTimelineBar();
        }
        PositionText.Text = FormatTime(elapsed);
        DurationText.Text = $"-{FormatTime(_playbackTimeline.EndTime - elapsed)}";
        UpdateLyricsPosition();
    }

    private void UpdateTimelineBar()
    {
        var trackWidth = Math.Max(0, TimelineBar.ActualWidth - 10);
        var ratio = _timelineMaxSeconds > 0 ? Math.Clamp(_timelineValue / _timelineMaxSeconds, 0, 1) : 0;
        TimelineFill.Width = ratio * trackWidth;
        TimelineThumbTransform.X = ratio * trackWidth;
    }

    private static string FormatTime(TimeSpan time)
    {
        if ((int)time.TotalHours > 0)
        {
            return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
        }
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private void OnTimelinePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelineSeekEnabled || _session is null)
        {
            return;
        }
        _seeking = true;
        _seekingAt = DateTimeOffset.Now;
        TimelineBar.CapturePointer(e.Pointer);
        UpdateTimelineFromPointer(e);
    }

    private void OnTimelinePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }
        UpdateTimelineFromPointer(e);
    }

    private void OnTimelinePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }
        UpdateTimelineFromPointer(e);
        _ = CompleteSeekAsync();
        TimelineBar.ReleasePointerCapture(e.Pointer);
    }

    private void OnTimelinePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }
        _ = CompleteSeekAsync();
    }

    private void UpdateTimelineFromPointer(PointerRoutedEventArgs e)
    {
        var trackWidth = Math.Max(1, TimelineBar.ActualWidth - 10);
        var x = Math.Clamp(e.GetCurrentPoint(TimelineBar).Position.X, 5, 5 + trackWidth);
        var ratio = (x - 5) / trackWidth;
        _timelineValue = ratio * _timelineMaxSeconds;
        UpdateTimelineBar();
        PositionText.Text = FormatTime(TimeSpan.FromSeconds(_timelineValue));
        var remaining = _timelineMaxSeconds - _timelineValue;
        if (remaining < 0)
        {
            remaining = 0;
        }
        DurationText.Text = $"-{FormatTime(TimeSpan.FromSeconds(remaining))}";
    }

    private async Task CompleteSeekAsync()
    {
        if (!_seeking)
        {
            return;
        }
        _seeking = false;
        var session = _session;
        if (session is null)
        {
            return;
        }
        try
        {
            await session.TryChangePlaybackPositionAsync((long)(_timelineValue * TimeSpan.TicksPerSecond));
        }
        catch
        {
        }
    }

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }
        try
        {
            var status = session.Playback.PlaybackStatus;
            if (status == SessionPlaybackState.Playing)
            {
                await session.TryPauseAsync();
            }
            else
            {
                await session.TryPlayAsync();
            }
        }
        catch
        {
        }
    }

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private async void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }
        try
        {
            await _session.TrySkipPreviousAsync();
        }
        catch
        {
        }
    }

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }
        try
        {
            await _session.TrySkipNextAsync();
        }
        catch
        {
        }
    }

    private static string GetSessionDisplayLabel(MediaSession session)
    {
        var source = session.SourceAppUserModelId;
        if (!string.IsNullOrEmpty(source))
        {
            try
            {
                var appInfo = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(source);
                var displayName = appInfo.DisplayInfo.DisplayName;
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return displayName;
                }
            }
            catch
            {
            }
            var bang = source.IndexOf('!');
            var segment = bang >= 0 ? source[..bang] : source;
            var underscore = segment.LastIndexOf('_');
            if (underscore > 0)
            {
                segment = segment[..underscore];
            }
            var dot = segment.LastIndexOf('.');
            if (dot > 0)
            {
                segment = segment[(dot + 1)..];
            }
            if (!string.IsNullOrWhiteSpace(segment))
            {
                return segment;
            }
        }
        return string.IsNullOrEmpty(source) ? "不明なアプリ" : source;
    }
}

