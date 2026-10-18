# Playback and native lifetime checks

Run the deterministic timeline checks:

```powershell
dotnet run --project Tests/PlaybackChecks/PlaybackChecks.csproj -c Release
```

Add `-- --stress` to publish an independent, silent media session for two minutes.
The test changes native playback/timeline properties, handles the resulting events,
renders from managed snapshots, and repeatedly forces collection/finalization.
It requires actual native notifications and display activity to pass. It does not
operate another application's playback. This is a stress check, not proof that an
intermittent native crash has been eliminated over hours of normal use.

## Process isolation and recovery

Build the app, then run the client against the built executable:

```powershell
dotnet run --project Tests/PlaybackChecks/PlaybackChecks.csproj -c Release -- --worker <path-to-PlayerApp.exe>
```

This test publishes its own silent Win32 SMTC session on an STA dispatcher. It
verifies IPC framing, metadata, image bytes, playback and timeline updates, then
terminates the worker three times. Every reconnection must restore data and allow
Play and Seek commands. Commands sent while disconnected must fail without replay.
Disposing the client must terminate the owned child process. Run this against the
published single EXE as well, to verify the worker startup path after extraction.

The UI and worker are two processes of the same distributed executable. Only the
worker activates GSMTC. It runs native calls on one STA dispatcher, coalesces change
notifications, and sends managed snapshots over a private, current-user-only named
pipe. No native session or timeline pointer enters the UI. The UI freezes transport
controls during reconnection, preserves the selected source where possible, and
supervises worker exit and heartbeat timeouts. Repeated early failures use bounded
backoff. The diagnostic log is stored under the user's LocalApplicationData folder
in `PlayerApp/logs/media-worker.log` and is capped/rotated.

The worker installs its synchronization context on every queued async continuation.
A standalone DispatcherQueue callback does not do this automatically. Without that
step, a later await can switch work to the thread pool, producing wrong-thread errors
for Win32 media sources even if the first callback ran on the STA. The integration
fixture also checks thread/context identity across real asynchronous operations.

## Crash investigation

Four application dumps from 2026-10-18 all failed at the same native frame:

```text
Windows.Media.MediaControl!
  RuntimeClassImpl<IGlobalSystemMediaTransportControlsSessionTimelineProperties>::Release
WinRT.IObjectReference.Finalize
```

The supplied report's dump showed an invalid native reference-count value during
Release. Its process uptime was 1 hour 13 minutes. The other matching failures
occurred after 5 minutes and 1 hour 49 minutes. The minidumps do not contain the
native heap needed to prove where the reference count first became invalid.

The first change removed polling and switched to native notifications. That build
still crashed at the same frame after 28 minutes. Notification-only reads therefore
do not eliminate the native failure. The current change isolates the entire GSMTC
lifetime, including finalization, in the supervised worker process. It contains the
native failure; it does not claim to repair the Windows DLL or prove exactly where
its reference count first becomes invalid.

A matching polling/finalization failure has also been reported in the Windows API
from both C# and Rust: [microsoft/windows-rs issue 3734](https://github.com/microsoft/windows-rs/issues/3734).
The report does not establish the exact internal Windows defect or a fixed OS version.
