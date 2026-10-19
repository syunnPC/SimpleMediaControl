using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace PlayerApp;

public sealed partial class MainWindow
{
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const double WindowWidth = 430;
    private const double CompactWindowHeight = 142;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private nint _mainHwnd;
    private XamlRoot? _windowXamlRoot;
    private double _windowRasterizationScale;
    private int _frameClearCounter;
    private bool _isPinned;

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
            _isPinned = presenter.IsAlwaysOnTop;
            var label = _isPinned ? "最前面の固定を解除" : "最前面に固定";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PinButton, label);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(PinButton, label);
            UpdateAuxiliaryButtonColors();
        }
    }

    private void ConfigureWindow()
    {
        Title = "PlayerApp";
        try
        {
            var appWindow = AppWindow;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = true;
                // Window.ExtendsContentIntoTitleBar reserves a one-pixel strip above XAML.
                // Hide the native chrome directly; UpdateNonClientRegions supplies dragging.
                presenter.SetBorderAndTitleBar(false, false);
            }
            _mainHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindow.Id);
            ClearFrameStyles();
            SetDwmAttributes();
            UpdateWindowSize();
        }
        catch
        {
        }
    }

    private void OnWindowContentLoaded(object sender, RoutedEventArgs args)
    {
        if (_windowXamlRoot is null)
        {
            _windowXamlRoot = RootGrid.XamlRoot;
            _windowXamlRoot.Changed += OnWindowXamlRootChanged;
        }
        UpdateWindowSize();
        OnUi(UpdateNonClientRegions);
    }

    private void OnWindowXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _windowRasterizationScale)
        {
            // Wait for WinUI to finish applying the monitor's DPI change.
            OnUi(() =>
            {
                UpdateWindowSize();
                UpdateNonClientRegions();
            });
        }
    }

    private void UpdateWindowSize()
    {
        // XAML dimensions are logical pixels; AppWindow dimensions are physical pixels.
        // XamlRoot is only available after loading, so use the HWND's DPI at startup.
        var scale = _windowXamlRoot?.RasterizationScale ?? GetDpiForWindow(_mainHwnd) / 96.0;
        if (scale <= 0)
        {
            return;
        }
        _windowRasterizationScale = scale;
        var height = CompactWindowHeight + (_lyricsVisible ? LyricsPanel.Height + LyricsPanel.Margin.Top : 0);
        var size = new SizeInt32((int)Math.Ceiling(WindowWidth * scale), (int)Math.Ceiling(height * scale));
        var appWindow = AppWindow;
        if (appWindow.ClientSize.Width != size.Width || appWindow.ClientSize.Height != size.Height)
        {
            // Size the content area after removing the native frame, rather than the outer window.
            appWindow.ResizeClient(size);
        }
        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var top = Math.Max(workArea.Y, Math.Min(appWindow.Position.Y, workArea.Y + workArea.Height - appWindow.Size.Height));
        if (top != appWindow.Position.Y)
        {
            appWindow.Move(new PointInt32(appWindow.Position.X, top));
        }
    }

    private void SetDwmAttributes()
    {
        try
        {
            var disabled = DWMNCRP_DISABLED;
            _ = DwmSetWindowAttribute(_mainHwnd, DWMWA_NCRENDERING_POLICY, ref disabled, sizeof(int));
            var none = DWMWA_COLOR_NONE;
            _ = DwmSetWindowAttribute(_mainHwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            var rounded = 2;
            _ = DwmSetWindowAttribute(_mainHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
        }
        catch
        {
        }
    }

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint hWndInsertAfter, int x, int y, int width, int height, uint flags);

    private void ClearFrameStyles()
    {
        try
        {
            if (_mainHwnd == 0)
            {
                return;
            }
            var style = GetWindowLong(_mainHwnd, GWL_STYLE);
            var cleared = style & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            if (cleared != style)
            {
                _ = SetWindowLong(_mainHwnd, GWL_STYLE, cleared);
                const uint SWP_FRAMECHANGED = 0x0020;
                const uint SWP_NOMOVE = 0x0002;
                const uint SWP_NOSIZE = 0x0001;
                const uint SWP_NOZORDER = 0x0004;
                _ = SetWindowPos(_mainHwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
        }
        catch
        {
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdatePaletteAnimationState();
        UpdateLyricsTimerState();
        if (args.DidSizeChange || args.DidPositionChange)
        {
            OnUi(UpdateNonClientRegions);
        }
    }

    private Microsoft.UI.Input.InputNonClientPointerSource? _nonClientSource;

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
        }
        catch
        {
        }
    }

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Close();
        }
        catch
        {
        }
    }

    private void UpdateNonClientRegions()
    {
        try
        {
            _nonClientSource ??= Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            if (Content?.XamlRoot is not { } xamlRoot || RootGrid.ActualWidth <= 0 || CaptionButtonPanel.ActualWidth <= 0)
            {
                return;
            }
            var scale = xamlRoot.RasterizationScale;
            if (scale <= 0)
            {
                return;
            }
            var headerHeight = (int)Math.Ceiling(68 * scale);
            var clientWidth = (int)Math.Ceiling(RootGrid.ActualWidth * scale);
            var bounds = CaptionButtonPanel.TransformToVisual(RootGrid).TransformBounds(
                new Windows.Foundation.Rect(0, 0, CaptionButtonPanel.ActualWidth, CaptionButtonPanel.ActualHeight));
            var buttonX = (int)Math.Floor(bounds.X * scale) - 3;
            var buttonY = (int)Math.Floor(bounds.Y * scale) - 3;
            var buttonWidth = (int)Math.Ceiling(bounds.Width * scale) + 6;
            var buttonHeight = (int)Math.Ceiling(bounds.Height * scale) + 6;
            buttonX = Math.Max(0, Math.Min(clientWidth, buttonX));
            var rects = new List<Windows.Graphics.RectInt32>();
            if (buttonX > 0)
            {
                rects.Add(new Windows.Graphics.RectInt32(0, 0, buttonX, headerHeight));
            }
            var belowY = buttonY + buttonHeight;
            if (belowY >= 0 && belowY < headerHeight)
            {
                rects.Add(new Windows.Graphics.RectInt32(buttonX, belowY, Math.Max(1, clientWidth - buttonX), headerHeight - belowY));
            }
            if (rects.Count == 0)
            {
                return;
            }
            _nonClientSource.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, [.. rects]);
        }
        catch
        {
        }
    }
}
