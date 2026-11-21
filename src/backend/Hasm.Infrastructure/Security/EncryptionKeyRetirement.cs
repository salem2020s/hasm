namespace Hasm.Infrastructure.Security;

/// <summary>
/// Which encryption master keys one kind of live data still needs (D-180): every key id named by a stored row OR by the header of its envelope on disk (a crash between the file replacement and
/// the row update of a rewrap can leave them different; both are kept). <see cref="Unknown"/> counts files whose key could not be determined (unreadable file or malformed envelope): any unknown
/// makes every non-active key non-retirable (fail closed). <see cref="UnderOtherKeys"/> counts the items not (yet) under the given active key.
/// </summary>
public sealed record EncryptionKeyUsage(IReadOnlyList<string> KeyIdsInUse, int Unknown, int UnderOtherKeys)
{
    public static EncryptionKeyUsage Empty { get; } = new([], 0, 0);
}

/// <summary>Collects key ids and the unknown/other-key counts while scanning stored items (one item = one row and, when present, its envelope file).</summary>
public sealed class EncryptionKeyUsageBuilder(string? activeKeyId)
{
    private readonly SortedSet<string> ids = new(StringComparer.Ordinal);
    private int unknown, other;

    /// <summary>Records one item: the key id its row names and the bytes of its envelope (null when the file does not exist; the row's key id still counts).</summary>
    public void Add(string? rowKeyId, Func<byte[]?> readEnvelope)
    {
        var keys = new List<string>();
        if (rowKeyId is not null)
        {
            if (KekRing.IsValidKeyId(rowKeyId)) keys.Add(rowKeyId); else { unknown++; return; }
        }
        byte[]? envelope;
        try { envelope = readEnvelope(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unknown++; Track(keys); return; }
        if (envelope is not null)
        {
            try { keys.Add(EnvelopeEncryptor.KeyIdOf(envelope)); }
            catch (Exception ex) when (ex is EncryptionUnavailableException or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException) { unknown++; }
        }
        else if (rowKeyId is null) { unknown++; return; }   // neither the row nor a file names a key
        Track(keys);
    }

    private void Track(List<string> keys)
    {
        foreach (var k in keys) ids.Add(k);
        if (activeKeyId is not null && keys.Any(k => !string.Equals(k, activeKeyId, StringComparison.Ordinal))) other++;
    }

    public EncryptionKeyUsage Build() => new([.. ids], unknown, other);
}

/// <summary>The live key usage of one registered company (<see cref="Readable"/> false: not Ready, not reachable or its storage not configured; then nothing is known and no key may be retired).</summary>
public sealed record CompanyKeyUsage(Guid CompanyId, bool Readable, string? Problem, EncryptionKeyUsage Attachments, EncryptionKeyUsage ImportOriginals);

/// <summary>
/// The encryption keys the RETAINED backup runs of one backup root need (D-138 key inventory); <see cref="InventoryComplete"/> false makes every non-active key non-retirable. It is false, with a
/// <see cref="Problem"/> code, when the root is missing, not a directory or a symbolic link, when it holds no complete, verified run (an empty or wrong folder proves nothing), or when a retained
/// run's key needs are unknown.
/// </summary>
public sealed record BackupRootKeyNeeds(string Root, bool InventoryComplete, string? Problem, IReadOnlyList<string> EncryptionKeysNeeded);

/// <summary>One key of the report. <see cref="Present"/> false: a key that data or a retained backup needs is NOT in the key directory (reported, never retirable).</summary>
public sealed record KeyRetirementEntry(string KeyId, bool Present, bool Retirable, IReadOnlyList<string> Reasons);

/// <summary>
/// The encryption-key retirement report (D-181). <see cref="Complete"/> false: at least one blocker; then NO key is retirable. <see cref="ScopeLimits"/> always lists what the report cannot see
/// (<see cref="EncryptionKeyRetirement.ScopeLimits"/>): "retirable" is a statement about the scanned scope only, made under the operator's attestation that no other backup copy exists.
/// </summary>
public sealed record KeyRetirementReport(IReadOnlyList<KeyRetirementEntry> Keys, IReadOnlyList<string> Blockers, bool Complete, IReadOnlyList<string> ScopeLimits);

/// <summary>
/// The retirement rule for encryption master keys (D-181; D-138 extended to live attachments and import originals; register item K). A key is retirable ONLY when:
/// it is not the active key; no live attachment (active, removed or purged row, D-114) and no live import original (not yet purged) needs it; no RETAINED backup run lists it;
/// and nothing is unknown anywhere: every registered company was read, every file's key determined, at least one backup root was evaluated, every root exists, is a real directory, holds at least
/// one complete, verified run and has a complete key inventory, AND the operator attested that the given roots are every place a backup of this installation is kept
/// (<paramref name="backupScopeAttested"/>; the report cannot discover other destinations, copies or standalone sets, so without the attestation it cannot know and nothing is retirable).
/// A report only: retiring a key needs independent approval (D-080(6)) and is not built here.
/// </summary>
public static class EncryptionKeyRetirement
{
    /// <summary>What the report never sees (codes; printed with every report and written into its JSON). The first two are covered only by the operator's attestation; the third by the re-check the
    /// future retirement operation must make under a lock that excludes uploads and rewraps.</summary>
    public static IReadOnlyList<string> ScopeLimits { get; } =
    [
        "backup_roots_not_given_are_not_scanned",
        "standalone_backup_company_sets_and_copies_are_not_scanned",
        "uploads_in_flight_and_orphan_files_are_not_scanned",
    ];

