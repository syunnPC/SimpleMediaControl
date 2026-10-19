using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PlayerApp;

internal sealed record AudioOutput(string Id, string Name, int FormFactor, Guid ContainerId);
internal sealed record AudioMixerState(bool Available, double Percent, bool Muted, AudioOutput[] Outputs);

// All Core Audio references are owned and used on the UI thread. Re-enumerating discovers
// new browser streams and application-specific routing, including nondefault endpoints.
internal sealed class AudioMixerController : IDisposable
{
    private static readonly PropertyKey ContainerKey = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<MMDevice> _devices = [];
    private readonly List<AudioSessionControl> _sessions = [];
    private MMDevice? _systemDevice;
    private string? _source;
    private bool _initialized;
    private DateTimeOffset _lastRefresh;
    private AudioMixerState _state = new(false, 0, false, []);

    public AudioMixerState Read(string? source, bool force = false)
    {
        if (force || !_initialized || source != _source || DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(1))
        {
            Refresh(source);
        }
        try
        {
            if (_systemDevice is not null)
            {
                var volume = _systemDevice.AudioEndpointVolume;
                return _state with { Percent = volume.MasterVolumeLevelScalar * 100, Muted = volume.Mute };
            }
            if (_sessions.Count > 0)
            {
                var active = _sessions.Where(s => s.State == AudioSessionState.AudioSessionStateActive).ToArray();
                return _state with
                {
                    Percent = (active.Length > 0 ? active : _sessions.ToArray()).Max(s => s.SimpleAudioVolume.Volume) * 100,
                    Muted = _sessions.All(s => s.SimpleAudioVolume.Mute)
                };
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine(ex);
            _initialized = false;
        }
        return _state with { Available = false };
    }

    public void SetVolume(string? source, double percent)
    {
        if (!Read(source).Available)
        {
            return;
        }

        var volume = (float)Math.Clamp(percent / 100, 0, 1);
        try
        {
            if (_systemDevice is not null)
            {
                _systemDevice.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
            }

            foreach (var session in _sessions)
            {
                session.SimpleAudioVolume.Volume = volume;
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine(ex);
            _initialized = false;
        }
    }

    public void ToggleMute(string? source)
    {
        var state = Read(source, true);
        if (!state.Available)
        {
            return;
        }

        try
        {
            if (_systemDevice is not null)
            {
                _systemDevice.AudioEndpointVolume.Mute = !state.Muted;
            }

            foreach (var session in _sessions)
            {
                session.SimpleAudioVolume.Mute = !state.Muted;
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine(ex);
            _initialized = false;
        }
    }

    private void Refresh(string? source)
    {
        Clear();
        _source = source;
        _initialized = true;
        _lastRefresh = DateTimeOffset.UtcNow;
        var outputs = new List<AudioOutput>();
        var activeOutputs = new List<AudioOutput>();
        _state = new(false, 0, false, []);
        try
        {
            if (source is null)
            {
                _systemDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _devices.Add(_systemDevice);
                outputs.Add(Describe(_systemDevice));
            }
            else
            {
                var identities = new Dictionary<uint, AudioIdentity>();
                foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var matched = false;
                    var active = false;
                    try
                    {
                        var manager = device.AudioSessionManager;
                        manager.RefreshSessions();
                        var sessions = manager.Sessions;
                        for (var i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            var keep = false;
                            try
                            {
                                if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired)
                                {
                                    continue;
                                }

                                var pid = session.GetProcessID;
                                if (!identities.TryGetValue(pid, out var identity))
                                {
                                    identities[pid] = identity = AudioIdentity.Read(pid);
                                }
                                if (!identity.MatchesAudioSource(source))
                                {
                                    continue;
                                }

                                _sessions.Add(session);
                                keep = matched = true;
                                active |= session.State == AudioSessionState.AudioSessionStateActive;
                            }
                            finally
                            {
                                if (!keep)
                                {
                                    session.Dispose();
                                }
                            }
                        }
                        if (matched)
                        {
                            var output = Describe(device);
                            outputs.Add(output);
                            if (active)
                            {
                                activeOutputs.Add(output);
                            }
                        }
                    }
                    catch (COMException ex)
                    {
                        Debug.WriteLine(ex);
                    }
                    finally
                    {
                        if (matched)
                        {
                            _devices.Add(device);
                        }
                        else
                        {
                            device.Dispose();
                        }
                    }
                }
            }
            _state = new(_systemDevice is not null || _sessions.Count > 0, 0, false,
                (activeOutputs.Count > 0 ? activeOutputs : outputs).ToArray());
        }
        catch (COMException ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static AudioOutput Describe(MMDevice device)
    {
        var key = PropertyKeys.PKEY_AudioEndpoint_FormFactor;
        var form = device.Properties.Contains(key) ? Convert.ToInt32(device.Properties[key].Value) : 10;
        // Audio endpoints publish their container directly. The instance-ID
        // property is absent on these endpoints, so MMDevice.InstanceId is not
        // a usable PnP lookup key.
        var container = device.Properties.Contains(ContainerKey) && device.Properties[ContainerKey].Value is Guid id
            ? id : Guid.Empty;
        return new(device.ID, device.FriendlyName, form, container);
    }

    private void Clear()
    {
        foreach (var session in _sessions)
        {
            try { session.Dispose(); }
            catch (COMException ex) { Debug.WriteLine(ex); }
        }

        _sessions.Clear();
        foreach (var device in _devices)
        {
            try { device.Dispose(); }
            catch (COMException ex) { Debug.WriteLine(ex); }
        }

        _devices.Clear();
        _systemDevice = null;
    }

    public void Dispose()
    {
        Clear();
        _enumerator.Dispose();
    }
}
