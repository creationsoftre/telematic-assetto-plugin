using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using Xunit;

namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class CleanLapEvidenceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-09T22:00:00Z");
    private static CleanLapEvidence Evidence => new("ASSETTO_SERVER_NATIVE", true, true, false, false,
        Start.ToString("O"), Start.AddSeconds(100).ToString("O"));

    [Theory]
    [InlineData(true, true, false, false, 100000, "ACCEPTED")]
    [InlineData(true, true, true, false, 100000, "TIMING_CUT")]
    [InlineData(true, true, false, true, 100000, "COLLISION")]
    [InlineData(false, true, false, false, 100000, "TELEMETRY_INCOMPLETE")]
    [InlineData(true, false, false, false, 100000, "IDENTITY_UNBOUND")]
    [InlineData(true, true, false, false, 0, "INVALID_DURATION")]
    [InlineData(true, true, false, false, 2147483648L, "INVALID_DURATION")]
    public void IndependentPolicyRequiresEveryCleanCondition(bool coverage, bool identity, bool cut, bool collision, long ms, string reason)
    {
        var result = CleanLapValidation.Evaluate(ms, Evidence with
        { CoverageComplete = coverage, IdentityMatched = identity, TimingCut = cut, Collision = collision });
        Assert.Equal(reason, result.Reason); Assert.Equal(reason == "ACCEPTED", result.Accepted);
    }

    [Fact]
    public void WrongSourceAndInvalidIntervalsFailClosed()
    {
        Assert.Equal("UNSUPPORTED_SOURCE", CleanLapValidation.Evaluate(100000, Evidence with { Source = "assetto_server_native" }).Reason);
        Assert.Equal("INVALID_EVIDENCE", CleanLapValidation.Evaluate(100000, Evidence with { IntervalStart = "yesterday" }).Reason);
        Assert.Equal("INVALID_EVIDENCE", CleanLapValidation.Evaluate(100000, Evidence with { IntervalEnd = Evidence.IntervalStart }).Reason);
        Assert.Equal("INVALID_EVIDENCE", CleanLapValidation.Evaluate(100000, Evidence with { IntervalStart = "2026-11-09T22:00:00" }).Reason);
        Assert.False(CleanLapValidation.Evaluate(100001, Evidence).Accepted);
    }

    [Fact]
    public void ObservedBoundariesNeverUpgradeNativeCollisionCapability()
    {
        var continuity = new NativeLapContinuity(Start);
        var first = continuity.Complete("s", "driver", 0, true, false, Start.AddSeconds(10));
        var second = continuity.Complete("s", "driver", 0, true, false, Start.AddSeconds(110));
        Assert.Equal(first.IntervalEnd, second.IntervalStart);
        Assert.False(first.CoverageComplete); Assert.False(second.CoverageComplete);
        Assert.Equal("TELEMETRY_INCOMPLETE", CleanLapValidation.Evaluate(100000, second).Reason);
        var switched = continuity.Complete("next", "driver", 0, true, false, Start.AddSeconds(210));
        Assert.NotEqual(second.IntervalEnd, switched.IntervalStart); Assert.False(switched.CoverageComplete);
        Assert.False(new NativeLapContinuity(Start.AddSeconds(200)).Complete("s", "driver", 0, true, false, Start.AddSeconds(210)).CoverageComplete);
    }

    private static void Set(object instance, string property, object? value) => instance.GetType()
        .GetField($"<{property}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Invoke<T>(object instance, string name, ACTcpClient client, T args) where T : EventArgs =>
        (instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)
            as global::AssettoServer.Server.EventHandler<ACTcpClient, T>)?.Invoke(client, args);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "native-lap-" + Guid.NewGuid().ToString("N"));
        public TelematicConfiguration Config { get; }
        public TelematicGatewayClient Gateway { get; }
        public EntryCarManager Cars { get; }
        public SessionManager Sessions { get; }
        public AssettoEventBridge Bridge { get; }
        public DurableSpool Spool => (DurableSpool)typeof(TelematicGatewayClient).GetField("_spool", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Gateway)!;
        public Fixture(bool session = true, bool auth = true)
        {
            Config = new() { Enabled = true, InstanceId = "native", PluginToken = "fixture", Spool = new() { RootDirectory = Root } };
            Gateway = new(Config, new OfflineTransport());
            var configuration = (ACServerConfiguration)RuntimeHelpers.GetUninitializedObject(typeof(ACServerConfiguration));
            Set(configuration, "Extra", new ACExtraConfiguration { UseSteamAuth = auth });
            Set(configuration, "Server", new ServerConfiguration { Track = "ks_laguna_seca" });
            Cars = (EntryCarManager)RuntimeHelpers.GetUninitializedObject(typeof(EntryCarManager));
            var slots = Enumerable.Range(0, 2).Select(i => {
                var car = (EntryCar)RuntimeHelpers.GetUninitializedObject(typeof(EntryCar));
                Set(car, "SessionId", (byte)i); Set(car, "Model", "car"); return car;
            }).ToArray();
            Set(Cars, "EntryCars", slots);
            Sessions = (SessionManager)RuntimeHelpers.GetUninitializedObject(typeof(SessionManager));
            if (session) Set(Sessions, "CurrentSession", NewSession());
            Bridge = new(Cars, Sessions, configuration, Gateway, new VehicleStateCache(configuration)); Bridge.Start();
        }
        public SessionState NewSession() => new(new SessionConfiguration { Id = 0, Name = "Practice", Type = SessionType.Practice }, Sessions);
        public ACTcpClient Join(int slot = 0)
        {
            var client = (ACTcpClient)RuntimeHelpers.GetUninitializedObject(typeof(ACTcpClient));
            client.Name = "Fixture"; client.SessionId = (byte)slot; client.IsConnected = true;
            Set(client, "Guid", 76561198000000000UL + (ulong)slot); Set(client, "EntryCar", Cars.EntryCars[slot]);
            Set(Cars.EntryCars[slot], "Client", client); Invoke(Cars, "ClientConnected", client, EventArgs.Empty); return client;
        }
        public void Lap(ACTcpClient client, byte cuts = 0, byte? packetSlot = null, uint duration = 100000) =>
            Invoke(client, "LapCompleted", client, new LapCompletedEventArgs(new LapCompletedOutgoing { SessionId = packetSlot ?? client.SessionId, LapTime = duration, Cuts = cuts }));
        public GatewayEnvelope[] Archive => Directory.GetFiles(Spool.ArchiveRoot, "*.json").Select(path =>
            JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("envelope").Deserialize<GatewayEnvelope>(TelematicProtocol.JsonOptions)!).ToArray();
        public void Dispose() { Bridge.Stop(); Gateway.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class OfflineTransport : IGatewayTransport
    {
        public Task ConnectAsync(Uri uri, string token, CancellationToken ct) => throw new NotSupportedException();
        public Task SendAsync(string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ReceiveAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(0, "TELEMETRY_INCOMPLETE")]
    [InlineData(1, "TIMING_CUT")]
    public void RealNativeCallbacksPreserveRawUnknownAndEndEqualsOccurredAt(byte cuts, string reason)
    {
        using var f = new Fixture(); var client = f.Join(); f.Lap(client, cuts); f.Lap(client, cuts);
        Assert.Equal(2, f.Archive.Length);
        foreach (var lap in f.Archive)
        {
            var p = lap.Payload; var v = p.GetProperty("recordValidation");
            Assert.Equal("UNKNOWN", p.GetProperty("validity").GetString()); Assert.Equal(cuts, p.GetProperty("cuts").GetByte());
            Assert.Equal(reason, v.GetProperty("reason").GetString()); Assert.False(v.GetProperty("accepted").GetBoolean());
            Assert.Equal(p.GetProperty("occurredAt").GetString(), v.GetProperty("evidence").GetProperty("intervalEnd").GetString());
            Assert.False(v.GetProperty("evidence").GetProperty("coverageComplete").GetBoolean());
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void MissingSessionUnauthenticatedOrWrongPacketSlotAreArchivedLocally(bool session, bool auth, bool wrongSlot)
    {
        using var f = new Fixture(session, auth); var client = f.Join(); f.Lap(client, packetSlot: wrongSlot ? (byte)1 : (byte)0);
        var lap = Assert.Single(f.Archive);
        Assert.Equal("local-only", lap.Delivery); Assert.False(lap.Payload.TryGetProperty("sessionId", out _));
        Assert.Equal("IDENTITY_UNBOUND", lap.Payload.GetProperty("recordValidation").GetProperty("reason").GetString());
        Assert.DoesNotContain(f.Spool.Eligible(new HashSet<string>(), 100), e => e.MessageType == "lap.completed");
    }

    [Fact]
    public void FailedPersistenceDoesNotPoisonCollisionDeduplication()
    {
        using var f = new Fixture(); var source = f.Join(); var target = f.Join(1);
        var old = f.Spool; old.Dispose();
        var fail = true;
        var replacement = new DurableSpool("native", f.Config.Spool, point => { if (fail && point == "before-write") throw new IOException("disk full"); });
        typeof(TelematicGatewayClient).GetField("_spool", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Gateway, replacement);
        var collision = new CollisionEventArgs(target.EntryCar, 10, Vector3.One, Vector3.Zero);
        Invoke(source, "Collision", source, collision);
        Assert.True(f.Gateway.Diagnostics().CaptureFailures > 0);
        Assert.Empty((System.Collections.IDictionary)typeof(AssettoEventBridge).GetField("_recentCollisions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Bridge)!);
        fail = false; Invoke(target, "Collision", target, new CollisionEventArgs(source.EntryCar, 10, Vector3.One, Vector3.Zero));
        Assert.Single(f.Spool.Eligible(new HashSet<string>(), 100), e => e.MessageType == "incident.car_collision");
        Invoke(source, "Collision", source, collision);
        Assert.Single(f.Spool.Eligible(new HashSet<string>(), 100), e => e.MessageType == "incident.car_collision");
    }

    [Fact]
    public void DelayedCollisionDoesNotMutateAlreadyArchivedOrSpooledLap()
    {
        using var f = new Fixture(); var client = f.Join(); f.Lap(client);
        var entry = Assert.Single(f.Spool.Eligible(new HashSet<string>(), 100), e => e.MessageType == "lap.completed");
        var before = f.Spool.Read(entry); var archive = File.ReadAllBytes(Directory.GetFiles(f.Spool.ArchiveRoot).Single());
        Invoke(client, "Collision", client, new CollisionEventArgs(null, 10, Vector3.One, Vector3.Zero));
        Assert.Equal(before, f.Spool.Read(entry)); Assert.Equal(archive, File.ReadAllBytes(Directory.GetFiles(f.Spool.ArchiveRoot).Single()));
    }

    [Fact]
    public void SessionSwitchAndReconnectKeepFirstLapIncompleteAndOldClientUnbound()
    {
        using var f = new Fixture(); var client = f.Join(); f.Lap(client);
        var previous = f.Sessions.CurrentSession; var next = f.NewSession(); Set(f.Sessions, "CurrentSession", next);
        (typeof(SessionManager).GetField("SessionChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Sessions)
            as global::AssettoServer.Server.EventHandler<SessionManager, SessionChangedEventArgs>)!
            .Invoke(f.Sessions, new SessionChangedEventArgs(previous, next));
        f.Lap(client);
        var replacement = f.Join(); f.Lap(client); f.Lap(replacement);
        Assert.Equal(4, f.Archive.Length);
        Assert.All(f.Archive, lap => Assert.False(lap.Payload.GetProperty("recordValidation").GetProperty("evidence").GetProperty("coverageComplete").GetBoolean()));
        Assert.Single(f.Archive, lap => lap.Delivery == "local-only");
        Assert.Equal(2, f.Archive.Where(lap => lap.Delivery == "durable").Select(lap => lap.Payload.GetProperty("sessionId").GetString()).Distinct().Count());
    }

    [Fact]
    public void InactiveSessionArchiveFailureIncrementsHealthCounterWithoutManufacturingPendingLap()
    {
        using var f = new Fixture(session: false); var client = f.Join(); f.Spool.Dispose();
        var failuresBefore = f.Gateway.Diagnostics().CaptureFailures;
        var replacement = new DurableSpool("native", f.Config.Spool, point => { if (point == "archive-before-write") throw new IOException("ENOSPC"); });
        typeof(TelematicGatewayClient).GetField("_spool", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Gateway, replacement);
        f.Lap(client);
        Assert.Equal(failuresBefore + 1, f.Gateway.Diagnostics().CaptureFailures);
        Assert.False(f.Gateway.Diagnostics().Spool!.StorageHealthy);
        Assert.Equal("LAP_ARCHIVE_WRITE_FAILED", f.Gateway.Diagnostics().Spool!.ArchiveError);
        Assert.Equal(1, f.Bridge.Diagnostics().CompletedLaps);
        Assert.False(f.Bridge.Diagnostics().NativeCleanLapCoverageAvailable);
        Assert.Empty(f.Archive);
    }

    [Fact]
    public void UnboundArchiveSurvivesRestartAndNeverBecomesDeliveryEligible()
    {
        using var f = new Fixture(session: false); f.Lap(f.Join());
        var original = Assert.Single(f.Archive);
        f.Bridge.Stop(); f.Gateway.Dispose();
        using var resumed = new DurableSpool("native", f.Config.Spool);
        Assert.Equal(1, resumed.Diagnostics().ArchivedLaps);
        Assert.Empty(resumed.Eligible(new HashSet<string>(), 100));
        using var record = JsonDocument.Parse(File.ReadAllBytes(Directory.GetFiles(resumed.ArchiveRoot, "*.json").Single()));
        Assert.Equal(original.MessageId, record.RootElement.GetProperty("envelope").GetProperty("messageId").GetString());
        Assert.Equal("local-only", record.RootElement.GetProperty("envelope").GetProperty("delivery").GetString());
    }
}
