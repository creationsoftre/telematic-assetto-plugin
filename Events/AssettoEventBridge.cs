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
    private readonly Dictionary<ACTcpClient, SortedDictionary<byte, long>> _sectors = new();
    private readonly Dictionary<string, DateTimeOffset> _recentCollisions = new();
    private readonly object _gate = new();
    private SessionState? _activeSession;
    private string? _activeSessionId;
    private int _started;

    public string? CurrentSessionId => _activeSessionId;

    public AssettoEventBridge(EntryCarManager cars, SessionManager sessions, ACServerConfiguration configuration, TelematicGatewayClient gateway, VehicleStateCache telemetryCache)
    { _cars = cars; _sessions = sessions; _configuration = configuration; _gateway = gateway; _telemetryCache = telemetryCache; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _cars.ClientConnected += OnClientConnected;
        _cars.ClientDisconnected += OnClientDisconnected;
        _sessions.SessionChanged += OnSessionChanged;
        foreach (var entry in _cars.EntryCars) if (entry.Client is { } client) Attach(client);
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
            _handlers.Clear(); _sectors.Clear();
            _recentCollisions.Clear();
        }
    }

    public void EmitSnapshot()
    {
        var players = _cars.EntryCars
            .Where(entry => entry.Client?.IsConnected == true)
            .Select(entry => AssettoEventMappers.Driver(entry.Client!, _configuration.Extra.UseSteamAuth))
            .ToArray();
        var payload = new PlayersSnapshotPayload(DateTimeOffset.UtcNow.ToString("O"), players, _activeSessionId);
        if (!_gateway.TryQueueEphemeralEvent("players.snapshot", payload)) Log.Error("Telematic ephemeral snapshot queue saturated");
        else Log.Information("Telematic player snapshot emitted: {PlayerCount} players", players.Length);
    }

    private void OnClientConnected(ACTcpClient client, EventArgs _)
    {
        Attach(client);
        Queue("driver.connected", new DriverEventPayload(Guid.NewGuid().ToString("N"), AssettoEventMappers.Driver(client, _configuration.Extra.UseSteamAuth), _activeSessionId ?? ""));
    }

    private void OnClientDisconnected(ACTcpClient client, EventArgs _)
    {
        Queue("driver.disconnected", new DriverEventPayload(Guid.NewGuid().ToString("N"), AssettoEventMappers.Driver(client, _configuration.Extra.UseSteamAuth), _activeSessionId ?? ""));
        _telemetryCache.Remove(client.SessionId);
        lock (_gate)
        {
            if (_handlers.Remove(client, out var handlers)) Detach(client, handlers);
            _sectors.Remove(client);
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
        if (_activeSession == session) return;
        _activeSession = session;
        _activeSessionId ??= Guid.NewGuid().ToString("N");
        if (messageType == "session.changed") _activeSessionId = Guid.NewGuid().ToString("N");
        Queue(messageType, AssettoEventMappers.Session(Guid.NewGuid().ToString("N"), _activeSessionId, session, _configuration));
        Log.Information("Telematic session event queued: {MessageType} {SessionId}", messageType, _activeSessionId);
    }

    private void Attach(ACTcpClient client)
    {
        lock (_gate)
        {
            if (_handlers.ContainsKey(client)) return;
            EventHandler<ACTcpClient, LapCompletedEventArgs> lap = OnLapCompleted;
            EventHandler<ACTcpClient, SectorSplitEventArgs> sector = OnSectorSplit;
            EventHandler<ACTcpClient, CollisionEventArgs> collision = OnCollision;
            EventHandlerIn<EntryCar, PositionUpdateIn> position = OnPositionUpdate;
            client.LapCompleted += lap;
            client.SectorSplit += sector;
            client.Collision += collision;
            client.EntryCar.PositionUpdateReceived += position;
            _telemetryCache.Attach(client);
            _handlers[client] = new ClientHandlers(lap, sector, collision, position);
            _sectors[client] = new SortedDictionary<byte, long>();
        }
    }

    private void Detach(ACTcpClient client, ClientHandlers handlers)
    { client.LapCompleted -= handlers.Lap; client.SectorSplit -= handlers.Sector; client.Collision -= handlers.Collision; client.EntryCar.PositionUpdateReceived -= handlers.Position; }

    private void OnPositionUpdate(EntryCar car, in PositionUpdateIn update)
        => _telemetryCache.Update(car, in update);

    private void OnSectorSplit(ACTcpClient client, SectorSplitEventArgs args)
    {
        lock (_gate) if (_sectors.TryGetValue(client, out var values)) values[args.Packet.SplitIndex] = args.Packet.SplitTime;
    }

    private void OnLapCompleted(ACTcpClient client, LapCompletedEventArgs args)
    {
        IReadOnlyList<long>? sectors;
        lock (_gate)
        {
            sectors = _sectors.TryGetValue(client, out var values) && values.Count > 0 ? values.OrderBy(x => x.Key).Select(x => x.Value).ToArray() : null;
            if (_sectors.TryGetValue(client, out var current)) current.Clear();
        }
        if (_activeSession is null || _activeSessionId is null) return;
        var payload = AssettoEventMappers.Lap(client, _activeSession, _configuration, _activeSessionId, args.Packet, sectors);
        Queue("lap.completed", payload);
        Log.Debug("Telematic lap event queued for {Driver}", client.Name);
    }

    private void OnCollision(ACTcpClient source, CollisionEventArgs args)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var target = args.TargetCar;
        var targetId = target?.SessionId;
        if (targetId.HasValue && !ShouldEmitCollision(source.SessionId, targetId.Value, args, capturedAt))
        {
            Log.Debug("Duplicate Telematic collision suppressed for {SourceSessionId}/{TargetSessionId}", source.SessionId, targetId.Value);
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
        Queue(target is null ? "incident.environment_collision" : "incident.car_collision", payload);
        Log.Debug("Telematic collision event queued for {Driver}", source.Name);
    }

    private bool ShouldEmitCollision(byte sourceSessionId, byte targetSessionId, CollisionEventArgs args, DateTimeOffset capturedAt)
    {
        var first = Math.Min(sourceSessionId, targetSessionId);
        var second = Math.Max(sourceSessionId, targetSessionId);
        var key = $"{first}:{second}:{MathF.Round(args.Position.X / 5)}:{MathF.Round(args.Position.Y / 5)}:{MathF.Round(args.Position.Z / 5)}:{MathF.Round(args.Speed / 5)}";
        lock (_gate)
        {
            foreach (var old in _recentCollisions.Where(x => capturedAt - x.Value > TimeSpan.FromSeconds(2)).Select(x => x.Key).ToArray()) _recentCollisions.Remove(old);
            if (_recentCollisions.ContainsKey(key)) return false;
            _recentCollisions[key] = capturedAt;
            return true;
        }
    }

    private void Queue(string messageType, object payload)
    { if (!_gateway.TryQueueGameplayEvent(messageType, payload)) Log.Error("Telematic gameplay event queue saturated; dropped {MessageType}", messageType); }

    private sealed record ClientHandlers(EventHandler<ACTcpClient, LapCompletedEventArgs> Lap, EventHandler<ACTcpClient, SectorSplitEventArgs> Sector, EventHandler<ACTcpClient, CollisionEventArgs> Collision, EventHandlerIn<EntryCar, PositionUpdateIn> Position);
}
