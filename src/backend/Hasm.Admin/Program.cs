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
//   Hasm.Admin trial-restore-company <set-dir> <company-id> <attachment-target-dir> [--keep] [--recovery-key <file> (--signing-trust <file> | --signing-public-key <pem>) [--passphrase-stdin]]
//              (restores into a NEW unregistered database, runs the pre-cutover checks, drops it unless --keep; an encrypted set needs the recovery key; audited 2010; exit 0 ok, 8 warnings, 1 failed)
//   Hasm.Admin backup-recovery-test <run-dir> --recovery-key <file> (--signing-trust <file> | --signing-public-key <pem>) [--keys-export <dir>] [--passphrase-stdin]
//              (Phase 4.3 slice 5: verifies, decrypts and restores a whole encrypted run into disposable databases; writes recovery-test.json when passed; audited 2011; exit 0 passed, 8 passed with warnings, 1 failed)
//   Hasm.Admin drop-trial-restore <kept-file>  (drops ONLY the trial database a --keep run recorded in that owner-only file)
//   Hasm.Admin backup-run <root>  (Phase 4.3 slice 4: system database + every Ready company into <root>/<run id>/, run.json last; exit 0 ok, 8 partial, 1 failed, 4 another run in progress)
//   Hasm.Admin backup-signing-keygen  (Phase 4.3 slice 5: a new ECDSA P-256 backup signing key in Hasm__Backup__SigningKeyDirectory, owner-only, never overwrites; audited 2013)
//   Hasm.Admin backup-keys <destination-dir>  (Phase 4.3 slice 5: every key directory as ONE encrypted, signed export; signing PUBLIC keys only; audited 2012)
//   Hasm.Admin backup-retention-plan <root> [--json <file>]  (Phase 4.3 slice 6: the D-086 retention preview with every run's keep reasons; reads the databases; deletes nothing; audited 2014)
//   Hasm.Admin backup-retention-apply <root>  (Phase 4.3 slice 6: automatic deletion, INACTIVE (D-137): refuses with retention_deletion_inactive, audited 2017)
//   Hasm.Admin backup-hold-place (--run <run-id> | --company <id> [--from <date>] [--to <date>]) --reason <text>  (a backup legal hold; never released by this tool; audited 2018)
//   Hasm.Admin backup-hold-list
//   Hasm.Admin encryption-key-retirement-report <backup-root> [<backup-root> ...] [--all-backup-copies-listed] [--json <file>]
//              (D-181: which encryption master keys may be retired: none that is active, that a live attachment or import original needs, or that a retained run of ANY given backup root lists;
//               read-only; retires nothing (D-080(6)); exit 0 complete, 8 incomplete (then no key is retirable), 3 configuration. Fail closed: a root that is missing, not a directory, a
//               symbolic link or without a complete, verified run is a blocker. It reads ONLY the given roots: other destinations, copies and standalone backup-company sets are not seen, so
//               without --all-backup-copies-listed (the operator's attestation that the given roots are every place a backup is kept) the report is incomplete)
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
string? recoveryKeyFile = null, signingTrustFile = null, signingPublicKeyFile = null, keysExportDir = null;
string? holdRun = null, holdReason = null, planJson = null;
List<string> retirementRoots = [];
var retirementScopeAttested = false;
Guid? holdCompany = null;
DateOnly? holdFrom = null, holdTo = null;
var passphraseStdin = false;
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
    "trial-restore-company" => args.Length >= 4 && Guid.TryParse(args[2], out companyId) && companyId != Guid.Empty && Path.IsPathFullyQualified(args[1]) && Path.IsPathFullyQualified(args[3])
        && ParseRestoreFlags(4, allowKeep: true, allowKeysExport: false),
    "backup-recovery-test" => args.Length >= 2 && Path.IsPathFullyQualified(args[1]) && ParseRestoreFlags(2, allowKeep: false, allowKeysExport: true) && recoveryKeyFile is not null,
    "drop-trial-restore" => args.Length == 2 && Path.IsPathFullyQualified(args[1]),
    "backup-recovery-keygen" => args.Length >= 2 && args.Length <= 4 && Path.IsPathFullyQualified(args[1]) && args.Skip(2).All(a => a is "--separate-passphrases" or "--passphrase-stdin") && args.Skip(2).Distinct().Count() == args.Length - 2,
    "backup-run" => args.Length == 2 && Path.IsPathFullyQualified(args[1]),
    "backup-signing-keygen" => args.Length == 1,
    "backup-keys" => args.Length == 2 && Path.IsPathFullyQualified(args[1]),
    "backup-retention-plan" => Path.IsPathFullyQualified(args.Length > 1 ? args[1] : "")
        && (args.Length == 2 || (args.Length == 4 && args[2] == "--json" && Path.IsPathFullyQualified(planJson = args[3]))),
    "backup-retention-apply" => args.Length == 2 && Path.IsPathFullyQualified(args[1]),
    "backup-hold-place" => ParseHoldFlags(),
    "backup-hold-list" => args.Length == 1,
    "encryption-key-retirement-report" => ParseRetirementArgs(),
    "backup-status" => Path.IsPathFullyQualified(args.Length > 1 ? args[1] : "") && (args.Length == 2
        || (args.Length == 4 && args[2] == "--max-age-hours" && int.TryParse(args[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxAgeHours) && maxAgeHours is >= 1 and <= 8760)),
    "backup-retention-preview" => Path.IsPathFullyQualified(args.Length > 1 ? args[1] : "") && (args.Length is 4 or 6) && args[2] == "--keep-last"
        && int.TryParse(args[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out keepLast) && keepLast is >= 1 and <= 100000
        && (args.Length == 4 || (args[4] == "--keep-days" && int.TryParse(args[5], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kd) && kd is >= 0 and <= 100000 && (keepDays = kd) is not null)),
    _ => false,
};
if (!validUsage)
{
    Console.Error.WriteLine("Usage: Hasm.Admin init-system-db | register-company | migrate-companies [--company <id>] | grant-company-runtime <company-id> [--rotate-credential] | bootstrap-admin <login> [display name] | provision-company <company-id> | grant-company-admin <login> <company-id> | grant-fiscal-approver <login> <company-id> | grant-journal-approver <login> <company-id> | grant-import-approver <login> <company-id> | revoke-import-approver <login> <company-id> | revoke-journal-approver <login> <company-id> | revoke-fiscal-approver <login> <company-id> | audit-keygen <key-id> | encryption-keygen <key-id> | audit-verify <system|company-id|business:company-id> [--checkpoint-dir <dir>] | audit-anchor <system|company-id|business:company-id> [--establish-baseline] | backup-company <company-id> <destination-dir> | trial-restore-company <set-dir> <company-id> <attachment-target-dir> [--keep] [--recovery-key <file> (--signing-trust <file> | --signing-public-key <pem>) [--passphrase-stdin]] | backup-recovery-test <run-dir> --recovery-key <file> (--signing-trust <file> | --signing-public-key <pem>) [--keys-export <dir>] [--passphrase-stdin] | drop-trial-restore <kept-file> | backup-recovery-keygen <out-dir> [--separate-passphrases] [--passphrase-stdin] | backup-run <root> | backup-signing-keygen | backup-keys <destination-dir> | backup-retention-plan <root> [--json <file>] | backup-retention-apply <root> | backup-hold-place (--run <run-id> | --company <id> [--from <yyyy-mm-dd>] [--to <yyyy-mm-dd>]) --reason <text> | backup-hold-list | encryption-key-retirement-report <backup-root> [<backup-root> ...] [--all-backup-copies-listed] [--json <file>] | backup-status <root> [--max-age-hours N] | backup-retention-preview <root> --keep-last N [--keep-days D]  (all paths absolute)");
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
    // the server's own signing PUBLIC keys are the trust anchor for monitoring (an attacker controlling the server could forge a status anyway); restores use the operator's recovery-kit trust list
    var statusOptions = BackupOptions.FromConfiguration(configuration);
    var runs = BackupRunStatus.List(args[1], BackupMaterialLoader.ServerTrust(statusOptions));
    if (statusOptions.UnencryptedDevTestOnly is not null) Console.WriteLine(BackupGuard.DegradedStatus);
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
    if (status.UnreadableRuns > 0) Console.WriteLine($"  [WARNING] unreadable_reports count={status.UnreadableRuns} (a damaged or altered run.json; investigate before relying on the newest run)");
    // slice 6 (D-136): a deletion that started and did not finish is never silent
    var deleting = RetentionInventory.DeletingFolderCount(args[1]);
    if (deleting > 0)
    {
        Console.WriteLine($"  [WARNING] retention_deletion_incomplete count={deleting} (the next backup-retention-apply completes it)");
        if (status.ExitCode == 0) { Console.WriteLine("Backup status: retention_deletion_incomplete (exit 8)."); return 8; }
    }
    return status.ExitCode;
}

if (command == "backup-recovery-keygen")
{
    // Phase 4.3 slice 5 (D-085, D-089): run on an OFFLINE workstation. Refused where database settings exist, so it is not run on a configured server by accident; the private key is written only
    // passphrase-encrypted, in two copies. Passphrases come from a prompt (twice) or, for scripted use, from standard input (--passphrase-stdin); never from an argument or a variable.
    if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k => k.StartsWith("Hasm__Database__", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine("backup-recovery-keygen refused (recovery_keygen_on_server): Hasm__Database__* settings are present. Generate the recovery key pair on an offline workstation; only the PUBLIC key goes to the server.");
        return BadConfig;
    }
    var separate = args.Contains("--separate-passphrases");
    var fromStdin = args.Contains("--passphrase-stdin");
    if (!fromStdin && Console.IsInputRedirected) { Console.Error.WriteLine("Run interactively to be prompted, or pass --passphrase-stdin."); return Usage; }
    string? Ask(string label)
    {
        var first = fromStdin ? Console.In.ReadLine() : ReadHidden($"{label}: ");
        var again = fromStdin ? Console.In.ReadLine() : ReadHidden($"{label} (again): ");
        if (first is null || first != again) { Console.Error.WriteLine("The passphrases did not match; nothing was written."); return null; }
        if (first.Length < RecoveryKeys.MinimumPassphraseLength) { Console.Error.WriteLine($"A recovery passphrase needs at least {RecoveryKeys.MinimumPassphraseLength} characters (or six diceware words); nothing was written."); return null; }
        return first;
    }
    var p1 = Ask(separate ? "Passphrase for copy 1" : "Recovery key passphrase");
    if (p1 is null) return Failed;
    var p2 = separate ? Ask("Passphrase for copy 2 (a different custodian)") : p1;
    if (p2 is null) return Failed;
    if (separate && p1 == p2) { Console.Error.WriteLine("Separate passphrases must differ; nothing was written."); return Failed; }
    try
    {
        var pair = RecoveryKeys.Generate(separate ? [p1, p2] : [p1]);
        var files = RecoveryKit.Write(args[1], pair);
        Console.WriteLine($"Recovery key pair {pair.KeyId} written to {args[1]}:");
        foreach (var f in files) Console.WriteLine($"  {f}");
        Console.WriteLine($"Put ONLY {pair.KeyId}.pub.pem on the server (Hasm__Backup__RecoveryPublicKeyFile). Move copy-1 and copy-2 to two separate offline media held apart (separate custodians when the passphrases differ),");
        Console.WriteLine($"keep {pair.KeyId}.sha256 with each copy, then delete the private-key files from this workstation. Run backup-recovery-test before relying on the key (D-085).");
        return Ok;
    }
    catch (BackupException ex) { Console.Error.WriteLine($"backup-recovery-keygen stopped ({ex.Code}); nothing usable was written."); return Failed; }
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
        case "backup-recovery-test":
            return await BackupCommandAsync(command!, args, companyId, keepTrial, options, configuration, new RestoreArgs(recoveryKeyFile, signingTrustFile, signingPublicKeyFile, keysExportDir, passphraseStdin));
        case "backup-signing-keygen":
        case "backup-keys":
            return await BackupKeyCommandAsync(command!, args, options, configuration, auditOptions);
        case "encryption-key-retirement-report":
            return await KeyRetirementCommandAsync(retirementRoots, retirementScopeAttested, options, configuration, planJson);
        case "backup-retention-plan":
        case "backup-retention-apply":
        case "backup-hold-place":
        case "backup-hold-list":
            return await RetentionCommandAsync(command!, args, options, configuration, auditOptions, planJson, new BackupHoldRequest(holdRun, holdCompany, holdFrom, holdTo, holdReason));
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
static async Task<int> BackupCommandAsync(string command, string[] args, Guid companyId, bool keep, HasmDatabaseOptions options, IConfiguration configuration, RestoreArgs restore)
{
    const int Ok = 0, Failed = 1, BadConfig = 3, InProgress = 4, Warnings = 8;
    var backupOptions = BackupOptions.FromConfiguration(configuration);
    var trail = AuditTrail.FromOptions(AuditOptions.FromConfiguration(configuration), TimeProvider.System);
    var systemConnection = options.BuildSystemConnectionString(admin: true);
    // review M4 of #116: the audit and backup signing key directories are protected like the attachment and encryption ones
    IReadOnlyList<string?> protectedDirs = [AuditOptions.FromConfiguration(configuration).KeyDirectory, backupOptions.SigningKeyDirectory];
    var service = new CompanyBackupService(options, AttachmentOptions.FromConfiguration(configuration), EncryptionOptions.FromConfiguration(configuration), protectedDirectories: protectedDirs);
    BackupMaterial? material = null;
    try
    {
        if (command is "backup-company" or "backup-run")
        {
            // slice 5 (D-095): encrypted and signed by default; an unencrypted set only with the exact DEV/TEST acknowledgement, never silently
            if (BackupGuard.Resolve(backupOptions) == BackupProtection.Encrypted) material = BackupMaterialLoader.Load(backupOptions);
            else Console.Error.WriteLine($"WARNING {BackupGuard.DegradedStatus}: the set is NOT encrypted or signed (Hasm__Backup__UnencryptedDevTestOnly); never use this in production (D-088).");
        }
        switch (command)
        {
            case "backup-company":
            {
                IReadOnlyList<string> missingKeys;
                if (material is not null)
                {
                    var e = await service.BackupCompanyEncryptedAsync(companyId, args[2], material);
                    Console.WriteLine($"Encrypted, signed backup of company {companyId} written and verified: {e.Manifest.Files.Count - 1} attachment file(s), {e.Manifest.Company!.KeyIdsRequired.Count} key id(s) needed, company migration level {e.Manifest.Company.CompanyMigrationVersion:0000}; recovery key {e.Manifest.RecoveryKeyId}, signing key {e.Manifest.SigningKeyId}.");
                    missingKeys = e.MissingKeyIds;
                }
                else
                {
                    var r = await service.BackupCompanyAsync(companyId, args[2]);
                    Console.WriteLine($"UNENCRYPTED (DEV/TEST only) backup of company {companyId} written and verified: {r.Manifest.Attachments.Count} attachment file(s), {r.Manifest.KeyIdsRequired.Count} key id(s) needed, company migration level {r.Manifest.CompanyMigrationVersion:0000}.");
                    if (r.StagingFoldersSwept > 0) Console.WriteLine($"Stale staging folder(s) of earlier runs removed: {r.StagingFoldersSwept}.");
                    missingKeys = r.MissingKeyIds;
                }
                Console.WriteLine("Back up the key directories with backup-keys (D-087); never alongside the sets.");
                if (missingKeys.Count == 0) return Ok;
                Console.Error.WriteLine($"WARNING key_ids_missing: the key directory lacks {missingKeys.Count} key id(s) the set needs: {string.Join(", ", missingKeys)}.");
                return Warnings;
            }
            case "trial-restore-company":
            {
                // slice 5: an encrypted set needs the operator's recovery key and trust list; an unencrypted set is accepted only under THIS reader's DEV/TEST acknowledgement (D-095, review M1)
                RestoreKeys? keys = null;
                if (CompanyBackupService.IsEncryptedSet(args[1])) keys = restore.RecoveryKeyFile is null ? throw new BackupException("recovery_key_required") : LoadRestoreKeys(restore);
                else if (!BackupGuard.UnencryptedReadAllowed(backupOptions)) throw new BackupException("unencrypted_set_refused");
                else Console.Error.WriteLine($"WARNING {BackupGuard.DegradedStatus}: restoring an UNENCRYPTED, unsigned set (DEV/TEST only).");
                TrialRestoreResult r;
                try { r = await service.TrialRestoreAsync(args[1], companyId, args[3], keep, keys, allowUnencrypted: keys is null && BackupGuard.UnencryptedReadAllowed(backupOptions)); }
                finally { keys?.Recovery.Dispose(); }
                Console.WriteLine($"Trial restore of company {companyId}: {r.Verdict switch { TrialRestoreVerdict.Ok => "OK", TrialRestoreVerdict.OkWithWarnings => "OK WITH WARNINGS", _ => "FAILED" }}.");
                Console.WriteLine($"Attachment files restored: {r.AttachmentFiles} (without a row: {r.UnreferencedFiles}); company migration level {(r.MigrationVersion is { } v ? v.ToString("0000") : "unknown")}.");
                foreach (var f in r.Failures) Console.WriteLine($"  [FAILURE] {f.Code} count={f.Count}");
                foreach (var w in r.Warnings) Console.WriteLine($"  [WARNING] {w.Code} count={w.Count}");
                Console.WriteLine(r.KeptFile is { } kept
                    ? $"The trial database was KEPT; it is recorded in {kept} (owner-only). Remove it with: drop-trial-restore {kept}"
                    : "The trial database was dropped. Nothing was registered and no live database or folder was touched.");
                Console.WriteLine($"Measured trial restore time: {r.Elapsed.TotalSeconds:0.0} s (record it in the drill log; see docs/DEVELOPMENT.md).");
                Console.WriteLine("This is a trial restore: the cutover and the audited restore acknowledgement are not available (BLOCKED).");
                // D-118: a restore operation; the system-stream event names the operator, key ids, the company and a TAG of the trial database (never its name). Fail closed when it cannot be written.
                if (!await TryAuditAsync(systemConnection, trail, BackupAuditEvents.TrialRestore,
                    $"trial restore operator={BackupAudit.Operator()} company={companyId:D} outcome=completed verdict={r.Verdict.ToString().ToLowerInvariant()} encrypted={(r.Encrypted ? "yes" : "no")} recovery={r.RecoveryKeyId ?? "none"} signing={r.SigningKeyId ?? "none"} db={r.DatabaseTag ?? "none"} kept={(r.KeptFile is null ? "no" : "yes")}",
                    companyId)) return Failed;
                return r.Verdict switch { TrialRestoreVerdict.Ok => Ok, TrialRestoreVerdict.OkWithWarnings => Warnings, _ => Failed };
            }
            case "backup-recovery-test":
            {
                var keys = LoadRestoreKeys(restore);
                var work = Path.Combine(Path.GetTempPath(), "hasm-recovery-test-" + Guid.NewGuid().ToString("N"));
                RecoveryTestResult t;
                try { t = await new RecoveryTestService(options, AttachmentOptions.FromConfiguration(configuration), EncryptionOptions.FromConfiguration(configuration)).RunAsync(args[1], keys, restore.KeysExport, work); }
                finally { keys.Recovery.Dispose(); }
                Console.WriteLine($"Recovery test of run {t.RunId}: {(t.Passed ? "PASSED" : "FAILED")} in {t.Elapsed.TotalSeconds:0.0} s; recovery key {t.RecoveryKeyId}, signing key {t.SigningKeyId}; {t.Companies.Count} company set(s) restored into disposable databases (dropped); key export {(t.KeyExportChecked ? "checked" : "NOT checked")}.");
                foreach (var f in t.Failures) Console.WriteLine($"  [FAILURE] {f.Code} count={f.Count}");
                foreach (var w in t.Warnings) Console.WriteLine($"  [WARNING] {w.Code} count={w.Count}");
                if (!await TryAuditAsync(systemConnection, trail, BackupAuditEvents.RecoveryTest,
                    $"recovery test operator={BackupAudit.Operator()} run={t.RunId} outcome=completed verdict={(t.Passed ? "passed" : "failed")} recovery={t.RecoveryKeyId} signing={t.SigningKeyId} key_export={(t.KeyExportChecked ? "yes" : "no")} db={string.Join(',', t.DatabaseTags)} companies={string.Join(',', t.Companies.Select(c => c.ToString("N")))}"))
                    return Failed;
                if (!t.Passed) return Failed;
                RecoveryTestService.WriteProof(args[1], t, DateTimeOffset.UtcNow);
                Console.WriteLine($"recovery-test.json written to the run folder: backup-status now counts recovery key {t.RecoveryKeyId} as tested (D-085). Remove the private-key file from this machine now (runbook).");
                return t.Warnings.Count == 0 ? Ok : Warnings;
            }
            case "backup-run":
            {
                var (report, runDir) = await new BackupRunService(options, AttachmentOptions.FromConfiguration(configuration), EncryptionOptions.FromConfiguration(configuration), protectedDirectories: protectedDirs).RunAsync(args[1], material);
                Console.WriteLine($"Backup run {report.RunId}: {report.Verdict.ToUpperInvariant()} in {report.DurationMs / 1000.0:0.0} s; written to {runDir} (run.json written last).");
                Console.WriteLine($"System database: {report.System.Outcome}{(report.System.Code is { } sc ? $" ({sc})" : "")}, {report.System.DurationMs / 1000.0:0.0} s; data excluded: {string.Join(", ", report.Excluded)}.");
                foreach (var c in report.Companies) Console.WriteLine($"  Company {c.CompanyId}: {c.Outcome}{(c.Code is { } cc ? $" ({cc})" : "")}, {c.DurationMs / 1000.0:0.0} s, {c.AttachmentFiles} attachment file(s)");
                if (report.Skipped > 0) Console.WriteLine($"  Not Ready (skipped): {report.Skipped}");
                foreach (var w in report.Warnings ?? []) Console.Error.WriteLine($"WARNING {w}: the run lock connection was lost during the run (check idle_session_timeout of the admin role and the network); the sets were written and verified.");
                if (report.MissingKeyIds.Count > 0) Console.Error.WriteLine($"WARNING key_ids_missing: the key directory lacks {report.MissingKeyIds.Count} key id(s) the sets need: {string.Join(", ", report.MissingKeyIds)}.");
                Console.WriteLine(material is not null
                    ? "The run is encrypted to the offline recovery key and signed (run.status.json is the only readable file). Back up the key directories with backup-keys. The run is not one point in time across databases."
                    : $"{BackupGuard.DegradedStatus}: the run is NOT encrypted; DEV/TEST only. The run is not one point in time across databases.");
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
        // review L3 of #128 (D-118): a REFUSED trial restore or recovery test is a restore attempt and is audited too (outcome refused, the code; never a name)
        if (command is "trial-restore-company")
            await TryAuditAsync(systemConnection, trail, BackupAuditEvents.TrialRestore,
                $"trial restore operator={BackupAudit.Operator()} company={companyId:D} outcome=refused code={(BackupRunFiles.IsCode(ex.Code) ? ex.Code : "refused")}", companyId);
        else if (command is "backup-recovery-test")
            await TryAuditAsync(systemConnection, trail, BackupAuditEvents.RecoveryTest,
                $"recovery test operator={BackupAudit.Operator()} run={(BackupRunFiles.IsRunId(Path.GetFileName(Path.TrimEndingDirectorySeparator(args[1]))) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(args[1])) : "unknown")} outcome=refused code={(BackupRunFiles.IsCode(ex.Code) ? ex.Code : "refused")}");
        return ex.Code switch
        {
            "attachment_storage_not_configured" or "database_not_configured" => BadConfig,
            // slice 5 (D-095): encryption is the default; a missing or contradictory configuration is a configuration error, never a silent fallback
            "backup_encryption_not_configured" or "unencrypted_ack_invalid" or "unencrypted_ack_with_recovery_key" or "recovery_key_invalid" or "signing_key_unavailable" => BadConfig,
            "company_busy" or "run_in_progress" => InProgress,
            _ => Failed,
        };
    }
    catch (PostgresException ex)
    {
        Console.Error.WriteLine($"{command} failed: PostgreSQL error {ex.SqlState}.");
        return Failed;
    }
}

// Phase 4.3 slice 5 key commands (D-087, D-092, D-094, D-118). Interim mode: operating-system access, no named administrator with MFA until Phase 4.1 (D-099; a dated limitation). Both write a
// system-stream audit event; when it cannot be written the command fails closed and removes what it created.
static async Task<int> BackupKeyCommandAsync(string command, string[] args, HasmDatabaseOptions options, IConfiguration configuration, AuditOptions auditOptions)
{
    const int Ok = 0, Failed = 1, BadConfig = 3;
    var backupOptions = BackupOptions.FromConfiguration(configuration);
    var encryption = EncryptionOptions.FromConfiguration(configuration);
    var attachments = AttachmentOptions.FromConfiguration(configuration);
    var trail = AuditTrail.FromOptions(auditOptions, TimeProvider.System);
    var system = options.BuildSystemConnectionString(admin: true);
    try
    {
        if (command == "backup-signing-keygen")
        {
            if (backupOptions.SigningKeyDirectory is not { } dir || !Path.IsPathFullyQualified(dir)) { Console.Error.WriteLine("Hasm__Backup__SigningKeyDirectory (absolute) is required."); return BadConfig; }
            KeyExport.CheckDestination(dir, [attachments.StorageDirectory, encryption.KeyDirectory, auditOptions.KeyDirectory]);
            var (keyId, publicPem) = BackupSigningKeys.Generate(dir);
            try { await BackupAudit.WriteAsync(system, trail, BackupAuditEvents.SigningKeyGenerated, $"backup signing key generated operator={BackupAudit.Operator()} signing={keyId}"); }
            catch
            {
                foreach (var f in new[] { keyId + BackupSigningKeys.PrivateSuffix, keyId + BackupSigningKeys.PublicSuffix }) try { File.Delete(Path.Combine(dir, f)); } catch (IOException) { }
                throw;
            }
            Console.WriteLine($"Backup signing key {keyId} created (owner-only; the private key never leaves this directory and is never exported).");
            Console.WriteLine($"Public key SHA-256: {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(publicPem)))}. Record it in the recovery kit.");
            Console.WriteLine($"Set Hasm__Backup__SigningKeyId={keyId} to use it, then run backup-keys so the recovery kit holds the new public key (D-093).");
            return Ok;
        }
        // backup-keys: always needs the recovery key and the signing key, even when the DEV/TEST acknowledgement is set (D-095)
        if (BackupGuard.Resolve(backupOptions) != BackupProtection.Encrypted) throw new BackupException("backup_encryption_not_configured");
        if (encryption.KeyDirectory is null) { Console.Error.WriteLine("Hasm__Encryption__KeyDirectory is required."); return BadConfig; }
        var destination = KeyExport.CheckDestination(args[1], [attachments.StorageDirectory, encryption.KeyDirectory, auditOptions.KeyDirectory, backupOptions.SigningKeyDirectory]);
        var material = BackupMaterialLoader.Load(backupOptions);
        KeyExportManifest m;
        try { m = await KeyExport.WriteAsync(destination, encryption.KeyDirectory, auditOptions.KeyDirectory, backupOptions.SigningKeyDirectory!, material, DateTimeOffset.UtcNow); }
        finally { material.Recovery.Dispose(); material.Signer.Dispose(); }
        try
        {
            await BackupAudit.WriteAsync(system, trail, BackupAuditEvents.KeysExported, $"backup keys exported operator={BackupAudit.Operator()} recovery={m.RecoveryKeyId} signing={m.SigningKeyId} "
                + $"encryption={string.Join(',', m.EncryptionKeyIds)} audit={string.Join(',', m.AuditKeyIds)} signing_public={string.Join(',', m.SigningPublicKeyIds)}");
        }
        catch { try { Directory.Delete(destination, true); } catch (IOException) { } throw; }
        Console.WriteLine($"Key export written and verified to {args[1]}: {m.EncryptionKeyIds.Count} encryption key(s), {m.AuditKeyIds.Count} audit key(s), {m.SigningPublicKeyIds.Count} signing PUBLIC key(s); encrypted to recovery key {m.RecoveryKeyId}, signed by {m.SigningKeyId}.");
        Console.WriteLine($"Move it to the recovery kit, away from the backup sets; keep {KeyExport.TrustListName} with every kit copy and mark retired or compromised signing keys there (D-093). Signing private keys are never exported.");
        return Ok;
    }
    catch (BackupException ex)
    {
        Console.Error.WriteLine($"{command} stopped ({ex.Code}); nothing usable was written.");
        return ex.Code is "backup_encryption_not_configured" or "recovery_key_invalid" or "signing_key_unavailable" ? BadConfig : Failed;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"{command} stopped: the key files could not be written (existing keys are never overwritten); nothing usable was written.");
        return Failed;
    }
}

// encryption-key-retirement-report: one or more absolute backup roots, then the optional attestation --all-backup-copies-listed, then an optional --json <file>.
bool ParseRetirementArgs()
{
    var end = args.Length;
    if (end >= 4 && args[end - 2] == "--json" && Path.IsPathFullyQualified(args[end - 1])) { planJson = args[end - 1]; end -= 2; }
    if (end >= 3 && args[end - 1] == "--all-backup-copies-listed") { retirementScopeAttested = true; end--; }
    for (var i = 1; i < end; i++)
        if (!Path.IsPathFullyQualified(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal)) return false;
    retirementRoots = [.. args[1..end].Distinct(StringComparer.Ordinal)];
    return retirementRoots.Count >= 1;
}

// The options of backup-hold-place (slice 6): exactly one scope, an optional date range for a company, and a reason.
bool ParseHoldFlags()
{
    for (var i = 1; i < args.Length; i++)
    {
        var value = i + 1 < args.Length ? args[i + 1] : null;
        switch (args[i])
        {
            case "--run" when holdRun is null && value is not null && BackupRunFiles.IsRunId(value): holdRun = value; i++; break;
            case "--company" when holdCompany is null && Guid.TryParse(value, out var g) && g != Guid.Empty: holdCompany = g; i++; break;
            case "--from" when holdFrom is null && DateOnly.TryParseExact(value, "yyyy-MM-dd", out var f): holdFrom = f; i++; break;
            case "--to" when holdTo is null && DateOnly.TryParseExact(value, "yyyy-MM-dd", out var t): holdTo = t; i++; break;
            case "--reason" when holdReason is null && !string.IsNullOrWhiteSpace(value): holdReason = value; i++; break;
            default: return false;
        }
    }
    return (holdRun is null) != (holdCompany is null) && holdReason is not null && (holdCompany is not null || (holdFrom is null && holdTo is null));
}

// The encryption-key retirement report (D-179..D-181). Read-only; no audit event (it changes nothing; retiring a key is a separate, independently approved operation, D-080(6)).
static async Task<int> KeyRetirementCommandAsync(IReadOnlyList<string> roots, bool scopeAttested, HasmDatabaseOptions options, IConfiguration configuration, string? json)
{
    const int Ok = 0, Failed = 1, BadConfig = 3, Incomplete = 8;
    var encryption = EncryptionOptions.FromConfiguration(configuration);
    if (encryption.KeyDirectory is null) { Console.Error.WriteLine($"{EncryptionOptions.SectionName}:KeyDirectory is required."); return BadConfig; }
    var service = new EncryptionKeyRetirementService(options, AttachmentOptions.FromConfiguration(configuration), Hasm.Infrastructure.Imports.ImportOptions.FromConfiguration(configuration), encryption);
    KeyRetirementReport report;
    try { report = await service.ReportAsync(FileKekRing.ListKeyIds(encryption.KeyDirectory), encryption.ActiveKeyId, roots, BackupMaterialLoader.ServerTrust(BackupOptions.FromConfiguration(configuration)), scopeAttested); }
    catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or UnauthorizedAccessException) { Console.Error.WriteLine("encryption-key-retirement-report stopped: the system database or a key directory could not be read."); return Failed; }
    foreach (var k in report.Keys)
        Console.WriteLine(k.Retirable
            ? $"  {k.KeyId} RETIRABLE WITHIN THE SCANNED SCOPE (retiring it needs independent approval, D-080(6); keep it with every older backup kit)"
            : $"  {k.KeyId} keep: {string.Join(", ", k.Reasons)}");
    foreach (var b in report.Blockers) Console.WriteLine($"  [BLOCKER] {b}");
    // the scope, printed with EVERY report (review H1 of PR 182): what was read and what the tool cannot see
    Console.WriteLine($"Scope: the live sensitive attachments and import originals of every registered company, and EVERY run present (also one that retention would delete) in ONLY these {roots.Count} backup root(s):");
    foreach (var root in roots) Console.WriteLine($"  {root}");
    Console.WriteLine("NOT seen: any other backup destination or copy (another disk, an off-site or offline copy), standalone backup-company sets (they can hold attachment envelopes under a key), "
        + "uploads in flight and orphan files.");
    Console.WriteLine(scopeAttested
        ? "The operator attested (--all-backup-copies-listed) that the roots above are every place a backup of this installation is kept. If that is not true, do not retire any key."
        : "No attestation (--all-backup-copies-listed) was given that the roots above are every place a backup is kept: the tool cannot know, so no key is reported retirable.");
    Console.WriteLine($"Generated {report.GeneratedUtc:yyyy-MM-ddTHH:mm:ssZ} (UTC). The report describes that moment only.");
    Console.WriteLine(report.Complete
        ? $"Encryption-key retirement report (D-181): {report.Keys.Count} key(s), {report.Keys.Count(k => k.Retirable)} retirable within the scanned scope."
        : $"Encryption-key retirement report (D-181) INCOMPLETE ({report.Blockers.Count} blocker(s)): no key may be retired.");
    if (json is not null)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        AuditFilePermissions.CreateOwnerOnlyFile(json, bytes);
        Console.WriteLine($"Report written to {json}.");
    }
    return report.Complete ? Ok : Incomplete;
}

// Phase 4.3 slice 6 retention commands (D-130..D-139). Interim mode: operating-system access (dated limitation); every command writes its audit event and fails closed when it cannot.
static async Task<int> RetentionCommandAsync(string command, string[] args, HasmDatabaseOptions options, IConfiguration configuration, AuditOptions auditOptions, string? planJson, BackupHoldRequest hold)
{
    const int Ok = 0, Failed = 1, BadConfig = 3, InProgress = 4;
    var backupOptions = BackupOptions.FromConfiguration(configuration);
    var retention = RetentionOptions.FromConfiguration(configuration);
    var trail = AuditTrail.FromOptions(auditOptions, TimeProvider.System);
    var systemConnection = options.BuildSystemConnectionString(admin: true);
    var service = new RetentionService(options, retention);
    try
    {
        switch (command)
        {
            case "backup-hold-place":
            {
                var id = await new BackupHoldStore(options, trail).PlaceAsync(hold.Run, hold.Company, hold.From, hold.To, hold.Reason!, BackupAudit.Operator());
                Console.WriteLine($"Backup legal hold {id:D} placed ({(hold.Run is not null ? $"run {hold.Run}" : $"company {hold.Company:D}, runs started {hold.From?.ToString("yyyy-MM-dd") ?? "any time"} to {hold.To?.ToString("yyyy-MM-dd") ?? "any time"}")}). It takes effect at once.");
                Console.WriteLine("It cannot be released by this tool until the Phase 4.1 named login and independent approval exist (D-134).");
                return Ok;
            }
            case "backup-hold-list":
            {
                await using var conn = new Npgsql.NpgsqlConnection(systemConnection);
                await conn.OpenAsync();
                var holds = await BackupHoldStore.ListAsync(conn, CancellationToken.None);
                foreach (var h in holds)
                    Console.WriteLine($"  {h.Id:D} {(h.Released ? "released" : "ACTIVE")} {(h.Scope == 1 ? $"run {h.RunId}" : $"company {h.CompanyId:D} from {h.From?.ToString("yyyy-MM-dd") ?? "any"} to {h.To?.ToString("yyyy-MM-dd") ?? "any"}")}");
                Console.WriteLine($"{holds.Count(h => !h.Released)} active backup legal hold(s).");
                return Ok;
            }
            case "backup-retention-plan":
            {
                var (plan, inputs) = await service.PlanAsync(args[1], BackupMaterialLoader.ServerTrust(backupOptions));
                foreach (var d in plan.Runs)
                    Console.WriteLine(d.Eligible ? $"  {d.RunId} ELIGIBLE (automatic deletion is INACTIVE: nothing is deleted)" : $"  {d.RunId} keep: {string.Join(", ", d.Reasons)}");
                var byReason = plan.Runs.SelectMany(d => d.Reasons).GroupBy(r => r).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}").ToList();
                Console.WriteLine($"Retention plan (D-086): {plan.Runs.Count} run(s), {plan.Eligible.Count()} eligible, plan {plan.PlanHash}; keep reasons: {string.Join(", ", byReason)}.");
                Console.WriteLine($"Key inventory of the retained runs: {(plan.KeyInventoryComplete ? "complete" : "INCOMPLETE (backup_inventory_incomplete): no key may be retired")}; recovery key(s) still needed: {string.Join(", ", plan.RecoveryKeysNeeded)}.");
                if (inputs.DeletingFolders.Count > 0) Console.WriteLine($"  [WARNING] retention_deletion_incomplete count={inputs.DeletingFolders.Count}");
                if (!await TryAuditAsync(systemConnection, trail, BackupAuditEvents.RetentionEvaluated,
                    $"backup retention evaluated operator={BackupAudit.Operator()} runs={plan.Runs.Count} eligible={plan.Eligible.Count()} plan={plan.PlanHash} inventory={(plan.KeyInventoryComplete ? "complete" : "incomplete")} reasons={string.Join(',', byReason)}"))
                    return Failed;
                if (planJson is not null)
                {
                    var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                    AuditFilePermissions.CreateOwnerOnlyFile(planJson, json);
                    Console.WriteLine($"Plan written to {planJson}.");
                }
                return Ok;
            }
            default: // backup-retention-apply
            {
                var r = await service.ApplyAsync(args[1], BackupMaterialLoader.ServerTrust(backupOptions), backupOptions, trail, BackupAudit.Operator());
                foreach (var id in r.Deleted) Console.WriteLine($"  deleted {id}");
                foreach (var (id, code) in r.Skipped) Console.WriteLine($"  [SKIPPED] {id} {code}");
                return r.Skipped.Count > 0 ? 8 : Ok;
            }
        }
    }
    catch (BackupException ex)
    {
        Console.Error.WriteLine($"{command} stopped ({ex.Code}); nothing was deleted.");
        return ex.Code switch
        {
            "retention_deletion_inactive" or "retention_below_minimum" or "retention_config_invalid" or "retention_time_zone_invalid" or "backup_encryption_not_configured"
                or "unencrypted_ack_invalid" or "unencrypted_ack_with_recovery_key" or "restore_safeguards_missing" => BadConfig,
            "run_in_progress" or "restore_in_progress" => InProgress,
            _ => Failed,
        };
    }
    catch (Exception ex) when (ex is AuditConfigurationException or AuditUnavailableException)
    {
        Console.Error.WriteLine($"{command} stopped: the system audit event could not be written; nothing was changed (D-139).");
        return Failed;
    }
    catch (IOException)
    {
        Console.Error.WriteLine($"{command} stopped: a file could not be written (an existing file is never overwritten).");
        return Failed;
    }
}

