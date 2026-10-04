using System.Text.Json;
using AssettoServer.Shared.Model;

namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void EnvelopeUsesSupervisorV1FieldNames()
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new { protocolVersion = 1, messageId = "m1", messageType = "instance.heartbeat", instanceId = "i1", occurredAt = now, sentAt = now, delivery = "ephemeral", payload = new InstanceHeartbeat() };
        var json = JsonSerializer.Serialize(envelope, TelematicProtocol.JsonOptions);
        Assert.Contains("\"protocolVersion\":1", json);
        Assert.Contains("\"messageType\":\"instance.heartbeat\"", json);
        Assert.Contains("\"lifecycle\":\"READY\"", json);
    }

    [Fact]
    public void ConfigurationRedactsTokenFromToString()
    {
        var configuration = new TelematicConfiguration { Enabled = true, PluginToken = "secret-token" };
        var text = configuration.ToString();
        Assert.DoesNotContain("secret-token", text);
        Assert.Contains("<redacted>", text);
    }

    [Theory]
    [InlineData(SessionType.Booking, "custom")]
    [InlineData(SessionType.Practice, "practice")]
    [InlineData(SessionType.Qualifying, "qualifying")]
    [InlineData(SessionType.Race, "race")]
    public void SessionTypesMapExplicitly(SessionType native, string expected)
        => Assert.Equal(expected, AssettoEventMappers.SessionType(native));

    [Fact]
    public void FutureSessionTypesBecomeUnknown()
        => Assert.Equal("unknown", AssettoEventMappers.SessionType((SessionType)99));

    [Fact]
    public void PlayerSnapshotIsEphemeralStateNotLifecycleHistory()
    {
        var payload = new PlayersSnapshotPayload("2026-10-03T00:00:00Z", [new DriverReference("Driver", "76561198000000000", "4")]);
        var json = JsonSerializer.Serialize(payload, TelematicProtocol.JsonOptions);
        Assert.Contains("\"capturedAt\"", json);
        Assert.Contains("\"players\"", json);
        Assert.DoesNotContain("connectedAt", json);
    }

    [Fact]
    public void CollisionMetadataPreservesCallbackTimingAndRelativePosition()
    {
        var payload = new CollisionEventPayload("incident-1", "session-1", [new DriverReference("Driver", "1", "4")], 42.3f, new WorldPosition(1, 2, 3), "2026-10-03T00:00:00Z", new Dictionary<string, object?> { ["timestampSource"] = "plugin_callback", ["relativePosition"] = new WorldPosition(4, 5, 6) });
        var json = JsonSerializer.Serialize(payload, TelematicProtocol.JsonOptions);
        Assert.Contains("\"impactSpeedKmh\":42.3", json);
        Assert.Contains("\"timestampSource\":\"plugin_callback\"", json);
        Assert.Contains("\"relativePosition\"", json);
    }

    [Fact]
    public void TelemetryIsDisabledByDefaultAndUsesTwoHertz()
    {
        var telemetry = new TelematicConfiguration().Telemetry;
        Assert.False(telemetry.Enabled);
        Assert.Equal(2, telemetry.SampleRateHz);
        Assert.Equal(5, telemetry.StaleSeconds);
    }

    [Fact]
    public void TelemetryPreservesVerifiedFieldsAndRawMetadata()
    {
        var payload = new TelemetrySnapshotPayload("2026-10-03T00:00:00Z", [new VehicleTelemetryPayload(
            new DriverReference("Driver", null, "4"), new TelemetryVector(1, 2, 3), new TelemetryVector(4, 5, 6),
            0.25f, 7000, 4, new Dictionary<string, object?> { ["gasRaw"] = 200, ["steerAngleRaw"] = 127 })], "session-1");
        var json = JsonSerializer.Serialize(payload, TelematicProtocol.JsonOptions);
        Assert.Contains("\"normalizedPosition\":0.25", json);
        Assert.Contains("\"gasRaw\":200", json);
        Assert.Contains("\"steerAngleRaw\":127", json);
        Assert.DoesNotContain("\"brake\"", json);
        Assert.DoesNotContain("\"steering\"", json);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(50)]
    public void TelemetrySnapshotFrameSizeScalesWithinGatewayLimit(int playerCount)
    {
        var players = Enumerable.Range(0, playerCount).Select(index => new VehicleTelemetryPayload(
            new DriverReference($"Driver {index}", null, index.ToString(), "car", "skin"),
            new TelemetryVector(index, index + 1, index + 2), new TelemetryVector(1, 2, 3), 0.5f, 7000, 4,
            new Dictionary<string, object?> { ["gasRaw"] = 128, ["steerAngleRaw"] = 127 })).ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new TelemetrySnapshotPayload("2026-10-03T00:00:00Z", players, "session-1"), TelematicProtocol.JsonOptions);
        Assert.InRange(json.Length, 1, 128 * 1024);
    }
}
