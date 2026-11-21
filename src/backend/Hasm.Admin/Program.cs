using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;
using Npgsql;

// Explicit administrative tool. Never started by the API; uses the privileged database account.
// Usage:
//   Hasm.Admin init-system-db
//   Hasm.Admin register-company              (registers a Pending company; prints its id only)
//   Hasm.Admin bootstrap-admin <login> [display name]  (creates the FIRST identity administrator; refuses if one exists)
//   Hasm.Admin provision-company <company-id> (creates + migrates its separate database; safe to repeat/resume)
//   Hasm.Admin migrate-companies [--company <id>]  (applies pending forward-only company migrations to Ready companies; per-company outcome; exit 8 on partial failure)
//   Hasm.Admin grant-company-runtime <company-id> [--rotate-credential]  (creates/updates the company's own least-privilege runtime login role and credential file; idempotent)
//   Hasm.Admin grant-company-admin <login> <company-id>  (offline initial/recovery: membership + Company Administrator role; audited)
//   Hasm.Admin grant-import-approver <login> <company-id> | revoke-import-approver <login> <company-id>  (offline delegation of the Import Approver role: approval of imports; audited)
//   Hasm.Admin audit-keygen <key-id>          (creates a new random audit key file with owner-only permissions; never overwrites)
//   Hasm.Admin encryption-keygen <key-id>     (creates a new random master key file for the import/attachment file encryption with owner-only permissions; never overwrites; back it up SEPARATELY from the data)
//   Hasm.Admin audit-verify <system|company-id|business:company-id> [--checkpoint-dir <dir>]   (READ-ONLY integrity verification; exit 0 verified, 6 failed, 7 incomplete)
//   Hasm.Admin audit-anchor <system|company-id> [--establish-baseline]  (records the head of a sound stream in the checkpoint store; never changes the database)
//   Hasm.Admin backup-company <company-id> <destination-dir>  (Phase 4.3: database + attachment folder of ONE Ready company into a verified set; exit 8 when key ids are missing)
//   Hasm.Admin trial-restore-company <set-dir> <company-id> <attachment-target-dir> [--keep]  (restores into a NEW unregistered database, runs the pre-cutover checks, drops it unless --keep; exit 0 ok, 8 warnings, 1 failed)
//   Hasm.Admin drop-trial-restore <kept-file>  (drops ONLY the trial database a --keep run recorded in that owner-only file)
//   Hasm.Admin backup-run <root>  (Phase 4.3 slice 4: system database + every Ready company into <root>/<run id>/, run.json last; exit 0 ok, 8 partial, 1 failed, 4 another run in progress)
//   Hasm.Admin backup-status <root> [--max-age-hours N]  (READ-ONLY, no database: newest complete run; exit 0 ok, 8 partial or missed runs, 1 failed, missing or stale)
//   Hasm.Admin backup-retention-preview <root> --keep-last N [--keep-days D]  (READ-ONLY, no database: lists the runs a policy WOULD remove; deletes nothing)
// Configuration comes only from environment variables (Hasm__Database__*); see docs/DEVELOPMENT.md.
// Physical database names are never printed.

const int Ok = 0, Failed = 1, Usage = 2, BadConfig = 3, InProgress = 4, AdminExists = 5, AuditFailed = 6, AuditIncomplete = 7, PartialFailure = 8;

