using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using PlayerApp;

if (args.Contains("--inspect"))
{
    var media = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    var sources = media.GetSessions().Select(s => s.SourceAppUserModelId).Distinct().ToArray();
    Console.WriteLine($"Media sources: {string.Join(", ", sources)}");
    using var enumerator = new MMDeviceEnumerator();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        using (device)
        {
            Console.WriteLine($"Output: {device.FriendlyName}; form: {device.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value}");
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                var identity = AudioIdentity.Read(session.GetProcessID);
                Console.WriteLine($"  PID={session.GetProcessID}; state={session.State}; exe={System.IO.Path.GetFileName(identity.Executable)}; appId={identity.ApplicationId}; family={identity.PackageFamily}; match={string.Join(",", sources.Where(identity.MatchesAudioSource))}");
            }
        }
    }
    var audioThread = new Thread(() =>
    {
        // Match the WinUI thread's apartment without opening or operating UI.
        using var mixer = new AudioMixerController();
        foreach (var source in sources)
        {
            var state = mixer.Read(source, true);
            Console.WriteLine($"STA mixer {source}: available={state.Available}; volume={state.Percent}; outputs={string.Join(",", state.Outputs.Select(o => $"{o.Name} ({o.FormFactor})"))}");
            foreach (var output in state.Outputs)
            {
                Console.WriteLine($"  Glyph={AudioDevicePresentation.Glyph(output.FormFactor)}; container={output.ContainerId}; battery={AudioDeviceBattery.ReadPercentage(output.ContainerId)?.ToString() ?? "unavailable"}");
            }
        }
    });
    audioThread.SetApartmentState(ApartmentState.STA);
    audioThread.Start();
    audioThread.Join();
    return;
}

var checks = 0;
void Check(bool passed, string label)
{
    if (!passed)
    {
        throw new InvalidOperationException(label);
    }

    checks++;
}

var desktop = new AudioIdentity(@"C:\Program Files\Chrome\chrome.exe", "", "");
Check(desktop.Matches("chrome.exe"), "Desktop executable matches");
Check(desktop.Matches("Chrome"), "Desktop stem/case matches");
Check(!desktop.Matches("chromium.exe"), "Similar executables stay separate");
Check(!desktop.Matches(""), "Empty source never matches");
Check(!new AudioIdentity("org.example.exe", "", "").Matches("org.example.Player"), "Dotted application IDs are not truncated into executable names");
Check(!new AudioIdentity("", "", "").Matches(".exe"), "Unavailable process identity never matches");
Check(!desktop.Matches("Chrome_Publisher!App"), "Packaged IDs never match executable guesses");
var packaged = new AudioIdentity(@"C:\Apps\App.exe", "Music_Publisher!Music", "Music_Publisher");
Check(packaged.Matches("Music_Publisher!Music"), "Exact packaged application matches");
Check(!packaged.Matches("Other_Publisher!App"), "Generic package entry points do not match");
Check(!packaged.Matches("Music_Publisher!Other"), "Separate app IDs stay separate");
Check(new AudioIdentity("player.exe", "Company.Desktop.Player", "").Matches("Company.Desktop.Player"), "Explicit desktop AUMID matches");
var appleHelper = new AudioIdentity("AMPLibraryAgent.exe", "AppleInc.AppleMusicWin_nzyj5cx40ttqa!LibraryServer", "AppleInc.AppleMusicWin_nzyj5cx40ttqa");
Check(appleHelper.MatchesAudioSource("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"), "Apple Music audio helper matches the player package");
Check(!appleHelper.Matches("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"), "Icon lookup still prefers the exact application");
Check(!appleHelper.MatchesAudioSource("OtherPublisher.AppleMusicWin_nzyj5cx40ttqa!App"), "Similar package names never match");
Check(!appleHelper.MatchesAudioSource("OtherPlayer_Publisher!LibraryServer"), "Same helper entry point in another package never matches");
Check(new AudioIdentity("helper.exe", "", "Music_Publisher").MatchesAudioSource("Music_Publisher!App"), "A packaged native helper needs no entry-point AUMID");
Check(!new AudioIdentity("App.exe", "", "").MatchesAudioSource("Music_Publisher!App"), "Generic executable never substitutes for package identity");
Check(desktop.MatchesAudioSource("chrome.exe"), "Unpackaged audio matching stays exact");
Check(!appleHelper.MatchesAudioSource(""), "Missing audio source never matches");
Check(!AudioDeviceBattery.IsPeripheral(Guid.Empty), "Unknown container has no battery");
Check(!AudioDeviceBattery.IsPeripheral(new("00000000-0000-0000-FFFF-FFFFFFFFFFFF")), "PC battery excluded");
Check(AudioDeviceBattery.IsPeripheral(Guid.NewGuid()), "Peripheral container accepted");
foreach (byte value in new byte[] { 0, 1, 50, 100 })
{
    Check(AudioDeviceBattery.ParsePercentage([value], 3) == value, "Valid battery percentage");
}

