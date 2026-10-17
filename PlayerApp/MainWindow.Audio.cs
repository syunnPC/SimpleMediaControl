using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml.Input;
using NAudio.CoreAudioApi;

namespace PlayerApp;

public sealed partial class MainWindow
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _audioDevice;
    private AudioSessionManager? _audioSessionManager;
    private AudioEndpointVolume? _masterVolume;
    private SimpleAudioVolume? _appVolume;
    private List<SimpleAudioVolume>? _appSessionControls;
    private bool _volumeSeeking;
    private DateTimeOffset _volumeSeekingAt;
    private DateTimeOffset _lastVolumeWrite;
    private double _volumePercent = 100;

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
            _deviceEnumerator = new MMDeviceEnumerator();
            _audioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _masterVolume = _audioDevice.AudioEndpointVolume;
            _audioSessionManager = _audioDevice.AudioSessionManager;
        }
        catch
        {
            _deviceEnumerator = null;
            _audioDevice = null;
            _masterVolume = null;
            _audioSessionManager = null;
        }
    }

    private void OnVolumePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _volumeSeeking = true;
        _volumeSeekingAt = DateTimeOffset.Now;
        VolumeBar.CapturePointer(e.Pointer);
        UpdateVolumeFromPointer(e);
    }

    private void OnVolumePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_volumeSeeking)
        {
            return;
        }
        UpdateVolumeFromPointer(e);
    }

    private void OnVolumePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteVolumeSet();
        VolumeBar.ReleasePointerCapture(e.Pointer);
    }

    private void OnVolumePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CompleteVolumeSet();
    }

    private void UpdateVolumeFromPointer(PointerRoutedEventArgs e)
    {
        var trackWidth = Math.Max(1, VolumeBar.ActualWidth - 8);
        var x = Math.Clamp(e.GetCurrentPoint(VolumeBar).Position.X, 4, 4 + trackWidth);
        var ratio = (x - 4) / trackWidth;
        _volumePercent = ratio * 100.0;
        UpdateVolumeBar();
        VolumeText.Text = $"{Math.Round(_volumePercent)}%";
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

    private void ApplyVolume(double raw)
    {
        var value = (float)Math.Clamp(raw, 0, 100) / 100f;
        var appVolumes = _appSessionControls;
        try
        {
            if (appVolumes is not null && appVolumes.Count > 0)
            {
                foreach (var volume in appVolumes)
                {
                    volume.Volume = value;
                }
            }
            else if (_masterVolume is not null)
            {
                _masterVolume.MasterVolumeLevelScalar = value;
            }
        }
        catch
        {
        }
    }

    private void UpdateVolumeDisplay(bool force = false)
    {
        if (_volumeSeeking && DateTimeOffset.Now - _volumeSeekingAt > TimeSpan.FromSeconds(4))
        {
            _volumeSeeking = false;
        }
        if (_volumeSeeking && !force)
        {
            return;
        }

        var target = _appVolume;
        if (target is null)
        {
            RebindAppVolume();
            target = _appVolume;
        }

        double? percent = null;
        if (target is not null)
        {
            try
            {
                percent = target.Volume * 100.0;
            }
            catch
            {
                _appVolume = null;
                percent = null;
            }
        }

        if (percent is null && _masterVolume is not null)
        {
            try
            {
                percent = _masterVolume.MasterVolumeLevelScalar * 100.0;
            }
            catch
            {
                percent = null;
            }
        }

        if (percent is double p)
        {
            var clamped = Math.Clamp(p, 0, 100);
            _volumePercent = clamped;
            VolumeBar.Opacity = 1.0;
            UpdateVolumeBar();
            VolumeText.Text = $"{Math.Round(clamped)}%";
        }
        else
        {
            VolumeBar.Opacity = 0.4;
            VolumeText.Text = "--";
        }
    }

    private void UpdateVolumeBar()
    {
        var trackWidth = Math.Max(0, VolumeBar.ActualWidth - 8);
        var ratio = Math.Clamp(_volumePercent, 0, 100) / 100.0;
        VolumeFill.Width = ratio * trackWidth;
        VolumeThumbTransform.X = ratio * trackWidth;
    }

    private void RebindAppVolume()
    {
        _appVolume = null;
        _appSessionControls?.Clear();
        var target = _session;
        var manager = _audioSessionManager;
        if (target is null || manager is null)
        {
            return;
        }
        var aumid = target.SourceAppUserModelId;
        if (string.IsNullOrEmpty(aumid))
        {
            return;
        }
        var candidates = BuildProcessNameCandidates(aumid);
        var packageTokens = BuildPackagePathTokens(aumid);
        SessionCollection sessions;
        try
        {
            sessions = manager.Sessions;
        }
        catch
        {
            return;
        }
        for (var i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl audioSession;
            try
            {
                audioSession = sessions[i];
                if (audioSession.IsSystemSoundsSession)
                {
                    continue;
                }

                var procName = "";
                var pid = (int)audioSession.GetProcessID;
                if (pid > 0)
                {
                    try
                    {
                        procName = Process.GetProcessById(pid).ProcessName?.ToLowerInvariant() ?? "";
                    }
                    catch
                    {
                        procName = "";
                    }
                }

                var pathMatch = false;
                var identifier = audioSession.GetSessionIdentifier ?? "";
                var bar = identifier.IndexOf('|');
                if (bar >= 0)
                {
                    var next = identifier.IndexOf('%', bar + 1);
                    var path = (next > bar ? identifier[(bar + 1)..next] : identifier[(bar + 1)..]).ToLowerInvariant();
                    if (packageTokens is not null && IsPathInPackage(path, packageTokens))
                    {
                        pathMatch = true;
                    }
                    if (!pathMatch)
                    {
                        var slash = path.LastIndexOf('\\');
                        var fileName = slash >= 0 ? path[(slash + 1)..] : path;
                        var dot = fileName.LastIndexOf('.');
                        var stem = dot > 0 ? fileName[..dot] : fileName;
                        if (stem.Length > 0 && candidates.Contains(stem))
                        {
                            pathMatch = true;
                        }
                    }
                }

                var procMatch = procName.Length > 0 && candidates.Contains(procName);
                if (procMatch || pathMatch)
                {
                    var volume = audioSession.SimpleAudioVolume;
                    _appVolume ??= volume;
                    _appSessionControls ??= [];
                    _appSessionControls.Add(volume);
                }
            }
            catch
            {
                continue;
            }
        }
    }

    private static List<string>? BuildPackagePathTokens(string aumid)
    {
        var bang = aumid.IndexOf('!');
        if (bang <= 0)
        {
            return null;
        }
        var family = aumid[..bang];
        var tokens = new List<string>();
        try
        {
            var packages = new Windows.Management.Deployment.PackageManager();
            var package = packages.FindPackageForUser(string.Empty, family);
            var fullName = package?.Id?.FullName;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                tokens.Add(fullName.ToLowerInvariant() + "\\");
            }
        }
        catch
        {
        }

        if (tokens.Count == 0)
        {
            var underscore = family.LastIndexOf('_');
            if (underscore > 0)
            {
                tokens.Add(family[..underscore].ToLowerInvariant() + "_");
            }
        }
        return tokens.Count > 0 ? tokens : null;
    }

    private static bool IsPathInPackage(string path, List<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (path.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static HashSet<string> BuildProcessNameCandidates(string aumid)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            var v = value.Trim().ToLowerInvariant();
            if (v.Length > 0)
            {
                set.Add(v);
            }
        }
        var bang = aumid.IndexOf('!');
        var pfn = bang > 0 ? aumid[..bang] : aumid;
        if (bang > 0)
        {
            Add(aumid[(bang + 1)..]);
        }
        var underscore = pfn.LastIndexOf('_');
        if (underscore > 0)
        {
            Add(pfn[..underscore]);
        }
        var firstDot = pfn.IndexOf('.');
        if (firstDot > 0)
        {
            Add(pfn[..firstDot]);
        }
        var lastDot = pfn.LastIndexOf('.');
        if (lastDot > 0)
        {
            Add(pfn[(lastDot + 1)..]);
        }
        Add(pfn);
        return set;
    }

}