var command = args.Length > 0 ? args[0] : null;
Guid companyId = Guid.Empty;
AuditStream auditStream = default;
string? checkpointDirOverride = null;
Guid? onlyCompany = null;
var rotateCredential = false;
var keepTrial = false;
var maxAgeHours = 26;
var keepLast = 0;
int? keepDays = null;
var validUsage = command switch
{
    "init-system-db" or "register-company" => args.Length == 1,
    "bootstrap-admin" => args.Length is 2 or 3,
    "provision-company" => args.Length == 2 && Guid.TryParse(args[1], out companyId) && companyId != Guid.Empty,
    "migrate-companies" => args.Length == 1
        || (args.Length == 3 && args[1] == "--company" && Guid.TryParse(args[2], out var only) && only != Guid.Empty && (onlyCompany = only) is not null),
    "grant-company-runtime" => (args.Length == 2 || (args.Length == 3 && (rotateCredential = args[2] == "--rotate-credential")))
        && Guid.TryParse(args[1], out companyId) && companyId != Guid.Empty,
    "grant-company-admin" => args.Length == 3 && Guid.TryParse(args[2], out companyId) && companyId != Guid.Empty,
    "grant-fiscal-approver" or "revoke-fiscal-approver" or "grant-journal-approver" or "revoke-journal-approver" or "grant-import-approver" or "revoke-import-approver" => args.Length == 3 && Guid.TryParse(args[2], out companyId) && companyId != Guid.Empty,
    "audit-keygen" => args.Length == 2 && AuditKeyRing.IsValidKeyId(args[1]),
    "encryption-keygen" => args.Length == 2 && KekRing.IsValidKeyId(args[1]),
    "audit-verify" => AuditStream.TryParse(args.Length > 1 ? args[1] : null, out auditStream)
        && (args.Length == 2 || (args.Length == 4 && args[2] == "--checkpoint-dir" && !string.IsNullOrWhiteSpace(checkpointDirOverride = args[3]))),
    "audit-anchor" => AuditStream.TryParse(args.Length > 1 ? args[1] : null, out auditStream) && (args.Length == 2 || (args.Length == 3 && args[2] == "--establish-baseline")),
    "backup-company" => args.Length == 3 && Guid.TryParse(args[1], out companyId) && companyId != Guid.Empty && Path.IsPathFullyQualified(args[2]),
    "trial-restore-company" => (args.Length == 4 || (args.Length == 5 && (keepTrial = args[4] == "--keep")))
        && Guid.TryParse(args[2], out companyId) && companyId != Guid.Empty && Path.IsPathFullyQualified(args[1]) && Path.IsPathFullyQualified(args[3]),
    "drop-trial-restore" => args.Length == 2 && Path.IsPathFullyQualified(args[1]),
    "backup-run" => args.Length == 2 && Path.IsPathFullyQualified(args[1]),
    "backup-status" => Path.IsPathFullyQualified(args.Length > 1 ? args[1] : "") && (args.Length == 2
        || (args.Length == 4 && args[2] == "--max-age-hours" && int.TryParse(args[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxAgeHours) && maxAgeHours is >= 1 and <= 8760)),
    "backup-retention-preview" => Path.IsPathFullyQualified(args.Length > 1 ? args[1] : "") && (args.Length is 4 or 6) && args[2] == "--keep-last"
        && int.TryParse(args[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out keepLast) && keepLast is >= 1 and <= 100000
        && (args.Length == 4 || (args[4] == "--keep-days" && int.TryParse(args[5], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kd) && kd is >= 0 and <= 100000 && (keepDays = kd) is not null)),
    _ => false,
};
if (!validUsage)
{
    Console.Error.WriteLine("Usage: Hasm.Admin init-system-db | register-company | migrate-companies [--company <id>] | grant-company-runtime <company-id> [--rotate-credential] | bootstrap-admin <login> [display name] | provision-company <company-id> | grant-company-admin <login> <company-id> | grant-fiscal-approver <login> <company-id> | grant-journal-approver <login> <company-id> | grant-import-approver <login> <company-id> | revoke-import-approver <login> <company-id> | revoke-journal-approver <login> <company-id> | revoke-fiscal-approver <login> <company-id> | audit-keygen <key-id> | encryption-keygen <key-id> | audit-verify <system|company-id|business:company-id> [--checkpoint-dir <dir>] | audit-anchor <system|company-id|business:company-id> [--establish-baseline] | backup-company <company-id> <destination-dir> | trial-restore-company <set-dir> <company-id> <attachment-target-dir> [--keep] | drop-trial-restore <kept-file> | backup-run <root> | backup-status <root> [--max-age-hours N] | backup-retention-preview <root> --keep-last N [--keep-days D]  (all paths absolute)");
    return Usage;
}

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var auditOptions = AuditOptions.FromConfiguration(configuration);

if (command == "audit-keygen")
{
    // No database involved. The key is written to the configured key directory with owner-only permissions; only its id is printed.
    try
    {
        auditOptions.EnsureValid(requireActiveKey: false, requireCheckpoints: false);
        FileAuditKeyRing.Generate(auditOptions.KeyDirectory!, args[1]);
        Console.WriteLine($"Audit key '{args[1]}' created. Back it up now (see docs/DEVELOPMENT.md), then set Hasm__Audit__ActiveKeyId to activate it.");
        return Ok;
    }
    catch (AuditConfigurationException ex) { Console.Error.WriteLine(ex.Message); return BadConfig; }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("The key could not be created (it may already exist; existing keys are never overwritten)."); return Failed; }
}

if (command is "backup-status" or "backup-retention-preview")
{
    // Read-only and without any database: usable by an operating-system monitor with no credentials at all (D-071). Nothing is written or deleted.
    var runs = BackupRunStatus.List(args[1]);
    var now = DateTimeOffset.UtcNow;
    if (command == "backup-retention-preview")
    {
        var selected = BackupRunStatus.RetentionPreview(runs, now, keepLast, keepDays);
        Console.WriteLine($"Retention PREVIEW (nothing is deleted; deletion is an owner decision, package 4.3-2): {runs.Count(r => r.IsComplete)} complete run(s), {selected.Count} would be removed by --keep-last {keepLast}{(keepDays is { } d ? $" --keep-days {d}" : "")}.");
        foreach (var id in selected) Console.WriteLine($"  would remove: {id}");
        Console.WriteLine($"Never selected: the newest complete run; incomplete run(s): {runs.Count(r => !r.IsComplete && !r.HasReportFile)}; unreadable report(s): {runs.Count(r => r.IsUnreadable)}.");
        return Ok;
    }
    var status = BackupRunStatus.Evaluate(runs, now, TimeSpan.FromHours(maxAgeHours));
    Console.WriteLine($"Backup status: {status.Code} (exit {status.ExitCode}).");
    if (status.Latest is { } latest)
    {
        Console.WriteLine($"Newest complete run {latest.RunId}: {latest.Verdict}, finished {(now - latest.FinishedUtc).TotalHours:0.0} hour(s) ago, duration {latest.DurationMs / 1000.0:0.0} s; system {latest.System.Outcome}; companies: {latest.Companies.Count(c => c.Outcome == "ok")} ok, {latest.Companies.Count(c => c.Outcome == "ok_missing_keys")} missing keys, {latest.Companies.Count(c => c.Outcome == "busy")} busy, {latest.Companies.Count(c => c.Outcome == "failed")} failed, {latest.Skipped} not Ready.");
        Console.WriteLine($"Encrypted: {(latest.Encrypted ? "yes" : "no (keep the backup root on an encrypted volume)")}; data excluded from the system dump: {string.Join(", ", latest.Excluded)}.");
    }
    foreach (var (company, n) in status.MissedRuns) Console.WriteLine($"  [WARNING] company_missed_runs company={company} consecutive={n}");
    if (status.IncompleteRuns > 0) Console.WriteLine($"  [WARNING] incomplete_runs count={status.IncompleteRuns} (older than 24 hours, never deleted)");
    if (status.UnreadableRuns > 0) Console.WriteLine($"  [WARNING] unreadable_reports count={status.UnreadableRuns}");
    return status.ExitCode;
}

if (command == "encryption-keygen")
{
    // No database involved. The master key is written to the configured key directory with owner-only permissions; only its id is printed.
    var enc = EncryptionOptions.FromConfiguration(configuration);
    if (enc.KeyDirectory is null) { Console.Error.WriteLine($"{EncryptionOptions.SectionName}:KeyDirectory is required."); return BadConfig; }
    try
    {
        FileKekRing.Generate(enc.KeyDirectory, args[1]);
        Console.WriteLine($"Encryption key (key-encryption key) '{args[1]}' created. Back it up now, SEPARATELY from the encrypted data and the database (without it encrypted files cannot be read), then set Hasm__Encryption__ActiveKeyId to use it for new files. Older keys must stay in the directory until every file was re-wrapped.");
        return Ok;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("The key could not be created (it may already exist; existing keys are never overwritten)."); return Failed; }
}

HasmDatabaseOptions options;
try
{
    options = HasmDatabaseOptions.FromConfiguration(configuration);
    // Verification and anchoring are read-only with respect to the database and use the least-privilege runtime account.
    options.EnsureValid(requireAdmin: command is not ("audit-verify" or "audit-anchor"));
}
catch (DatabaseConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return BadConfig;
}

try
{
    switch (command)
    {
        case "init-system-db":
        {
            Console.WriteLine($"Initializing system database ({options}).");
            var result = await new SystemDatabaseInitializer(options).InitializeAsync();
            Console.WriteLine(result.DatabaseCreated ? "System database created." : "System database already exists (left unchanged).");
            Console.WriteLine(result.AppliedMigrations.Count == 0
                ? $"Schema up to date; {result.AlreadyApplied} migration(s) already applied."
                : $"Applied migration(s): {string.Join(", ", result.AppliedMigrations.Select(v => v.ToString("0000")))}.");
            return Ok;
        }
        case "register-company":
        {
            var company = await new CompanyRegistry(options.BuildSystemConnectionString(admin: true)).RegisterPendingAsync();
            Console.WriteLine($"Registered company {company.Id} (Pending). Run: provision-company {company.Id}");
            return Ok;
        }
        case "bootstrap-admin":
        {
            // The password is never accepted on the command line (process listings, shell history).
            var password = Environment.GetEnvironmentVariable("HASM_BOOTSTRAP_ADMIN_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("Set HASM_BOOTSTRAP_ADMIN_PASSWORD or run interactively to be prompted.");
                    return Usage;
                }
                password = ReadHidden("Password: ");
                if (password != ReadHidden("Repeat password: "))
                {
                    Console.Error.WriteLine("Passwords do not match.");
                    return Usage;
                }
            }

            AuthOptions authOptions;
            try { authOptions = AuthOptions.FromConfiguration(new ConfigurationBuilder().AddEnvironmentVariables().Build()); }
            catch (AuthConfigurationException ex) { Console.Error.WriteLine(ex.Message); return BadConfig; }

            // Administrative tool: the privileged account is acceptable here (never in the API runtime).
            var trail = AuditTrail.FromOptions(auditOptions, TimeProvider.System);
            await using var store = new IdentityStore(options.BuildSystemConnectionString(admin: true), TimeProvider.System, trail);
            var auth = new AuthService(store, new PasswordService(authOptions.Pbkdf2Iterations), authOptions);
            var result = await auth.BootstrapAdministratorAsync(args[1], args.Length == 3 ? args[2] : args[1], password);
            if (result is null)
            {
                Console.Error.WriteLine("An identity administrator already exists; nothing was changed.");
                return AdminExists;
            }
            switch (result.Status)
            {
                case CreateUserStatus.Created:
                    Console.WriteLine($"Initial identity administrator created (id {result.User!.Id}).");
                    return Ok;
                case CreateUserStatus.WeakPassword:
                    Console.Error.WriteLine($"Password rejected: {string.Join(", ", result.PasswordProblems!)} (minimum {PasswordPolicy.MinLength} characters).");
                    return Usage;
                default:
                    Console.Error.WriteLine($"Not created: {result.Status}.");
                    return Usage;
            }
        }
        case "grant-company-admin":
        {
            // Offline initial/recovery administration: no HTTP endpoint exists for this. Audited without an actor user.
            await using var access = new AccessService(options.BuildSystemConnectionString(admin: true), TimeProvider.System, PermissionCatalog.Default, AuditTrail.FromOptions(auditOptions, TimeProvider.System));
            var status = await access.BootstrapCompanyAdministratorAsync(args[1], companyId);
            switch (status)
            {
                case AccessService.BootstrapStatus.Granted:
                    Console.WriteLine("Company membership and the Company Administrator role were granted.");
                    return Ok;
                case AccessService.BootstrapStatus.AlreadyAdministrator:
                    Console.WriteLine("The user is already a Company Administrator of this company; nothing changed.");
                    return Ok;
                case AccessService.BootstrapStatus.CompanyUnavailable:
                    Console.Error.WriteLine("The company does not exist or is not Ready; nothing was changed.");
                    return Failed;
                default:
                    Console.Error.WriteLine("No active user with that login; nothing was changed.");
                    return Failed;
            }
        }
        case "grant-fiscal-approver":
        case "revoke-fiscal-approver":
        case "grant-journal-approver":
        case "revoke-journal-approver":
        case "grant-import-approver":
        case "revoke-import-approver":
        {
            // Offline delegation of the Fiscal Approver role (approval of exceptional fiscal reopenings). No HTTP endpoint exists for this: a company administrator
            // cannot grant a permission they do not hold, so this is the supported way to create the first approver and the recovery path. Audited without an actor user.
            await using var access = new AccessService(options.BuildSystemConnectionString(admin: true), TimeProvider.System, PermissionCatalog.Default, AuditTrail.FromOptions(auditOptions, TimeProvider.System));
            var importRole = command.EndsWith("import-approver", StringComparison.Ordinal);
            var journalRole = command.EndsWith("journal-approver", StringComparison.Ordinal);
            var roleName = importRole ? "Import Approver" : journalRole ? "Journal Approver" : "Fiscal Approver";
            var granting = command.StartsWith("grant-", StringComparison.Ordinal);
            var status = importRole
                ? (granting ? await access.GrantImportApproverOfflineAsync(args[1], companyId) : await access.RevokeImportApproverOfflineAsync(args[1], companyId))
                : journalRole
                ? (granting ? await access.GrantJournalApproverOfflineAsync(args[1], companyId) : await access.RevokeJournalApproverOfflineAsync(args[1], companyId))
                : (granting ? await access.GrantFiscalApproverOfflineAsync(args[1], companyId) : await access.RevokeFiscalApproverOfflineAsync(args[1], companyId));
            switch (status)
            {
                case AccessService.FiscalApproverStatus.Granted:
                    Console.WriteLine($"The {roleName} role was granted (company-wide).");
                    return Ok;
                case AccessService.FiscalApproverStatus.AlreadyApprover:
                    Console.WriteLine($"The user is already a {roleName} of this company; nothing changed.");
                    return Ok;
                case AccessService.FiscalApproverStatus.Revoked:
                    Console.WriteLine($"The {roleName} role was removed.");
                    return Ok;
                case AccessService.FiscalApproverStatus.NotApprover:
                    Console.WriteLine($"The user is not a {roleName} of this company; nothing changed.");
                    return Ok;
                case AccessService.FiscalApproverStatus.CompanyUnavailable:
                    Console.Error.WriteLine("The company does not exist or is not Ready; nothing was changed.");
                    return Failed;
                case AccessService.FiscalApproverStatus.NotMember:
                    Console.Error.WriteLine("The user is not an active member of this company (membership is never created by this command); nothing was changed.");
                    return Failed;
                default:
                    Console.Error.WriteLine("No active user with that login; nothing was changed.");
                    return Failed;
            }
        }
        case "audit-verify":
        {
            var (keys, store) = BuildAuditEvidence(auditOptions, checkpointDirOverride, requireActiveKey: false);
            var report = await new AuditVerifier(await VerifierConnectionAsync(options, auditStream), keys, store).VerifyAsync(auditStream);
            PrintReport(report);
            return report.Verdict switch { AuditVerdict.Verified => Ok, AuditVerdict.Failed => AuditFailed, _ => AuditIncomplete };
        }
        case "audit-anchor":
        {
            var (keys, store) = BuildAuditEvidence(auditOptions, null, requireActiveKey: true);
            var anchored = await new AuditVerifier(await VerifierConnectionAsync(options, auditStream), keys, store).AnchorHeadAsync(auditStream, TimeProvider.System, args.Length == 3);
            Console.WriteLine(anchored is { } seq ? $"Stream head anchored at sequence {seq}." : "The stream is empty; nothing to anchor.");
            return Ok;
        }
        case "backup-company":
        case "trial-restore-company":
        case "drop-trial-restore":
        case "backup-run":
            return await BackupCommandAsync(command!, args, companyId, keepTrial, options, configuration);
        case "migrate-companies":
        {
            var registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: true));
            var outcomes = await new CompanyMigrator(options, registry).MigrateAsync(onlyCompany);
            foreach (var o in outcomes)
                Console.WriteLine($"Company {o.CompanyId}: {o.Status}" + (o.Applied.Count > 0 ? $" (applied {string.Join(", ", o.Applied.Select(v => v.ToString("0000")))})" : "") + (o.ErrorCode is null ? "" : $" [{o.ErrorCode}]"));
            var failed = outcomes.Count(o => o.Status == CompanyMigrationStatus.Failed);
            var busy = outcomes.Count(o => o.Status == CompanyMigrationStatus.Busy);
            var skipped = outcomes.Count(o => o.Status == CompanyMigrationStatus.NotReady);
            var done = outcomes.Count(o => o.Status is CompanyMigrationStatus.Migrated or CompanyMigrationStatus.UpToDate);
            Console.WriteLine($"Summary: {outcomes.Count} company(ies): {done} current, {failed} failed, {busy} busy, {skipped} not Ready (skipped).");
            if (outcomes.Any(o => o.Status == CompanyMigrationStatus.Migrated))
                Console.WriteLine("Run grant-company-runtime for each migrated company so the runtime role receives privileges on newly created tables.");
            if (failed > 0) return done > 0 ? PartialFailure : Failed;
            if (busy > 0) return InProgress;
            return onlyCompany is not null && skipped > 0 ? Failed : Ok;
        }
        case "grant-company-runtime":
        {
            var registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: true));
            RuntimeGrantResult result;
            try { result = await new CompanyRuntimeProvisioner(options, registry).GrantAsync(companyId, rotateCredential); }
            catch (CompanyRuntimeProvisioningException ex) { Console.Error.WriteLine($"grant-company-runtime stopped ({ex.Code}): {ex.Message}"); return Failed; }
            catch (CompanyCredentialException ex) { Console.Error.WriteLine($"grant-company-runtime stopped ({ex.Code}): the credential store is unusable or unsafe."); return Failed; }
            if (result.Outcome == RuntimeGrantOutcome.InProgressElsewhere)
            {
                Console.Error.WriteLine("Another process is working on this company; nothing was done.");
                return InProgress;
            }
            Console.WriteLine(result.RoleCreated ? "Runtime login role created." : "Runtime login role already existed (verified safe).");
            Console.WriteLine(result.CredentialWritten ? "A new credential was generated and installed in the credential directory (never printed)." : "Existing credential kept.");
            Console.WriteLine("Runtime privileges applied and verified: this company database only; no ownership, DDL, or audit modification.");
            if (!result.CredentialPresent)
            {
                Console.Error.WriteLine("No credential file exists for this company (the role was created outside this tool). Install its credential or run again with --rotate-credential.");
                return Failed;
            }
            Console.WriteLine(result.LoginVerified ? "Runtime login and schema gate verified." : "Runtime login could not be verified.");
            return result.LoginVerified ? Ok : Failed;
        }
        default: // provision-company
        {
            Console.WriteLine($"Provisioning company {companyId} ({options}).");
            var registry = new CompanyRegistry(options.BuildSystemConnectionString(admin: true));
            var result = await new CompanyProvisioner(options, registry).ProvisionAsync(companyId);
            switch (result.Outcome)
            {
                case ProvisioningOutcome.InProgressElsewhere:
                    Console.Error.WriteLine("Another process is provisioning this company; nothing was done.");
                    return InProgress;
                case ProvisioningOutcome.AlreadyReady:
                    Console.WriteLine("Company is already Ready; mapping verified, nothing changed.");
                    return Ok;
                default:
                    Console.WriteLine(result.DatabaseCreated ? "Company database created." : "Company database already existed (resumed).");
                    Console.WriteLine($"Company is Ready (attempt {result.Attempt}); applied migration(s): {(result.AppliedMigrations.Count == 0 ? "none" : string.Join(", ", result.AppliedMigrations.Select(v => v.ToString("0000"))))}.");
                    return Ok;
            }
        }
    }
}
catch (AuditConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return BadConfig;
}
catch (AuditUnavailableException ex)
{
    Console.Error.WriteLine($"{command} stopped: audit protection is unavailable or the stream is inconsistent ({ex.Code}); nothing was changed.");
    return command is "audit-anchor" ? AuditFailed : Failed;
}
catch (CompanyNotFoundException)
{
    Console.Error.WriteLine($"{command} stopped: the company is not registered; nothing was changed.");
    return Failed;
}
catch (PostgresException ex) when (command is "migrate-companies" or "grant-company-runtime")
{
    // PostgreSQL messages may quote object names; only the SQLSTATE is printed.
    Console.Error.WriteLine($"{command} failed: PostgreSQL error {ex.SqlState}.");
    return Failed;
}
catch (Exception ex)
{
    // Never print connection strings or secrets; scrub the message and omit stack traces.
    // For provisioning failures the company is marked Failed centrally; its database is never dropped.
    Console.Error.WriteLine($"{command} failed: {ex.GetType().Name}: {options.Scrub(ex.Message)}");
    return Failed;
}

