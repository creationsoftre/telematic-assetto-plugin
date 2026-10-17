using System.Text.Json;
using AssettoServer.Server;
using AssettoServer.Shared.Network.Packets.Shared;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Telematic.AssettoServer.Plugin;

public sealed class TelematicPluginService : BackgroundService
{
    private readonly TelematicConfiguration _configuration;
    private readonly EntryCarManager _entryCarManager;
    private readonly TelematicGatewayClient _gateway;
    private readonly AssettoEventBridge _events;
    private readonly TelemetrySampler _telemetry;
    private readonly IGatewayTransport _transport;

    public TelematicPluginService(TelematicConfiguration configuration, EntryCarManager entryCarManager, TelematicGatewayClient gateway, AssettoEventBridge events, TelemetrySampler telemetry, IGatewayTransport transport)
    { _configuration = configuration; _entryCarManager = entryCarManager; _gateway = gateway; _events = events; _telemetry = telemetry; _transport = transport; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.Enabled) { Log.Information("Telematic AssettoServer plugin is disabled"); return; }
        _gateway.SetCommandHandler(HandleCommandAsync);
        _events.Start();
        _gateway.SetConnectionReadyHandler(() => { _events.EmitSnapshot(); _telemetry.EmitSnapshot(); });
        await _gateway.RunAsync(stoppingToken);
    }

    private Task<CommandResult> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var invalid = !_configuration.Enabled || !_gateway.Diagnostics().Connected ? "COMMAND_NOT_AUTHORIZED" : CommandSafety.Validate(request, _configuration.InstanceId, DateTimeOffset.UtcNow);
        if (invalid is not null) return Task.FromResult(new CommandResult(request.CommandId, _configuration.InstanceId, "rejected", DateTimeOffset.UtcNow, new ProtocolError(invalid, "Command not dispatched")));
        if (request.CommandType == TelematicProtocol.CapabilityChatBroadcast)
        {
            if (!request.Parameters.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(message.GetString()))
                return Task.FromResult(new CommandResult(request.CommandId, _configuration.InstanceId, "rejected", DateTimeOffset.UtcNow, new ProtocolError("INVALID_ARGUMENT", "message is required")));
            _entryCarManager.BroadcastPacket(new ChatMessage { SessionId = 255, Message = message.GetString()! });
            return Task.FromResult(new CommandResult(request.CommandId, _configuration.InstanceId, "succeeded", DateTimeOffset.UtcNow));
        }
        return Task.FromResult(new CommandResult(request.CommandId, _configuration.InstanceId, "unsupported", DateTimeOffset.UtcNow, new ProtocolError("UNSUPPORTED_COMMAND", $"Unsupported command: {request.CommandType}")));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Stop();
        await base.StopAsync(cancellationToken);
        await _transport.CloseAsync(cancellationToken);
        await _transport.DisposeAsync();
        _gateway.Dispose();
    }
}
