using System.Text.Json;
using Xunit;

namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class NativeLapContextTests
{
    [Fact]
    public void SourceContextSurvivesDurableNativeSerializationWithoutValidityPromotion()
    {
        var payload = new LapEventPayload("lap-context", "session", new DriverReference("Fixture", "76561198074952048", "12", "car"),
            "track", "GP", 100000, 0, 0, "UNKNOWN", [0, 31000, 69000], " Soft ", "2026-10-11T12:00:00Z",
            new Dictionary<string, object?> { ["validitySource"] = "assetto_server_cuts_preserved", ["unknown"] = null, ["zero"] = 0 });
        var frame = DurableEventValidation.Serialize("native", "lap-context", "lap.completed", payload);
        var parsed = DurableEventValidation.ValidateFrame(frame, "native").Payload;
        Assert.Equal("GP", parsed.GetProperty("layoutKey").GetString());
        Assert.Equal(" Soft ", parsed.GetProperty("tyre").GetString());
        Assert.Equal(0, parsed.GetProperty("cuts").GetInt32());
        Assert.Equal(0, parsed.GetProperty("lapNumber").GetInt32());
        Assert.Equal(0, parsed.GetProperty("sectorTimesMs")[0].GetInt64());
        Assert.Equal("UNKNOWN", parsed.GetProperty("validity").GetString());
        Assert.Equal(JsonValueKind.Null, parsed.GetProperty("gameMetadata").GetProperty("unknown").ValueKind);
    }

    [Fact]
    public void UnsupportedSplitsAndTyresRemainAbsentRatherThanInferred()
    {
        var payload = new LapEventPayload("lap-legacy", "session", new DriverReference("Fixture", "76561198074952048", "12", "car"),
            "track", null, 100000, null, 0, "UNKNOWN", null, null, "2026-10-11T12:00:00Z");
        var parsed = DurableEventValidation.ValidateFrame(DurableEventValidation.Serialize("native", "lap-legacy", "lap.completed", payload), "native").Payload;
        Assert.False(parsed.TryGetProperty("sectorTimesMs", out _));
        Assert.False(parsed.TryGetProperty("tyre", out _));
        Assert.False(parsed.TryGetProperty("layoutKey", out _));
        Assert.False(parsed.TryGetProperty("lapNumber", out _));
        Assert.Equal(0, parsed.GetProperty("cuts").GetInt32());
    }
}