// Phase 4.3 backup commands (D-058): stable codes, counts, the company id and operator file paths only; never a physical database name, a password or tool output.
static async Task<int> BackupCommandAsync(string command, string[] args, Guid companyId, bool keep, HasmDatabaseOptions options, IConfiguration configuration)
{
    const int Ok = 0, Failed = 1, BadConfig = 3, InProgress = 4, Warnings = 8;
    var service = new CompanyBackupService(options, AttachmentOptions.FromConfiguration(configuration), EncryptionOptions.FromConfiguration(configuration));
    try
    {
        switch (command)
        {
            case "backup-company":
            {
                var r = await service.BackupCompanyAsync(companyId, args[2]);
                Console.WriteLine($"Backup of company {companyId} written and verified: {r.Manifest.Attachments.Count} attachment file(s), {r.Manifest.KeyIdsRequired.Count} key id(s) needed, company migration level {r.Manifest.CompanyMigrationVersion:0000}.");
                if (r.StagingFoldersSwept > 0) Console.WriteLine($"Stale staging folder(s) of earlier runs removed: {r.StagingFoldersSwept}.");
                Console.WriteLine("Back up the key directory SEPARATELY (decision K); the set is not encrypted or signed yet.");
                if (r.MissingKeyIds.Count == 0) return Ok;
                Console.Error.WriteLine($"WARNING key_ids_missing: the key directory lacks {r.MissingKeyIds.Count} key id(s) the set needs: {string.Join(", ", r.MissingKeyIds)}.");
                return Warnings;
            }
            case "trial-restore-company":
            {
                var r = await service.TrialRestoreAsync(args[1], companyId, args[3], keep);
                Console.WriteLine($"Trial restore of company {companyId}: {r.Verdict switch { TrialRestoreVerdict.Ok => "OK", TrialRestoreVerdict.OkWithWarnings => "OK WITH WARNINGS", _ => "FAILED" }}.");
                Console.WriteLine($"Attachment files restored: {r.AttachmentFiles} (without a row: {r.UnreferencedFiles}); company migration level {(r.MigrationVersion is { } v ? v.ToString("0000") : "unknown")}.");
                foreach (var f in r.Failures) Console.WriteLine($"  [FAILURE] {f.Code} count={f.Count}");
                foreach (var w in r.Warnings) Console.WriteLine($"  [WARNING] {w.Code} count={w.Count}");
                Console.WriteLine(r.KeptFile is { } kept
                    ? $"The trial database was KEPT; it is recorded in {kept} (owner-only). Remove it with: drop-trial-restore {kept}"
                    : "The trial database was dropped. Nothing was registered and no live database or folder was touched.");
                Console.WriteLine($"Measured trial restore time: {r.Elapsed.TotalSeconds:0.0} s (record it in the drill log; see docs/DEVELOPMENT.md).");
                Console.WriteLine("This is a trial restore: the cutover and the audited restore acknowledgement are not available (BLOCKED).");
                return r.Verdict switch { TrialRestoreVerdict.Ok => Ok, TrialRestoreVerdict.OkWithWarnings => Warnings, _ => Failed };
            }
            case "backup-run":
            {
                var (report, runDir) = await new BackupRunService(options, AttachmentOptions.FromConfiguration(configuration), EncryptionOptions.FromConfiguration(configuration)).RunAsync(args[1]);
                Console.WriteLine($"Backup run {report.RunId}: {report.Verdict.ToUpperInvariant()} in {report.DurationMs / 1000.0:0.0} s; written to {runDir} (run.json written last).");
                Console.WriteLine($"System database: {report.System.Outcome}{(report.System.Code is { } sc ? $" ({sc})" : "")}, {report.System.DurationMs / 1000.0:0.0} s; data excluded: {string.Join(", ", report.Excluded)}.");
                foreach (var c in report.Companies) Console.WriteLine($"  Company {c.CompanyId}: {c.Outcome}{(c.Code is { } cc ? $" ({cc})" : "")}, {c.DurationMs / 1000.0:0.0} s, {c.AttachmentFiles} attachment file(s)");
                if (report.Skipped > 0) Console.WriteLine($"  Not Ready (skipped): {report.Skipped}");
                if (report.MissingKeyIds.Count > 0) Console.Error.WriteLine($"WARNING key_ids_missing: the key directory lacks {report.MissingKeyIds.Count} key id(s) the sets need: {string.Join(", ", report.MissingKeyIds)}.");
                Console.WriteLine("The run is NOT encrypted (owner package 4.3-2): keep the backup root on an encrypted volume; back up the key directory separately. The run is not one point in time across databases.");
                return report.Verdict switch { "ok" => Ok, "partial" => Warnings, _ => Failed };
            }
            default: // drop-trial-restore
                await service.DropTrialRestoreAsync(args[1]);
                Console.WriteLine("The kept trial database was dropped and its file removed.");
                return Ok;
        }
    }
    catch (BackupException ex)
    {
        Console.Error.WriteLine($"{command} stopped ({ex.Code}{(ex.Cause is null ? "" : $", cause {ex.Cause}")}).");
        return ex.Code switch { "attachment_storage_not_configured" or "database_not_configured" => BadConfig, "company_busy" or "run_in_progress" => InProgress, _ => Failed };
    }
    catch (PostgresException ex)
    {
        Console.Error.WriteLine($"{command} failed: PostgreSQL error {ex.SqlState}.");
        return Failed;
    }
}