// The restore options of trial-restore-company and backup-recovery-test (slice 5).
bool ParseRestoreFlags(int from, bool allowKeep, bool allowKeysExport)
{
    for (var i = from; i < args.Length; i++)
    {
        var hasValue = i + 1 < args.Length && Path.IsPathFullyQualified(args[i + 1]);
        switch (args[i])
        {
            case "--keep" when allowKeep && !keepTrial: keepTrial = true; break;
            case "--passphrase-stdin" when !passphraseStdin: passphraseStdin = true; break;
            case "--recovery-key" when recoveryKeyFile is null && hasValue: recoveryKeyFile = args[++i]; break;
            case "--signing-trust" when signingTrustFile is null && signingPublicKeyFile is null && hasValue: signingTrustFile = args[++i]; break;
            case "--signing-public-key" when signingTrustFile is null && signingPublicKeyFile is null && hasValue: signingPublicKeyFile = args[++i]; break;
            case "--keys-export" when allowKeysExport && keysExportDir is null && hasValue: keysExportDir = args[++i]; break;
            default: return false;
        }
    }
    // the key options come together: a recovery key without a trust anchor (or the reverse) is a usage error
    return (recoveryKeyFile is null) == (signingTrustFile is null && signingPublicKeyFile is null) && (!passphraseStdin || recoveryKeyFile is not null);
}

