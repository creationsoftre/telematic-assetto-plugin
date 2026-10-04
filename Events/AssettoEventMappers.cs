using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using AssettoServer.Network.Tcp;
using AssettoServer.Shared.Network.Packets.Outgoing;

namespace Telematic.AssettoServer.Plugin;

public static class AssettoEventMappers
{
    public static DriverReference Driver(ACTcpClient client, bool steamAuthEnabled)
    {
        var metadata = new Dictionary<string, object?> { ["steamAuthEnabled"] = steamAuthEnabled, ["identityVerified"] = steamAuthEnabled };
        if (!steamAuthEnabled) metadata["observedGuid"] = client.Guid.ToString();
        if (client.OwnerGuid.HasValue && client.OwnerGuid.Value != client.Guid) metadata["ownerGuid"] = client.OwnerGuid.Value.ToString();
        return new DriverReference(client.Name ?? "", steamAuthEnabled ? client.Guid.ToString() : null, client.SessionId.ToString(), client.EntryCar?.Model, client.EntryCar?.Skin, metadata);
    }

    public static DriverReference PartialDriver(EntryCar car)
        => new(car.AiName ?? $"car-{car.SessionId}", null, car.SessionId.ToString(), car.Model, car.Skin, new Dictionary<string, object?> { ["identityVerified"] = false, ["partialTarget"] = true, ["aiControlled"] = car.AiControlled });

    public static string SessionType(SessionType native) => native switch
    {
        AssettoServer.Shared.Model.SessionType.Booking => "custom",
        AssettoServer.Shared.Model.SessionType.Practice => "practice",
        AssettoServer.Shared.Model.SessionType.Qualifying => "qualifying",
        AssettoServer.Shared.Model.SessionType.Race => "race",
        _ => "unknown"
    };

    public static SessionEventPayload Session(string eventId, string sessionId, SessionState state, ACServerConfiguration configuration)
    {
        var native = state.Configuration.Type.ToString();
        return new SessionEventPayload(eventId, sessionId, SessionType(state.Configuration.Type), native, configuration.Server.Track, configuration.Server.TrackConfig, DateTimeOffset.UtcNow.ToString("O"), new Dictionary<string, object?> { ["nativeSessionName"] = state.Configuration.Name, ["nativeSessionId"] = state.Configuration.Id });
    }

    public static LapEventPayload Lap(ACTcpClient client, SessionState state, ACServerConfiguration configuration, string sessionId, LapCompletedOutgoing packet, IReadOnlyList<long>? sectors)
    {
        var number = state.Results?.TryGetValue(client.SessionId, out var result) == true ? result.NumLaps : null;
        return new LapEventPayload(Guid.NewGuid().ToString("N"), sessionId, Driver(client, configuration.Extra.UseSteamAuth), configuration.Server.Track, configuration.Server.TrackConfig, packet.LapTime, number, packet.Cuts, "unknown", sectors, client.EntryCar.Status.CurrentTyreCompound, DateTimeOffset.UtcNow.ToString("O"), new Dictionary<string, object?> { ["validitySource"] = "assetto_server_cuts_preserved" });
    }
}
