using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Network.Packets.Incoming;
using CommunityToolkit.Common;
using Serilog;

namespace Telematic.AssettoServer.Plugin;

public sealed class AssettoEventBridge
{
    private readonly EntryCarManager _cars;
    private readonly SessionManager _sessions;
    private readonly ACServerConfiguration _configuration;
    private readonly TelematicGatewayClient _gateway;
    private readonly VehicleStateCache _telemetryCache;
    private readonly Dictionary<ACTcpClient, ClientHandlers> _handlers = new();
    private readonly Dictionary<string, DateTimeOffset> _recentCollisions = new();
    private readonly object _gate = new();
    private SessionState? _activeSession;
    private string? _activeSessionId;
    private NativeGeneration? _activeGeneration;
    private int _started;
    private long _lapCount;
    private long _localOnlyLapCount;
    private DateTimeOffset? _lastLapAt;
    private string? _lastLapDriver;
    private DateTimeOffset _lastPlayerSnapshotAt;

    public string? CurrentSessionId => _activeSessionId;

    public EventDiagnosticsSnapshot Diagnostics()
        => new(Interlocked.Read(ref _lapCount), _lastLapAt, _lastLapDriver, _activeSessionId,
            LocalOnlyLaps: Interlocked.Read(ref _localOnlyLapCount));

    public AssettoEventBridge(EntryCarManager cars, SessionManager sessions, ACServerConfiguration configuration, TelematicGatewayClient gateway, VehicleStateCache telemetryCache)
    { _cars = cars; _sessions = sessions; _configuration = configuration; _gateway = gateway; _telemetryCache = telemetryCache; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _cars.ClientConnected += OnClientConnected;
        _cars.ClientDisconnected += OnClientDisconnected;
        _sessions.SessionChanged += OnSessionChanged;
        foreach (var entry in _cars.EntryCars) if (entry.Client is { } client && IsCurrentPlayer(client)) Attach(client);
        if (_sessions.CurrentSession is { } current) ObserveSession(current, "session.started");
        Log.Information("Telematic subscribed to AssettoServer gameplay events");
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        _cars.ClientConnected -= OnClientConnected;
        _cars.ClientDisconnected -= OnClientDisconnected;
        _sessions.SessionChanged -= OnSessionChanged;
        lock (_gate)
        {
            foreach (var pair in _handlers.ToArray()) Detach(pair.Key, pair.Value);
            _handlers.Clear();
            _recentCollisions.Clear();
        }
    }

