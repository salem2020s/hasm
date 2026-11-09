using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Audit;

public sealed class AuditConfigurationException(string message) : AuditUnavailableException("audit_not_configured", message);

/// <summary>
/// Audit integrity settings (section <c>Hasm:Audit</c>, environment <c>Hasm__Audit__*</c>). No defaults for paths or keys,
/// nothing is hardcoded. Parsing never throws; validation happens when the audit trail is first used, so an unconfigured
/// deployment still starts (and fails audited mutations closed with 503) rather than writing unaudited changes.
/// </summary>
public sealed class AuditOptions
{
    public const string SectionName = "Hasm:Audit";
    public const int MaxUnanchoredLimit = 1000;

    /// <summary>Directory holding the key files (<c>&lt;keyId&gt;.hkey</c>), readable only by the service account.</summary>
    public string? KeyDirectory { get; init; }
    /// <summary>Identifier of the key used to authenticate NEW records. Older keys stay in the directory for verification.</summary>
    public string? ActiveKeyId { get; init; }
    /// <summary>Directory for the append-only checkpoint files; must be outside the audited database and ideally on separate media.</summary>
    public string? CheckpointDirectory { get; init; }
    /// <summary>
    /// How many committed events may be waiting for their external checkpoint before new audited mutations are refused.
    /// 0 (default, strictest) means every previous event must be anchored first.
    /// </summary>
    public int MaxUnanchoredEvents { get; init; }
    /// <summary>Operator-supplied directory/key-permission relaxation for disposable test environments only.</summary>
    public bool AllowInsecureFilePermissions { get; init; }

    public static AuditOptions FromConfiguration(IConfiguration configuration)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{key}"]) ? null : configuration[$"{SectionName}:{key}"]!.Trim();
        var max = 0;
        var maxText = Get("MaxUnanchoredEvents");
        var invalid = maxText is not null && (!int.TryParse(maxText, out max) || max is < 0 or > MaxUnanchoredLimit);
        var insecure = false;
        var insecureText = Get("AllowInsecureFilePermissions");
        if (insecureText is not null && !bool.TryParse(insecureText, out insecure)) invalid = true;
        return new AuditOptions
        {
            KeyDirectory = Get("KeyDirectory"), ActiveKeyId = Get("ActiveKeyId"), CheckpointDirectory = Get("CheckpointDirectory"),
            MaxUnanchoredEvents = invalid ? -1 : max, AllowInsecureFilePermissions = insecure,
        };
    }

    /// <summary>Throws a message naming the setting (never its value) when the options cannot be used.</summary>
    public void EnsureValid(bool requireActiveKey, bool requireCheckpoints)
    {
        var errors = new List<string>();
        if (KeyDirectory is null) errors.Add($"{SectionName}:KeyDirectory is required.");
        if (requireActiveKey && ActiveKeyId is null) errors.Add($"{SectionName}:ActiveKeyId is required.");
        if (ActiveKeyId is not null && !AuditKeyRing.IsValidKeyId(ActiveKeyId)) errors.Add($"{SectionName}:ActiveKeyId is not a valid key identifier.");
        if (requireCheckpoints && CheckpointDirectory is null) errors.Add($"{SectionName}:CheckpointDirectory is required.");
        if (MaxUnanchoredEvents < 0) errors.Add($"{SectionName}:MaxUnanchoredEvents / AllowInsecureFilePermissions are invalid (0-{MaxUnanchoredLimit}; true or false).");
        if (errors.Count > 0) throw new AuditConfigurationException(string.Join(" ", errors));
    }
}

/// <summary>
/// One key version. Two independent sub-keys are derived with HKDF-SHA-256 (RFC 5869) so that the event-authentication key and
/// the checkpoint-authentication key are domain-separated; the master secret is never used directly and never exposed.
/// </summary>
public sealed class AuditKeyHandle
{
    public const int MinimumKeyBytes = 32;
    private readonly byte[] eventKey, checkpointKey;