// The recovery PRIVATE key is read from the operator's file and decrypted with a passphrase from a prompt or standard input (review M3 of #116: never an argument or an environment variable); it lives in
// memory only and is disposed by the caller.
static RestoreKeys LoadRestoreKeys(RestoreArgs a)
{
    var trust = a.TrustFile is not null ? BackupMaterialLoader.LoadTrustList(a.TrustFile) : BackupMaterialLoader.TrustFromPublicKey(a.PublicKeyFile!);
    if (!a.PassphraseStdin && Console.IsInputRedirected) throw new BackupException("passphrase_missing");
    var passphrase = a.PassphraseStdin ? Console.In.ReadLine() : ReadHidden("Recovery key passphrase: ");
    if (string.IsNullOrEmpty(passphrase)) throw new BackupException("passphrase_missing");
    string pem;
    try { pem = File.ReadAllText(a.RecoveryKeyFile!); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BackupException("recovery_key_unreadable"); }
    return new RestoreKeys(RecoveryPrivateKey.FromEncryptedPem(pem, passphrase), trust);
}

// Writes one backup audit event; false (with a message) when it cannot be written: the command then fails closed (D-118).
static async Task<bool> TryAuditAsync(string systemConnection, AuditTrail trail, short eventType, string detail, Guid? subject = null)
{
    try { await BackupAudit.WriteAsync(systemConnection, trail, eventType, detail, subject); return true; }
    catch (Exception ex) when (ex is AuditConfigurationException or AuditUnavailableException or NpgsqlException or InvalidOperationException)
    {
        Console.Error.WriteLine($"The system audit event {eventType} could not be written ({(ex is AuditUnavailableException a ? a.Code : ex is AuditConfigurationException ? "audit_not_configured" : "audit_write_failed")}); the operation is reported as FAILED (D-118).");
        return false;
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

/// <summary>The restore options parsed from the command line (slice 5).</summary>
sealed record RestoreArgs(string? RecoveryKeyFile, string? TrustFile, string? PublicKeyFile, string? KeysExport, bool PassphraseStdin);

/// <summary>The options of backup-hold-place (slice 6).</summary>
sealed record BackupHoldRequest(string? Run, Guid? Company, DateOnly? From, DateOnly? To, string? Reason);
