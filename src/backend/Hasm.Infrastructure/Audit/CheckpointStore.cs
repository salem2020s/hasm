using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hasm.Infrastructure.Audit;

/// <summary>
/// A signed statement "the stream's record <see cref="Seq"/> had hash <see cref="Hash"/> at time <see cref="AtMicros"/>", kept
/// OUTSIDE the audited database. Checkpoints of one stream are themselves chained (<see cref="PrevMac"/> is the previous
/// checkpoint's MAC), so removing or reordering checkpoints inside the file is detected.
/// </summary>
public sealed record AuditCheckpoint(string StreamKey, long Seq, byte[] Hash, long AtMicros, string KeyId, byte[] PrevMac, byte[] Mac)
{
    public static byte[] GenesisPrevMac(string streamKey) => SHA256.HashData(Encoding.UTF8.GetBytes("HASM-AUDIT-CP-GENESIS-v1\0" + streamKey));

    public byte[] Canonical() => AuditCanonical.SerializeCheckpoint(StreamKey, Seq, Hash, AtMicros, KeyId, PrevMac);
}

/// <summary>What a checkpoint file contained, without throwing on damaged content (verification classifies it).</summary>
public sealed record CheckpointRead(IReadOnlyList<AuditCheckpoint> Checkpoints, int UnreadableLines, bool FileExists);

/// <summary>Protected external anchor of audit stream heads. Implementations must live outside the audited PostgreSQL database.</summary>
public interface ICheckpointStore
{
    /// <summary>The newest well-formed checkpoint of the stream, or null when none exists. Throws when the store is unusable.</summary>
    Task<AuditCheckpoint?> ReadLastAsync(string streamKey, CancellationToken ct = default);

    /// <summary>
    /// Appends a checkpoint durably (flushed to disk) and returns it. A sequence number below the newest checkpoint is
    /// refused (checkpoints only move forward); repeating the identical (seq, hash) is a no-op; the same seq with another hash
    /// is an integrity failure (a fork of the stream).
    /// </summary>
    Task<AuditCheckpoint> AppendAsync(string streamKey, long seq, byte[] hash, long atMicros, AuditKeyHandle key, CancellationToken ct = default);

    /// <summary>Every checkpoint of the stream, for verification.</summary>
    Task<CheckpointRead> ReadAllAsync(string streamKey, CancellationToken ct = default);
}

/// <summary>
/// One append-only text file per stream (JSON lines) in a directory outside the database, named by a hash of the stream key
/// (no company ids in file names). Each append is flushed to disk before returning. Offline by design: copy the directory to
/// separate media (or a write-once share) regularly; <c>audit-verify --checkpoint-dir</c> verifies against any copy.
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    public const string Extension = ".hcp";
    private const int Version = 1;
    private readonly string directory;
    private readonly bool enforcePermissions;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileCheckpointStore(string directory, bool enforcePermissions = true)
    {
        this.directory = directory;
        this.enforcePermissions = enforcePermissions;
    }

    public static string FileNameFor(string streamKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hasm-audit-cp/" + streamKey)))[..32].ToLowerInvariant() + Extension;

    private string PathFor(string streamKey) => Path.Combine(directory, FileNameFor(streamKey));

    /// <summary>
    /// Opens the file, retrying briefly while another writer (this or another process) holds it exclusively during an append.
    /// </summary>
    private static async Task<FileStream> OpenAsync(string path, FileStreamOptions options, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, options); }
            catch (IOException) when (attempt < 80) { await Task.Delay(25, ct); }          // sharing violation: an append is in progress
        }
    }

    private static FileStreamOptions ReadOptions => new() { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete };

    /// <summary>The newest well-formed checkpoint among the last lines of the stream (one line is about 330 bytes).</summary>
    private static async Task<AuditCheckpoint?> TailAsync(FileStream fs, string streamKey, CancellationToken ct)
    {
        var take = (int)Math.Min(fs.Length, 8192);
        fs.Seek(-take, SeekOrigin.End);
        var buffer = new byte[take];
        await fs.ReadExactlyAsync(buffer, ct);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
            if (TryParse(lines[i], out var cp) && cp!.StreamKey == streamKey) return cp;
        return null;
    }

    public async Task<AuditCheckpoint?> ReadLastAsync(string streamKey, CancellationToken ct = default)
    {
        var path = PathFor(streamKey);
        try
        {
            if (!File.Exists(path)) return null;
            await using var fs = await OpenAsync(path, ReadOptions, ct);
            return await TailAsync(fs, streamKey, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditUnavailableException("audit_checkpoint_unavailable", "The audit checkpoint store cannot be read.");
        }
    }

    public async Task<CheckpointRead> ReadAllAsync(string streamKey, CancellationToken ct = default)
    {
        var path = PathFor(streamKey);
        try
        {
            if (!File.Exists(path)) return new CheckpointRead([], 0, false);
            var list = new List<AuditCheckpoint>();
            var unreadable = 0;
            await using var fs = await OpenAsync(path, ReadOptions, ct);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                if (TryParse(line, out var cp) && cp!.StreamKey == streamKey) list.Add(cp); else unreadable++;
            }
            return new CheckpointRead(list, unreadable, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditUnavailableException("audit_checkpoint_unavailable", "The audit checkpoint store cannot be read.");
        }
    }

    public async Task<AuditCheckpoint> AppendAsync(string streamKey, long seq, byte[] hash, long atMicros, AuditKeyHandle key, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            PrepareDirectory();
            // The exclusive handle spans "read the newest checkpoint" and "append", so two writers (even in different processes sharing
            // the directory) can never both chain onto the same predecessor.
            var o = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var fs = await OpenAsync(PathFor(streamKey), o, ct);

            var last = fs.Length > 0 ? await TailAsync(fs, streamKey, ct) : null;
            if (last is not null)
            {
                if (seq == last.Seq)
                    return CryptographicOperations.FixedTimeEquals(last.Hash, hash) ? last
                        : throw new AuditIntegrityException("audit_fork", "The stream head conflicts with its protected checkpoint.");
                if (seq < last.Seq)
                    throw new AuditIntegrityException("audit_checkpoint_rollback", "Checkpoints can only move forward.");
            }
            var prevMac = last?.Mac ?? AuditCheckpoint.GenesisPrevMac(streamKey);
            var unsigned = new AuditCheckpoint(streamKey, seq, hash, atMicros, key.Id, prevMac, []);
            var cp = unsigned with { Mac = key.CheckpointMac(unsigned.Canonical()) };

            fs.Seek(0, SeekOrigin.End);
            // A torn previous write (crash mid-line) must not glue onto this record.
            var torn = false;
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                torn = fs.ReadByte() != '\n';
                fs.Seek(0, SeekOrigin.End);
            }
            var line = Encoding.UTF8.GetBytes((torn ? "\n" : "") + Serialize(cp) + "\n");
            await fs.WriteAsync(line, ct);
            await fs.FlushAsync(ct);
            fs.Flush(flushToDisk: true);
            return cp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditUnavailableException("audit_checkpoint_unavailable", "The audit checkpoint could not be persisted.");
        }
        finally { gate.Release(); }
    }

    private void PrepareDirectory()
    {
        if (!Directory.Exists(directory)) AuditFilePermissions.CreateOwnerOnlyDirectory(directory);
        if (enforcePermissions) AuditFilePermissions.EnsureNotWritableByOthers(directory);
    }

    private sealed record Line(int v, string stream, long seq, string hash, long at, string key, string prev, string mac);

    private static string Serialize(AuditCheckpoint c) => JsonSerializer.Serialize(new Line(Version, c.StreamKey, c.Seq,
        Convert.ToHexString(c.Hash).ToLowerInvariant(), c.AtMicros, c.KeyId, Convert.ToHexString(c.PrevMac).ToLowerInvariant(), Convert.ToHexString(c.Mac).ToLowerInvariant()));

    private static bool TryParse(string text, out AuditCheckpoint? cp)
    {
        cp = null;
        try
        {
            var l = JsonSerializer.Deserialize<Line>(text);
            if (l is null || l.v != Version || l.stream is null || l.key is null || !AuditKeyRing.IsValidKeyId(l.key)) return false;
            var hash = Convert.FromHexString(l.hash);
            var prev = Convert.FromHexString(l.prev);
            var mac = Convert.FromHexString(l.mac);
            if (hash.Length != 32 || prev.Length != 32 || mac.Length != 32 || l.seq < 1) return false;
            cp = new AuditCheckpoint(l.stream, l.seq, hash, l.at, l.key, prev, mac);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException) { return false; }
    }
}

