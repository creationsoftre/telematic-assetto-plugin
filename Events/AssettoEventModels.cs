namespace Telematic.AssettoServer.Plugin;

public sealed record DriverReference(
    string DisplayName,
    string? SteamId,
    string GameDriverId,
    string? CarCode = null,
    string? Skin = null,
    IReadOnlyDictionary<string, object?>? GameMetadata = null);

public sealed record DriverEventPayload(
    string EventId,
    DriverReference Driver,
    string SessionId,
    IReadOnlyDictionary<string, object?>? GameMetadata = null);

public sealed record SessionEventPayload(
    string EventId,
    string SessionId,
    string SessionType,
    string NativeType,
    string TrackKey,
    string LayoutKey,
    string StartedAt,
    IReadOnlyDictionary<string, object?>? GameMetadata = null);

public sealed record LapEventPayload(
    string EventId,
    string SessionId,
    DriverReference Driver,
    string TrackKey,
    string LayoutKey,
    long LapTimeMs,
    uint? LapNumber,
    byte Cuts,
    string Validity,
    IReadOnlyList<long>? SectorTimesMs,
    string? Tyre,
    string OccurredAt,
    IReadOnlyDictionary<string, object?>? GameMetadata = null);

public sealed record PlayersSnapshotPayload(
    string CapturedAt,
    IReadOnlyList<DriverReference> Players,
    string? SessionId = null);

public sealed record WorldPosition(float X, float Y, float Z);

public sealed record CollisionEventPayload(
    string IncidentId,
    string? SessionId,
    IReadOnlyList<DriverReference> Participants,
    float? ImpactSpeedKmh,
    WorldPosition Position,
    string OccurredAt,
    IReadOnlyDictionary<string, object?> GameMetadata);

public readonly record struct TelemetryVector(float X, float Y, float Z);

public sealed record VehicleTelemetryPayload(
    DriverReference Driver,
    TelemetryVector? Position = null,
    TelemetryVector? Velocity = null,
    float? NormalizedPosition = null,
    float? Rpm = null,
    int? Gear = null,
    IReadOnlyDictionary<string, object?>? GameMetadata = null);

public sealed record TelemetrySnapshotPayload(
    string CapturedAt,
    IReadOnlyList<VehicleTelemetryPayload> Players,
    string? SessionId = null);
