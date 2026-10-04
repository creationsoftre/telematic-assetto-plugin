using System.Collections.Concurrent;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Network.Packets.Incoming;

namespace Telematic.AssettoServer.Plugin;

public readonly record struct CachedVehicleState(
    DriverReference Driver,
    TelemetryVector? Position,
    TelemetryVector? Velocity,
    float? NormalizedPosition,
    ushort Rpm,
    byte Gear,
    byte GasRaw,
    byte SteerAngleRaw,
    long UpdatedAtTicks,
    bool InvalidNumericSample,
    bool HasSample);

public sealed class VehicleStateCache
{
    private readonly ConcurrentDictionary<byte, CachedVehicleState> _states = new();
    private readonly ACServerConfiguration _configuration;
    private long _updates;
    private long _invalidSamples;

    public VehicleStateCache(ACServerConfiguration configuration) => _configuration = configuration;

    public long UpdatesReceived => Interlocked.Read(ref _updates);
    public long InvalidSamples => Interlocked.Read(ref _invalidSamples);
    public int Count => _states.Count;

    public void Attach(ACTcpClient client)
    {
        _states[client.SessionId] = new CachedVehicleState(AssettoEventMappers.Driver(client, _configuration.Extra.UseSteamAuth), null, null, null, 0, 0, 0, 127, 0, false, false);
    }

    public void Remove(byte sessionId) => _states.TryRemove(sessionId, out _);

    public void ClearSamples()
    {
        foreach (var pair in _states.ToArray())
            _states[pair.Key] = pair.Value with { Position = null, Velocity = null, NormalizedPosition = null, HasSample = false, InvalidNumericSample = false };
    }

    public void Update(EntryCar car, in PositionUpdateIn update)
    {
        Interlocked.Increment(ref _updates);
        if (!_states.TryGetValue(car.SessionId, out var previous)) return;

        var positionValid = IsFinite(update.Position) && update.Position.Length() <= 100_000f;
        var velocityValid = IsFinite(update.Velocity) && update.Velocity.Length() <= 500f;
        var normalizedValid = float.IsFinite(update.NormalizedPosition) && update.NormalizedPosition >= 0f && update.NormalizedPosition <= 1f;
        var invalid = !positionValid || !velocityValid || !normalizedValid;
        if (invalid) Interlocked.Increment(ref _invalidSamples);

        _states[car.SessionId] = new CachedVehicleState(
            previous.Driver,
            positionValid ? ToVector(update.Position) : null,
            velocityValid ? ToVector(update.Velocity) : null,
            normalizedValid ? update.NormalizedPosition : null,
            update.EngineRpm,
            update.Gear,
            update.Gas,
            update.SteerAngle,
            DateTime.UtcNow.Ticks,
            invalid,
            true);
    }

    public CachedVehicleState[] FreshStates(DateTime now, TimeSpan staleAfter)
    {
        var cutoff = now - staleAfter;
        return _states.Values.Where(state => state.HasSample && new DateTime(state.UpdatedAtTicks, DateTimeKind.Utc) >= cutoff).ToArray();
    }

    private static bool IsFinite(System.Numerics.Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static TelemetryVector ToVector(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
}
