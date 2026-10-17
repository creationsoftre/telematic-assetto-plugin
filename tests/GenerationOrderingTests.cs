using System.Text.Json;
using Xunit;

namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class GenerationOrderingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "telematic-generation-" + Guid.NewGuid().ToString("N"));
    private SpoolConfiguration Configuration => new() { RootDirectory = _root, MaxRecords = 100, MaxBytes = 1024 * 1024 };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void EmptySpoolRestartRetainsLineageAndHighWater()
    {
        NativeGeneration first;
        using (var spool = new DurableSpool("native", Configuration)) first = spool.AllocateGeneration("session-one");
        using var resumed = new DurableSpool("native", Configuration);
        Assert.Equal(0, resumed.Diagnostics().Pending);
        var next = resumed.AllocateGeneration("session-two");
        Assert.Equal(first.Lineage, next.Lineage); Assert.Equal(first.Ordinal + 1, next.Ordinal);
        Assert.Equal(next, resumed.AllocateGeneration("session-two"));
    }

    [Theory]
    [InlineData("generation-before-write", 2)]
    [InlineData("generation-after-flush", 2)]
    [InlineData("generation-after-commit", 3)]
    public void InterruptedAllocationNeverReusesAnEmittedGeneration(string seam, long expected)
    {
        using (var spool = new DurableSpool("native", Configuration)) spool.AllocateGeneration("first");
        using (var failing = new DurableSpool("native", Configuration, point => { if (point == seam) throw new IOException("synthetic process interruption"); }))
            Assert.Throws<IOException>(() => failing.AllocateGeneration("second"));
        using var recovered = new DurableSpool("native", Configuration);
        Assert.Equal(expected, recovered.AllocateGeneration("third").Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptInitializedStateFailsClosed(bool corrupt)
    {
        string path;
        using (var spool = new DurableSpool("native", Configuration)) { spool.AllocateGeneration("first"); path = Path.Combine(spool.Root, "generation.json"); }
        if (corrupt) File.WriteAllText(path, "{}"); else File.Delete(path);
        using var recovered = new DurableSpool("native", Configuration);
        Assert.Throws<InvalidDataException>(() => recovered.AllocateGeneration("second"));
    }

    [Fact]
    public void PersistedEventReplayRetainsGenerationAndNativeBinding()
    {
        NativeGeneration generation;
        using (var spool = new DurableSpool("native", Configuration))
        {
            generation = spool.AllocateGeneration("session");
            var payload = new SessionEventPayload("event", "session", "practice", "Practice", "ks_laguna_seca", null, "2026-10-04T12:00:00Z", Generation: generation);
            Assert.True(spool.TryPersist(DurableEventValidation.Serialize("native", "message", "session.started", payload), out _));
        }
        using var replay = new DurableSpool("native", Configuration);
        var entry = Assert.Single(replay.Eligible(new HashSet<string>(), 10));
        using var document = JsonDocument.Parse(replay.Read(entry)!);
        Assert.Equal(generation.Lineage, document.RootElement.GetProperty("payload").GetProperty("generation").GetProperty("lineage").GetString());
        Assert.Equal(generation.Ordinal, document.RootElement.GetProperty("payload").GetProperty("generation").GetProperty("ordinal").GetInt64());
        Assert.Equal("session", document.RootElement.GetProperty("payload").GetProperty("sessionId").GetString());
        Assert.True(replay.Retire("message")); Assert.False(replay.Retire("message"));
        Assert.Equal(generation.Ordinal + 1, replay.AllocateGeneration("next").Ordinal);
    }
}