    public static KeyRetirementReport Evaluate(IReadOnlyCollection<string> presentKeyIds, string? activeKeyId, IReadOnlyList<CompanyKeyUsage> companies, IReadOnlyList<BackupRootKeyNeeds> backups,
        bool backupScopeAttested)
    {
        var blockers = new List<string>();
        if (activeKeyId is null) blockers.Add("active_key_not_configured");
        foreach (var c in companies.Where(c => !c.Readable)) blockers.Add($"company_unreadable:{c.CompanyId:N}:{c.Problem ?? "unknown"}");
        foreach (var c in companies.Where(c => c.Readable && c.Attachments.Unknown > 0)) blockers.Add($"attachment_key_unknown:{c.CompanyId:N}:{c.Attachments.Unknown}");
        foreach (var c in companies.Where(c => c.Readable && c.ImportOriginals.Unknown > 0)) blockers.Add($"import_original_key_unknown:{c.CompanyId:N}:{c.ImportOriginals.Unknown}");
        if (backups.Count == 0) blockers.Add("no_backup_root");
        foreach (var b in backups.Where(b => !b.InventoryComplete)) blockers.Add($"backup_inventory_incomplete:{b.Problem ?? "unknown"}");
        if (!backupScopeAttested) blockers.Add("backup_scope_not_attested");

        var attachments = companies.Where(c => c.Readable).SelectMany(c => c.Attachments.KeyIdsInUse).ToHashSet(StringComparer.Ordinal);
        var imports = companies.Where(c => c.Readable).SelectMany(c => c.ImportOriginals.KeyIdsInUse).ToHashSet(StringComparer.Ordinal);
        var backed = backups.SelectMany(b => b.EncryptionKeysNeeded).ToHashSet(StringComparer.Ordinal);
        var present = presentKeyIds.ToHashSet(StringComparer.Ordinal);
        var all = new SortedSet<string>(present, StringComparer.Ordinal);
        all.UnionWith(attachments); all.UnionWith(imports); all.UnionWith(backed);
        if (activeKeyId is not null) all.Add(activeKeyId);

        var entries = new List<KeyRetirementEntry>();
        foreach (var id in all)
        {
            var reasons = new List<string>();
            if (id == activeKeyId) reasons.Add("active_key");
            if (attachments.Contains(id)) reasons.Add("needed_by_attachments");
            if (imports.Contains(id)) reasons.Add("needed_by_import_originals");
            if (backed.Contains(id)) reasons.Add("needed_by_retained_backups");
            if (!present.Contains(id)) reasons.Add("missing_from_key_directory");
            if (blockers.Count > 0) reasons.Add("report_incomplete");
            entries.Add(new KeyRetirementEntry(id, present.Contains(id), reasons.Count == 0, reasons));
        }
        return new KeyRetirementReport(entries, blockers, blockers.Count == 0, ScopeLimits);
    }
}