/// <summary>In-memory store for tests (with switchable failure to exercise fail-safe behaviour).</summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly Dictionary<string, List<AuditCheckpoint>> data = [];
    /// <summary>Test switches: <see cref="Fail"/> makes every operation fail; <see cref="FailAppend"/> only writes (reads keep working).</summary>
    public volatile bool Fail, FailAppend;

    public Task<AuditCheckpoint?> ReadLastAsync(string streamKey, CancellationToken ct = default)
    {
        if (Fail) throw new AuditUnavailableException("audit_checkpoint_unavailable", "The audit checkpoint store cannot be read.");
        lock (data) return Task.FromResult(data.TryGetValue(streamKey, out var l) ? l[^1] : null);
    }

    public Task<CheckpointRead> ReadAllAsync(string streamKey, CancellationToken ct = default)
    {
        lock (data) return Task.FromResult(data.TryGetValue(streamKey, out var l) ? new CheckpointRead([.. l], 0, true) : new CheckpointRead([], 0, false));
    }

    public Task<AuditCheckpoint> AppendAsync(string streamKey, long seq, byte[] hash, long atMicros, AuditKeyHandle key, CancellationToken ct = default)
    {
        if (Fail || FailAppend) throw new AuditUnavailableException("audit_checkpoint_unavailable", "The audit checkpoint could not be persisted.");
        lock (data)
        {
            var list = data.TryGetValue(streamKey, out var l) ? l : data[streamKey] = [];
            var last = list.Count > 0 ? list[^1] : null;
            if (last is not null)
            {
                if (seq == last.Seq)
                    return Task.FromResult(CryptographicOperations.FixedTimeEquals(last.Hash, hash) ? last
                        : throw new AuditIntegrityException("audit_fork", "The stream head conflicts with its protected checkpoint."));
                if (seq < last.Seq) throw new AuditIntegrityException("audit_checkpoint_rollback", "Checkpoints can only move forward.");
            }
            var unsigned = new AuditCheckpoint(streamKey, seq, hash, atMicros, key.Id, last?.Mac ?? AuditCheckpoint.GenesisPrevMac(streamKey), []);
            var cp = unsigned with { Mac = key.CheckpointMac(unsigned.Canonical()) };
            list.Add(cp);
            return Task.FromResult(cp);
        }
    }

    /// <summary>Test helper: simulates losing the newest checkpoints (an attacker with file access).</summary>
    public void TruncateTo(string streamKey, int count) { lock (data) if (data.TryGetValue(streamKey, out var l)) l.RemoveRange(count, l.Count - count); }
}
