using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PlayerApp;

public sealed partial class MainWindow
{
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly byte[] _pigmentPixels = new byte[PigmentFlow.Width * PigmentFlow.Height * 4];
    private readonly Stopwatch _paletteClock = new();
    private DispatcherQueueTimer? _paletteTimer;
    private PigmentFlow? _pigmentFlow;
    private WriteableBitmap? _pigmentBitmap;
    private Stream? _pigmentStream;
    private AlbumPalette _currentPalette = AlbumPalette.Neutral;
    private Color _paletteForeground = AlbumPalette.Neutral.Foreground;

    private void InitializePaletteRenderer()
    {
        _pigmentBitmap = new WriteableBitmap(PigmentFlow.Width, PigmentFlow.Height);
        _pigmentStream = _pigmentBitmap.PixelBuffer.AsStream();
        _pigmentFlow = new PigmentFlow(_currentPalette);
        _paletteTimer = DispatcherQueue.CreateTimer();
        _paletteTimer.Interval = TimeSpan.FromSeconds(1.0 / 30);
        _paletteTimer.Tick += OnPaletteTick;
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
        RootGrid.Loaded += OnPaletteLoaded;
    }

    private void OnPaletteLoaded(object sender, RoutedEventArgs args) => UpdatePaletteAnimationState();
    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        => OnUi(() => ApplyPalette(_currentPalette));
    private void OnSystemColorsChanged(UISettings sender, object args)
        => OnUi(() => ApplyPalette(_currentPalette));

    private void UpdatePaletteAnimationState()
    {
        if (_paletteTimer is null || _closed)
        {
            return;
        }

        var minimized = AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized;
        if (_uiSettings.AnimationsEnabled && !_accessibilitySettings.HighContrast && RootGrid.IsLoaded && !minimized)
        {
            if (!_paletteTimer.IsRunning)
            {
                _paletteClock.Restart();
                _paletteTimer.Start();
            }
        }
        else
        {
            _paletteTimer.Stop();
            _paletteClock.Reset();
        }
    }

    private void OnPaletteTick(DispatcherQueueTimer sender, object args)
    {
        if (_closed)
        {
            return;
        }

        var dt = _paletteClock.Elapsed.TotalSeconds;
        _paletteClock.Restart();
        _pigmentFlow!.Advance(dt);
        RenderPigments();
        UpdateSessionFades();
    }

    private void RenderPigments()
    {
        _pigmentFlow!.Render(_pigmentPixels);
        _pigmentStream!.Position = 0;
        _pigmentStream.Write(_pigmentPixels, 0, _pigmentPixels.Length);
        _pigmentBitmap!.Invalidate();
    }

    private void ApplyPalette(AlbumPalette palette)
    {
        if (_closed)
        {
            return;
        }

        _currentPalette = palette;
        var animate = _uiSettings.AnimationsEnabled;
        _pigmentFlow!.SetPalette(palette, !animate);
        if (_accessibilitySettings.HighContrast)
        {
            ApplyHighContrastPalette();
        }
        else
        {
            RootGrid.RequestedTheme = ElementTheme.Dark;
            RootGrid.Background = new ImageBrush { ImageSource = _pigmentBitmap, Stretch = Stretch.Fill };
            RenderPigments();
            ApplyControlColors(palette.Foreground, palette.Secondary, palette.Accent,
                palette.OnAccent, palette.Track, palette.Hover);
        }
        UpdatePaletteAnimationState();
        UpdateSessionFades();
    }

    private void ApplyControlColors(Color foreground, Color secondary, Color accent, Color onAccent, Color track, Color hover)
    {
        _paletteForeground = foreground;
        var foregroundBrush = new SolidColorBrush(foreground);
        var secondaryBrush = new SolidColorBrush(secondary);
        var accentBrush = new SolidColorBrush(accent);
        var hoverBrush = new SolidColorBrush(hover);
        ((SolidColorBrush)Application.Current.Resources["PlayerHoverBrush"]).Color = hover;
        TitleText.Foreground = foregroundBrush;
        ArtistText.Foreground = secondaryBrush;
        PositionText.Foreground = secondaryBrush;
        DurationText.Foreground = secondaryBrush;
        VolumeText.Foreground = secondaryBrush;
        VolumeIcon.Foreground = secondaryBrush;
        ArtPlaceholder.Foreground = secondaryBrush;
        foreach (var button in new[] { PreviousButton, PlayPauseButton, NextButton, MinimizeButton, CloseButton }
            .Concat(SessionSwitchPanel.Children.OfType<Button>()))
        {
            button.Foreground = foregroundBrush;
            button.BorderBrush = hoverBrush;
            button.FocusVisualPrimaryBrush = foregroundBrush;
            button.FocusVisualSecondaryBrush = new SolidColorBrush(onAccent);
        }
        PlayPauseButton.Background = accentBrush;
        PlayPauseButton.Foreground = new SolidColorBrush(onAccent);
        PlayPauseIcon.Foreground = PlayPauseButton.Foreground;
        TimelineTrack.Background = new SolidColorBrush(track);
        VolumeTrack.Background = TimelineTrack.Background;
        TimelineFill.Background = accentBrush;
        VolumeFill.Background = accentBrush;
        TimelineThumb.Fill = accentBrush;
        VolumeThumb.Fill = accentBrush;
        TimelineThumb.Stroke = foregroundBrush;
        VolumeThumb.Stroke = foregroundBrush;
        HighlightSessionSwitchButtons();
    }

    private void ApplyHighContrastPalette()
    {
        RootGrid.RequestedTheme = ElementTheme.Default;
        var background = _uiSettings.GetColorValue(UIColorType.Background);
        var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
        var accent = _uiSettings.UIElementColor(UIElementType.Highlight);
        var onAccent = _uiSettings.UIElementColor(UIElementType.HighlightText);
        RootGrid.Background = new SolidColorBrush(background);
        ApplyControlColors(foreground, foreground, accent, onAccent, background,
            Color.FromArgb(64, foreground.R, foreground.G, foreground.B));
    }

    private void UpdateEdgeFadeBrush(Border edge, bool right)
    {
        // Sample the live pigment field at each scroller edge rather than a stale gradient.
        var bounds = SessionSwitchArea.TransformToVisual(RootGrid).TransformBounds(
            new Windows.Foundation.Rect(0, 0, SessionSwitchArea.ActualWidth, SessionSwitchArea.ActualHeight));
        var x = (right ? bounds.Right : bounds.Left) / Math.Max(1, RootGrid.ActualWidth);
        var y = (bounds.Top + bounds.Height / 2) / Math.Max(1, RootGrid.ActualHeight);
        var color = _pigmentFlow!.Sample(x, y);
        var transparent = Color.FromArgb(0, color.R, color.G, color.B);
        if (edge.Background is not LinearGradientBrush brush)
        {
            brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            brush.GradientStops.Add(new GradientStop { Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Offset = 1 });
            edge.Background = brush;
        }
        brush.GradientStops[0].Color = right ? transparent : color;
        brush.GradientStops[1].Color = right ? color : transparent;
    }

    private void ClearArtPalette() => ApplyPalette(AlbumPalette.Neutral);

    private void DisposePaletteRenderer()
    {
        _paletteTimer?.Stop();
        if (_paletteTimer is not null)
        {
            _paletteTimer.Tick -= OnPaletteTick;
        }

        _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
        RootGrid.Loaded -= OnPaletteLoaded;
        _pigmentStream?.Dispose();
    }
}
