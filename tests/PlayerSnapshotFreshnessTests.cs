using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Incoming;
using Xunit;

namespace Telematic.AssettoServer.Plugin.Tests;

// Actual pinned 0.0.54 types/events with synthetic slot state. No native host,
// real player, TCP game listener or Steam verification is claimed here.
public sealed class PlayerSnapshotFreshnessTests
{
    private static void Set(object instance, string property, object? value)
        => instance.GetType().GetField($"<{property}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Raise(object instance, string name, ACTcpClient client)
        => (instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance) as global::AssettoServer.Server.EventHandler<ACTcpClient, EventArgs>)?.Invoke(client, EventArgs.Empty);

    private sealed class Transport : IGatewayTransport
    {
        private Channel<string?> _receive = Channel.CreateUnbounded<string?>();
        public readonly ConcurrentQueue<(int Connection, GatewayEnvelope Message)> Sent = new();
        public int Connections;
        public bool AutoAck = true;
        public Func<CancellationToken, Task>? BeforeSnapshot;
        public Task ConnectAsync(Uri uri, string token, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Connections); _receive = Channel.CreateUnbounded<string?>();
            Push("connection.accepted", new { protocolVersion = 1, durableAckVersion = 2 }); return Task.CompletedTask;
        }
        private void Push(string type, object payload) => _receive.Writer.TryWrite(JsonSerializer.Serialize(
            new GatewayEnvelope(1, Guid.NewGuid().ToString("N"), type, "m15", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "ephemeral", JsonSerializer.SerializeToElement(payload, TelematicProtocol.JsonOptions)), TelematicProtocol.JsonOptions));
        public async Task SendAsync(string frame, CancellationToken token)
        {
            var message = JsonSerializer.Deserialize<GatewayEnvelope>(frame, TelematicProtocol.JsonOptions)!;
            if (message.MessageType == "players.snapshot" && BeforeSnapshot is { } hook) await hook(token);
            Sent.Enqueue((Connections, message));
            if (AutoAck && message.Delivery == "durable") Push("ack", new Acknowledgement(message.MessageId, message.InstanceId, true));
        }
        public void Disconnect() => _receive.Writer.TryWrite(null);
        public async Task<string?> ReceiveAsync(CancellationToken token) => await _receive.Reader.ReadAsync(token);
        public Task CloseAsync(CancellationToken token) { _receive.Writer.TryComplete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "telematic-m15-" + Guid.NewGuid().ToString("N"));
        public EntryCarManager Cars { get; }
        public EntryCar[] Slots { get; }
        public VehicleStateCache Cache { get; }
        public AssettoEventBridge Bridge { get; }
        public TelematicGatewayClient Gateway { get; }
        public TelematicConfiguration Config { get; }
        public Transport Transport { get; } = new();
        public Fixture(int slots = 3, bool steamAuth = true)
        {
            Config = new() { Enabled = true, InstanceId = "m15", PluginToken = "local-fixture", ConnectTimeoutSeconds = 1, WriteTimeoutSeconds = 2, AckTimeoutSeconds = 5,
                Telemetry = new() { Enabled = false }, Spool = new() { RootDirectory = Root } };
            Gateway = new(Config, Transport);
            var configuration = (ACServerConfiguration)RuntimeHelpers.GetUninitializedObject(typeof(ACServerConfiguration));
            Set(configuration, "Extra", new ACExtraConfiguration { UseSteamAuth = steamAuth });
            Set(configuration, "Server", new ServerConfiguration { Track = "ks_laguna_seca", TrackConfig = "" });
            Cars = (EntryCarManager)RuntimeHelpers.GetUninitializedObject(typeof(EntryCarManager));
            Slots = Enumerable.Range(0, slots).Select(index => {
                var car = (EntryCar)RuntimeHelpers.GetUninitializedObject(typeof(EntryCar));
                Set(car, "SessionId", (byte)index); Set(car, "Model", "ks_bmw_m235i_racing"); Set(car, "Skin", "fixture"); return car;
            }).ToArray();
            Set(Cars, "EntryCars", Slots);
            var sessions = (SessionManager)RuntimeHelpers.GetUninitializedObject(typeof(SessionManager));
            Set(sessions, "CurrentSession", new SessionState(new SessionConfiguration { Id = 0, Name = "Practice", Type = SessionType.Practice }, sessions));
            Cache = new(configuration); Bridge = new(Cars, sessions, configuration, Gateway, Cache);
            Bridge.Start(); Gateway.SetConnectionReadyHandler(Bridge.EmitSnapshot);
        }
        public ACTcpClient Client(int slot, ulong guid = 76561198000000000)
        {
            var client = (ACTcpClient)RuntimeHelpers.GetUninitializedObject(typeof(ACTcpClient));
            client.Name = "Fixture"; client.SessionId = (byte)slot; client.IsConnected = true;
            Set(client, "Guid", guid); Set(client, "EntryCar", Slots[slot]); Set(Slots[slot], "Client", client); return client;
        }
        public ACTcpClient Join(int slot = 0, ulong guid = 76561198000000000)
        { var client = Client(slot, guid); Raise(Cars, "ClientConnected", client); return client; }
        public void Disconnect(ACTcpClient client)
        { Set(client.EntryCar, "Client", null); client.IsConnected = false; Raise(Cars, "ClientDisconnected", client); }
        public PlayersSnapshotPayload Latest => (PlayersSnapshotPayload)typeof(TelematicGatewayClient).GetField("_latestPlayerSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Gateway)!;
        public GatewayEnvelope[] Pending {
            get {
                var spool = (DurableSpool)typeof(TelematicGatewayClient).GetField("_spool", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Gateway)!;
                return spool.Eligible(new HashSet<string>(), 1000).Select(entry => JsonSerializer.Deserialize<GatewayEnvelope>(spool.Read(entry)!, TelematicProtocol.JsonOptions)!).ToArray();
            }
        }
        public void Dispose() { Bridge.Stop(); Gateway.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10, timeout.Token); }

