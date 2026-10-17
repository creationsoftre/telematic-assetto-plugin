using System.Text.Json;
using System.Text.RegularExpressions;

namespace Telematic.AssettoServer.Plugin;

public static class DurableEventValidation
{
    private static readonly Regex Id = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,254}$", RegexOptions.CultureInvariant);
    public static bool IsIdentifier(string? value) => value is not null && Id.IsMatch(value);
    public static bool IsDurableType(string type) => type is "instance.register" or "instance.ready"
        or "session.started" or "session.changed" or "session.ended" or "driver.connected"
        or "driver.disconnected" or "lap.completed" or "incident.car_collision" or "incident.environment_collision";

    public static byte[] Serialize(string instanceId, string messageId, string type, object payload)
    {
        if (!IsIdentifier(instanceId) || !IsIdentifier(messageId) || !IsDurableType(type))
            throw new InvalidDataException("INVALID_EVENT_ID_OR_TYPE");
        // The default JSON writer rejects non-finite floats, including arbitrary
        // nested gameMetadata values, before any disk/window accounting changes.
        var element = JsonSerializer.SerializeToElement(payload, TelematicProtocol.JsonOptions);
        ValidatePayload(type, element);
        var now = DateTimeOffset.UtcNow;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new GatewayEnvelope(TelematicProtocol.Version,
            messageId, type, instanceId, now, now, TelematicProtocol.Durable, element), TelematicProtocol.JsonOptions);
        if (bytes.Length > 120 * 1024) throw new InvalidDataException("EVENT_TOO_LARGE");
        return bytes;
    }

    public static GatewayEnvelope ValidateFrame(byte[] bytes, string instanceId)
    {
        if (bytes.Length > 120 * 1024) throw new InvalidDataException("EVENT_TOO_LARGE");
        var envelope = JsonSerializer.Deserialize<GatewayEnvelope>(bytes, TelematicProtocol.JsonOptions)
            ?? throw new InvalidDataException("INVALID_RECORD");
        if (envelope.ProtocolVersion != TelematicProtocol.Version || envelope.InstanceId != instanceId ||
            envelope.Delivery != TelematicProtocol.Durable || !IsIdentifier(envelope.MessageId) ||
            !IsDurableType(envelope.MessageType) || envelope.OccurredAt == default)
            throw new InvalidDataException("INVALID_RECORD");
        ValidatePayload(envelope.MessageType, envelope.Payload);
        return envelope;
    }

    private static string Required(JsonElement value, string name, bool identifier = false)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(field.GetString()) || (identifier && !IsIdentifier(field.GetString())))
            throw new InvalidDataException("INVALID_" + name.ToUpperInvariant());
        return field.GetString()!;
    }

    private static void Driver(JsonElement value)
    {
        Required(value, "displayName");
        Required(value, "gameDriverId", true);
        if (value.TryGetProperty("steamId", out var steam) && steam.ValueKind != JsonValueKind.Null &&
            (steam.ValueKind != JsonValueKind.String || !Regex.IsMatch(steam.GetString()!, "^[0-9]{17}$")))
            throw new InvalidDataException("INVALID_STEAM_ID");
    }

    public static void ValidatePayload(string type, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) throw new InvalidDataException("INVALID_PAYLOAD");
        CheckNumbers(p);
        if (type == "instance.ready") return;
        if (type == "instance.register")
        {
            if (Required(p, "game") != "ASSETTO_CORSA" || Required(p, "adapter") != "ASSETTOSERVER_NATIVE" ||
                !p.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != TelematicProtocol.Version)
                throw new InvalidDataException("INVALID_INSTANCE_RELATIONSHIP");
            Required(p, "pluginVersion");
            return;
        }
        if (type.StartsWith("incident.", StringComparison.Ordinal))
        {
            Required(p, "incidentId", true);
            if (!p.TryGetProperty("participants", out var players) || players.ValueKind != JsonValueKind.Array ||
                players.GetArrayLength() < (type == "incident.car_collision" ? 2 : 1))
                throw new InvalidDataException("INVALID_PARTICIPANTS");
            foreach (var driver in players.EnumerateArray()) Driver(driver);
            return;
        }
        Required(p, "eventId", true);
        Required(p, "sessionId", true);
        if (type.StartsWith("session.", StringComparison.Ordinal))
        {
            Required(p, "trackKey"); Required(p, "sessionType");
            if (p.TryGetProperty("generation", out var generation))
            {
                if (generation.ValueKind != JsonValueKind.Object || !Guid.TryParseExact(Required(generation, "lineage"), "N", out _) ||
                    !generation.TryGetProperty("ordinal", out var ordinal) || !ordinal.TryGetInt64(out var value) || value is < 1 or > 9_007_199_254_740_991L)
                    throw new InvalidDataException("GENERATION_INVALID");
            }
            return;
        }
        if (!p.TryGetProperty("driver", out var d)) throw new InvalidDataException("INVALID_DRIVER");
        Driver(d);
        if (type == "lap.completed")
        {
            Required(p, "trackKey");
            if (!p.TryGetProperty("lapTimeMs", out var time) || !time.TryGetInt64(out var ms) || ms < 0)
                throw new InvalidDataException("INVALID_LAP_TIME");
            if (Required(p, "validity") is not ("VALID" or "INVALID" or "UNKNOWN")) throw new InvalidDataException("INVALID_VALIDITY");
            if (p.TryGetProperty("sectorTimesMs", out var sectors) && sectors.ValueKind == JsonValueKind.Array)
                foreach (var sector in sectors.EnumerateArray())
                    if (!sector.TryGetInt64(out var split) || split < 0) throw new InvalidDataException("INVALID_SECTOR");
        }
    }

    private static void CheckNumbers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new InvalidDataException("NON_FINITE_NUMBER");
        if (element.ValueKind == JsonValueKind.Object) foreach (var field in element.EnumerateObject()) CheckNumbers(field.Value);
        if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) CheckNumbers(value);
    }
}