// Business streams live in the company database and are read with that company runtime role (least privilege).
static async Task<string> VerifierConnectionAsync(HasmDatabaseOptions options, AuditStream stream)
{
    if (!stream.IsBusiness) return options.BuildSystemConnectionString(admin: false);
    if (options.CompanyCredentialDirectory is null) throw new DatabaseConfigurationException("Invalid database configuration: Hasm:Database:CompanyCredentialDirectory is required.");
    var company = await new CompanyRegistry(options.BuildSystemConnectionString(admin: false)).GetAsync(stream.CompanyId!.Value) ?? throw new CompanyNotFoundException(stream.CompanyId.Value);
    var role = CompanyRuntimeRole.NameFor(company.PhysicalDatabaseName);
    var credential = await new FileCompanyCredentialStore(options.CompanyCredentialDirectory).GetAsync(company.Id);
    if (credential is null || credential.Username != role) throw new DatabaseConfigurationException("No runtime credential is provisioned for this company; run grant-company-runtime.");
    return options.BuildCompanyRuntimeConnectionString(company.PhysicalDatabaseName, role, credential.Password);
}

static string ReadHidden(string prompt)
{
    Console.Error.Write(prompt);
    var sb = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
        else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    Console.Error.WriteLine();
    return sb.ToString();
}

