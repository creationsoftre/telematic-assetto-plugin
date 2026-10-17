using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
        NativeTransportPolicy.ValidateSupervisorUrl(uri.AbsoluteUri);
        _socket?.Dispose(); _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("X-Telematic-Plugin-Token", token);
        await _socket.ConnectAsync(uri, cancellationToken);
    }
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open } socket) throw new WebSocketException("Gateway is not open.");
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
    }
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return null;
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || buffer.Length + result.Count > 256 * 1024) throw new InvalidDataException("GATEWAY_FRAME_INVALID");
            buffer.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    public Task CloseAsync(CancellationToken cancellationToken) { _socket?.Abort(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _socket?.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class TelematicGatewayClient : IDisposable
{
    private readonly TelematicConfiguration _configuration;
    private readonly IGatewayTransport _transport;
    private readonly object _gate = new();
    private DurableSpool? _spool;
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);
    private DateTimeOffset _nextDurableAttempt;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<string> _ephemeralEvents = Channel.CreateBounded<string>(4);
    private readonly Channel<bool> _telemetrySignals = Channel.CreateBounded<bool>(1);
    private readonly object _playerSnapshotGate = new();
    private object? _latestPlayerSnapshot;
    private long _playerSnapshotVersion;
    private bool _playerSnapshotQueued;
    private readonly object _telemetryGate = new();
    private object? _latestTelemetry;
    private Action? _connectionReady;
    private Func<CommandRequest, CancellationToken, Task<CommandResult>> _commandHandler = static (request, _) => Task.FromResult(new CommandResult(request.CommandId, request.InstanceId, "unsupported", DateTimeOffset.UtcNow));
    private bool _connected;
    private DateTimeOffset? _connectedAt, _lastMessageAt, _lastDurableSentAt, _lastAcknowledgedAt, _lastDurableAckAt;
    private string? _lastMessageType, _lastDurableMessageId, _lastDurableMessageType, _lastAcknowledgedMessageId, _lastError;
    private bool? _lastAcknowledgedAccepted;
    private long _accepted, _rejected, _retrying, _reconnects, _captureFailures;

    public TelematicGatewayClient(TelematicConfiguration configuration, IGatewayTransport transport)
    { _configuration = configuration; _transport = transport; if (configuration.Enabled) { NativeTransportPolicy.ValidateSupervisorUrl(configuration.SupervisorUrl); EnsureSpool(); } }

    private void EnsureSpool()
    {
        lock (_gate)
        {
            if (_spool is not null) return;
            try { _spool = new DurableSpool(_configuration.InstanceId, _configuration.Spool); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { _lastError = "SPOOL_UNAVAILABLE"; Log.Error("Telematic durability degraded {DiagnosticCode}", _lastError); }
        }
    }

    public void SetCommandHandler(Func<CommandRequest, CancellationToken, Task<CommandResult>> handler) => _commandHandler = handler;
    public void SetConnectionReadyHandler(Action handler) => _connectionReady = handler;
    public NativeGeneration AllocateGeneration(string sessionId)
    {
        EnsureSpool();
        lock (_gate) return (_spool ?? throw new IOException("SPOOL_UNAVAILABLE")).AllocateGeneration(sessionId);
    }
    public GatewayDiagnosticsSnapshot Diagnostics()
    {
        lock (_gate)
        {
            var spool = _spool?.Diagnostics();
            return new(_connected, _connectedAt, _lastMessageAt, _lastMessageType, Math.Max(0, (spool?.Pending ?? 0) - _pending.Count), _pending.Count,
                _lastDurableMessageId, _lastDurableMessageType, _lastDurableSentAt, _lastAcknowledgedMessageId, _lastAcknowledgedAccepted, _lastAcknowledgedAt, _lastError,
                spool, _accepted, _rejected, _retrying, _reconnects, _captureFailures, _lastDurableAckAt,
                _pending.Count == 0 ? null : _pending.Values.Min(), _connected ? "transport-connected" : "transport-disconnected",
                _lastDurableAckAt is null ? "not-yet-accepted" : "durable-acceptance-observed", "unknown-plugin-boundary");
        }
    }

    public bool TryQueueGameplayEvent(string messageType, object payload)
    {
        if (!_configuration.Enabled) return false;
        try
        {
            var bytes = DurableEventValidation.Serialize(_configuration.InstanceId, Guid.NewGuid().ToString("N"), messageType, payload);
            EnsureSpool();
            lock (_gate)
            {
                if (_spool is not null && _spool.TryPersist(bytes, out _)) return true;
                _lastError = _spool?.Diagnostics().ErrorCode ?? "SPOOL_UNAVAILABLE"; _captureFailures++; return false;
            }
        }
        catch (Exception)
        {
            lock (_gate) { _captureFailures++; _lastError = "DURABLE_CAPTURE_REJECTED"; }
            Log.Error("Telematic durable capture failed {DiagnosticCode} {MessageType}", "DURABLE_CAPTURE_REJECTED", DurableEventValidation.IsDurableType(messageType) ? messageType : "unsupported"); return false;
        }
    }
    public bool TryQueueEphemeralEvent(string messageType, object payload)
    {
        if (messageType != "players.snapshot") return false;
        lock (_playerSnapshotGate)
        {
            _latestPlayerSnapshot = payload; _playerSnapshotVersion++;
            if (_playerSnapshotQueued) return true;
            _playerSnapshotQueued = _ephemeralEvents.Writer.TryWrite(messageType); return _playerSnapshotQueued;
        }
    }
    public bool TryQueueTelemetrySnapshot(object payload)
    { lock (_telemetryGate) _latestTelemetry = payload; _telemetrySignals.Writer.TryWrite(true); return true; }
    public void DiscardPendingTelemetrySnapshots()
    { lock (_telemetryGate) _latestTelemetry = null; while (_telemetrySignals.Reader.TryRead(out _)) { } }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.Enabled) return; ValidateConfiguration(); var delaySeconds = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RunConnectionAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                lock (_gate) { _lastError ??= "GATEWAY_PROGRESS_FAILED"; _reconnects++; }
                Log.Warning("Telematic gateway reconnect {DiagnosticCode}", Diagnostics().LastError);
            }
            if (cancellationToken.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds + Random.Shared.NextDouble() * .25), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            delaySeconds = Math.Min(delaySeconds * 2, 30);
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); Task[] tasks = [];
        try
        {
            EnsureSpool(); DiscardPendingTelemetrySnapshots();
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(_configuration.ConnectTimeoutSeconds));
                await _transport.ConnectAsync(new Uri(_configuration.SupervisorUrl), _configuration.PluginToken, handshake.Token);
                await SendEphemeralAsync("connection.hello", new ConnectionHello([TelematicProtocol.Version], "Telematic.AssettoServer.Plugin", "0.2.0", "ASSETTO_CORSA", "ASSETTOSERVER_NATIVE", Capabilities(), SupportedDurableAckVersions: [TelematicProtocol.DurableAckVersion]), handshake.Token);
                var first = await _transport.ReceiveAsync(handshake.Token) ?? throw new IOException("HANDSHAKE_CLOSED");
                var envelope = JsonSerializer.Deserialize<GatewayEnvelope>(first, TelematicProtocol.JsonOptions);
                if (envelope?.MessageType != "connection.accepted" || envelope.InstanceId != _configuration.InstanceId || envelope.ProtocolVersion != TelematicProtocol.Version) throw new InvalidDataException("HANDSHAKE_REJECTED");
                var accepted = envelope.Payload.Deserialize<ConnectionAccepted>(TelematicProtocol.JsonOptions);
                if (accepted?.DurableAckVersion != TelematicProtocol.DurableAckVersion || accepted.ProtocolVersion != TelematicProtocol.Version)
                { lock (_gate) _lastError = "DURABLE_ACK_VERSION_UNSUPPORTED"; throw new InvalidDataException("DURABLE_ACK_VERSION_UNSUPPORTED"); }
            }
            lock (_gate) { _pending.Clear(); _nextDurableAttempt = DateTimeOffset.MinValue; _connected = true; _connectedAt = DateTimeOffset.UtcNow; _lastError = null; }
            // The receive loop starts before replay/bootstrap/window acquisition.
            var receive = ReceiveLoopAsync(connectionCancellation.Token);
            tasks = [receive, DrainDurableAsync(connectionCancellation.Token), SendHeartbeatsAsync(connectionCancellation.Token), WatchAcknowledgementsAsync(connectionCancellation.Token)];
            // Replace cached pre-disconnect presence before any ephemeral drain.
            // Durable receive/replay remains live while ready state is captured.
            _connectionReady?.Invoke();
            lock (_playerSnapshotGate)
                if (_latestPlayerSnapshot is not null && !_playerSnapshotQueued) _playerSnapshotQueued = _ephemeralEvents.Writer.TryWrite("players.snapshot");
            tasks = [.. tasks, DrainEphemeralEventsAsync(connectionCancellation.Token), DrainTelemetryEventsAsync(connectionCancellation.Token)];
            await await Task.WhenAny(tasks); throw new IOException("GATEWAY_LOOP_STOPPED");
        }
        finally
        {
            connectionCancellation.Cancel(); await _transport.CloseAsync(CancellationToken.None);
            try { await Task.WhenAll(tasks); } catch (Exception) { /* disk still owns durable work */ }
            lock (_gate) { _pending.Clear(); _connected = false; }
        }
    }

    private async Task DrainDurableAsync(CancellationToken token)
    {
        var registered = false; var ready = false;
        while (!token.IsCancellationRequested)
        {
            if (!registered) registered = TryQueueGameplayEvent("instance.register", new InstanceRegister("ASSETTO_CORSA", "ASSETTOSERVER_NATIVE", "0.2.0", TelematicProtocol.Version, Capabilities()));
            if (registered && !ready) ready = TryQueueGameplayEvent("instance.ready", new { });
            IReadOnlyList<SpoolEntry> entries;
            lock (_gate)
                entries = DateTimeOffset.UtcNow < _nextDurableAttempt || _spool is null ? [] : _spool.Eligible(_pending.Keys.ToHashSet(StringComparer.Ordinal), _configuration.MaxPendingDurableMessages - _pending.Count);
            foreach (var entry in entries)
            {
                string? frame;
                lock (_gate) { frame = _spool!.Read(entry); if (frame is null) continue; _pending[entry.MessageId] = DateTimeOffset.UtcNow; }
                await SendRawAsync(frame, token);
                lock (_gate) { _lastDurableMessageId = entry.MessageId; _lastDurableMessageType = entry.MessageType; _lastDurableSentAt = DateTimeOffset.UtcNow; _lastMessageType = entry.MessageType; }
            }
            await Task.Delay(50, token);
        }
    }
    private async Task WatchAcknowledgementsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (_gate)
                if (_pending.Count > 0 && DateTimeOffset.UtcNow - _pending.Values.Min() >= TimeSpan.FromSeconds(_configuration.AckTimeoutSeconds))
                { _lastError = "DURABLE_ACK_TIMEOUT"; throw new TimeoutException("DURABLE_ACK_TIMEOUT"); }
            await Task.Delay(100, token);
        }
    }
    private async Task SendHeartbeatsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_configuration.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(token)) await SendEphemeralAsync("instance.heartbeat", new { lifecycle = "READY", pluginVersion = "0.2.0", metadata = new { delivery = Diagnostics() } }, token);
    }
    private async Task DrainEphemeralEventsAsync(CancellationToken token)
    {
        await foreach (var type in _ephemeralEvents.Reader.ReadAllAsync(token))
        {
            object? payload; long version; lock (_playerSnapshotGate) { payload = _latestPlayerSnapshot; version = _playerSnapshotVersion; }
            try { if (payload is not null) await SendEphemeralAsync(type, payload, token); }
            finally
            {
                lock (_playerSnapshotGate)
                {
                    _playerSnapshotQueued = false;
                    if (_playerSnapshotVersion != version) _playerSnapshotQueued = _ephemeralEvents.Writer.TryWrite(type);
                }
            }
        }
    }
    private async Task DrainTelemetryEventsAsync(CancellationToken token)
    {
        await foreach (var _ in _telemetrySignals.Reader.ReadAllAsync(token))
        {
            object? payload; lock (_telemetryGate) { payload = _latestTelemetry; _latestTelemetry = null; }
            if (payload is not null) await SendEphemeralAsync("telemetry.snapshot", payload, token);
        }
    }
    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var text = await _transport.ReceiveAsync(token) ?? throw new IOException("GATEWAY_CLOSED"); GatewayEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<GatewayEnvelope>(text, TelematicProtocol.JsonOptions); }
            catch (JsonException) { lock (_gate) _lastError = "MALFORMED_GATEWAY_MESSAGE"; continue; }
            if (envelope is null || envelope.ProtocolVersion != TelematicProtocol.Version || envelope.InstanceId != _configuration.InstanceId) continue;
            if (envelope.MessageType == "ack") { HandleAcknowledgement(envelope.Payload); continue; }
            if (envelope.MessageType == "command.request")
            {
                CommandRequest? request;
                try { request = envelope.Payload.Deserialize<CommandRequest>(TelematicProtocol.JsonOptions); } catch (JsonException) { continue; }
                if (request is not null) {
                    var error = CommandSafety.HasExplicitTimestampOffsets(envelope.Payload) ? CommandSafety.Validate(request, _configuration.InstanceId, DateTimeOffset.UtcNow) : "COMMAND_TIMESTAMP_INVALID";
                    if (error is null) {
                        try { lock (_gate) error = (_spool ?? throw new IOException()).ClaimCommand(request); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { error = "COMMAND_LEDGER_UNAVAILABLE"; }
                    }
                    var result = error is null ? await _commandHandler(request, token) : new CommandResult(request.CommandId, _configuration.InstanceId, "rejected", DateTimeOffset.UtcNow, new ProtocolError(error, "Command not dispatched; reconcile uncertain replay"));
                    await SendEphemeralAsync("command.result", result, token);
                }
            }
        }
    }
    public void HandleAcknowledgement(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("accepted", out var acceptedField) || acceptedField.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        Acknowledgement? ack;
        try { ack = payload.Deserialize<Acknowledgement>(TelematicProtocol.JsonOptions); } catch (JsonException) { return; }
        if (ack is null || ack.InstanceId != _configuration.InstanceId || !DurableEventValidation.IsIdentifier(ack.MessageId)) return;
        if (ack.Accepted && ack.Error is not null) return;
        if (!ack.Accepted && (ack.Error is null || string.IsNullOrWhiteSpace(ack.Error.Code) || !payload.GetProperty("error").TryGetProperty("retryable", out var retry) || retry.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) return;
        lock (_gate)
        {
            if (!_pending.ContainsKey(ack.MessageId) || _spool is null) return;
            _lastAcknowledgedMessageId = ack.MessageId; _lastAcknowledgedAccepted = ack.Accepted; _lastAcknowledgedAt = DateTimeOffset.UtcNow;
            if (ack.Accepted) { if (_spool.Retire(ack.MessageId)) { _accepted++; _lastDurableAckAt = DateTimeOffset.UtcNow; } }
            else if (ack.Error!.Retryable) { _retrying++; _nextDurableAttempt = DateTimeOffset.UtcNow.AddSeconds(_configuration.RetryDelaySeconds); _lastError = "SUPERVISOR_RETRYABLE_REJECTION"; }
            else { if (_spool.Reject(ack.MessageId, ack.Error.Code)) _rejected++; _lastError = "SUPERVISOR_PERMANENT_REJECTION"; }
            _pending.Remove(ack.MessageId);
        }
    }
    private Task SendEphemeralAsync<T>(string type, T payload, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return SendRawAsync(JsonSerializer.Serialize(new { protocolVersion = TelematicProtocol.Version, messageId = Guid.NewGuid().ToString("N"), messageType = type, instanceId = _configuration.InstanceId, occurredAt = now, sentAt = now, delivery = TelematicProtocol.Ephemeral, payload }, TelematicProtocol.JsonOptions), token);
    }
    private async Task SendRawAsync(string frame, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(_configuration.WriteTimeoutSeconds));
        await _sendLock.WaitAsync(deadline.Token);
        try { await _transport.SendAsync(frame, deadline.Token); lock (_gate) _lastMessageAt = DateTimeOffset.UtcNow; }
        finally { _sendLock.Release(); }
    }
    private string[] Capabilities()
    {
        var capabilities = new List<string> { "sessions.read", "laps.read", "incidents.read", TelematicProtocol.CapabilityPlayersRead, TelematicProtocol.CapabilityChatBroadcast };
        if (_configuration.Telemetry.Enabled) { capabilities.Add("telemetry.position"); capabilities.Add("telemetry.vehicle"); }
        return capabilities.ToArray();
    }
    private void ValidateConfiguration()
    {
        if (!DurableEventValidation.IsIdentifier(_configuration.InstanceId) || string.IsNullOrWhiteSpace(_configuration.PluginToken)) throw new InvalidOperationException("INVALID_GATEWAY_CONFIGURATION");
        if (_configuration.HeartbeatSeconds is < 5 or > 3600 || _configuration.MaxPendingDurableMessages is < 1 or > 4096 || _configuration.ConnectTimeoutSeconds is < 1 or > 300 || _configuration.WriteTimeoutSeconds is < 1 or > 300 || _configuration.AckTimeoutSeconds is < 1 or > 3600 || _configuration.RetryDelaySeconds is < 1 or > 300) throw new InvalidOperationException("INVALID_GATEWAY_LIMITS");
        if (_configuration.Telemetry.Enabled && _configuration.Telemetry.SampleRateHz is < 1 or > 5) throw new InvalidOperationException("INVALID_TELEMETRY_RATE");
    }
    public void Dispose() { lock (_gate) _spool?.Dispose(); }
}

public sealed record GatewayDiagnosticsSnapshot(bool Connected, DateTimeOffset? ConnectedAt, DateTimeOffset? LastMessageAt, string? LastMessageType, int QueuedGameplayMessages, int AwaitingAcknowledgement,
    string? LastDurableMessageId, string? LastDurableMessageType, DateTimeOffset? LastDurableSentAt, string? LastAcknowledgedMessageId, bool? LastAcknowledgedAccepted, DateTimeOffset? LastAcknowledgedAt, string? LastError,
    SpoolDiagnostics? Spool, long Accepted, long PermanentlyRejected, long Retrying, long ReconnectCount, long CaptureFailures, DateTimeOffset? LastDurableAckAt, DateTimeOffset? OldestAwaitingAckAt,
    string TransportState, string SupervisorAcceptanceState, string CloudReceivingState);
