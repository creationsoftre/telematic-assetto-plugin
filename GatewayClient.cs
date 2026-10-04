using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Telematic.AssettoServer.Plugin;

public interface IGatewayTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string token, CancellationToken cancellationToken);
    Task SendAsync(string text, CancellationToken cancellationToken);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

public sealed class ClientWebSocketTransport : IGatewayTransport
{
    private ClientWebSocket? _socket;
    public async Task ConnectAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("X-Telematic-Plugin-Token", token);
        await _socket.ConnectAsync(uri, cancellationToken);
    }
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open } socket) throw new WebSocketException("Gateway is not open.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return null;
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(bytes, cancellationToken); if (result.MessageType == WebSocketMessageType.Close) return null; buffer.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    public async Task CloseAsync(CancellationToken cancellationToken)
    { if (_socket is { State: WebSocketState.Open } socket) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopping", cancellationToken); }
    public ValueTask DisposeAsync() { _socket?.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class TelematicGatewayClient
{
    private readonly TelematicConfiguration _configuration;
    private readonly IGatewayTransport _transport;
    private Func<CommandRequest, CancellationToken, Task<CommandResult>> _commandHandler = static (request, _) => Task.FromResult(new CommandResult(request.CommandId, request.InstanceId, "unsupported", DateTimeOffset.UtcNow));
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();
    private readonly ConcurrentDictionary<string, string> _pendingFrames = new();
    private readonly Channel<QueuedGameplayEvent> _gameplayEvents;
    private readonly Channel<QueuedEphemeralEvent> _ephemeralEvents = Channel.CreateBounded<QueuedEphemeralEvent>(4);
    private readonly object _playerSnapshotGate = new();
    private object? _latestPlayerSnapshot;
    private long _playerSnapshotVersion;
    private bool _playerSnapshotQueued;
    private readonly Channel<bool> _telemetrySignals = Channel.CreateBounded<bool>(1);
    private readonly object _telemetryGate = new();
    private object? _latestTelemetry;
    private Action? _connectionReady;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _pendingCount;

    public TelematicGatewayClient(TelematicConfiguration configuration, IGatewayTransport transport)
    { _configuration = configuration; _transport = transport; _gameplayEvents = Channel.CreateBounded<QueuedGameplayEvent>(new BoundedChannelOptions(Math.Max(1, configuration.MaxPendingDurableMessages)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false }); }

    public void SetCommandHandler(Func<CommandRequest, CancellationToken, Task<CommandResult>> handler) => _commandHandler = handler;

    public void SetConnectionReadyHandler(Action handler) => _connectionReady = handler;

    public bool TryQueueGameplayEvent(string messageType, object payload)
        => _gameplayEvents.Writer.TryWrite(new QueuedGameplayEvent(Guid.NewGuid().ToString("N"), messageType, payload));

    public bool TryQueueEphemeralEvent(string messageType, object payload)
    {
        if (messageType != "players.snapshot") return _ephemeralEvents.Writer.TryWrite(new QueuedEphemeralEvent(messageType, payload));
        lock (_playerSnapshotGate)
        {
            _latestPlayerSnapshot = payload;
            _playerSnapshotVersion++;
            if (_playerSnapshotQueued) return true;
            _playerSnapshotQueued = _ephemeralEvents.Writer.TryWrite(new QueuedEphemeralEvent(messageType, null));
            return _playerSnapshotQueued;
        }
    }

    public bool TryQueueTelemetrySnapshot(object payload)
    {
        lock (_telemetryGate) _latestTelemetry = payload;
        _telemetrySignals.Writer.TryWrite(true);
        return true;
    }

    public void DiscardPendingTelemetrySnapshots()
    {
        lock (_telemetryGate) _latestTelemetry = null;
        while (_telemetrySignals.Reader.TryRead(out _)) { }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.Enabled) return;
        ValidateConfiguration();
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RunConnectionAsync(cancellationToken); delay = TimeSpan.FromSeconds(1); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warning(ex, "Telematic gateway connection failed; retrying"); }
            if (!cancellationToken.IsCancellationRequested) { await Task.Delay(delay, cancellationToken); delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30)); }
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        DiscardPendingTelemetrySnapshots();
        await _transport.ConnectAsync(new Uri(_configuration.SupervisorUrl), _configuration.PluginToken, cancellationToken);
        var capabilities = Capabilities();
        await SendAsync("connection.hello", TelematicProtocol.Ephemeral, new ConnectionHello([TelematicProtocol.Version], "0.1.0", "ASSETTOSERVER_NATIVE", "ASSETTO_CORSA", capabilities), cancellationToken);
        var first = await _transport.ReceiveAsync(cancellationToken) ?? throw new IOException("Gateway closed during handshake.");
        var accepted = JsonSerializer.Deserialize<GatewayEnvelope>(first, TelematicProtocol.JsonOptions);
        if (accepted is null || accepted.MessageType != "connection.accepted") throw new InvalidOperationException("Gateway rejected the connection handshake.");
        foreach (var frame in _pendingFrames.Values) await SendRawAsync(frame, cancellationToken);
        lock (_playerSnapshotGate)
        {
            if (_latestPlayerSnapshot is not null && !_playerSnapshotQueued)
                _playerSnapshotQueued = _ephemeralEvents.Writer.TryWrite(new QueuedEphemeralEvent("players.snapshot", null));
        }
        await SendAsync("instance.register", TelematicProtocol.Durable, new InstanceRegister("ASSETTO_CORSA", "ASSETTOSERVER_NATIVE", "0.1.0", TelematicProtocol.Version, capabilities), cancellationToken);
        await SendAsync("instance.ready", TelematicProtocol.Durable, new { }, cancellationToken);
        _connectionReady?.Invoke();

        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(_configuration.HeartbeatSeconds));
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = ReceiveLoopAsync(connectionCancellation.Token);
        var heartbeatTask = SendHeartbeatsAsync(heartbeat, connectionCancellation.Token);
        var gameplayTask = DrainGameplayEventsAsync(connectionCancellation.Token);
        var ephemeralTask = DrainEphemeralEventsAsync(connectionCancellation.Token);
        var telemetryTask = DrainTelemetryEventsAsync(connectionCancellation.Token);
        await Task.WhenAny(receiveTask, heartbeatTask, gameplayTask, ephemeralTask, telemetryTask);
        connectionCancellation.Cancel();
        try { await Task.WhenAll(receiveTask, heartbeatTask, gameplayTask, ephemeralTask, telemetryTask); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("Gateway connection stopped."); }
    }

    private async Task SendHeartbeatsAsync(PeriodicTimer heartbeat, CancellationToken cancellationToken)
    { while (await heartbeat.WaitForNextTickAsync(cancellationToken)) await SendAsync("instance.heartbeat", TelematicProtocol.Ephemeral, new InstanceHeartbeat(), cancellationToken); }

    private async Task DrainGameplayEventsAsync(CancellationToken cancellationToken)
    { await foreach (var item in _gameplayEvents.Reader.ReadAllAsync(cancellationToken)) await SendAsync(item.MessageType, TelematicProtocol.Durable, item.Payload, cancellationToken, item.MessageId); }

    private async Task DrainEphemeralEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in _ephemeralEvents.Reader.ReadAllAsync(cancellationToken))
        {
            if (item.MessageType == "players.snapshot" && item.Payload is null)
            {
                object? payload;
                long version;
                lock (_playerSnapshotGate) { payload = _latestPlayerSnapshot; version = _playerSnapshotVersion; }
                if (payload is null) continue;
                try { await SendAsync(item.MessageType, TelematicProtocol.Ephemeral, payload, cancellationToken); }
                catch { lock (_playerSnapshotGate) _playerSnapshotQueued = false; throw; }
                lock (_playerSnapshotGate)
                {
                    if (version == _playerSnapshotVersion) _playerSnapshotQueued = false;
                    else if (!_playerSnapshotQueued) _playerSnapshotQueued = _ephemeralEvents.Writer.TryWrite(new QueuedEphemeralEvent(item.MessageType, null));
                }
                continue;
            }
            if (item.Payload is not null) await SendAsync(item.MessageType, TelematicProtocol.Ephemeral, item.Payload, cancellationToken);
        }
    }

    private async Task DrainTelemetryEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (var _ in _telemetrySignals.Reader.ReadAllAsync(cancellationToken))
        {
            object? payload;
            lock (_telemetryGate) { payload = _latestTelemetry; _latestTelemetry = null; }
            if (payload is not null) await SendAsync("telemetry.snapshot", TelematicProtocol.Ephemeral, payload, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var text = await _transport.ReceiveAsync(cancellationToken); if (text is null) throw new IOException("Gateway closed the connection.");
            var message = JsonSerializer.Deserialize<GatewayEnvelope>(text, TelematicProtocol.JsonOptions); if (message is null) continue;
            if (message.MessageType == "ack") { var ack = message.Payload.Deserialize<Acknowledgement>(TelematicProtocol.JsonOptions); if (ack != null && _pendingAcks.TryRemove(ack.MessageId, out var waiter)) { _pendingFrames.TryRemove(ack.MessageId, out _); Interlocked.Decrement(ref _pendingCount); waiter.TrySetResult(ack.Accepted); } continue; }
            if (message.MessageType == "command.request") { var request = message.Payload.Deserialize<CommandRequest>(TelematicProtocol.JsonOptions); if (request != null) await SendAsync("command.result", TelematicProtocol.Ephemeral, await _commandHandler(request, cancellationToken), cancellationToken); }
        }
    }

    private async Task SendAsync<T>(string messageType, string delivery, T payload, CancellationToken cancellationToken, string? messageId = null)
    {
        var id = messageId ?? Guid.NewGuid().ToString("N");
        if (delivery == TelematicProtocol.Durable && Interlocked.Increment(ref _pendingCount) > _configuration.MaxPendingDurableMessages) { Interlocked.Decrement(ref _pendingCount); throw new InvalidOperationException("Durable gateway queue is full."); }
        var waiter = delivery == TelematicProtocol.Durable ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        var now = DateTimeOffset.UtcNow;
        var envelope = new { protocolVersion = TelematicProtocol.Version, messageId = id, messageType, instanceId = _configuration.InstanceId, occurredAt = now, sentAt = now, delivery, payload };
        var frame = JsonSerializer.Serialize(envelope, TelematicProtocol.JsonOptions);
        if (waiter != null) { _pendingAcks[id] = waiter; _pendingFrames[id] = frame; }
        try { await SendRawAsync(frame, cancellationToken); }
        catch { if (waiter != null && _pendingAcks.TryRemove(id, out _)) { _pendingFrames.TryRemove(id, out _); Interlocked.Decrement(ref _pendingCount); } throw; }
    }

    private async Task SendRawAsync(string frame, CancellationToken cancellationToken)
    { await _sendLock.WaitAsync(cancellationToken); try { await _transport.SendAsync(frame, cancellationToken); } finally { _sendLock.Release(); } }

    private string[] Capabilities()
    {
        var capabilities = new List<string> { "sessions.read", "laps.read", "incidents.read", TelematicProtocol.CapabilityPlayersRead, TelematicProtocol.CapabilityChatBroadcast };
        if (_configuration.Telemetry.Enabled) { capabilities.Add("telemetry.position"); capabilities.Add("telemetry.vehicle"); }
        return capabilities.ToArray();
    }

    private void ValidateConfiguration()
    { if (string.IsNullOrWhiteSpace(_configuration.InstanceId)) throw new InvalidOperationException("InstanceId is required."); if (string.IsNullOrWhiteSpace(_configuration.PluginToken)) throw new InvalidOperationException("PluginToken is required."); if (_configuration.HeartbeatSeconds < 5) throw new InvalidOperationException("HeartbeatSeconds must be at least 5."); if (_configuration.Telemetry.Enabled && _configuration.Telemetry.SampleRateHz is < 1 or > 5) throw new InvalidOperationException("Telemetry.SampleRateHz must be between 1 and 5."); }

    private sealed record QueuedGameplayEvent(string MessageId, string MessageType, object Payload);
    private sealed record QueuedEphemeralEvent(string MessageType, object? Payload);
}
