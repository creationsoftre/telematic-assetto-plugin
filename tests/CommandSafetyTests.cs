using System.Text.Json;
using Xunit;
namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class CommandSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "telematic-command-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
    private static CommandRequest Request(string message = "hello") => new("cmd", "native", TelematicProtocol.CapabilityChatBroadcast, JsonSerializer.SerializeToElement(new { message }), Now.AddSeconds(-1), Now.AddMinutes(1), "key");
    private DurableSpool Open() => new("native", new SpoolConfiguration { RootDirectory = root, MaxRecords = 100, MaxBytes = 1024 * 1024 });
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void NativePacketCodepointBoundary(int size, bool accepted) => Assert.Equal(accepted, CommandSafety.Validate(Request(new string('x', size)), "native", Now) is null);
    [Fact] public void SupplementaryUnicodeUsesCodepointsNotUtf16Units() => Assert.Null(CommandSafety.Validate(Request(string.Concat(Enumerable.Repeat("🏁", 255))), "native", Now));
    [Fact] public void TimestampOffsetsAreRequiredWithoutLocalClockInference()
    {
        Assert.True(CommandSafety.HasExplicitTimestampOffsets(JsonSerializer.SerializeToElement(new { requestedAt = "2026-10-11T12:00:00Z", expiresAt = "2026-10-11T08:01:00-04:00" })));
        Assert.False(CommandSafety.HasExplicitTimestampOffsets(JsonSerializer.SerializeToElement(new { requestedAt = "2026-10-11T12:00:00", expiresAt = "2026-10-11T12:01:00Z" })));
    }
    [Fact] public void ExpiryIdentityAuthorizationAndUnsupportedFailClosed()
    {
        var request = Request();
        Assert.NotNull(CommandSafety.Validate(request with { ExpiresAt = Now }, "native", Now));
        Assert.NotNull(CommandSafety.Validate(request with { CommandId = "" }, "native", Now));
        Assert.NotNull(CommandSafety.Validate(request with { IdempotencyKey = null }, "native", Now));
        Assert.NotNull(CommandSafety.Validate(request, "another", Now));
        Assert.NotNull(CommandSafety.Validate(request with { CommandType = "ban" }, "native", Now));
        Assert.NotNull(CommandSafety.Validate(request with { Parameters = JsonSerializer.SerializeToElement(new { message = 123 }) }, "native", Now));
        Assert.Null(CommandSafety.Validate(request, "native", Now));
    }
    [Fact] public void DurableClaimSuppressesReplayAcrossReconnectAndRestart()
    {
        using (var spool = Open()) { Assert.Null(spool.ClaimCommand(Request())); Assert.Equal("COMMAND_REPLAY_UNCERTAIN", spool.ClaimCommand(Request())); }
        using var reopened = Open();
        Assert.Equal("COMMAND_REPLAY_UNCERTAIN", reopened.ClaimCommand(Request()));
        Assert.Equal("COMMAND_REPLAY_UNCERTAIN", reopened.ClaimCommand(Request() with { CommandId = "new-id" }));
        Assert.Equal("COMMAND_IDENTITY_CONFLICT", reopened.ClaimCommand(Request("different")));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrCorruptLedgerCannotReexecute(bool corrupt)
    {
        string path;
        using (var spool = Open()) { spool.ClaimCommand(Request()); path = Path.Combine(spool.Root, "commands.json"); }
        if (corrupt) File.WriteAllText(path, "{}"); else File.Delete(path);
        using var reopened = Open();
        Assert.Throws<InvalidDataException>(() => reopened.ClaimCommand(Request()));
    }
}
