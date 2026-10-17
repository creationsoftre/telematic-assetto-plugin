using System.Text;

namespace Telematic.AssettoServer.Plugin;

public static class CommandSafety
{
    public static bool HasExplicitTimestampOffsets(System.Text.Json.JsonElement payload)
    {
        if (payload.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var name in new[] { "requestedAt", "expiresAt" })
            if (!payload.TryGetProperty(name, out var field) || field.ValueKind != System.Text.Json.JsonValueKind.String || !System.Text.RegularExpressions.Regex.IsMatch(field.GetString()!, "(?:Z|[+-][0-9]{2}:[0-9]{2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return false;
        return true;
    }
    public static string? Validate(CommandRequest request, string instance, DateTimeOffset now)
    {
        if (request.InstanceId != instance) return "COMMAND_INSTANCE_MISMATCH";
        if (!DurableEventValidation.IsIdentifier(request.CommandId) || !DurableEventValidation.IsIdentifier(request.IdempotencyKey)) return "COMMAND_IDENTITY_REQUIRED";
        if (request.RequestedAt is null || request.ExpiresAt is null || request.RequestedAt > now || request.ExpiresAt <= now || request.ExpiresAt <= request.RequestedAt || request.ExpiresAt - request.RequestedAt > TimeSpan.FromMinutes(10)) return "COMMAND_EXPIRED_OR_INVALID";
        if (request.CommandType != TelematicProtocol.CapabilityChatBroadcast) return "UNSUPPORTED_COMMAND";
        if (request.Parameters.ValueKind != System.Text.Json.JsonValueKind.Object || !request.Parameters.TryGetProperty("message", out var message) || message.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(message.GetString())) return "INVALID_ARGUMENT";
        // v0.0.54 writes UTF32 codepoint count into one byte. Never truncate.
        var text = message.GetString()!;
        try { if (new UTF32Encoding(false, false, true).GetByteCount(text) > 255 * 4) return "CHAT_TOO_LARGE"; }
        catch (EncoderFallbackException) { return "INVALID_ARGUMENT"; }
        return null;
    }
}