    public string Id { get; }

    public AuditKeyHandle(string id, byte[] master)
    {
        if (!AuditKeyRing.IsValidKeyId(id)) throw new AuditConfigurationException("Invalid audit key identifier.");
        if (master.Length < MinimumKeyBytes) throw new AuditConfigurationException("An audit key must be at least 32 bytes.");
        Id = id;
        eventKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("hasm-audit/event/v1/" + id));
        checkpointKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("hasm-audit/checkpoint/v1/" + id));
    }

    public byte[] EventMac(byte[] data) => HMACSHA256.HashData(eventKey, data);
    public byte[] CheckpointMac(byte[] data) => HMACSHA256.HashData(checkpointKey, data);

    public bool VerifyEventMac(byte[] data, byte[] mac) => CryptographicOperations.FixedTimeEquals(EventMac(data), mac);
    public bool VerifyCheckpointMac(byte[] data, byte[] mac) => CryptographicOperations.FixedTimeEquals(CheckpointMac(data), mac);

    public override string ToString() => $"AuditKey({Id})"; // never prints material
}

/// <summary>Versioned audit keys, looked up by identifier so historical records stay verifiable after rotation.</summary>
public abstract class AuditKeyRing
{
    private static readonly Regex KeyIdPattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static bool IsValidKeyId(string id) => KeyIdPattern.IsMatch(id);

    /// <summary>The key for new records. Throws <see cref="AuditUnavailableException"/> when missing or unusable.</summary>
    public abstract AuditKeyHandle GetActive();

    /// <summary>A specific (possibly retired) key, or null when it is not available to this process.</summary>
    public abstract AuditKeyHandle? Find(string keyId);
}

/// <summary>Fixed keys held in memory; for tests and composition only.</summary>
public sealed class InMemoryAuditKeyRing : AuditKeyRing
{
    private readonly ConcurrentDictionary<string, AuditKeyHandle> keys = new();
    private volatile string? active;

    /// <summary>Test switch: simulates an unreachable key store (the active key cannot be read, no key can be found).</summary>
    public volatile bool Unavailable;

    public InMemoryAuditKeyRing(string activeId = "k1", byte[]? master = null)
    {
        Add(activeId, master ?? RandomNumberGenerator.GetBytes(32));
        active = activeId;
    }

    public void Add(string id, byte[] master) => keys[id] = new AuditKeyHandle(id, master);
    public void Activate(string id) => active = keys.ContainsKey(id) ? id : throw new KeyNotFoundException();
    public bool Remove(string id) => keys.TryRemove(id, out _);
    public void Clear() { keys.Clear(); }

    public override AuditKeyHandle GetActive() =>
        !Unavailable && active is { } id && keys.TryGetValue(id, out var k) ? k : throw new AuditUnavailableException("audit_key_unavailable", "The active audit key is not available.");

    public override AuditKeyHandle? Find(string keyId) => Unavailable ? null : keys.GetValueOrDefault(keyId);
}

/// <summary>
/// Key files <c>&lt;keyId&gt;.hkey</c> (one line, base64, at least 32 random bytes) in a directory that only the service
/// account can read. Linux: the directory and file must not grant any group/other access (mode 0700 / 0600). Windows: the file's
/// ACL must not grant read access to Everyone, Users or Authenticated Users. Keys are loaded on first use and cached for the
/// process lifetime (restart to pick up a rotation); key material is never logged and never part of an exception message.
/// </summary>
public sealed class FileAuditKeyRing(AuditOptions options) : AuditKeyRing
{
    private readonly ConcurrentDictionary<string, AuditKeyHandle> cache = new();
    public const string Extension = ".hkey";

    public override AuditKeyHandle GetActive()
    {
        options.EnsureValid(requireActiveKey: true, requireCheckpoints: false);
        var key = Find(options.ActiveKeyId!);
        return key ?? throw new AuditUnavailableException("audit_key_unavailable", "The active audit key is missing or unusable.");
    }

