using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;

// Explicit administrative tool. Never started by the API; uses the privileged database account.
// Usage:
//   Hasm.Admin init-system-db
//   Hasm.Admin register-company              (registers a Pending company; prints its id only)
//   Hasm.Admin bootstrap-admin <login> [display name]  (creates the FIRST identity administrator; refuses if one exists)
//   Hasm.Admin provision-company <company-id> (creates + migrates its separate database; safe to repeat/resume)
//   Hasm.Admin grant-company-admin <login> <company-id>  (offline initial/recovery: membership + Company Administrator role; audited)
//   Hasm.Admin audit-keygen <key-id>          (creates a new random audit key file with owner-only permissions; never overwrites)
//   Hasm.Admin audit-verify <system|company-id> [--checkpoint-dir <dir>]   (READ-ONLY integrity verification; exit 0 verified, 6 failed, 7 incomplete)
//   Hasm.Admin audit-anchor <system|company-id> [--establish-baseline]  (records the head of a sound stream in the checkpoint store; never changes the database)
// Configuration comes only from environment variables (Hasm__Database__*); see docs/DEVELOPMENT.md.
// Physical database names are never printed.

const int Ok = 0, Failed = 1, Usage = 2, BadConfig = 3, InProgress = 4, AdminExists = 5, AuditFailed = 6, AuditIncomplete = 7;

var command = args.Length > 0 ? args[0] : null;
Guid companyId = Guid.Empty;
AuditStream auditStream = default;
string? checkpointDirOverride = null;
var validUsage = command switch
{
    "init-system-db" or "register-company" => args.Length == 1,
    "bootstrap-admin" => args.Length is 2 or 3,
    "provision-company" => args.Length == 2 && Guid.TryParse(args[1], out companyId) && companyId != Guid.Empty,
    "grant-company-admin" => args.Length == 3 && Guid.TryParse(args[2], out companyId) && companyId != Guid.Empty,
    "audit-keygen" => args.Length == 2 && AuditKeyRing.IsValidKeyId(args[1]),
    "audit-verify" => AuditStream.TryParse(args.Length > 1 ? args[1] : null, out auditStream)
        && (args.Length == 2 || (args.Length == 4 && args[2] == "--checkpoint-dir" && !string.IsNullOrWhiteSpace(checkpointDirOverride = args[3]))),
    "audit-anchor" => AuditStream.TryParse(args.Length > 1 ? args[1] : null, out auditStream) && (args.Length == 2 || (args.Length == 3 && args[2] == "--establish-baseline")),
    _ => false,
};
if (!validUsage)
{
    Console.Error.WriteLine("Usage: Hasm.Admin init-system-db | register-company | bootstrap-admin <login> [display name] | provision-company <company-id> | grant-company-admin <login> <company-id> | audit-keygen <key-id> | audit-verify <system|company-id> [--checkpoint-dir <dir>] | audit-anchor <system|company-id> [--establish-baseline]");
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
        case "audit-verify":
        {
            var (keys, store) = BuildAuditEvidence(auditOptions, checkpointDirOverride, requireActiveKey: false);
            var report = await new AuditVerifier(options.BuildSystemConnectionString(admin: false), keys, store).VerifyAsync(auditStream);
            PrintReport(report);
            return report.Verdict switch { AuditVerdict.Verified => Ok, AuditVerdict.Failed => AuditFailed, _ => AuditIncomplete };
        }
        case "audit-anchor":
        {
            var (keys, store) = BuildAuditEvidence(auditOptions, null, requireActiveKey: true);
            var anchored = await new AuditVerifier(options.BuildSystemConnectionString(admin: false), keys, store).AnchorHeadAsync(auditStream, TimeProvider.System, args.Length == 3);
            Console.WriteLine(anchored is { } seq ? $"Stream head anchored at sequence {seq}." : "The stream is empty; nothing to anchor.");
            return Ok;
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
catch (Exception ex)
{
    // Never print connection strings or secrets; scrub the message and omit stack traces.
    // For provisioning failures the company is marked Failed centrally; its database is never dropped.
    Console.Error.WriteLine($"{command} failed: {ex.GetType().Name}: {options.Scrub(ex.Message)}");
    return Failed;
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
