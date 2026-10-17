using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Telematic.AssettoServer.Plugin;

// A base-.NET-8 process executing the unchanged spool/validator source. This
// deliberately does not reference/load AssettoServer or certify plugin loading.
static byte[] Frame() => DurableEventValidation.Serialize("phase10b", "stable-lap", "lap.completed",
    new { eventId = "event-1", sessionId = "session-1", trackKey = "ks_laguna_seca", lapTimeMs = 100307,
        validity = "UNKNOWN", driver = new { displayName = "Synthetic driver", gameDriverId = "4", steamId = "76561198000000000" } });
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static SpoolConfiguration Configuration(string root) => new() { RootDirectory = root };
if (args.Length is 3 or 4 && args[0] == "websocket")
{
    Check(Environment.Version.Major == 8, "base .NET 8 required");
    var url = new Uri(args[1]);
    Check(url.Scheme == "ws" && url.IsLoopback, "fixture must remain loopback");
    var instance = args.Length == 4 ? args[3] : "phase10b";
    Check(DurableEventValidation.IsIdentifier(instance), "invalid fixture instance");
    var configuration = new TelematicConfiguration { Enabled = true, InstanceId = instance, SupervisorUrl = url.ToString(),
        PluginToken = Environment.GetEnvironmentVariable("PHASE10B_SYNTHETIC_TOKEN") ?? "", Spool = Configuration(args[2]) };
    await using var transport = new ClientWebSocketTransport();
    using var client = new TelematicGatewayClient(configuration, transport);
    var lap = JsonSerializer.Deserialize<GatewayEnvelope>(Frame(), TelematicProtocol.JsonOptions)!;
    Check(client.TryQueueGameplayEvent("lap.completed", lap.Payload), "lap capture failed");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var run = client.RunAsync(cancellation.Token);
    while (client.Diagnostics().Accepted < 3) await Task.Delay(10, cancellation.Token);
    Check(client.Diagnostics().Spool?.Pending == 0, "positive ACK did not retire records");
    cancellation.Cancel(); await run;
    Console.WriteLine($"PASS real C# ClientWebSocket -> Go gateway durable ACK; instance={instance}; runtime={Environment.Version}; source-component only");
    return;
}
if (args.Length == 3 && args[0] == "child")
{
    var boundary = args[2];
    void Pause()
    {
        File.WriteAllText(Path.Combine(args[1], "checkpoint"), boundary);
        Thread.Sleep(Timeout.Infinite);
    }
    using var spool = new DurableSpool("phase10b", Configuration(args[1]), phase => { if (phase == boundary) Pause(); });
    if (boundary.StartsWith("generation-"))
    {
        spool.AllocateGeneration("second");
        throw new InvalidOperationException("generation checkpoint not reached");
    }
    Check(spool.TryPersist(Frame(), out _), "capture refused");
    if (boundary == "persisted") Pause();
    if (boundary is "before-retire" or "after-retire") spool.Retire("stable-lap");
    throw new InvalidOperationException("checkpoint not reached");
}
var generationOnly = args.Length == 2 && args[1] == "generation";
Check((args.Length == 1 || generationOnly) && Path.IsPathFullyQualified(args[0]), "explicit isolated root required");
Check(Environment.Version.Major == 8, "base .NET 8 required; no runtime roll-forward");
Directory.CreateDirectory(args[0]);
foreach (var boundary in generationOnly
    ? new[] { "generation-before-write", "generation-after-flush", "generation-after-commit" }
    : new[] { "after-flush", "after-rename", "persisted", "before-retire", "after-retire" })
{
    var root = Path.Combine(args[0], boundary);
    Directory.CreateDirectory(root);
    NativeGeneration? original = null;
    if (generationOnly)
        using (var initial = new DurableSpool("phase10b", Configuration(root))) original = initial.AllocateGeneration("first");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in new[] { "child", root, boundary }) start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new IOException("child creation failed");
    try
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "checkpoint")))
        {
            Check(!child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10), "checkpoint timeout");
            Thread.Sleep(10);
        }
        child.Refresh();
        Console.WriteLine($"OBSERVE {boundary} pid={child.Id} workingSet={child.WorkingSet64} handles={child.HandleCount} threads={child.Threads.Count} cpuMs={child.TotalProcessorTime.TotalMilliseconds:F0}");
        // The retained Process is the exact newly created fixture, never a name lookup.
        child.Kill(); Check(child.WaitForExit(5000), "fixture did not exit");
        using var recovered = new DurableSpool("phase10b", Configuration(root));
        if (generationOnly)
        {
            var next = recovered.AllocateGeneration("third");
            Check(next.Lineage == original!.Lineage, "lineage lost after kill");
            Check(next.Ordinal == (boundary == "generation-after-commit" ? 3 : 2), "generation high water lost after kill");
            Check(recovered.Diagnostics().Pending == 0, "generation metadata became a pending event");
            Console.WriteLine($"PASS actual generation process kill/reopen {boundary}; ordinal={next.Ordinal}");
            continue;
        }
        var eligible = recovered.Eligible(new HashSet<string>(), 10);
        Check(eligible.Count == (boundary == "after-retire" ? 0 : 1), "unexpected recovered record count");
        if (eligible.Count > 0)
        {
            Check(eligible[0].MessageId == "stable-lap" && recovered.Read(eligible[0]) is not null, "stable record lost");
            Check(recovered.TryPersist(Frame(), out _) && recovered.Diagnostics().Pending == 1, "replay duplicated");
            Check(recovered.Retire("stable-lap") && !recovered.Retire("stable-lap"), "retirement not idempotent");
        }
        Console.WriteLine($"PASS actual process kill/reopen {boundary}");
    }
    finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
}
Console.WriteLine($"PASS runtime={Environment.Version}; source-component only; no native game/plugin loading");

namespace Telematic.AssettoServer.Plugin
{
    public sealed class TelematicConfiguration
    {
        public bool Enabled { get; init; }
        public string InstanceId { get; init; } = "phase10b";
        public string SupervisorUrl { get; init; } = "";
        public string PluginToken { get; init; } = "";
        public int HeartbeatSeconds { get; init; } = 15;
        public int MaxPendingDurableMessages { get; init; } = 256;
        public int ConnectTimeoutSeconds { get; init; } = 2;
        public int WriteTimeoutSeconds { get; init; } = 2;
        public int AckTimeoutSeconds { get; init; } = 3;
        public int RetryDelaySeconds { get; init; } = 1;
        public SpoolConfiguration Spool { get; init; } = new();
        public TelemetryConfiguration Telemetry { get; init; } = new();
    }
    public sealed class TelemetryConfiguration
    {
        public bool Enabled { get; init; }
        public int SampleRateHz { get; init; } = 2;
    }
    // Exact production spool option shape/defaults, isolated from the game host graph.
    public sealed class SpoolConfiguration
    {
        public string? RootDirectory { get; init; }
        public long MaxBytes { get; init; } = 100L * 1024 * 1024;
        public int MaxRecords { get; init; } = 20_000;
    }
}