foreach (byte value in new byte[] { 101, 255 })
{
    Check(AudioDeviceBattery.ParsePercentage([value], 3) is null, "Unknown battery excluded");
}

Check(AudioDeviceBattery.ParsePercentage(null, 3) is null, "Missing battery excluded");
Check(AudioDeviceBattery.ParsePercentage([50, 0, 0, 0], 7) is null, "Incorrect property type excluded");
Check(AudioDevicePresentation.Glyph(3) != AudioDevicePresentation.Glyph(5), "Headphone and headset differ");
Check(AudioDevicePresentation.Glyph(9) != AudioDevicePresentation.Glyph(1), "Display and speaker differ");
Check(AudioDevicePresentation.Glyph(-1) == AudioDevicePresentation.Glyph(10), "Unknown output uses generic glyph");

if (args.Contains("--native"))
{
    using var mixer = new AudioMixerController();
    using var enumerator = new MMDeviceEnumerator();
    using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var masterBefore = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
    var masterMuteBefore = endpoint.AudioEndpointVolume.Mute;
    var source = Process.GetCurrentProcess().ProcessName;
    var system = mixer.Read(null, true);
    Check(system.Available && system.Outputs.Length == 1, "Default endpoint discovered");
    var containerKey = new PropertyKey(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    if (endpoint.Properties.Contains(containerKey) && endpoint.Properties[containerKey].Value is Guid expectedContainer)
    {
        Check(system.Outputs[0].ContainerId == expectedContainer, "Container read directly from endpoint properties, without requiring instance ID");
    }
    Console.WriteLine($"Output: {system.Outputs[0].Name}; form factor: {system.Outputs[0].FormFactor}; battery: {AudioDeviceBattery.ReadPercentage(system.Outputs[0].ContainerId)?.ToString() ?? "unavailable"}");
    Check(!mixer.Read(source, true).Available, "No synthetic session before creation");

    // Initialized, never started WASAPI clients produce no sound. Separate GUIDs
    // create independent sessions, including a session added after enumeration.
    using var firstDevice = enumerator.GetDevice(endpoint.ID);
    using var first = firstDevice.AudioClient;
    first.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, first.MixFormat, Guid.NewGuid());
    var state = mixer.Read(source, true);
    Check(state.Available, "New session discovered after initial empty enumeration");
    mixer.SetVolume(source, 37);
    Check(Math.Abs(mixer.Read(source).Percent - 37) < 0.01, "Per-process volume written and read");
    mixer.ToggleMute(source);
    Check(mixer.Read(source).Muted, "Session muted");
    Check(Math.Abs(mixer.Read(source).Percent - 37) < 0.01, "Mute preserves session volume");
    mixer.ToggleMute(source);
    Check(!mixer.Read(source).Muted, "Session unmuted");

    using var secondDevice = enumerator.GetDevice(endpoint.ID);
    using var second = secondDevice.AudioClient;
    second.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, second.MixFormat, Guid.NewGuid());
    mixer.Read(source, true);
    mixer.SetVolume(source, 62);
    Check(Math.Abs(mixer.Read(source).Percent - 62) < 0.01, "New stream included in volume writes");
    mixer.ToggleMute(source);
    using (var inspector = new MMDeviceEnumerator())
    using (var inspectDevice = inspector.GetDevice(endpoint.ID))
    {
        var sessions = inspectDevice.AudioSessionManager.Sessions;
        var found = 0;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.GetProcessID != Environment.ProcessId)
            {
                continue;
            }

            found++;
            Check(Math.Abs(session.SimpleAudioVolume.Volume - 0.62) < 0.001, "Every matching stream volume changes");
            Check(session.SimpleAudioVolume.Mute, "Every matching stream is muted");
        }
        Check(found >= 2, "Two separate sessions verified");
    }
    mixer.SetVolume("no-such-player.exe", 0);
    mixer.ToggleMute("no-such-player.exe");
    Check(!mixer.Read("no-such-player.exe").Available, "Unmatched source remains unavailable");
    Check(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar == masterBefore, "App writes never change system volume");
    Check(endpoint.AudioEndpointVolume.Mute == masterMuteBefore, "App mute never changes system mute");
    mixer.ToggleMute(source);
    // Native fixture session state is discarded when these private clients close.
}

Console.WriteLine($"PASS: {checks} audio checks");
