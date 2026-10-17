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
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    private nint _mainHwnd;
    private int _frameClearCounter;

    private void ConfigureWindow()
    {
        Title = "PlayerApp";
        try
        {
            var appWindow = AppWindow;
            appWindow.Resize(new SizeInt32(430, 142));
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(DragZone);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = true;
            }
            _mainHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindow.Id);
            ClearFrameStyles();
            SetDwmAttributes();
        }
        catch
        {
        }
    }

    private void SetDwmAttributes()
    {
        try
        {
            var disabled = 2;
            _ = DwmSetWindowAttribute(_mainHwnd, 2, ref disabled, sizeof(int));
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
