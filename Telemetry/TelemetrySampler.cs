using Microsoft.Extensions.Hosting;
using Serilog;

namespace Telematic.AssettoServer.Plugin;

public sealed class TelemetrySampler : BackgroundService
{
    private readonly TelematicConfiguration _configuration;
    private readonly VehicleStateCache _cache;
    private readonly TelematicGatewayClient _gateway;
    private readonly AssettoEventBridge _events;
    private int _hadPlayers;

    public TelemetrySampler(TelematicConfiguration configuration, VehicleStateCache cache, TelematicGatewayClient gateway, AssettoEventBridge events)
    { _configuration = configuration; _cache = cache; _gateway = gateway; _events = events; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.Enabled || !_configuration.Telemetry.Enabled) return;
        Validate();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / _configuration.Telemetry.SampleRateHz));
        while (await timer.WaitForNextTickAsync(stoppingToken)) EmitSnapshot(false);
    }

    public void EmitSnapshot(bool force = true)
    {
        if (!_configuration.Enabled || !_configuration.Telemetry.Enabled) return;
        var states = _cache.FreshStates(DateTime.UtcNow, TimeSpan.FromSeconds(_configuration.Telemetry.StaleSeconds));
        if (states.Length == 0 && !force && Interlocked.Exchange(ref _hadPlayers, 0) == 0) return;
        Interlocked.Exchange(ref _hadPlayers, states.Length > 0 ? 1 : 0);
        var players = states.Select(Map).ToArray();
        var payload = new TelemetrySnapshotPayload(DateTimeOffset.UtcNow.ToString("O"), players, _events.CurrentSessionId);
        if (!_gateway.TryQueueTelemetrySnapshot(payload)) Log.Warning("Telematic telemetry snapshot coalesced or dropped");
    }

    private VehicleTelemetryPayload Map(CachedVehicleState state)
    {
        var metadata = new Dictionary<string, object?> { ["velocityUnit"] = "upstream_unspecified", ["gasRaw"] = state.GasRaw, ["steerAngleRaw"] = state.SteerAngleRaw };
        if (state.InvalidNumericSample) metadata["invalidNumericFieldsOmitted"] = true;
        return new VehicleTelemetryPayload(state.Driver, state.Position, state.Velocity, state.NormalizedPosition, state.Rpm, state.Gear, metadata);
    }

    private void Validate()
    { if (_configuration.Telemetry.SampleRateHz is < 1 or > 5) throw new InvalidOperationException("Telemetry.SampleRateHz must be between 1 and 5."); if (_configuration.Telemetry.StaleSeconds < 1) throw new InvalidOperationException("Telemetry.StaleSeconds must be positive."); }
}