    [Fact]
    public void QualificationUsesSupportedDotNetEightWithoutMajorRollForward()
    {
        Assert.Equal(8, Environment.Version.Major);
        Assert.Equal(8, typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly.GetName().Version!.Major);
    }

    [Fact]
    public async Task InitialReadyCapturesExistingPlayersWithoutTelemetry()
    {
        using var f = new Fixture(); f.Client(0);
        using var stop = new CancellationTokenSource(); var run = f.Gateway.RunAsync(stop.Token);
        try { await Until(() => f.Transport.Sent.Any(item => item.Message.MessageType == "players.snapshot"));
            var frame = f.Transport.Sent.First(item => item.Message.MessageType == "players.snapshot").Message;
            Assert.Single(frame.Payload.GetProperty("players").EnumerateArray()); Assert.Equal("ephemeral", frame.Delivery); Assert.False(f.Config.Telemetry.Enabled);
        } finally { stop.Cancel(); await run; }
    }
    [Fact]
    public void JoinAndFinalLeaveUpdateSnapshotAndPreserveDurableLifecycle()
    {
        using var f = new Fixture(); var client = f.Join();
        Assert.Equal("76561198000000000", Assert.Single(f.Latest.Players).SteamId);
        Assert.Contains(f.Pending, frame => frame.MessageType == "driver.connected" && frame.Delivery == "durable");
        var joinedAt = DateTimeOffset.Parse(f.Latest.CapturedAt); f.Disconnect(client);
        Assert.Empty(f.Latest.Players); Assert.True(DateTimeOffset.Parse(f.Latest.CapturedAt) > joinedAt);
        Assert.Contains(f.Pending, frame => frame.MessageType == "driver.disconnected" && frame.Delivery == "durable");
        Assert.DoesNotContain(f.Pending, frame => frame.MessageType == "players.snapshot");
    }
    [Fact]
    public void AbruptDisconnectingRemovesPresenceBeforeNativeFinalCleanup()
    {
        using var f = new Fixture(); var client = f.Join();
        typeof(ACTcpClient).GetField("_disconnectRequested", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, 1);
        Raise(client, "Disconnecting", client); Assert.Empty(f.Latest.Players);
        Assert.True(client.IsConnected); // Native final slot clear happens later.
        Assert.DoesNotContain(f.Pending, frame => frame.MessageType == "driver.disconnected");
        f.Disconnect(client); Assert.Empty(f.Latest.Players);
        Assert.Single(f.Pending, frame => frame.MessageType == "driver.disconnected");
    }
    [Fact]
    public void DelayedOldDisconnectCannotRemoveRapidReconnectOrReplacementCache()
    {
        using var f = new Fixture(); var old = f.Join(); old.IsConnected = false;
        var replacement = f.Join(0); Raise(f.Cars, "ClientDisconnected", old);
        Assert.Single(f.Latest.Players); Assert.Equal(1, f.Cache.Count);
        Assert.DoesNotContain(f.Pending, frame => frame.MessageType == "driver.disconnected");
        Raise(f.Cars, "ClientConnected", old); Assert.Single(f.Latest.Players);
        f.Disconnect(replacement); Assert.Empty(f.Latest.Players); Assert.Equal(0, f.Cache.Count);
        Raise(f.Cars, "ClientDisconnected", old); Assert.Empty(f.Latest.Players);
    }
    [Fact]
    public void DelayedTelemetryFromOldClientCannotPopulateReplacementSlot()
    {
        using var f = new Fixture();
        var old = f.Join();
        f.Disconnect(old);
        var replacement = f.Join();

        var staleUpdate = default(PositionUpdateIn);
        f.Cache.Update(old, old.EntryCar, in staleUpdate);

        Assert.Equal(1, f.Cache.Count);
        Assert.Empty(f.Cache.FreshStates(DateTime.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(1)));
        Assert.Same(replacement, f.Slots[0].Client);
        Assert.Equal("76561198000000000", Assert.Single(f.Latest.Players).SteamId);
    }
    [Fact]
    public void MultipleSimultaneousPlayersAreUniqueAndIndependentOfSampling()
    {
        using var f = new Fixture(8); Parallel.For(0, 8, slot => f.Join(slot, 76561198000000000UL + (ulong)slot));
        Assert.Equal(8, f.Latest.Players.Count); Assert.Equal(8, f.Latest.Players.Select(player => player.SteamId).Distinct().Count());
        Assert.Equal(8, f.Latest.Players.Select(player => player.GameDriverId).Distinct().Count());
        var client = f.Slots[3].Client!; f.Disconnect(client); Assert.Equal(7, f.Latest.Players.Count);
        Assert.False(f.Config.Telemetry.Enabled); Assert.Equal(0, f.Cache.UpdatesReceived);
    }
    [Fact]
    public void AmbiguousOrMissingIdentityFailsClosedWithoutInventingSteamAuthority()
    {
        using var f = new Fixture(3, steamAuth: false); f.Join(0); f.Join(1);
        Assert.Empty(f.Latest.Players); // Two current slots claim the same Steam GUID.
        f.Join(2, 0); f.Bridge.EmitSnapshot(); Assert.Empty(f.Latest.Players);
        f.Disconnect(f.Slots[1].Client!);
        var remaining = Assert.Single(f.Latest.Players);
        Assert.Equal("76561198000000000", remaining.SteamId); Assert.Equal(false, remaining.GameMetadata!["identityVerified"]);
    }
    [Fact]
    public void AmbiguousReplacementJoinClearsPreviouslyCapturedPresence()
    {
        using var f = new Fixture(); var old = f.Join(); Assert.Single(f.Latest.Players);
        old.IsConnected = false; f.Join(0, 0);
        Assert.Empty(f.Latest.Players);
        Assert.Single(f.Pending, message => message.MessageType == "driver.connected");
    }
    [Fact]
    public void InitialReadyAndCallbacksCannotPublishNonIncreasingTimestamps()
    {
        using var f = new Fixture(); f.Join();
        var future = DateTimeOffset.UtcNow.AddMinutes(1);
        typeof(AssettoEventBridge).GetField("_lastPlayerSnapshotAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Bridge, future);
        f.Bridge.EmitSnapshot(); Assert.True(DateTimeOffset.Parse(f.Latest.CapturedAt) > future);
        var first = f.Latest; Parallel.For(0, 100, _ => f.Bridge.EmitSnapshot());
        Assert.True(DateTimeOffset.Parse(f.Latest.CapturedAt) > DateTimeOffset.Parse(first.CapturedAt));
    }
    [Fact]
    public async Task SnapshotChangesDuringSendCoalesceToLatestRoster()
    {
        using var f = new Fixture(); var client = f.Join();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        f.Transport.BeforeSnapshot = async token => { if (Interlocked.Increment(ref count) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        using var stop = new CancellationTokenSource(); var run = f.Gateway.RunAsync(stop.Token);
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Disconnect(client);
            for (var i = 0; i < 100; i++) f.Bridge.EmitSnapshot(); release.TrySetResult();
            await Until(() => f.Transport.Sent.Count(item => item.Message.MessageType == "players.snapshot") == 2);
            var snapshots = f.Transport.Sent.Where(item => item.Message.MessageType == "players.snapshot").Select(item => item.Message.Payload.Deserialize<PlayersSnapshotPayload>(TelematicProtocol.JsonOptions)!).ToArray();
            Assert.Single(snapshots[0].Players); Assert.Empty(snapshots[1].Players);
            Assert.True(DateTimeOffset.Parse(snapshots[1].CapturedAt) > DateTimeOffset.Parse(snapshots[0].CapturedAt));
        } finally { release.TrySetResult(); stop.Cancel(); await run; }
    }
    [Fact]
    public async Task ReconnectRefreshesBeforeCachedSnapshotDrainAndDurableReplayKeepsIdentity()
    {
        using var f = new Fixture(); var client = f.Join(); f.Transport.AutoAck = false;
        using var stop = new CancellationTokenSource(); var run = f.Gateway.RunAsync(stop.Token);
        try {
            await Until(() => f.Transport.Sent.Any(item => item.Message.MessageType == "driver.connected") && f.Transport.Sent.Any(item => item.Message.MessageType == "players.snapshot"));
            var original = f.Transport.Sent.First(item => item.Message.MessageType == "driver.connected").Message;
            f.Transport.Disconnect(); await Until(() => !f.Gateway.Diagnostics().Connected);
            // Model current native slot state changing while callbacks were unavailable.
            Set(client.EntryCar, "Client", null); client.IsConnected = false;
            f.Transport.AutoAck = true;
            await Until(() => f.Transport.Sent.Any(item => item.Connection == 2 && item.Message.MessageType == "players.snapshot") && f.Gateway.Diagnostics().Spool!.Pending == 0);
            var refreshed = f.Transport.Sent.First(item => item.Connection == 2 && item.Message.MessageType == "players.snapshot").Message;
            Assert.Empty(refreshed.Payload.GetProperty("players").EnumerateArray());
            var replay = f.Transport.Sent.First(item => item.Connection == 2 && item.Message.MessageId == original.MessageId).Message;
            Assert.Equal(original.Payload.GetRawText(), replay.Payload.GetRawText()); Assert.Equal("m15", replay.InstanceId);
            Assert.Equal("76561198000000000", replay.Payload.GetProperty("driver").GetProperty("steamId").GetString());
        } finally { stop.Cancel(); await run; }
    }
    [Fact]
    public void DuplicateFinalDisconnectAndStoppedCallbacksCannotResurrectPresence()
    {
        using var f = new Fixture(); var client = f.Join(); f.Disconnect(client);
        var previous = f.Latest; Raise(f.Cars, "ClientDisconnected", client);
        Assert.Same(previous, f.Latest); Assert.Single(f.Pending, frame => frame.MessageType == "driver.disconnected");
        f.Bridge.Stop(); f.Join(1, 76561198000000001); f.Bridge.EmitSnapshot(); Assert.Same(previous, f.Latest);
    }
}
