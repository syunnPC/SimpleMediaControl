using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace PlayerApp;

public sealed partial class MainWindow
{
    private AudioMixerController? _audioMixer;
    private bool _volumeSeeking;
    private uint _volumePointerId;
    private DateTimeOffset _lastVolumeWrite;
    private double _volumePercent = 100;
    private Guid _batteryContainer;
    private DateTimeOffset _lastBatteryRead;
    private bool _batteryReading;
    private int? _deviceBattery;
    private int _batteryGeneration;

    private void HookVolumePointerEvents()
    {
        VolumeBar.PointerPressed += OnVolumePointerPressed;
        VolumeBar.PointerMoved += OnVolumePointerMoved;
        VolumeBar.PointerReleased += OnVolumePointerReleased;
        VolumeBar.PointerCaptureLost += OnVolumePointerCaptureLost;
    }

    private void InitializeAudioVolumes()
    {
        try
        {
            _audioMixer = new AudioMixerController();
        }
        catch (COMException ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnVolumePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_volumeSeeking || !VolumeBar.CapturePointer(e.Pointer))
        {
            return;
        }
        _volumeSeeking = true;
        _volumePointerId = e.Pointer.PointerId;
        UpdateVolumeFromPointer(e);
    }

    private void OnVolumePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_volumeSeeking || e.Pointer.PointerId != _volumePointerId)
        {
            return;
        }
        UpdateVolumeFromPointer(e);
    }

    private void OnVolumePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_volumeSeeking || e.Pointer.PointerId != _volumePointerId)
        {
            return;
        }
        UpdateVolumeFromPointer(e);
        CompleteVolumeSet();
        VolumeBar.ReleasePointerCapture(e.Pointer);
    }

    private void OnVolumePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_volumeSeeking || e.Pointer.PointerId != _volumePointerId)
        {
            return;
        }
        CompleteVolumeSet();
    }

    private void CancelVolumeSet()
    {
        _volumeSeeking = false;
        VolumeBar.ReleasePointerCaptures();
    }

    private void UpdateVolumeFromPointer(PointerRoutedEventArgs e)
    {
        var trackWidth = Math.Max(1, VolumeBar.ActualWidth - 8);
        var x = Math.Clamp(e.GetCurrentPoint(VolumeBar).Position.X, 4, 4 + trackWidth);
        var ratio = (x - 4) / trackWidth;
        _volumePercent = ratio * 100.0;
        UpdateVolumeBar();
        VolumeText.Text = UiText.Format(UiText.VolumePercent, _volumePercent);
        var now = DateTimeOffset.Now;
        if ((now - _lastVolumeWrite).TotalMilliseconds >= 80)
        {
            _lastVolumeWrite = now;
            ApplyVolume(_volumePercent);
        }
    }

    private void CompleteVolumeSet()
    {
        if (!_volumeSeeking)
        {
            return;
        }
        _volumeSeeking = false;
        ApplyVolume(_volumePercent);
    }

    private void ApplyVolume(double raw) => _audioMixer?.SetVolume(_session?.SourceAppUserModelId, raw);

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Click handler referenced by MainWindow.xaml.")]
    private void OnVolumeDeviceClick(object sender, RoutedEventArgs e)
    {
        _audioMixer?.ToggleMute(_session?.SourceAppUserModelId);
        UpdateVolumeDisplay(true);
    }

    private void UpdateVolumeDisplay(bool force = false)
    {
        var state = _audioMixer?.Read(_session?.SourceAppUserModelId, force)
            ?? new AudioMixerState(false, 0, false, []);
        VolumeBar.IsHitTestVisible = state.Available;
        VolumeDeviceButton.IsEnabled = state.Available;
        VolumeBar.Opacity = state.Available ? 1 : 0.4;
        VolumeMuteBadge.Visibility = state.Muted ? Visibility.Visible : Visibility.Collapsed;
        VolumeIcon.Opacity = state.Muted ? 0.5 : 1;
        var forms = state.Outputs.Select(o => o.FormFactor).Distinct().ToArray();
        VolumeIcon.Glyph = AudioDevicePresentation.Glyph(forms.Length == 1 ? forms[0] : 10);
        UpdateDeviceBattery(state.Outputs);
        DeviceBatteryText.Text = _deviceBattery is { } battery ? UiText.Format(UiText.VolumePercent, battery) : "";
        DeviceBatteryText.Visibility = _deviceBattery.HasValue ? Visibility.Visible : Visibility.Collapsed;
        var scope = _session is null ? UiText.SystemVolume : UiText.AppVolume;
        var action = state.Muted ? UiText.Unmute : UiText.Mute;
        var description = state.Available
            ? string.Join("\n", state.Outputs.Select(o => o.Name).Distinct())
            : UiText.AudioSessionUnavailable;
        if (_deviceBattery is { } percent)
        {
            description += "\n" + UiText.Format(UiText.BatteryDescription, percent);
        }

        // Retain the tooltip instance across timer ticks so hover opening is not reset.
        if (!string.Equals(VolumeDeviceToolTip.Content as string, description, StringComparison.Ordinal))
        {
            VolumeDeviceToolTip.Content = description;
        }
        AutomationProperties.SetName(VolumeDeviceButton, UiText.Format(UiText.AudioActionName, action, description));
        ToolTipService.SetToolTip(VolumeBar, scope);

        if (!state.Available)
        {
            CancelVolumeSet();
            VolumeText.Text = "--";
            return;
        }
        if (_volumeSeeking && !force)
        {
            return;
        }

        _volumePercent = Math.Clamp(state.Percent, 0, 100);
        UpdateVolumeBar();
        VolumeText.Text = UiText.Format(UiText.VolumePercent, _volumePercent);
    }

    private void UpdateDeviceBattery(AudioOutput[] outputs)
    {
        var containers = outputs.Select(o => o.ContainerId).Distinct().ToArray();
        var container = containers.Length == 1 && AudioDeviceBattery.IsPeripheral(containers[0]) ? containers[0] : Guid.Empty;
        if (container != _batteryContainer)
        {
            _batteryContainer = container;
            _deviceBattery = null;
            _batteryGeneration++;
            _batteryReading = false;
            _lastBatteryRead = default;
        }
        if (container == Guid.Empty || _batteryReading || DateTimeOffset.UtcNow - _lastBatteryRead < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _batteryReading = true;
        _lastBatteryRead = DateTimeOffset.UtcNow;
        _ = ReadDeviceBatteryAsync(container, _batteryGeneration);
    }

    private async Task ReadDeviceBatteryAsync(Guid container, int generation)
    {
        var percent = await Task.Run(() => AudioDeviceBattery.ReadPercentage(container));
        if (_closed || generation != _batteryGeneration)
        {
            return;
        }

        _batteryReading = false;
        _deviceBattery = percent;
        UpdateVolumeDisplay();
    }

    private void UpdateVolumeBar()
    {
        var trackWidth = Math.Max(0, VolumeBar.ActualWidth - 8);
        var ratio = Math.Clamp(_volumePercent, 0, 100) / 100.0;
        VolumeFill.Width = ratio * trackWidth;
        VolumeThumbTransform.X = ratio * trackWidth;
    }

}
