using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class CleanLapInteropTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "native-clean-lap-v1.json"));

    [Fact]
    public void ConcreteInteropFixtureRoundTripsWithExactNativeSourceAndCanonicalTime()
    {
        Assert.Equal(8, Environment.Version.Major);
        var envelope = DurableEventValidation.ValidateFrame(Encoding.UTF8.GetBytes(Fixture), "native-fixture");
        var lap = envelope.Payload.Deserialize<LapEventPayload>(TelematicProtocol.JsonOptions)!;
        Assert.Equal("UNKNOWN", lap.Validity); Assert.Equal(0, lap.Cuts);
        var validation = lap.RecordValidation!;
        Assert.Equal("ASSETTO_SERVER_NATIVE", validation.Evidence.Source);
        Assert.Equal(lap.OccurredAt, validation.Evidence.IntervalEnd);
        Assert.Equal(lap.LapTimeMs, (DateTimeOffset.Parse(validation.Evidence.IntervalEnd) - DateTimeOffset.Parse(validation.Evidence.IntervalStart)).TotalMilliseconds);
        Assert.Equal(validation, CleanLapValidation.Evaluate(lap.LapTimeMs, validation.Evidence));
        Assert.False(validation.Accepted); Assert.False(validation.Evidence.CoverageComplete);
        var roundTrip = JsonSerializer.SerializeToElement(lap, TelematicProtocol.JsonOptions);
        Assert.Equal(envelope.Payload.GetProperty("recordValidation").GetProperty("evidence").GetProperty("source").GetString(),
            roundTrip.GetProperty("recordValidation").GetProperty("evidence").GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("accepted", true)]
    [InlineData("source", "assetto_server_native")]
    [InlineData("coverageComplete", true)]
    public void InconsistentClaimsCannotEnterSpool(string field, object value)
    {
        var fixture = JsonNode.Parse(Fixture)!;
        var validation = fixture["payload"]!["recordValidation"]!;
        var target = field == "accepted" ? validation : validation["evidence"]!;
        target[field] = JsonSerializer.SerializeToNode(value);
        Assert.Throws<InvalidDataException>(() => DurableEventValidation.ValidateFrame(Encoding.UTF8.GetBytes(fixture.ToJsonString()), "native-fixture"));
    }

    [Fact]
    public void OptionalFieldAbsenceRemainsLegacyAndIsNotRewritten()
    {
        var fixture = JsonNode.Parse(Fixture)!;
        fixture["payload"]!.AsObject().Remove("recordValidation");
        var envelope = DurableEventValidation.ValidateFrame(Encoding.UTF8.GetBytes(fixture.ToJsonString()), "native-fixture");
        Assert.Null(envelope.Payload.Deserialize<LapEventPayload>(TelematicProtocol.JsonOptions)!.RecordValidation);
        Assert.False(envelope.Payload.TryGetProperty("recordValidation", out _));
    }
}
