using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Security;
using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// Builds the encryption-key retirement report (D-181) from the live data of every registered company (sensitive attachments and not-yet-purged import originals, read over the administrative
/// connection; D-180) and the key needs of EVERY backup run present in every given backup root (the signed run status of slice 6, D-138). Read-only: it never retires, rewraps or deletes anything (retirement needs independent
/// approval, D-080(6)) and creates no directory. Fail closed: a company that is not Ready (except a Pending one, which has no database and therefore no data), cannot be read, or whose storage
/// is not configured, and a backup root whose inventory cannot be evaluated, make the report incomplete, and then no key is retirable. A backup root that does not exist, is not a directory, is a
/// symbolic link, or holds no complete, verified run is such a root (review H1 of PR 182: a typo, an unmounted share or the wrong folder must never read as "no run present needs any key").
/// Every run on disk counts, also one the retention plan would delete: it is still present and restorable, and automatic deletion is inactive (review M-FH1 of PR 182). The retention preview's own
/// key inventory (D-138: the runs the plan keeps) is not used and not changed. A present run whose key needs are unknown, an unfinished deletion, or any entry of the root that is not a run folder,
/// a <c>.deleting-*</c> folder or <c>.retention</c> (review M-FH3 of PR 182), makes the root's inventory incomplete.
/// Scope: only the given roots are read. Other destinations, copies and standalone <c>backup-company</c> sets are invisible, so the caller must pass the operator's attestation
/// (<paramref name="backupScopeAttested"/> of <see cref="ReportAsync"/>); without it the report is incomplete.
/// </summary>
public sealed partial class EncryptionKeyRetirementService(HasmDatabaseOptions database, AttachmentOptions attachments, ImportOptions imports, EncryptionOptions encryption, TimeProvider? clock = null)
{
    public async Task<KeyRetirementReport> ReportAsync(IReadOnlyCollection<string> presentKeyIds, string? activeKeyId, IReadOnlyList<string> backupRoots, SigningTrust? trust, bool backupScopeAttested,
        CancellationToken ct = default)
    {
        var companies = new List<CompanyKeyUsage>();
        var registry = new CompanyRegistry(database.BuildSystemConnectionString(admin: true));
        foreach (var company in await registry.ListAsync(ct))
            companies.Add(await CompanyUsageAsync(company, activeKeyId, ct));

        var backups = new List<BackupRootKeyNeeds>();
        foreach (var root in backupRoots)
        {
            try
            {
                if (RootProblem(root) is { } rootProblem) { backups.Add(new BackupRootKeyNeeds(root, false, rootProblem, [])); continue; }
                // every entry of the root is listed before AND after the scan (review M-FH3 of PR 182): an entry that is not a run folder can hold a run the scan does not read
                var before = UnrecognisedEntries(root);
                var (runs, deleting) = RetentionInventory.Scan(root, trust);
                var unrecognised = before.Union(UnrecognisedEntries(root), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                // checked AFTER the scan as well: a root that vanished meanwhile yields no runs and is refused here
                var (problem, keys) = InventoryOfPresentRuns(runs, deleting, unrecognised);
                problem = RootProblem(root) ?? problem;
                backups.Add(new BackupRootKeyNeeds(root, problem is null, problem, keys));
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or NpgsqlException or InvalidOperationException)
            {
                backups.Add(new BackupRootKeyNeeds(root, false, ex is BackupException b ? b.Code : "backup_root_unreadable", []));
            }
        }
        return EncryptionKeyRetirement.Evaluate(presentKeyIds, activeKeyId, companies, backups, backupScopeAttested, (clock ?? TimeProvider.System).GetUtcNow());
    }

    /// <summary>
    /// The encryption keys of EVERY run present in a root, and why the inventory is incomplete (or null). Not only the runs the retention plan would keep: a deletion-eligible run is still on disk and
    /// restorable (review M-FH1 of PR 182). Incomplete when the root holds an entry that is not a run folder, a <c>.deleting-*</c> folder or <c>.retention</c>
    /// (<c>backup_root_unrecognised_entry:&lt;names&gt;</c>: a run moved or copied inside the root under another name, e.g. <c>archive/&lt;run id&gt;</c> or <c>&lt;run id&gt;.bak</c>, or an archive file,
    /// is not read as a run, so its key needs are unknown; review M-FH3 of PR 182; also anything inside <c>.retention</c> that is not a slice 6 tombstone, review M-OBKP4), when a present run holds
    /// anything a run does not hold (<c>backup_run_unrecognised_content:&lt;run ids&gt;</c>: the retention scan's <c>Clean</c> is false, e.g. another run copied into <c>&lt;run&gt;/old/&lt;run id&gt;/</c>,
    /// whose key needs are not read; review M-OBKP4 of PR 182), when no run proves the root (<c>backup_root_no_complete_verified_run</c>), when a deletion started and did not
    /// finish (<c>backup_root_deletion_incomplete</c>: what is left of that run cannot be read as a run), or when any present run's key needs are unknown (<c>backup_run_key_needs_unknown</c>: an
    /// unfinished, unverifiable, version 1 or unencrypted DEV/TEST run). The known keys of EVERY present run are returned in every case (also a run whose verdict is <c>failed</c>: its company sets
    /// can still have been written), so they are reported as needed.
    /// </summary>
    public static (string? Problem, IReadOnlyList<string> EncryptionKeysNeeded) InventoryOfPresentRuns(IReadOnlyList<RetentionRun> runs, IReadOnlyList<string> deletingFolders,
        IReadOnlyList<string> unrecognisedEntries)
    {
        var unrecognisedContent = runs.Where(r => !r.Clean).Select(r => r.RunId).Order(StringComparer.Ordinal).ToList();
        var problem = unrecognisedEntries.Count > 0 ? UnrecognisedEntryProblem(unrecognisedEntries)
            : unrecognisedContent.Count > 0 ? EncodedList("backup_run_unrecognised_content:", unrecognisedContent)
            : !runs.Any(IsCompleteVerifiedRun) ? "backup_root_no_complete_verified_run"
            : deletingFolders.Count > 0 ? "backup_root_deletion_incomplete"
            : runs.Any(r => !r.Complete || !r.Verified || r.Keys is null) ? "backup_run_key_needs_unknown"
            : null;
        return (problem, [.. runs.SelectMany(r => r.Keys?.EncryptionKeyIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>The most entry names a <c>backup_root_unrecognised_entry</c> code lists; the rest are counted (<c>+N_more</c>).</summary>
    public const int UnrecognisedEntriesListed = 20;

    /// <summary>
    /// <c>backup_root_unrecognised_entry:&lt;name&gt;,&lt;name&gt;...</c>, the names percent-encoded (RFC 3986 unreserved characters kept), so a name can neither break the code nor carry a control
    /// character into the output; at most <see cref="UnrecognisedEntriesListed"/> names, then <c>+N_more</c>.
    /// </summary>
    public static string UnrecognisedEntryProblem(IReadOnlyList<string> names) => EncodedList("backup_root_unrecognised_entry:", names);

    /// <summary>
    /// <paramref name="prefix"/> followed by the names, each percent-encoded per path segment (an entry inside <c>.retention</c> is named by its relative path, <c>.retention/archive</c>; a <c>/</c>
    /// cannot occur in a name, so the segments stay unambiguous), comma-separated, at most <see cref="UnrecognisedEntriesListed"/> of them, then <c>+N_more</c>.
    /// </summary>
    private static string EncodedList(string prefix, IReadOnlyList<string> names)
    {
        var listed = names.Take(UnrecognisedEntriesListed).Select(n => string.Join("/", n.Split('/').Select(Uri.EscapeDataString))).ToList();
        if (names.Count > UnrecognisedEntriesListed) listed.Add($"+{names.Count - UnrecognisedEntriesListed}_more");
        return prefix + string.Join(",", listed);
    }

    /// <summary>The files slice 6 writes into a tombstone (<c>.retention/tombstones/&lt;run id&gt;/</c>): the deleted run's signed status and signature, the deletion and the completion markers.</summary>
    private static readonly string[] TombstoneFiles =
        [SignedDocuments.RunStatusFileName, SignedDocuments.RunStatusFileName + SignedDocuments.SignatureSuffix, RetentionInventory.DeletedMarker, RetentionInventory.CompletedMarker];

    /// <summary>A temporary file left by a crash inside <c>BackupRunFiles.WriteOwnerOnlyAtomically</c> while it wrote one of the tombstone files (<c>&lt;name&gt;.tmp-&lt;32 hex&gt;</c>).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(run\.status\.json(\.sig)?|deleted\.json|completed\.json)\.tmp-[0-9a-f]{32}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TombstoneTemporaryFile();

    private static readonly EnumerationOptions EveryEntry =
        new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false, MatchType = MatchType.Simple };

    /// <summary>
    /// Review M-OBKP4 of PR 182: the entries inside <c>.retention</c> that are not exactly what slice 6 writes there, as paths relative to the root. Accepted are only a real directory
    /// <c>.retention/tombstones</c>, in it only real directories named by a run id, and in each only real files (no link) named <c>run.status.json</c>, <c>run.status.json.sig</c>,
    /// <c>deleted.json</c>, <c>completed.json</c> (any subset: a crash can interrupt the tombstone) or a crash leftover of one of them (<c>&lt;name&gt;.tmp-&lt;32 hex&gt;</c>). Anything else, e.g. a run
    /// kept in <c>.retention/archive/&lt;run id&gt;/</c>, is a place a restorable run can hide.
    /// </summary>
    private static IEnumerable<string> UnrecognisedInRetention(DirectoryInfo retention)
    {
        foreach (var e in retention.EnumerateFileSystemInfos("*", EveryEntry))
        {
            var here = RetentionInventory.RetentionFolder + "/" + e.Name;
            if (e is not DirectoryInfo tombstones || e.LinkTarget is not null || e.Name != RetentionInventory.TombstonesFolder) { yield return here; continue; }
            foreach (var t in tombstones.EnumerateFileSystemInfos("*", EveryEntry))
            {
                var tomb = here + "/" + t.Name;
                if (t is not DirectoryInfo tombstone || t.LinkTarget is not null || !BackupRunFiles.IsRunId(t.Name)) { yield return tomb; continue; }
                foreach (var f in tombstone.EnumerateFileSystemInfos("*", EveryEntry))
                    if (f is not FileInfo || f.LinkTarget is not null || !(TombstoneFiles.Contains(f.Name, StringComparer.Ordinal) || TombstoneTemporaryFile().IsMatch(f.Name)))
                        yield return tomb + "/" + f.Name;
            }
        }
    }

    /// <summary>
    /// For this report only (review M-FH3 of PR 182): the names of every entry directly in <paramref name="root"/> (files, directories, links, hidden entries included) that is NOT one of the three
    /// things a backup root holds: a run folder (a real directory named by a run id, <c>yyyyMMddTHHmmssZ-&lt;8 lower-case hex&gt;</c>), an unfinished deletion (a real directory
    /// <c>&lt;run id&gt;.deleting-&lt;16 hex&gt;</c>) or the retention folder (a real directory <c>.retention</c>). The retention scan (<see cref="RetentionInventory.Scan"/>) reads only run folders
    /// and ignores everything else; that is right for the retention preview and is not changed. For the report an ignored entry is a blind spot: a complete, signed run moved or copied inside the
    /// root under another name (<c>archive/&lt;run id&gt;</c>, <c>&lt;run id&gt;.bak</c>, <c>.old</c>, <c>run-&lt;run id&gt;</c>, an upper-case id, an archive file) is restorable and its key
    /// needs are not read. A symbolic link is never recognised, whatever its name. <c>.retention</c> is walked as well (review M-OBKP4 of PR 182): anything in it that is not a slice 6 tombstone is
    /// listed by its relative path (<c>.retention/archive</c>). Ordinal order. Reads names and metadata only.
    /// </summary>
    public static IReadOnlyList<string> UnrecognisedEntries(string root)
    {
        if (!Directory.Exists(root)) return [];
        var found = new List<string>();
        foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", EveryEntry))
        {
            if (!Recognised(e)) found.Add(e.Name);
            else if (e.Name == RetentionInventory.RetentionFolder) found.AddRange(UnrecognisedInRetention((DirectoryInfo)e));   // review M-OBKP4: .retention is walked, not trusted
        }
        return [.. found.Order(StringComparer.Ordinal)];

        static bool Recognised(FileSystemInfo e) =>
            e is DirectoryInfo && e.LinkTarget is null
            && (BackupRunFiles.IsRunId(e.Name) || RetentionInventory.DeletingFolder().IsMatch(e.Name) || e.Name == RetentionInventory.RetentionFolder);
    }

    /// <summary>
    /// Why a path cannot be evaluated as a backup root, or null: it must be fully qualified, exist, be a directory and not be a symbolic link (or junction) itself. Reads file-system metadata only.
    /// </summary>
    public static string? RootProblem(string root)
    {
        if (!Path.IsPathFullyQualified(root)) return "backup_root_not_absolute";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (new DirectoryInfo(full).LinkTarget is not null || new FileInfo(full).LinkTarget is not null) return "backup_root_symbolic_link";   // also a dangling link
        if (File.Exists(full)) return "backup_root_not_a_directory";
        return Directory.Exists(full) ? null : "backup_root_missing";
    }

    /// <summary>
    /// A run that proves the folder is a live backup root: finished, its signed status VERIFIED against the trusted signing keys, its key needs known (run status version 2, D-138) and its verdict
    /// <c>ok</c> or <c>partial</c> (at least the system set was written). An incomplete, failed, unverifiable or unencrypted DEV/TEST run does not.
    /// </summary>
    public static bool IsCompleteVerifiedRun(RetentionRun run) => run is { Complete: true, Verified: true, Encrypted: true, Keys: not null, Verdict: "ok" or "partial" };

    private async Task<CompanyKeyUsage> CompanyUsageAsync(CompanyRecord company, string? activeKeyId, CancellationToken ct)
    {
        CompanyKeyUsage Unreadable(string problem) => new(company.Id, false, problem, EncryptionKeyUsage.Empty, EncryptionKeyUsage.Empty);
        if (company.State == ProvisioningState.Pending) return new(company.Id, true, null, EncryptionKeyUsage.Empty, EncryptionKeyUsage.Empty);   // no database was ever created: no data
        if (company.State != ProvisioningState.Ready) return Unreadable("company_not_ready");
        try
        {
            await using var conn = new NpgsqlConnection(database.BuildCompanyAdminConnectionString(company.PhysicalDatabaseName));
            await conn.OpenAsync(ct);
            var attachmentUsage = await AttachmentService.KeyUsageAsync(conn, new AttachmentFileStore(attachments, encryption), company.Id, activeKeyId, ct);
            var importUsage = await ImportService.KeyUsageAsync(conn, new EncryptedFileStore(imports, encryption), company.Id, activeKeyId, ct);
            return new(company.Id, true, null, attachmentUsage, importUsage);
        }
        catch (EncryptionUnavailableException ex) { return Unreadable(ex.Code); }   // e.g. attachment_storage_not_configured, import_storage_not_configured
        catch (Exception ex) when (ex is NpgsqlException or IOException or UnauthorizedAccessException or InvalidOperationException) { return Unreadable("company_unreadable"); }
    }
}
