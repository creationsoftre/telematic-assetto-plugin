using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;

namespace Telematic.AssettoServer.Plugin.Tests;

public sealed class DeliveryReliabilityTests
{
    private readonly ITestOutputHelper _output;
    public DeliveryReliabilityTests(ITestOutputHelper output) => _output = output;
    private static LapEventPayload Lap(object? metadata = null) => new("event-1", "session-1",
        new DriverReference("Driver", "76561198000000000", "4", "ks_bmw_m235i_racing"), "ks_laguna_seca", null,
        100307, 2, 0, "UNKNOWN", null, null, "2026-10-07T00:00:00Z",
        metadata is null ? null : new Dictionary<string, object?> { ["value"] = metadata });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "telematic-spool-test-" + Guid.NewGuid().ToString("N"));
        public TelematicConfiguration Config(int window = 1, int records = 20_000, long bytes = 100L * 1024 * 1024, string instance = "test-1") =>
            new() { Enabled = true, InstanceId = instance, PluginToken = "test-token", MaxPendingDurableMessages = window,
                ConnectTimeoutSeconds = 1, WriteTimeoutSeconds = 1, AckTimeoutSeconds = 1, RetryDelaySeconds = 1,
                Spool = new() { RootDirectory = Root, MaxRecords = records, MaxBytes = bytes } };
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Transport : IGatewayTransport
    {
        private Channel<string?> _receive = Channel.CreateUnbounded<string?>();
        public readonly ConcurrentQueue<GatewayEnvelope> Sent = new();
        public int Connections;
        public bool AutoAck = true, SilenceHandshake, StallWrites, FailFirstDurable;
        public int AckContract = 2;
        public Func<GatewayEnvelope, int, Acknowledgement?>? AckPolicy;
        private int _durableCount;
        public Action<GatewayEnvelope>? BeforeDurable;
        public Task ConnectAsync(Uri uri, string token, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Connections); _receive = Channel.CreateUnbounded<string?>();
            if (!SilenceHandshake) Push("connection.accepted", "test-1", new { protocolVersion = 1, durableAckVersion = AckContract });
            return Task.CompletedTask;
        }
        public async Task SendAsync(string text, CancellationToken token)
        {
            var envelope = JsonSerializer.Deserialize<GatewayEnvelope>(text, TelematicProtocol.JsonOptions)!;
            if (StallWrites) { await Task.Delay(Timeout.Infinite, token); return; }
            if (envelope.Delivery == TelematicProtocol.Durable)
            {
                BeforeDurable?.Invoke(envelope);
                var count = Interlocked.Increment(ref _durableCount);
                Sent.Enqueue(envelope);
                if (FailFirstDurable && count == 1) throw new IOException("injected pre-frame failure");
                var ack = AckPolicy?.Invoke(envelope, count);
                if (ack is not null) Push("ack", envelope.InstanceId, ack);
                else if (AutoAck) Push("ack", envelope.InstanceId, new Acknowledgement(envelope.MessageId, envelope.InstanceId, true));
            }
            else Sent.Enqueue(envelope);
        }
        public void Push(string type, string instance, object payload) => _receive.Writer.TryWrite(JsonSerializer.Serialize(
            new GatewayEnvelope(1, Guid.NewGuid().ToString("N"), type, instance, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                TelematicProtocol.Ephemeral, JsonSerializer.SerializeToElement(payload, TelematicProtocol.JsonOptions)), TelematicProtocol.JsonOptions));
        public void Disconnect() => _receive.Writer.TryWrite(null);
        public async Task<string?> ReceiveAsync(CancellationToken token) => await _receive.Reader.ReadAsync(token);
        public Task CloseAsync(CancellationToken token) { _receive.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NormalDeliveryWritesBeforeSendAndPositiveAckRetiresExactlyOnce()
    {
        using var f = new Fixture(); var t = new Transport(); using var g = new TelematicGatewayClient(f.Config(), t);
        Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap()));
        t.BeforeDurable = envelope => Assert.Contains(Directory.EnumerateFiles(f.Root, "*.pending.json", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(envelope.MessageId));
        using var stop = new CancellationTokenSource(); var run = g.RunAsync(stop.Token);
        await Until(() => g.Diagnostics().Accepted >= 3 && g.Diagnostics().Spool!.Pending == 0);
        var lap = t.Sent.Single(x => x.MessageType == "lap.completed");
        t.Push("ack", "test-1", new Acknowledgement(lap.MessageId, "test-1", true, true));
        await Task.Delay(30); Assert.Equal(3, g.Diagnostics().Accepted);
        stop.Cancel(); await run; Assert.Equal(0, g.Diagnostics().AwaitingAcknowledgement);
    }

    [Fact]
    public async Task CommandDispatchRejectsUnauthorizedAndPersistsClaimAcrossReconnectRestart()
    {
        using var f = new Fixture(); var dispatched = 0;
        var now = DateTimeOffset.UtcNow;
        var request = new CommandRequest("command", "test-1", TelematicProtocol.CapabilityChatBroadcast, JsonSerializer.SerializeToElement(new { message = "authorized chat" }), now.AddSeconds(-1), now.AddMinutes(5), "key");
        async Task Exercise(bool restart)
        {
            var transport = new Transport(); using var gateway = new TelematicGatewayClient(f.Config(), transport);
            gateway.SetCommandHandler((r, _) => { Interlocked.Increment(ref dispatched); return Task.FromResult(new CommandResult(r.CommandId, r.InstanceId, "succeeded", DateTimeOffset.UtcNow)); });
            using var stop = new CancellationTokenSource(); var run = gateway.RunAsync(stop.Token);
            try {
                await Until(() => gateway.Diagnostics().Connected);
                transport.Push("command.request", "test-1", request with { InstanceId = "foreign" });
                transport.Push("command.request", "test-1", request with { CommandId = "expired", ExpiresAt = now.AddSeconds(-1) });
                transport.Push("command.request", "test-1", request with { CommandId = "oversized", Parameters = JsonSerializer.SerializeToElement(new { message = new string('x', 256) }) });
                transport.Push("command.request", "test-1", request);
                await Until(() => transport.Sent.Count(e => e.MessageType == "command.result") == 4);
                Assert.Equal(1, dispatched);
                if (!restart) {
                    transport.Disconnect(); await Until(() => transport.Connections >= 2 && gateway.Diagnostics().Connected);
                    transport.Push("command.request", "test-1", request);
                    await Until(() => transport.Sent.Count(e => e.MessageType == "command.result") == 5);
                    Assert.Equal(1, dispatched);
                }
            } finally { stop.Cancel(); await run; }
        }
        await Exercise(false); await Exercise(true); Assert.Equal(1, dispatched);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedSendOrDisconnectBeforeAckReplaysOriginalIdAfterRestart(bool failSend)
    {
        using var f = new Fixture(); var configuration = f.Config(); string original;
        var first = new Transport { AutoAck = false, FailFirstDurable = failSend };
        using (var g = new TelematicGatewayClient(configuration, first))
        {
            Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap()));
            using var stop = new CancellationTokenSource(); var run = g.RunAsync(stop.Token);
            await Until(() => first.Sent.Any(x => x.MessageType == "lap.completed"));
            original = first.Sent.First(x => x.MessageType == "lap.completed").MessageId;
            if (!failSend) first.Disconnect();
            stop.Cancel(); await run; Assert.True(g.Diagnostics().Spool!.Pending > 0);
        }
        var second = new Transport(); using var recovered = new TelematicGatewayClient(configuration, second);
        using var stopping = new CancellationTokenSource(); var recovery = recovered.RunAsync(stopping.Token);
        await Until(() => second.Sent.Any(x => x.MessageId == original) && recovered.Diagnostics().Spool!.Pending == 0);
        Assert.Single(second.Sent, x => x.MessageId == original); stopping.Cancel(); await recovery;
    }

    [Fact]
    public async Task ExactlyFullSpoolAndWindowDoNotBlockAckReaderOrBootstrap()
    {
        using var f = new Fixture(); var t = new Transport(); using var g = new TelematicGatewayClient(f.Config(records: 1), t);
        Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap()));
        using var stop = new CancellationTokenSource(); var run = g.RunAsync(stop.Token);
        await Until(() => g.Diagnostics().Accepted >= 3 && g.Diagnostics().Spool!.Pending == 0);
        Assert.Equal(1, t.Connections); stop.Cancel(); await run;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryableRetainsIdWhilePermanentRejectPreservesDeadLetter(bool retryable)
    {
        using var f = new Fixture(); var t = new Transport(); var rejectedOnce = false;
        t.AckPolicy = (e, _) =>
        {
            if (e.MessageType != "lap.completed" || rejectedOnce) return new Acknowledgement(e.MessageId, e.InstanceId, true);
            rejectedOnce = true; return new Acknowledgement(e.MessageId, e.InstanceId, false, Error: new ProtocolError("FIXTURE_REJECTED", "fixture", retryable));
        };
        using var g = new TelematicGatewayClient(f.Config(), t); Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap()));
        using var stop = new CancellationTokenSource(); var run = g.RunAsync(stop.Token);
        await Until(() => retryable ? t.Sent.Count(x => x.MessageType == "lap.completed") == 2 && g.Diagnostics().Spool!.Pending == 0 : g.Diagnostics().PermanentlyRejected == 1);
        var laps = t.Sent.Where(x => x.MessageType == "lap.completed").ToArray();
        if (retryable) { Assert.Equal(laps[0].MessageId, laps[1].MessageId); Assert.Equal(1, g.Diagnostics().Retrying); }
        else { Assert.Single(laps); Assert.Single(Directory.GetFiles(f.Root, "*.rejected-FIXTURE_REJECTED.json", SearchOption.AllDirectories)); }
        stop.Cancel(); await run;
    }

    [Fact]
    public async Task MalformedUnknownForeignAndUnsolicitedAcksRetainRecord()
    {
        using var f = new Fixture(); var t = new Transport { AutoAck = false }; using var g = new TelematicGatewayClient(f.Config(), t);
        Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap())); using var stop = new CancellationTokenSource(); var run = g.RunAsync(stop.Token);
        await Until(() => t.Sent.Any(x => x.MessageType == "lap.completed")); var e = t.Sent.First(x => x.MessageType == "lap.completed");
        foreach (var ack in new object[] { new { messageId = e.MessageId, instanceId = "test-1" },
            new { messageId = e.MessageId, instanceId = "test-1", accepted = false },
            new Acknowledgement(e.MessageId, "other", true), new Acknowledgement("unsolicited", "test-1", true),
            new Acknowledgement(e.MessageId, "test-1", true, Error: new ProtocolError("INVALID", "invalid")) })
            g.HandleAcknowledgement(JsonSerializer.SerializeToElement(ack, TelematicProtocol.JsonOptions));
        Assert.Equal(0, g.Diagnostics().Accepted); Assert.Equal(1, g.Diagnostics().AwaitingAcknowledgement);
        t.Push("ack", "test-1", new Acknowledgement(e.MessageId, "test-1", true)); await Until(() => g.Diagnostics().Accepted == 1);
        stop.Cancel(); await run;
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("handshake")]
    [InlineData("write")]
    public async Task SilentPeerHasBoundedRecoveryAndRetainsOriginalRecord(string mode)
    {
        using var f = new Fixture(); var t = new Transport { AutoAck = false, SilenceHandshake = mode == "handshake", StallWrites = mode == "write" };
        using var g = new TelematicGatewayClient(f.Config(), t); Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap()));
        var original = Directory.GetFiles(f.Root, "*.pending.json", SearchOption.AllDirectories).Single();
        using var stop = new CancellationTokenSource(); var run = g.RunAsync(stop.Token);
        await Until(() => t.Connections >= 2);
        Assert.True(File.Exists(original)); Assert.True(g.Diagnostics().ReconnectCount >= 1);
        if (mode == "ack") Assert.Equal(t.Sent.First(x => x.MessageType == "lap.completed").MessageId,
            t.Sent.Last(x => x.MessageType == "lap.completed").MessageId);
        stop.Cancel(); await run;
    }

    [Fact]
    public void PersistBeforeSendRestartAndPartialWriteRecovery()
    {
        using var f = new Fixture(); var c = f.Config(); string root, id;
        using (var s = new DurableSpool(c.InstanceId, c.Spool))
        {
            root = s.Root; id = Guid.NewGuid().ToString("N");
            Assert.True(s.TryPersist(DurableEventValidation.Serialize(c.InstanceId, id, "lap.completed", Lap()), out _));
        }
        var original = Directory.GetFiles(root, "*.pending.json").Single(); File.Move(original, original + ".tmp");
        File.WriteAllText(Path.Combine(root, "partial.pending.json.tmp"), "{partial");
        using var recovered = new DurableSpool(c.InstanceId, c.Spool);
        Assert.Equal(id, recovered.Eligible(new HashSet<string>(), 10).Single().MessageId);
        Assert.Equal(1, recovered.Diagnostics().Quarantined); Assert.True(File.Exists(original));
    }
    [Fact]
    public async Task OldSupervisorCannotSilentlyDowngradeDurabilityContract()
    {
        using var f = new Fixture(); var t = new Transport { AckContract = 0 }; using var g = new TelematicGatewayClient(f.Config(), t);
        Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap()));
        using var stop = new CancellationTokenSource(); var run = g.RunAsync(stop.Token);
        await Until(() => g.Diagnostics().ReconnectCount >= 1);
        Assert.False(g.Diagnostics().Connected); Assert.Equal("DURABLE_ACK_VERSION_UNSUPPORTED", g.Diagnostics().LastError);
        Assert.DoesNotContain(t.Sent, x => x.Delivery == TelematicProtocol.Durable); Assert.Equal(1, g.Diagnostics().Spool!.Pending);
        stop.Cancel(); await run;
    }

    [Fact]
    public void CapacityNeverEvictsAndAcceptedCleanupRecoversCapacity()
    {
        using var f = new Fixture(); var c = f.Config(records: 1); using var spool = new DurableSpool(c.InstanceId, c.Spool);
        var first = DurableEventValidation.Serialize(c.InstanceId, "first", "lap.completed", Lap());
        Assert.True(spool.TryPersist(first, out var e)); Assert.False(spool.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "second", "lap.completed", Lap()), out _));
        Assert.Equal("first", spool.Eligible(new HashSet<string>(), 10).Single().MessageId);
        Assert.Equal("SPOOL_FULL", spool.Diagnostics().ErrorCode);
        Assert.True(spool.Retire(e!.MessageId)); Assert.False(spool.Retire(e.MessageId));
        Assert.True(spool.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "second", "lap.completed", Lap()), out _));
    }

    [Fact]
    public void CorruptRecordDoesNotPreventOtherRecoveryAndCleanupNeedsNoJournal()
    {
        using var f = new Fixture(); var c = f.Config(); string root;
        using (var spool = new DurableSpool(c.InstanceId, c.Spool))
        {
            root = spool.Root;
            Assert.True(spool.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "accepted", "lap.completed", Lap()), out _));
            Assert.True(spool.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "keep", "lap.completed", Lap()), out _));
            Assert.True(spool.Retire("accepted"));
        }
        File.WriteAllText(Path.Combine(root, "broken.pending.json"), "{broken");
        using var recovered = new DurableSpool(c.InstanceId, c.Spool);
        Assert.Equal("keep", recovered.Eligible(new HashSet<string>(), 10).Single().MessageId);
        Assert.Equal(1, recovered.Diagnostics().Quarantined); Assert.Equal(2, Directory.GetFiles(root).Count(x => Path.GetFileName(x) != "owner.lock"));
    }

    [Fact]
    public void TwoInstancesAreIsolatedAndSecondOwnerOfSameInstanceCannotConsume()
    {
        using var f = new Fixture(); var a = f.Config(instance: "A"); var b = f.Config(instance: "B");
        using var sa = new DurableSpool("A", a.Spool); using var sb = new DurableSpool("B", b.Spool);
        Assert.NotEqual(sa.Root, sb.Root); Assert.True(sa.TryPersist(DurableEventValidation.Serialize("A", "a", "lap.completed", Lap()), out var aEntry));
        Assert.Null(sb.Read(aEntry!));
        Assert.Empty(sb.Eligible(new HashSet<string>(), 10)); Assert.Throws<IOException>(() => new DurableSpool("A", a.Spool));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteNestedValuesLeaveAllAccountingBalanced(double number)
    {
        using var f = new Fixture(); using var g = new TelematicGatewayClient(f.Config(), new Transport());
        Assert.False(g.TryQueueGameplayEvent("lap.completed", Lap(number)));
        Assert.Equal(0, g.Diagnostics().Spool!.Pending); Assert.Equal(0, g.Diagnostics().AwaitingAcknowledgement);
        Assert.True(g.TryQueueGameplayEvent("lap.completed", Lap()));
    }
    [Theory]
    [InlineData("instance.register")]
    [InlineData("instance.ready")]
    [InlineData("session.started")]
    [InlineData("session.changed")]
    [InlineData("session.ended")]
    [InlineData("driver.connected")]
    [InlineData("driver.disconnected")]
    [InlineData("lap.completed")]
    [InlineData("incident.car_collision")]
    [InlineData("incident.environment_collision")]
    public void EverySupportedDurableTypePersistsAndRecovers(string type)
    {
        using var f = new Fixture(); var c = f.Config();
        var driver = new DriverReference("Driver", "76561198000000000", "4");
        object payload = type switch
        {
            "instance.register" => new InstanceRegister("ASSETTO_CORSA", "ASSETTOSERVER_NATIVE", "0.2.0", 1, []),
            "instance.ready" => new { },
            "session.started" or "session.changed" or "session.ended" => new SessionEventPayload("event", "session", "practice", "Practice", "ks_laguna_seca", null, "2026-10-07T00:00:00Z"),
            "driver.connected" or "driver.disconnected" => new DriverEventPayload("event", driver, "session"),
            "lap.completed" => Lap(),
            _ => new CollisionEventPayload("incident", "session", [driver, driver with { GameDriverId = "5" }], 42, new WorldPosition(1, 2, 3), "2026-10-07T00:00:00Z", new Dictionary<string, object?>())
        };
        using (var s = new DurableSpool(c.InstanceId, c.Spool)) Assert.True(s.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "message", type, payload), out _));
        using var recovered = new DurableSpool(c.InstanceId, c.Spool); Assert.Equal(type, recovered.Eligible(new HashSet<string>(), 1).Single().MessageType);
    }
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteCollisionPositionAndSpeedAreRejected(float value)
    {
        using var f = new Fixture(); using var g = new TelematicGatewayClient(f.Config(), new Transport());
        var drivers = new[] { new DriverReference("Driver", null, "4") };
        var payload = new CollisionEventPayload("incident", "session", drivers, value, new WorldPosition(value, 0, 0), "2026-10-07T00:00:00Z", new Dictionary<string, object?>());
        Assert.False(g.TryQueueGameplayEvent("incident.environment_collision", payload));
        Assert.Equal(0, g.Diagnostics().AwaitingAcknowledgement); Assert.Equal(0, g.Diagnostics().Spool!.Pending);
    }
    private sealed class ThrowingPayload { public string Value => throw new InvalidOperationException("fixture-secret-must-not-be-logged"); }
    [Fact]
    public void SerializationExceptionAndInvalidFieldsLeaveAccountingBalanced()
    {
        using var f = new Fixture(); using var g = new TelematicGatewayClient(f.Config(), new Transport());
        Assert.False(g.TryQueueGameplayEvent("lap.completed", new ThrowingPayload()));
        Assert.False(g.TryQueueGameplayEvent("unsupported", Lap()));
        Assert.False(g.TryQueueGameplayEvent("lap.completed", new { lapTimeMs = -1 }));
        Assert.Equal(0, g.Diagnostics().Spool!.Pending); Assert.Equal(0, g.Diagnostics().AwaitingAcknowledgement);
        Assert.DoesNotContain("fixture-secret", g.Diagnostics().LastError);
    }
    [Fact]
    public void EphemeralTelemetryIsCoalescedAndNeverSpools()
    {
        using var f = new Fixture(); using var g = new TelematicGatewayClient(f.Config(), new Transport());
        for (var i = 0; i < 10_000; i++) { Assert.True(g.TryQueueTelemetrySnapshot(new { position = i })); Assert.True(g.TryQueueEphemeralEvent("players.snapshot", new { players = i })); }
        Assert.Equal(0, g.Diagnostics().Spool!.Pending); Assert.Equal(0, g.Diagnostics().Spool!.BytesUsed);
    }
    [Theory]
    [InlineData("after-flush")]
    [InlineData("after-rename")]
    public void InterruptedAtomicWriteRecoversSameId(string stage)
    {
        using var f = new Fixture(); var c = f.Config();
        using (var s = new DurableSpool(c.InstanceId, c.Spool, step => { if (step == stage) throw new IOException("injected interruption"); }))
            Assert.False(s.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "recover-id", "lap.completed", Lap()), out _));
        using var recovered = new DurableSpool(c.InstanceId, c.Spool);
        Assert.Equal("recover-id", recovered.Eligible(new HashSet<string>(), 10).Single().MessageId);
    }
    [Theory]
    [InlineData("before-retire", 1)]
    [InlineData("after-retire", 0)]
    public void InterruptedAcceptedCleanupRecoversOrHasAlreadyRetired(string stage, int expected)
    {
        using var f = new Fixture(); var c = f.Config();
        using (var s = new DurableSpool(c.InstanceId, c.Spool, step => { if (step == stage) throw new IOException("injected interruption"); }))
        {
            Assert.True(s.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "accepted-id", "lap.completed", Lap()), out _));
            Assert.Throws<IOException>(() => s.Retire("accepted-id"));
        }
        using var recovered = new DurableSpool(c.InstanceId, c.Spool); Assert.Equal(expected, recovered.Diagnostics().Pending);
        // No journal compaction exists: per-record cleanup has these same two outcomes.
    }
    [Fact]
    public void ChecksumDetectsSyntacticallyValidRecordCorruption()
    {
        using var f = new Fixture(); var c = f.Config(); string root;
        using (var s = new DurableSpool(c.InstanceId, c.Spool))
        { root = s.Root; Assert.True(s.TryPersist(DurableEventValidation.Serialize(c.InstanceId, "corrupt-me", "lap.completed", Lap()), out _)); }
        var path = Directory.GetFiles(root, "*.pending.json").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("100307", "100308"));
        using var recovered = new DurableSpool(c.InstanceId, c.Spool);
        Assert.Equal(1, recovered.Diagnostics().Quarantined); Assert.Equal(0, recovered.Diagnostics().Pending);
    }
    [Fact]
    public void ActualDefaultHundredMiBCapRetainsAllPreviouslyAcceptedRecords()
    {
        using var f = new Fixture(); var c = f.Config(); using var spool = new DurableSpool(c.InstanceId, c.Spool);
        var paddedLap = Lap(new string('x', 110_000)); var accepted = 0; string? first = null;
        while (true)
        {
            var bytes = DurableEventValidation.Serialize(c.InstanceId, "large-" + accepted, "lap.completed", paddedLap);
            if (!spool.TryPersist(bytes, out var entry)) break;
            first ??= entry!.FileName; accepted++;
        }
        Assert.Equal(accepted, spool.Diagnostics().Pending); Assert.InRange(spool.Diagnostics().BytesUsed, 100L * 1024 * 1024 - 128 * 1024, 100L * 1024 * 1024);
        Assert.True(File.Exists(Path.Combine(spool.Root, first!))); Assert.Equal("SPOOL_FULL", spool.Diagnostics().ErrorCode);
        _output.WriteLine($"Actual 100 MiB cap: records={accepted}; bytes={spool.Diagnostics().BytesUsed}; next append refused; no old record evicted");
    }
    [Fact]
    public void ResourceMeasurementAndDefaultHundredMiBCap()
    {
        using var f = new Fixture(); var c = f.Config(); GC.Collect(); var baseline = GC.GetTotalMemory(true);
        using var spool = new DurableSpool(c.InstanceId, c.Spool); var idle = GC.GetTotalMemory(true) - baseline;
        long lapBytes = 0;
        for (var i = 0; i < 1000; i++)
        { var bytes = DurableEventValidation.Serialize(c.InstanceId, "lap-" + i, "lap.completed", Lap()); lapBytes += DurableSpool.EncodeRecord(bytes).Length; Assert.True(spool.TryPersist(bytes, out _)); }
        var queued = GC.GetTotalMemory(true) - baseline;
        _output.WriteLine($"Resource approximate managed bytes: idle={idle}; 1000 queued={queued}; lap disk bytes={lapBytes}; cap={spool.Diagnostics().CapacityBytes}");
        Assert.Equal(100L * 1024 * 1024, spool.Diagnostics().CapacityBytes); Assert.Equal(lapBytes, spool.Diagnostics().BytesUsed);
        long mixed = 0;
        for (var i = 0; i < 1000; i++)
        { var type = i % 2 == 0 ? "lap.completed" : "driver.connected"; object payload = i % 2 == 0 ? Lap() : new DriverEventPayload("event-" + i, new DriverReference("Driver", "76561198000000000", "4"), "session-1"); mixed += DurableSpool.EncodeRecord(DurableEventValidation.Serialize(c.InstanceId, "mixed-" + i, type, payload)).Length; }
        _output.WriteLine($"1000 representative mixed serialized disk bytes={mixed}; no filesystem block overhead included");
    }
}