    public void EmitSnapshot()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _started) == 0) return;
            // Capture and enqueue under the same lifecycle lock. A delayed
            // ready capture must not overwrite a newer leave/reconnect roster.
            var observed = new List<(ACTcpClient Client, DriverReference Driver)>();
            foreach (var entry in _cars.EntryCars)
            {
                var client = entry.Client;
                if (client is null || !IsCurrentPlayer(client) || client.IsDisconnectRequested) continue;
                observed.Add((client, AssettoEventMappers.Driver(client, _configuration.Extra.UseSteamAuth)));
            }
            // Upstream mutates slot state before invoking our callback: recheck
            // references rather than reading EntryCar.Client a second time in a mapper.
            var stable = observed.Where(item => IsCurrentPlayer(item.Client) && !item.Client.IsDisconnectRequested).ToArray();
            var ambiguous = stable.GroupBy(item => item.Driver.SteamId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            var ambiguousSlots = stable.GroupBy(item => item.Driver.GameDriverId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            var players = stable.Where(item => !ambiguous.Contains(item.Driver.SteamId) && !ambiguousSlots.Contains(item.Driver.GameDriverId)).Select(item => item.Driver).ToArray();
            if (ambiguous.Count > 0 || ambiguousSlots.Count > 0) Log.Warning("Telematic ambiguous player identities excluded from snapshot");
            var at = DateTimeOffset.UtcNow;
            if (at <= _lastPlayerSnapshotAt) at = _lastPlayerSnapshotAt.AddTicks(1);
            _lastPlayerSnapshotAt = at;
            var payload = new PlayersSnapshotPayload(at.ToString("O"), players, _activeSessionId);
            if (!_gateway.TryQueueEphemeralEvent("players.snapshot", payload)) Log.Error("Telematic ephemeral snapshot queue saturated");
            else Log.Debug("Telematic player snapshot emitted: {PlayerCount} players", players.Length);
        }
    }

    private bool IsCurrentPlayer(ACTcpClient client)
        => client.IsConnected && client.Guid.ToString().Length == 17
            && client.EntryCar is { } entry && entry.SessionId == client.SessionId
            && ReferenceEquals(entry.Client, client) && _cars.EntryCars.Contains(entry);

    private void OnClientConnected(ACTcpClient client, EventArgs _)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _started) == 0) return;
            if (!IsCurrentPlayer(client) || client.IsDisconnectRequested) { EmitSnapshot(); return; }
            Attach(client);
            Queue("driver.connected", new DriverEventPayload(Guid.NewGuid().ToString("N"), AssettoEventMappers.Driver(client, _configuration.Extra.UseSteamAuth), _activeSessionId ?? ""));
            EmitSnapshot();
        }
    }

    private void OnClientDisconnected(ACTcpClient client, EventArgs _)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _started) == 0 || !_handlers.Remove(client, out var handlers)) return;
            Detach(client, handlers);
            // A delayed callback for an obsolete client cannot disconnect or
            // remove the cache of a replacement that now owns the numeric slot.
            if (!client.IsConnected && client.EntryCar.Client is null)
            {
                Queue("driver.disconnected", new DriverEventPayload(Guid.NewGuid().ToString("N"), AssettoEventMappers.Driver(client, _configuration.Extra.UseSteamAuth), _activeSessionId ?? ""));
                _telemetryCache.Remove(client);
            }
            EmitSnapshot();
        }
    }

    private void OnClientDisconnecting(ACTcpClient client, EventArgs _)
    {
        lock (_gate)
        {
            if (_handlers.ContainsKey(client) && client.IsDisconnectRequested) EmitSnapshot();
        }
    }

    private void OnSessionChanged(SessionManager _, SessionChangedEventArgs args)
    {
        _telemetryCache.ClearSamples();
        var type = _activeSession == null ? "session.started" : "session.changed";
        ObserveSession(args.NextSession, type);
    }

    private void ObserveSession(SessionState session, string messageType)
    {
        lock (_gate)
        {
            if (_activeSession == session) return;
            // Stop capture against the previous session before attempting the
            // durable transition. A failed START must not bind later laps to it.
            _activeSession = null;
            _activeSessionId = null;
            _activeGeneration = null;
            _recentCollisions.Clear();
            foreach (var handlers in _handlers.Values) handlers.Continuity = new();
            try
            {
                var sessionId = Guid.NewGuid().ToString("N");
                var generation = _gateway.AllocateGeneration(sessionId);
                if (!Queue(messageType, AssettoEventMappers.Session(Guid.NewGuid().ToString("N"), sessionId, session, _configuration) with { Generation = generation })) return;
                _activeSession = session;
                _activeSessionId = sessionId;
                _activeGeneration = generation;
                Log.Information("Telematic session event persisted: {MessageType} {SessionId}", messageType, sessionId);
            }
            catch (Exception)
            {
                // Never crash the game callback or disclose paths/credentials.
                Log.Error("Telematic authoritative session capture disabled: durable generation transition failed");
            }
        }
    }

    private void Attach(ACTcpClient client)
    {
        lock (_gate)
        {
            if (_handlers.ContainsKey(client)) return;
            EventHandler<ACTcpClient, LapCompletedEventArgs> lap = OnLapCompleted;
            EventHandler<ACTcpClient, CollisionEventArgs> collision = OnCollision;
            void PositionHandler(EntryCar car, in PositionUpdateIn update) => OnPositionUpdate(client, car, in update);
            EventHandlerIn<EntryCar, PositionUpdateIn> position = PositionHandler;
            client.LapCompleted += lap;
            client.Collision += collision;
            client.Disconnecting += OnClientDisconnecting;
            client.EntryCar.PositionUpdateReceived += position;
            _telemetryCache.Attach(client);
            _handlers[client] = new ClientHandlers(lap, collision, position, client.Guid.ToString(), client.SessionId, client.EntryCar);
        }
    }

    private void Detach(ACTcpClient client, ClientHandlers handlers)
    { client.LapCompleted -= handlers.Lap; client.Collision -= handlers.Collision; client.Disconnecting -= OnClientDisconnecting; client.EntryCar.PositionUpdateReceived -= handlers.Position; }

    private void OnPositionUpdate(ACTcpClient client, EntryCar car, in PositionUpdateIn update)
    {
        if (!ReferenceEquals(car.Client, client) || !client.IsConnected || client.IsDisconnectRequested) return;
        _telemetryCache.Update(client, car, in update);
    }

    private void OnLapCompleted(ACTcpClient client, LapCompletedEventArgs args)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _handlers.TryGetValue(client, out var handlers);
            var bound = handlers is not null && IsCurrentPlayer(client) && !client.IsDisconnectRequested
                && handlers.Identity == client.Guid.ToString() && handlers.Slot == client.SessionId
                && ReferenceEquals(handlers.Car, client.EntryCar) && args.Packet.SessionId == client.SessionId
                && _activeSessionId is not null
                && _activeSession is not null && ReferenceEquals(_sessions.CurrentSession, _activeSession);
            var continuity = handlers?.Continuity ?? new NativeLapContinuity();
            // Routing an observed completion is independent of Steam ticket
            // authentication. Preserve it in the bound native session even
            // when the server deliberately runs without Steam authentication;
            // its identity/evidence must still remain explicitly unverified.
            var evidence = continuity.Complete(_activeSessionId, client.Guid.ToString(), client.SessionId,
                bound && _configuration.Extra.UseSteamAuth, args.Packet.Cuts > 0, now);
            // Obsolete clients and wrong native slots remain local-only; lack of
            // Steam ticket authentication does not invalidate session routing.
            var payload = AssettoEventMappers.Lap(client, bound ? _activeSession : null, _configuration,
                bound ? _activeSessionId : null, args.Packet, null) with
            { OccurredAt = now.ToString("O"), Validity = args.Packet.Cuts > 0 ? "INVALID" : "UNKNOWN",
                RecordValidation = CleanLapValidation.Evaluate(args.Packet.LapTime, evidence) };
            var persisted = _gateway.TryCaptureCompletedLap(payload, bound);
            Interlocked.Increment(ref _lapCount);
            _lastLapAt = now; _lastLapDriver = client.Name;
            if (persisted && !bound)
            {
                Interlocked.Increment(ref _localOnlyLapCount);
                Log.Warning("Telematic completed lap archived locally but not uploaded: current native session/player routing could not be established");
            }
            else if (persisted) Log.Debug("Telematic completed lap retained for {Driver}", client.Name);
            else Log.Error("Telematic completed lap capture failed; inspect durability health");
        }
    }

    private void OnCollision(ACTcpClient source, CollisionEventArgs args)
    {
        lock (_gate)
        {
        var capturedAt = DateTimeOffset.UtcNow;
        var target = args.TargetCar;
        var targetId = target?.SessionId;
        var key = targetId.HasValue ? CollisionKey(source.SessionId, targetId.Value, args) : null;
        foreach (var old in _recentCollisions.Where(x => capturedAt - x.Value > TimeSpan.FromSeconds(2)).Select(x => x.Key).ToArray()) _recentCollisions.Remove(old);
        if (key is not null && _recentCollisions.ContainsKey(key))
        {
            Log.Debug("Duplicate Telematic collision suppressed for {SourceSessionId}/{TargetSessionId}", source.SessionId, targetId);
            return;
        }

        var participants = new List<DriverReference> { AssettoEventMappers.Driver(source, _configuration.Extra.UseSteamAuth) };
        if (target is not null)
        {
            participants.Add(target.Client is { } targetClient
                ? AssettoEventMappers.Driver(targetClient, _configuration.Extra.UseSteamAuth)
                : AssettoEventMappers.PartialDriver(target));
        }

        var metadata = new Dictionary<string, object?>
        {
            ["rawUpstreamSpeed"] = args.Speed,
            ["timestampSource"] = "plugin_callback",
            ["collisionDeliveryMayBeDelayed"] = true,
            ["sourceClientSessionId"] = source.SessionId.ToString(),
            ["relativePosition"] = new WorldPosition(args.RelPosition.X, args.RelPosition.Y, args.RelPosition.Z)
        };
        if (target is not null) metadata["targetCarId"] = target.SessionId.ToString();
        var payload = new CollisionEventPayload(Guid.NewGuid().ToString("N"), _activeSessionId, participants, args.Speed, new WorldPosition(args.Position.X, args.Position.Y, args.Position.Z), capturedAt.ToString("O"), metadata);
        if (Queue(target is null ? "incident.environment_collision" : "incident.car_collision", payload))
        {
            if (key is not null) _recentCollisions[key] = capturedAt;
            Log.Debug("Telematic collision event persisted for {Driver}", source.Name);
        }
        }
    }

    private string CollisionKey(byte sourceSessionId, byte targetSessionId, CollisionEventArgs args)
    {
        var first = Math.Min(sourceSessionId, targetSessionId);
        var second = Math.Max(sourceSessionId, targetSessionId);
        // Include connection identity and native session so a reused slot cannot
        // inherit a previous occupant's suppression window.
        string? ConnectionToken(byte slot)
        {
            var client = _cars.EntryCars.FirstOrDefault(c => c.SessionId == slot)?.Client;
            return client is not null && _handlers.TryGetValue(client, out var handlers) ? handlers.ConnectionId : null;
        }
        return $"{_activeSessionId}:{first}:{second}:{ConnectionToken(first)}:{ConnectionToken(second)}:{MathF.Round(args.Position.X / 5)}:{MathF.Round(args.Position.Y / 5)}:{MathF.Round(args.Position.Z / 5)}:{MathF.Round(args.Speed / 5)}";
    }

    private bool Queue(string messageType, object payload)
    {
        if (_gateway.TryQueueGameplayEvent(messageType, payload)) return true;
        Log.Error("Telematic durable capture failed; event was NOT safely persisted {MessageType}", messageType);
        return false;
    }

    private sealed record ClientHandlers(EventHandler<ACTcpClient, LapCompletedEventArgs> Lap, EventHandler<ACTcpClient, CollisionEventArgs> Collision, EventHandlerIn<EntryCar, PositionUpdateIn> Position, string Identity, byte Slot, EntryCar Car)
    {
        public NativeLapContinuity Continuity { get; set; } = new();
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    }
}

public sealed record EventDiagnosticsSnapshot(long CompletedLaps, DateTimeOffset? LastLapAt, string? LastLapDriver, string? SessionId,
    bool NativeCleanLapCoverageAvailable = false,
    string NativeCleanLapCoverageGap = "COLLISION_TIMESTAMP_AND_DELIVERY_WATERMARK_UNAVAILABLE",
    long LocalOnlyLaps = 0);
