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
/// symbolic link, or holds no complete, verified run is such a root (review H1 of PR 182: a typo, an unmounted share or the wrong folder must never read as "no retained run needs any key").
/// Every run on disk counts, also one the retention plan would delete: it is still present and restorable, and automatic deletion is inactive (review M-FH1 of PR 182). The retention preview's own
/// key inventory (D-138: the runs the plan keeps) is not used and not changed. A present run whose key needs are unknown, or an unfinished deletion, makes the root's inventory incomplete.
/// Scope: only the given roots are read. Other destinations, copies and standalone <c>backup-company</c> sets are invisible, so the caller must pass the operator's attestation
/// (<paramref name="backupScopeAttested"/> of <see cref="ReportAsync"/>); without it the report is incomplete.
/// </summary>
public sealed class EncryptionKeyRetirementService(HasmDatabaseOptions database, AttachmentOptions attachments, ImportOptions imports, EncryptionOptions encryption, TimeProvider? clock = null)
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
                var (runs, deleting) = RetentionInventory.Scan(root, trust);
                // checked AFTER the scan as well: a root that vanished meanwhile yields no runs and is refused here
                var (problem, keys) = InventoryOfPresentRuns(runs, deleting);
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
    /// restorable (review M-FH1 of PR 182). Incomplete when no run proves the root (<c>backup_root_no_complete_verified_run</c>), when a deletion started and did not finish
    /// (<c>backup_root_deletion_incomplete</c>: what is left of that run cannot be read as a run), or when any present run's key needs are unknown (<c>backup_run_key_needs_unknown</c>: an
    /// unfinished, unverifiable, version 1 or unencrypted DEV/TEST run). The known keys are returned in every case, so they are reported as needed.
    /// </summary>
    public static (string? Problem, IReadOnlyList<string> EncryptionKeysNeeded) InventoryOfPresentRuns(IReadOnlyList<RetentionRun> runs, IReadOnlyList<string> deletingFolders)
    {
        var problem = !runs.Any(IsCompleteVerifiedRun) ? "backup_root_no_complete_verified_run"
            : deletingFolders.Count > 0 ? "backup_root_deletion_incomplete"
            : runs.Any(r => !r.Complete || !r.Verified || r.Keys is null) ? "backup_run_key_needs_unknown"
            : null;
        return (problem, [.. runs.SelectMany(r => r.Keys?.EncryptionKeyIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
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
