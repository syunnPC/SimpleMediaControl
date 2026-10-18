using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerApp;

internal sealed class MediaClient(string executablePath, Action<Action> dispatch, Action<string> log) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<Guid, MediaSession> _sessions = new();
    private readonly object _processGate = new();
    private Connection? _connection;
    private Process? _process;
    private Task? _supervisor;
    private int _disposed;
    private int _generation;

    public bool IsConnected { get; private set; }
    public int? WorkerProcessId { get { lock (_processGate) { return _process?.Id; } } }
    public event Action<MediaClient, object>? SessionsChanged;
    public event Action<MediaClient, object>? ConnectionChanged;
    public IReadOnlyList<MediaSession> GetSessions() => _sessions.Values.ToArray();

    public void Start()
    {
        if (_supervisor is not null)
        {
            throw new InvalidOperationException("Media client is already running.");
        }
        _supervisor = Task.Run(SuperviseAsync);
    }

    public Task<bool> SendAsync(MediaCommand command)
    {
        var connection = Volatile.Read(ref _connection);
        return connection is null
            ? Task.FromException<bool>(new IOException("Media control is reconnecting."))
            : connection.SendAsync(command);
    }

    private async Task SuperviseAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            var generation = Interlocked.Increment(ref _generation);
            var connectedAt = Stopwatch.StartNew();
            var ready = false;
            Process? process = null;
            Connection? connection = null;
            Task? heartbeat = null;
            try
            {
                var pipeName = "PlayerApp.Media." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var start = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--media-worker");
                start.ArgumentList.Add(pipeName);
                lock (_processGate)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    process = Process.Start(start) ?? throw new IOException("Unable to start the media worker.");
                    _process = process;
                }
                log($"Worker started PID={process.Id}.");
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                connection = new Connection(new MediaPipe(server), _stop.Token);
                Volatile.Write(ref _connection, connection);
                heartbeat = RunHeartbeatAsync(connection);
                while (!_stop.IsCancellationRequested)
                {
                    var message = await connection.Pipe.ReadAsync<MediaMessage>(connection.Token).ConfigureAwait(false);
                    if (message.Kind == MediaMessageKind.Result)
                    {
                        connection.Complete(message);
                        continue;
                    }
                    if (message.Kind == MediaMessageKind.Error)
                    {
                        log("Worker API error: " + message.Error);
                        continue;
                    }
                    if (message.Kind == MediaMessageKind.Ready)
                    {
                        ready = true;
                        connectedAt.Restart();
                        log($"Worker connected PID={process.Id}, {message.Error}.");
                    }
                    dispatch(() =>
                    {
                        if (!_stop.IsCancellationRequested && generation == Volatile.Read(ref _generation))
                        {
                            Apply(message);
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException
                or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    log($"Worker connection lost: {ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                Volatile.Write(ref _connection, null);
                connection?.Dispose();
                if (heartbeat is not null)
                {
                    await heartbeat.ConfigureAwait(false);
                }
                if (process is not null)
                {
                    lock (_processGate)
                    {
                        StopWorker(process);
                    }
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    log($"Worker exited PID={process.Id}, code=0x{process.ExitCode:X8}.");
                    lock (_processGate)
                    {
                        process.Dispose();
                        _process = null;
                    }
                }
                dispatch(() =>
                {
                    if (!_stop.IsCancellationRequested && generation == Volatile.Read(ref _generation))
                    {
                        IsConnected = false;
                        ConnectionChanged?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
            if (_stop.IsCancellationRequested)
            {
                break;
            }
            failures = ready && connectedAt.Elapsed >= TimeSpan.FromSeconds(30) ? 0 : Math.Min(failures + 1, 5);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Max(0, failures - 1))), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task RunHeartbeatAsync(Connection connection)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(connection.Token).ConfigureAwait(false))
            {
                await connection.SendAsync(new(0, MediaCommandKind.Ping)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            connection.Dispose();
        }
    }

    private void Apply(MediaMessage message)
    {
        if (message.Kind == MediaMessageKind.Ready)
        {
            IsConnected = true;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (message.Kind == MediaMessageKind.Sessions && message.Sessions is { } descriptors)
        {
            var ids = descriptors.Select(s => s.Id).ToHashSet();
            foreach (var id in _sessions.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                _sessions.Remove(id);
            }
            foreach (var descriptor in descriptors)
            {
                _sessions.TryAdd(descriptor.Id, new MediaSession(this, descriptor));
            }
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_sessions.TryGetValue(message.SessionId, out var session))
        {
            session.Apply(message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stop.Cancel();
        Volatile.Read(ref _connection)?.Dispose();
        lock (_processGate)
        {
            if (_process is { } process)
            {
                StopWorker(process);
            }
        }
    }

    private void StopWorker(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // It may have exited between checking HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log("Unable to stop media worker: " + ex.Message);
        }
    }

    private sealed class Connection : IDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pending = new();
        private long _nextRequest;
        private int _disposed;
        public MediaPipe Pipe { get; }
        public CancellationToken Token { get; }

        public Connection(MediaPipe pipe, CancellationToken stop)
        {
            Pipe = pipe;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Token = _lifetime.Token;
        }

        public async Task<bool> SendAsync(MediaCommand command)
        {
            var id = Interlocked.Increment(ref _nextRequest);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await Pipe.SendAsync(command with { RequestId = id }, timeout.Token).ConfigureAwait(false);
                return await result.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Commands are never replayed after reconnecting (especially Next/Seek).
                Dispose();
                throw;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public void Complete(MediaMessage message)
        {
            if (_pending.TryRemove(message.RequestId, out var result))
            {
                result.TrySetResult(message.Success);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _lifetime.Cancel();
            Pipe.Dispose();
            foreach (var result in _pending.Values)
            {
                result.TrySetException(new IOException("Media worker disconnected."));
            }
            _pending.Clear();
            _lifetime.Dispose();
        }
    }
}
