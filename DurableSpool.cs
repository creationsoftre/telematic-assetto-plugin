using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Telematic.AssettoServer.Plugin;

public sealed record SpoolEntry(string MessageId, string MessageType, string FileName, long Bytes, DateTimeOffset CreatedAt);
public sealed record SpoolDiagnostics(int Pending, int Rejected, int Quarantined, long BytesUsed, long CapacityBytes,
    bool StorageHealthy, string? ErrorCode, DateTimeOffset? OldestPendingAt);

/// <summary>Single-owner, bounded per-record spool. Only metadata is held in RAM.</summary>
public sealed class DurableSpool : IDisposable
{
    private readonly object _gate = new();
    private readonly SortedDictionary<string, SpoolEntry> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpoolEntry> _byId = new(StringComparer.Ordinal);
    private readonly FileStream _owner;
    private readonly string _instance;
    private readonly long _capacity;
    private readonly int _maxRecords;
    private readonly Action<string>? _fault;
    private long _bytes;
    private long _sequence;
    private int _rejected, _quarantined, _fileCount;
    private string? _error;
    private bool _needsRecovery;
    public string Root { get; }

    public DurableSpool(string instanceId, SpoolConfiguration configuration, Action<string>? fault = null)
    {
        if (!DurableEventValidation.IsIdentifier(instanceId)) throw new InvalidDataException("INVALID_INSTANCE_ID");
        if (configuration.MaxBytes is < 1024 or > 1024L * 1024 * 1024 || configuration.MaxRecords is < 1 or > 100_000)
            throw new InvalidDataException("INVALID_SPOOL_LIMITS");
        _instance = instanceId; _capacity = configuration.MaxBytes; _maxRecords = configuration.MaxRecords; _fault = fault;
        var basePath = configuration.RootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telematic", "plugin-state");
        if (string.IsNullOrWhiteSpace(basePath) || !Path.IsPathFullyQualified(basePath)) throw new InvalidDataException("SPOOL_PATH_MUST_BE_ABSOLUTE");
        Root = Path.Combine(basePath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId))).ToLowerInvariant(), "spool");
        SecureDirectory(Root);
        var ownerPath = Path.Combine(Root, "owner.lock");
        RejectLink(ownerPath);
        _owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { Recover(); } catch { _owner.Dispose(); throw; }
    }

    public SpoolDiagnostics Diagnostics()
    {
        lock (_gate) return new(_byId.Count, _rejected, _quarantined, _bytes, _capacity,
            _error is null && _quarantined == 0 && _rejected == 0 && _bytes < _capacity && _fileCount < _maxRecords, _error,
            _records.Count == 0 ? null : _records.First().Value.CreatedAt);
    }

    public IReadOnlyList<SpoolEntry> Eligible(ISet<string> excluded, int limit)
    { lock (_gate) return _records.Values.Where(x => !excluded.Contains(x.MessageId)).Take(limit).ToArray(); }

    public bool TryPersist(byte[] bytes, out SpoolEntry? entry)
    {
        entry = null;
        var envelope = DurableEventValidation.ValidateFrame(bytes, _instance);
        var stored = EncodeRecord(bytes);
        lock (_gate)
        {
            if (_needsRecovery)
            {
                _records.Clear(); _byId.Clear(); _bytes = 0; _fileCount = 0; _rejected = 0; _quarantined = 0;
                Recover(); _needsRecovery = false;
            }
            if (_byId.TryGetValue(envelope.MessageId, out entry)) return true;
            if (_fileCount >= _maxRecords || _bytes + stored.Length > _capacity)
            { Fail("SPOOL_FULL"); return false; }
            var sequence = Math.Max(DateTime.UtcNow.Ticks, _sequence + 1); _sequence = sequence;
            var name = $"{sequence:D19}-{envelope.MessageId}.pending.json";
            var destination = Path.Combine(Root, name);
            var temporary = destination + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { _fault?.Invoke("before-write"); stream.Write(stored); stream.Flush(true); _fault?.Invoke("after-flush"); }
                File.Move(temporary, destination);
                _fault?.Invoke("after-rename");
                SyncDirectory();
                entry = new(envelope.MessageId, envelope.MessageType, name, stored.Length, envelope.OccurredAt);
                _records.Add(name, entry); _byId.Add(entry.MessageId, entry); _bytes += stored.Length; _fileCount++;
                _error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An uncertain rename/fsync is not sent as successful capture.
                // Preserve any temporary/final bytes for next-start recovery.
                _needsRecovery = true; Fail("SPOOL_WRITE_FAILED");
                return false;
            }
        }
    }

    public string? Read(SpoolEntry entry)
    {
        lock (_gate)
        {
            if (!_byId.ContainsKey(entry.MessageId)) return null;
            var path = Path.Combine(Root, entry.FileName);
            try
            {
                RejectLink(path);
                if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("RECORD_TOO_LARGE");
                var bytes = DecodeRecord(File.ReadAllBytes(path));
                var envelope = DurableEventValidation.ValidateFrame(bytes, _instance);
                if (envelope.MessageId != entry.MessageId) throw new InvalidDataException("RECORD_ID_MISMATCH");
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            { Quarantine(path); RemoveMetadata(entry); return null; }
        }
    }

    public bool Retire(string messageId)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(messageId, out var entry)) return false;
            _fault?.Invoke("before-retire");
            File.Delete(Path.Combine(Root, entry.FileName));
            _fault?.Invoke("after-retire");
            SyncDirectory(); // crash may restore a positively accepted record: duplicate replay is safe
            RemoveMetadata(entry); _bytes -= entry.Bytes; _fileCount--;
            if (_error == "SPOOL_FULL") _error = null;
            return true;
        }
    }

    public bool Reject(string messageId, string code)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(messageId, out var entry)) return false;
            var safeCode = new string(code.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(64).ToArray());
            if (safeCode.Length == 0) safeCode = "REJECTED";
            File.Move(Path.Combine(Root, entry.FileName), Path.Combine(Root, entry.FileName.Replace(".pending.json", $".rejected-{safeCode}.json")));
            SyncDirectory(); RemoveMetadata(entry); _rejected++; Fail("PERMANENT_REJECTION");
            return true;
        }
    }

    private void RemoveMetadata(SpoolEntry entry) { _records.Remove(entry.FileName); _byId.Remove(entry.MessageId); }
    private void Fail(string code)
    {
        if (_error != code) Log.Error("Telematic durability degraded {DiagnosticCode} {Pending} {SpoolBytes} {Capacity}", code, _byId.Count, _bytes, _capacity);
        _error = code;
    }

    private void Recover()
    {
        foreach (var path in Directory.EnumerateFiles(Root).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name == "owner.lock") continue;
            if (name is "generation.json" or "generation.initialized" or "generation.json.tmp" or "commands.json" or "commands.initialized" or "commands.json.tmp") { RejectLink(path); continue; }
            RejectLink(path);
            var length = new FileInfo(path).Length;
            _bytes += length; _fileCount++;
            if (name.Contains(".corrupt-", StringComparison.Ordinal)) { _quarantined++; continue; }
            if (name.Contains(".rejected-", StringComparison.Ordinal)) { _rejected++; continue; }
            try
            {
                if (length > 128 * 1024 || !name.Contains(".pending.json", StringComparison.Ordinal)) throw new InvalidDataException("INVALID_RECORD_FILE");
                var envelope = DurableEventValidation.ValidateFrame(DecodeRecord(File.ReadAllBytes(path)), _instance);
                var expectedSuffix = "-" + envelope.MessageId + ".pending.json";
                var finalName = name.EndsWith(".tmp", StringComparison.Ordinal) ? name[..^4] : name;
                if (finalName.Length < 19 || !finalName.EndsWith(expectedSuffix, StringComparison.Ordinal) || !long.TryParse(finalName[..19], out var sequence)) throw new InvalidDataException("INVALID_RECORD_NAME");
                _sequence = Math.Max(_sequence, sequence);
                if (name.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    // A complete interrupted write is recovered; partial writes are quarantined.
                    if (File.Exists(Path.Combine(Root, finalName))) { Quarantine(path); continue; }
                    File.Move(path, Path.Combine(Root, finalName)); SyncDirectory();
                }
                if (_byId.ContainsKey(envelope.MessageId)) { Quarantine(Path.Combine(Root, finalName)); continue; }
                var entry = new SpoolEntry(envelope.MessageId, envelope.MessageType, finalName, length, envelope.OccurredAt);
                _records.Add(finalName, entry); _byId.Add(entry.MessageId, entry);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
            { Quarantine(path); }
        }
        if (_quarantined > 0) Fail("SPOOL_CORRUPTION");
        if (_rejected > 0) Fail("PERMANENT_REJECTION");
        if (_bytes >= _capacity || _fileCount >= _maxRecords) Fail("SPOOL_FULL");
    }

    // The exclusive spool owner also owns the producer lineage. Unlike pending
    // filename sequence, this high-water survives retirement of every event.
    // Lost/corrupt initialized state is never reset into a guessed new lineage.
    public NativeGeneration AllocateGeneration(string sessionId)
    {
        if (!DurableEventValidation.IsIdentifier(sessionId)) throw new InvalidDataException("GENERATION_SESSION_INVALID");
        lock (_gate)
        {
            var path = Path.Combine(Root, "generation.json");
            var marker = Path.Combine(Root, "generation.initialized");
            RejectLink(path); RejectLink(marker);
            GenerationState state;
            if (!File.Exists(path))
            {
                if (File.Exists(marker)) throw new InvalidDataException("GENERATION_STATE_MISSING");
                // A crash while initializing leaves an explicit missing-state
                // failure, not a silently recreated lineage.
                using (var initial = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) initial.Flush(true);
                SyncDirectory();
                state = new(1, Guid.NewGuid().ToString("N"), 0, "");
            }
            else
            {
                if (!File.Exists(marker) || new FileInfo(path).Length > 4096) throw new InvalidDataException("GENERATION_STATE_INVALID");
                state = JsonSerializer.Deserialize<GenerationState>(DecodeRecord(File.ReadAllBytes(path)), TelematicProtocol.JsonOptions)
                    ?? throw new InvalidDataException("GENERATION_STATE_INVALID");
                if (state.Version != 1 || !Guid.TryParseExact(state.Lineage, "N", out _) || state.Lineage != state.Lineage.ToLowerInvariant() || state.Ordinal < 1 || state.Ordinal > 9_007_199_254_740_991L || !DurableEventValidation.IsIdentifier(state.SessionId))
                    throw new InvalidDataException("GENERATION_STATE_INVALID");
                if (state.SessionId == sessionId) return new(state.Lineage, state.Ordinal);
            }
            // Cap to JSON-safe integers for the existing C# -> Go -> JS path.
            if (state.Ordinal >= 9_007_199_254_740_991L) throw new InvalidDataException("GENERATION_EXHAUSTED");
            var next = new GenerationState(1, state.Lineage, state.Ordinal + 1, sessionId);
            var temporary = path + ".tmp"; RejectLink(temporary);
            _fault?.Invoke("generation-before-write");
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(EncodeRecord(JsonSerializer.SerializeToUtf8Bytes(next, TelematicProtocol.JsonOptions))); stream.Flush(true); }
            _fault?.Invoke("generation-after-flush");
            File.Move(temporary, path, true); SyncDirectory();
            _fault?.Invoke("generation-after-commit");
            return new(next.Lineage, next.Ordinal);
        }
    }

    private sealed record GenerationState(int Version, string Lineage, long Ordinal, string SessionId);

    // Persist the dispatch claim before the side effect. Ambiguous replay never
    // executes again; no claim of distributed exactly-once or successful result.
    public string? ClaimCommand(CommandRequest request)
    {
        lock (_gate)
        {
            var path = Path.Combine(Root, "commands.json");
            var marker = Path.Combine(Root, "commands.initialized");
            RejectLink(path); RejectLink(marker);
            Dictionary<string, string> claims;
            if (File.Exists(path))
            {
                if (!File.Exists(marker) || new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("COMMAND_LEDGER_INVALID");
                claims = JsonSerializer.Deserialize<Dictionary<string, string>>(DecodeRecord(File.ReadAllBytes(path))) ?? throw new InvalidDataException("COMMAND_LEDGER_INVALID");
                if (claims.Count > 8192 || claims.Any(x => x.Value is null || x.Value.Length != 64 || !x.Value.All(char.IsAsciiHexDigit) || !(x.Key.StartsWith("id:", StringComparison.Ordinal) || x.Key.StartsWith("key:", StringComparison.Ordinal)))) throw new InvalidDataException("COMMAND_LEDGER_INVALID");
            }
            else
            {
                if (File.Exists(marker)) throw new InvalidDataException("COMMAND_LEDGER_MISSING");
                using (var initialized = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) initialized.Flush(true);
                SyncDirectory(); claims = new();
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.InstanceId, request.CommandType, request.Parameters, request.RequestedAt, request.ExpiresAt })));
            var keys = new[] { "id:" + request.CommandId, "key:" + request.IdempotencyKey };
            foreach (var key in keys) if (claims.TryGetValue(key, out var prior)) return prior == fingerprint ? "COMMAND_REPLAY_UNCERTAIN" : "COMMAND_IDENTITY_CONFLICT";
            if (claims.Count + 2 > 8192) return "COMMAND_LEDGER_FULL";
            foreach (var key in keys) claims[key] = fingerprint;
            var temporary = path + ".tmp"; RejectLink(temporary);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(EncodeRecord(JsonSerializer.SerializeToUtf8Bytes(claims))); file.Flush(true); }
            File.Move(temporary, path, true); SyncDirectory();
            return null;
        }
    }

    private void Quarantine(string path)
    {
        File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
        SyncDirectory(); _quarantined++; Fail("SPOOL_CORRUPTION");
    }

    public static byte[] EncodeRecord(byte[] frame)
    {
        // Canonicalize before hashing: the JSON writer escapes characters such as
        // timezone '+', and embedding a JsonElement can normalize their spelling.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<JsonElement>(frame), TelematicProtocol.JsonOptions);
        return JsonSerializer.SerializeToUtf8Bytes(new
        { formatVersion = 1, sha256 = Convert.ToHexString(SHA256.HashData(canonical)), envelope = JsonSerializer.Deserialize<JsonElement>(canonical) }, TelematicProtocol.JsonOptions);
    }

    private static byte[] DecodeRecord(byte[] stored)
    {
        using var document = JsonDocument.Parse(stored);
        var root = document.RootElement;
        if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var format) || format != 1 ||
            !root.TryGetProperty("sha256", out var digest) || digest.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("envelope", out var envelope)) throw new InvalidDataException("INVALID_RECORD_FORMAT");
        var bytes = Encoding.UTF8.GetBytes(envelope.GetRawText());
        if (!string.Equals(digest.GetString(), Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new InvalidDataException("RECORD_CHECKSUM_MISMATCH");
        return bytes;
    }

    private static void RejectLink(string path)
    { if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("SPOOL_LINK_NOT_ALLOWED"); }

    private static void SecureDirectory(string path)
    {
        for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return; }
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("SPOOL_OWNER_UNAVAILABLE");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{sid})", 1, out var descriptor, out _))
            throw new IOException("SPOOL_ACL_FAILED");
        try { if (!SetFileSecurity(path, 0x80000004, descriptor)) throw new IOException("SPOOL_ACL_FAILED"); }
        finally { LocalFree(descriptor); }
    }

    private void SyncDirectory()
    {
        // Unix rename/delete directory entries require fsync. Windows write-through
        // file flush + same-volume rename provide process-crash recovery; power-loss
        // guarantees still depend on the filesystem/storage hardware.
        if (OperatingSystem.IsWindows()) return;
        var fd = OpenDirectory(Root, 0);
        if (fd < 0) throw new IOException("SPOOL_DIRECTORY_OPEN_FAILED");
        try { if (Fsync(fd) != 0) throw new IOException("SPOOL_DIRECTORY_SYNC_FAILED"); }
        finally { CloseDirectory(fd); }
    }
    public void Dispose() => _owner.Dispose();
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetFileSecurity(string path, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close")] private static extern int CloseDirectory(int fd);
}
