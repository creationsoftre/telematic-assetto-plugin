using System.Globalization;

namespace Telematic.AssettoServer.Plugin;

public static class CleanLapValidation
{
    public const string NativeSource = "ASSETTO_SERVER_NATIVE";

    // Independent of raw validity. Only an authoritative producer may supply
    // complete coverage or an incident attributable to this exact interval.
    public static RecordValidation Evaluate(long durationMs, CleanLapEvidence evidence)
    {
        static bool Timestamp(string value, out DateTimeOffset parsed)
        {
            var valid = DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
            return valid && (value.EndsWith('Z') || (value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':'));
        }
        var validStart = Timestamp(evidence.IntervalStart, out var start);
        var validEnd = Timestamp(evidence.IntervalEnd, out var end);
        var validInterval = validStart && validEnd && start < end;
        var reason = !validInterval ? "INVALID_EVIDENCE"
            : evidence.Source != NativeSource ? "UNSUPPORTED_SOURCE"
            : durationMs <= 0 || durationMs > int.MaxValue ? "INVALID_DURATION"
            : !evidence.IdentityMatched ? "IDENTITY_UNBOUND"
            : evidence.TimingCut ? "TIMING_CUT"
            : evidence.Collision ? "COLLISION"
            : !evidence.CoverageComplete || (end - start).TotalMilliseconds < durationMs ? "TELEMETRY_INCOMPLETE" : "ACCEPTED";
        return new("clean-lap-v1", reason == "ACCEPTED", reason, evidence);
    }
}

// Per connection, never per numeric slot. A previous completion anchors an
// observed interval; neither connection time nor duration back-calculation does.
public sealed class NativeLapContinuity
{
    private readonly DateTimeOffset _observedSince;
    public NativeLapContinuity(DateTimeOffset? observedSince = null) => _observedSince = observedSince ?? DateTimeOffset.UtcNow;
    private string? _session, _identity;
    private byte _slot;
    private DateTimeOffset? _previousCompletion;

    public CleanLapEvidence Complete(string? session, string? identity, byte slot, bool bound,
        bool timingCut, DateTimeOffset now)
    {
        var same = bound && _session == session && _identity == identity && _slot == slot && _previousCompletion <= now;
        var start = same && _previousCompletion.HasValue ? _previousCompletion.Value : _observedSince;
        _session = bound ? session : null; _identity = bound ? identity : null; _slot = slot;
        _previousCompletion = bound ? now : null;
        // v0.0.54 has no collision timestamp, global ID, delivery watermark,
        // or authoritative collision-free signal. Even consecutive observed
        // completions cannot establish complete clean-lap coverage.
        return new(CleanLapValidation.NativeSource, false, bound, timingCut, false,
            start.ToUniversalTime().ToString("O"), now.ToUniversalTime().ToString("O"));
    }
}
