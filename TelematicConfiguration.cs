using AssettoServer.Server.Configuration;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace Telematic.AssettoServer.Plugin;

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public sealed class TelematicConfiguration
{
    [YamlMember(Description = "Enable the Telematic gateway connection.")]
    public bool Enabled { get; init; } = false;

    [YamlMember(Description = "Stable AssettoServer instance identifier.")]
    public string InstanceId { get; init; } = "assetto-server";

    [YamlMember(Description = "Supervisor gateway WebSocket URL.")]
    public string SupervisorUrl { get; init; } = "ws://127.0.0.1:9780/game";

    [YamlMember(Description = "Token provisioned by the local Telematic Supervisor.")]
    public string PluginToken { get; init; } = "";

    [YamlMember(Description = "Separate local diagnostics header credential (at least 32 characters). Empty disables diagnostics; never reuse PluginToken.")]
    public string DiagnosticsToken { get; init; } = "";

    [YamlMember(Description = "Gateway heartbeat interval in seconds.")]
    public int HeartbeatSeconds { get; init; } = 15;

    [YamlMember(Description = "Maximum in-memory durable messages awaiting acknowledgement.")]
    public int MaxPendingDurableMessages { get; init; } = 256;

    public SpoolConfiguration Spool { get; init; } = new();
    public int ConnectTimeoutSeconds { get; init; } = 15;
    public int WriteTimeoutSeconds { get; init; } = 10;
    public int AckTimeoutSeconds { get; init; } = 30;
    public int RetryDelaySeconds { get; init; } = 2;

    [YamlMember(Description = "Sampled live vehicle telemetry settings.")]
    public TelemetryConfiguration Telemetry { get; init; } = new();

    public override string ToString() => $"Enabled={Enabled}, InstanceId={InstanceId}, SupervisorUrl=<redacted>, PluginToken=<redacted>, DiagnosticsToken=<redacted>";
}

public sealed class SpoolConfiguration
{
    // Null selects the current service account's application-data directory.
    public string? RootDirectory { get; init; }
    public long MaxBytes { get; init; } = 100L * 1024 * 1024;
    public int MaxRecords { get; init; } = 20_000;
}

public sealed class TelemetryConfiguration
{
    [YamlMember(Description = "Enable ephemeral telemetry snapshots.")]
    public bool Enabled { get; init; } = false;

    [YamlMember(Description = "Telemetry snapshots per second; supported range is 1-5.")]
    public int SampleRateHz { get; init; } = 2;

    [YamlMember(Description = "Maximum age of cached state included in a snapshot.")]
    public int StaleSeconds { get; init; } = 5;
}
