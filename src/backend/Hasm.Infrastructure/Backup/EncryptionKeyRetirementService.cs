using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Security;
using Npgsql;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// Builds the encryption-key retirement report (D-181) from the live data of every registered company (sensitive attachments and not-yet-purged import originals, read over the administrative
/// connection; D-180) and the key inventory of the retained backup runs of every given backup root (D-138). Read-only: it never retires, rewraps or deletes anything (retirement needs independent
/// approval, D-080(6)). Fail closed: a company that is not Ready (except a Pending one, which has no database and therefore no data), cannot be read, or whose storage is not configured, and a
/// backup root whose inventory cannot be evaluated, make the report incomplete, and then no key is retirable.
/// </summary>
public sealed class EncryptionKeyRetirementService(HasmDatabaseOptions database, RetentionOptions retention, AttachmentOptions attachments, ImportOptions imports, EncryptionOptions encryption,
    TimeProvider? clock = null)
{
    public async Task<KeyRetirementReport> ReportAsync(IReadOnlyCollection<string> presentKeyIds, string? activeKeyId, IReadOnlyList<string> backupRoots, SigningTrust? trust, CancellationToken ct = default)
    {
        var companies = new List<CompanyKeyUsage>();
        var registry = new CompanyRegistry(database.BuildSystemConnectionString(admin: true));
        foreach (var company in await registry.ListAsync(ct))
            companies.Add(await CompanyUsageAsync(company, activeKeyId, ct));

        var backups = new List<BackupRootKeyNeeds>();
        var service = new RetentionService(database, retention, clock);
        foreach (var root in backupRoots)
        {
            try
            {
                var (plan, _) = await service.PlanAsync(root, trust, ct);
                backups.Add(new BackupRootKeyNeeds(root, plan.KeyInventoryComplete, plan.KeyInventoryComplete ? null : "inventory_incomplete", plan.EncryptionKeysNeeded));
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or NpgsqlException or InvalidOperationException)
            {
                backups.Add(new BackupRootKeyNeeds(root, false, ex is BackupException b ? b.Code : "backup_root_unreadable", []));
            }
        }
        return EncryptionKeyRetirement.Evaluate(presentKeyIds, activeKeyId, companies, backups);
    }

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
