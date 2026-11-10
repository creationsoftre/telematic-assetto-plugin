using System.Text.Json;
using Xunit;

namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class LapRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lap-retention-" + Guid.NewGuid().ToString("N"));
    private SpoolConfiguration Configuration => new() { RootDirectory = _root, MaxRecords = 1, MaxBytes = 1024 * 1024 };
    private static byte[] Frame(string id = "lap") => DurableEventValidation.Serialize("native", id, "lap.completed",
        new LapEventPayload(id, "session", new("Fixture", "76561198000000000", "0"), "track", null, 100000, 2, 0, "UNKNOWN", null, null, "2026-11-09T22:01:40Z"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void ArchiveSurvivesLostAckRestartPositiveAckAndPermanentRejection()
    {
        var frame = Frame(); string path; byte[] archive;
        using (var spool = new DurableSpool("native", Configuration))
        {
            Assert.True(spool.TryPersist(frame, out var entry)); Assert.NotNull(spool.Read(entry!));
            path = Directory.GetFiles(spool.ArchiveRoot).Single(); archive = File.ReadAllBytes(path);
        } // Delivery happened but ACK was lost.
        using (var resumed = new DurableSpool("native", Configuration))
        {
            var entry = Assert.Single(resumed.Eligible(new HashSet<string>(), 10));
            Assert.Equal("lap", entry.MessageId);
            Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(frame).RootElement, TelematicProtocol.JsonOptions), resumed.Read(entry));
            Assert.True(resumed.Retire("lap")); Assert.False(resumed.Retire("lap"));
            Assert.Equal(archive, File.ReadAllBytes(path));
            Assert.True(resumed.TryPersist(Frame("rejected"), out _)); Assert.True(resumed.Reject("rejected", "INVALID"));
        }
        using var restarted = new DurableSpool("native", Configuration);
        Assert.Empty(restarted.Eligible(new HashSet<string>(), 10)); Assert.Equal(2, restarted.Diagnostics().ArchivedLaps);
        Assert.Equal(archive, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("archive-before-write", 0)]
    [InlineData("archive-after-flush", 1)]
    [InlineData("archive-after-rename", 1)]
    public void ArchiveWriteFailuresNeverBecomeDeliverySuccessAndRecoverWithoutDeletingBytes(string stage, int recovered)
    {
        using (var spool = new DurableSpool("native", Configuration, point => { if (point == stage) throw new IOException("ENOSPC"); }))
        {
            Assert.False(spool.TryPersist(Frame(), out _)); Assert.Equal(0, spool.Diagnostics().Pending);
            Assert.False(spool.Diagnostics().StorageHealthy); Assert.Equal("LAP_ARCHIVE_WRITE_FAILED", spool.Diagnostics().ArchiveError);
        }
        using var resumed = new DurableSpool("native", Configuration);
        Assert.Equal(recovered, resumed.Diagnostics().ArchivedLaps);
        Assert.Equal(recovered, resumed.Eligible(new HashSet<string>(), 10).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPendingLapCannotBeSentRetiredOrRejectedWhenArchivingFails(bool reject)
    {
        using var spool = new DurableSpool("native", Configuration, point => { if (point == "archive-before-write") throw new IOException("ENOSPC"); });
        // Simulate a legacy pending lap written by the pre-archive version.
        var frame = Frame(); var envelope = DurableEventValidation.ValidateFrame(frame, "native");
        File.WriteAllBytes(Path.Combine(spool.Root, $"{DateTime.UtcNow.Ticks:D19}-{envelope.MessageId}.pending.json"), DurableSpool.EncodeRecord(frame));
        spool.Dispose();
        using var resumed = new DurableSpool("native", Configuration, point => { if (point == "archive-before-write") throw new IOException("ENOSPC"); });
        var entry = Assert.Single(resumed.Eligible(new HashSet<string>(), 10));
        Assert.Null(resumed.Read(entry));
        Assert.False(reject ? resumed.Reject("lap", "INVALID") : resumed.Retire("lap"));
        Assert.Single(Directory.GetFiles(resumed.Root, "*.pending.json")); Assert.Equal(1, resumed.Diagnostics().Pending);
        Assert.False(resumed.Diagnostics().StorageHealthy);
    }

    [Fact]
    public void FullSpoolStillArchivesNewObservationAndNeverEvictsOldOne()
    {
        using var spool = new DurableSpool("native", Configuration);
        Assert.True(spool.TryPersist(Frame("old"), out _)); Assert.False(spool.TryPersist(Frame("new"), out _));
        Assert.Equal("old", Assert.Single(spool.Eligible(new HashSet<string>(), 10)).MessageId);
        Assert.Equal(2, Directory.GetFiles(spool.ArchiveRoot).Length); Assert.Equal("SPOOL_FULL", spool.Diagnostics().ErrorCode);
    }

    [Theory]
    [InlineData("archive-before-settle", false)]
    [InlineData("archive-settle-after-flush", false)]
    [InlineData("archive-settle-after-rename", false)]
    [InlineData("archive-before-settle", true)]
    public void FailedSettlementCannotDeleteOrRetirePendingLap(string stage, bool reject)
    {
        using (var spool = new DurableSpool("native", Configuration, point => { if (point == stage) throw new IOException("ENOSPC"); }))
        {
            Assert.True(spool.TryPersist(Frame(), out _));
            Assert.False(reject ? spool.Reject("lap", "INVALID") : spool.Retire("lap"));
            Assert.Equal(1, spool.Diagnostics().Pending); Assert.False(spool.Diagnostics().StorageHealthy);
            Assert.Equal("LAP_ARCHIVE_SETTLEMENT_FAILED", spool.Diagnostics().ArchiveError);
            Assert.Single(Directory.GetFiles(spool.Root, "*.pending.json"));
            Assert.Single(Directory.GetFiles(spool.ArchiveRoot, "*.json"));
        }
        using var resumed = new DurableSpool("native", Configuration);
        Assert.Single(resumed.Eligible(new HashSet<string>(), 10)); Assert.True(resumed.Retire("lap"));
        Assert.Single(Directory.GetFiles(resumed.ArchiveRoot, "*.json"));
    }

    [Fact]
    public void SpoolWriteFailureReplaysFlushedArchiveWithSameImmutableFrame()
    {
        var frame = Frame();
        var configuration = new SpoolConfiguration { RootDirectory = _root, MaxRecords = 2, MaxBytes = 1024 * 1024 };
        using (var spool = new DurableSpool("native", configuration, point => { if (point == "before-write") throw new IOException("ENOSPC"); }))
        {
            Assert.False(spool.TryPersist(frame, out _));
            Assert.Equal(1, spool.Diagnostics().ArchivedLaps); Assert.Equal(1, spool.Diagnostics().ArchivedAwaitingDelivery);
            Assert.False(spool.Diagnostics().StorageHealthy);
        }
        using var resumed = new DurableSpool("native", configuration);
        var entry = Assert.Single(resumed.Eligible(new HashSet<string>(), 10));
        Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(frame).RootElement, TelematicProtocol.JsonOptions), resumed.Read(entry));
        Assert.Equal(0, resumed.Diagnostics().ArchivedAwaitingDelivery);
    }

    [Fact]
    public void ArchiveConflictAndCorruptionArePreservedAndVisible()
    {
        string path;
        using (var spool = new DurableSpool("native", Configuration))
        {
            Assert.True(spool.TryPersist(Frame(), out _)); Assert.True(spool.Retire("lap"));
            path = Directory.GetFiles(spool.ArchiveRoot, "*.json").Single();
            var original = File.ReadAllBytes(path);
            Assert.False(spool.TryPersist(Frame(), out _)); Assert.Equal(original, File.ReadAllBytes(path));
        }
        File.WriteAllText(path, "{broken");
        using var resumed = new DurableSpool("native", Configuration);
        Assert.False(resumed.Diagnostics().StorageHealthy); Assert.Equal("LAP_ARCHIVE_RECOVERY_FAILED", resumed.Diagnostics().ArchiveError);
        Assert.True(resumed.TryPersist(Frame("another"), out _)); Assert.False(resumed.Diagnostics().StorageHealthy);
        Assert.Equal("{broken", File.ReadAllText(path));
    }
}