static (AuditKeyRing Keys, ICheckpointStore Store) BuildAuditEvidence(AuditOptions audit, string? checkpointDirOverride, bool requireActiveKey)
{
    audit.EnsureValid(requireActiveKey, requireCheckpoints: checkpointDirOverride is null);
    var dir = checkpointDirOverride ?? audit.CheckpointDirectory!;
    return (new FileAuditKeyRing(audit), new FileCheckpointStore(dir, enforcePermissions: false));
}

static void PrintReport(AuditVerificationReport r)
{
    // Codes and sequence numbers only: no keys, no credentials, no event payloads.
    Console.WriteLine($"Stream: {r.Stream}");
    Console.WriteLine($"Verdict: {r.Verdict.ToString().ToUpperInvariant()}");
    Console.WriteLine($"Records: {r.Events}" + (r.FirstSeq is { } f ? $" (sequence {f}..{r.LastSeq})" : ""));
    Console.WriteLine($"Verified through sequence: {r.VerifiedThroughSeq}");
    Console.WriteLine($"Checkpoints: {r.Checkpoints} (last valid anchored sequence: {(r.LastCheckpointSeq?.ToString() ?? "none")}; unanchored records: {r.UnanchoredEvents})");
    if (r.LegacyUnprotectedRows > 0) Console.WriteLine($"Legacy rows predating the chain (not authenticated): {r.LegacyUnprotectedRows}");
    foreach (var finding in r.Findings)
        Console.WriteLine($"  [{finding.Severity.ToString().ToUpperInvariant()}] {finding.Code}{(finding.Seq is { } q ? $" seq={q}" : "")}: {finding.Message}");
    if (r.FindingsOmitted > 0) Console.WriteLine($"  ... {r.FindingsOmitted} further finding(s) omitted.");
}
