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

    [YamlMember(Description = "Gateway heartbeat interval in seconds.")]
    public int HeartbeatSeconds { get; init; } = 15;

    [YamlMember(Description = "Maximum in-memory durable messages awaiting acknowledgement.")]
    public int MaxPendingDurableMessages { get; init; } = 256;

    [YamlMember(Description = "Sampled live vehicle telemetry settings.")]
    public TelemetryConfiguration Telemetry { get; init; } = new();

    public override string ToString() => $"Enabled={Enabled}, InstanceId={InstanceId}, SupervisorUrl={SupervisorUrl}, PluginToken=<redacted>";
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