    public override AuditKeyHandle? Find(string keyId)
    {
        if (!IsValidKeyId(keyId) || options.KeyDirectory is null) return null;
        if (cache.TryGetValue(keyId, out var cached)) return cached;
        var path = Path.Combine(options.KeyDirectory, keyId + Extension);
        try
        {
            if (!File.Exists(path)) return null;
            if (!options.AllowInsecureFilePermissions) AuditFilePermissions.EnsureOwnerOnly(options.KeyDirectory, path);
            var bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            var handle = new AuditKeyHandle(keyId, bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return cache.GetOrAdd(keyId, handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or AuditUnavailableException)
        {
            return null; // fail closed without leaking paths or contents
        }
    }

    /// <summary>
    /// Creates a new random 256-bit key file with owner-only permissions. Refuses to overwrite an existing key. Used by
    /// the administrative tool; returns only the identifier.
    /// </summary>
    public static void Generate(string directory, string keyId)
    {
        if (!IsValidKeyId(keyId)) throw new ArgumentException("Invalid key identifier (lowercase letters, digits, hyphen; max 32).", nameof(keyId));
        AuditFilePermissions.CreateOwnerOnlyDirectory(directory);
        var path = Path.Combine(directory, keyId + Extension);
        var secret = RandomNumberGenerator.GetBytes(32);
        try { AuditFilePermissions.CreateOwnerOnlyFile(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(secret) + "\n")); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}

/// <summary>Owner-only file creation and verification for key and checkpoint storage (Linux modes, Windows ACLs).</summary>
public static class AuditFilePermissions
{
    public static void CreateOwnerOnlyDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Creates a new file exclusively (never overwrites) readable and writable by the owner only.</summary>
    public static void CreateOwnerOnlyFile(string path, byte[] content)
    {
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, o)) { fs.Write(content); fs.Flush(true); }
        if (OperatingSystem.IsWindows()) RestrictToCurrentUserWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUserWindows(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);   // drop inherited grants
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier))) security.RemoveAccessRule(rule);
        using var me = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Throws <see cref="AuditUnavailableException"/> when the secret file (or its directory on Linux) is accessible to others.</summary>
    public static void EnsureOwnerOnly(string directory, string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(file) & others) != 0 || (File.GetUnixFileMode(directory) & others) != 0)
                throw new AuditUnavailableException("audit_key_permissions", "Audit key storage is accessible to other accounts.");
            return;
        }
        EnsureNotOpenToBroadGroupsWindows(file);
    }

    /// <summary>The checkpoint directory must not be writable by other accounts (Linux modes; Windows: no broad write ACLs).</summary>
    public static void EnsureNotWritableByOthers(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode others = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            if ((File.GetUnixFileMode(directory) & others) != 0)
                throw new AuditUnavailableException("audit_checkpoint_permissions", "The checkpoint directory is writable by other accounts.");
            return;
        }
        if (WindowsBroadAccess(new DirectoryInfo(directory).GetAccessControl(), write: true))
            throw new AuditUnavailableException("audit_checkpoint_permissions", "The checkpoint directory is writable by broad groups.");
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureNotOpenToBroadGroupsWindows(string file)
    {
        if (WindowsBroadAccess(new FileInfo(file).GetAccessControl(), write: false))
            throw new AuditUnavailableException("audit_key_permissions", "Audit key storage is accessible to broad groups.");
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsBroadAccess(FileSystemSecurity security, bool write)
    {
        var broad = new[] { WellKnownSidType.WorldSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid };
        var rights = write ? FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateFiles : FileSystemRights.ReadData;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & rights) != 0
                && broad.Any(b => rule.IdentityReference is SecurityIdentifier sid && sid.IsWellKnown(b)))
                return true;
        return false;
    }
}
