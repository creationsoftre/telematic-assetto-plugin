using System.Text.Json;
using System.Text.Json.Serialization;

namespace Telematic.AssettoServer.Plugin;

public static class TelematicProtocol
{
    public const int Version = 1;
    public const string Durable = "durable";
    public const string Ephemeral = "ephemeral";
    public const string CapabilityChatBroadcast = "chat.broadcast";
    public const string CapabilityPlayersRead = "players.read";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public sealed record GatewayEnvelope(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("sentAt")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("delivery")] string Delivery,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("sequence")] long? Sequence = null);

public sealed record ConnectionHello(
    [property: JsonPropertyName("supportedProtocolVersions")] IReadOnlyList<int> SupportedProtocolVersions,
    [property: JsonPropertyName("pluginName")] string PluginName,
    [property: JsonPropertyName("pluginVersion")] string PluginVersion,
    [property: JsonPropertyName("game")] string Game,
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("gameServerVersion")] string? GameServerVersion = null);

public sealed record ConnectionAccepted(
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("serverTime")] DateTimeOffset? ServerTime = null);

public sealed record ConnectionRejected(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record InstanceRegister(
    [property: JsonPropertyName("game")] string Game,
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("pluginVersion")] string PluginVersion,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("gameServerVersion")] string? GameServerVersion = null);

public sealed record InstanceHeartbeat(
    [property: JsonPropertyName("lifecycle")] string Lifecycle = "READY",
    [property: JsonPropertyName("pluginVersion")] string PluginVersion = "0.1.0");

public sealed record CommandRequest(
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("commandType")] string CommandType,
    [property: JsonPropertyName("parameters")] JsonElement Parameters,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset? RequestedAt = null,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey = null);

public sealed record CommandResult(
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("error")] ProtocolError? Error = null,
    [property: JsonPropertyName("result")] object? Result = null);

public sealed record Acknowledgement(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("duplicate")] bool Duplicate = false,
    [property: JsonPropertyName("error")] ProtocolError? Error = null);

public sealed record ProtocolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable = false);
