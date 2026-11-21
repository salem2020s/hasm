using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using AT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Attachment;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using GP = Hasm.Infrastructure.SystemDatabase.SystemSchema.GlobalParty;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PL = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyLink;
using PP = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyProposal;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 slice 3 on the disposable PostgreSQL server (D-058..D-062): registry-bound backup of one company, trial restore into a NEW unregistered database with the read-only pre-cutover checks of
/// SRS 16.2 / Scenario 21.8 (shared links, attachment rows against files, migration compatibility), kept trial databases and the real Hasm.Admin commands. Every database is created with the disposable
/// prefix (injected name generator) except the trial databases of the Hasm.Admin test (the production generator names them opaquely), which that test drops itself. The class runs in a
/// NON-PARALLEL collection, after every other test class: while such a database exists, the cluster-identity gate of a concurrently starting test would rightly refuse the cluster.
/// Needs pg_dump/pg_restore of the server's major version on PATH.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class CompanyBackupServiceTests : PostgresTestBase
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-cbs-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private CompanyRegistry registry = null!;
    private int trialNames;

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private EncryptionOptions Encryption => new() { KeyDirectory = Keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true };

    private CompanyBackupService Service(string? storage = "", IReadOnlyList<MigrationScript>? scripts = null) =>
        new(Options, new AttachmentOptions { StorageDirectory = storage == "" ? Storage : storage }, Encryption, nameGenerator: () => { trialNames++; return NewTestName(); }, companyScripts: scripts);

    private async Task Init()
    {
        Directory.CreateDirectory(work); Directory.CreateDirectory(Storage);
        FileKekRing.Generate(Keys, "k1");
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        registry = new CompanyRegistry(Options.BuildSystemConnectionString(admin: true), NewTestName);
    }

    private async Task<CompanyRecord> NewCompany()
    {
        var c = await registry.RegisterPendingAsync();
        Assert.StartsWith(PostgresTestEnvironment.DatabasePrefix, c.PhysicalDatabaseName);
        await new CompanyProvisioner(Options, registry).ProvisionAsync(c.Id);
        return (await registry.GetAsync(c.Id))!;
    }

    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);

    private static async Task Exec(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private string Db(string name) => Options.BuildCompanyAdminConnectionString(name);

    private async Task<bool> Exists(string name) =>
        await Scalar<long>(Options.BuildMaintenanceConnectionString(), $"SELECT count(*) FROM pg_database WHERE datname = '{name}'") == 1;

    /// <summary>Every public table with its exact row count and a hash of its rows.</summary>
    private static async Task<string> Digest(string connection)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1", conn))
        await using (var r = await list.ExecuteReaderAsync()) while (await r.ReadAsync()) tables.Add(r.GetString(0));
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(x::text, ',' ORDER BY x::text)), '') FROM {new NpgsqlCommandBuilder().QuoteIdentifier(table)} x", conn);
            sb.Append(table).Append('=').Append((string)(await cmd.ExecuteScalarAsync())!).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string H(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private async Task<Guid> Party(CompanyRecord c, string code, Guid? global = null)
    {
        var id = Guid.NewGuid();
        await Exec(Db(c.PhysicalDatabaseName), $"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) " +
                                               $"VALUES ('{id}', '{code}', 'p', true, 1, 1, '{Guid.NewGuid()}', now(), now())");
        if (global is { } g) await Exec(Db(c.PhysicalDatabaseName), $"UPDATE {LP.Table} SET {LP.LinkState} = 2, {LP.GlobalPartyId} = '{g}', {LP.Revision} = 2 WHERE {LP.Id} = '{id}'");
        return id;
    }

    private async Task<Guid> User()
    {
        var id = Guid.NewGuid();
        await Exec(SystemConnection, $"INSERT INTO {U.Table} ({U.Id}, {U.Login}, {U.DisplayName}, {U.PasswordHash}, {U.State}, {U.IsIdentityAdministrator}, {U.CreatedAtUtc}, {U.StateChangedAtUtc}, {U.PasswordChangedAtUtc}) " +
                                     $"VALUES ('{id}', 'u{id:N}', 'u', '{new string('x', 60)}', 1, false, now(), now(), now())");
        return id;
    }

    private async Task<Guid> Global(Guid user) =>
        await Scalar<Guid>(SystemConnection, $"INSERT INTO {GP.Table} ({GP.Id}, {GP.PartyKind}, {GP.LegalNameAr}, {GP.CreatedBy}, {GP.CreatedAtUtc}, {GP.UpdatedAtUtc}) VALUES (gen_random_uuid(), 1, 'g', '{user}', now(), now()) RETURNING {GP.Id}");

    private async Task Link(CompanyRecord c, Guid local, Guid global, Guid user)
    {
        var pid = await Scalar<Guid>(SystemConnection, $"INSERT INTO {PP.Table} ({PP.Id}, {PP.CompanyId}, {PP.RequestKey}, {PP.LocalPartyId}, {PP.Kind}, {PP.PartyKind}, {PP.LegalNameAr}, {PP.ProposedBy}, {PP.ProposedAtUtc}) " +
                                                        $"VALUES (gen_random_uuid(), '{c.Id}', gen_random_uuid(), '{local}', 1, 1, 'g', '{user}', now()) RETURNING {PP.Id}");
        await Exec(SystemConnection, $"INSERT INTO {PL.Table} ({PL.CompanyId}, {PL.LocalPartyId}, {PL.GlobalPartyId}, {PL.ProposalId}, {PL.LinkedAtUtc}) VALUES ('{c.Id}', '{local}', '{global}', '{pid}', now())");
    }

    /// <summary>An attachment row and (unless <paramref name="file"/> is null) its file in live storage. A sensitive file is encrypted under <paramref name="ring"/> (default: the key directory).</summary>
    private async Task Attachment(CompanyRecord c, Guid subject, byte[] content, byte[]? file, bool sensitive = false, string? rowKeyId = null, KekRing? ring = null)
    {
        var id = Guid.NewGuid(); var name = Guid.NewGuid().ToString("N") + ".att";
        var dir = Path.Combine(Storage, c.Id.ToString("N")); Directory.CreateDirectory(dir);
        string? keyId = null;
        if (file is not null)
        {
            var bytes = sensitive ? new EnvelopeEncryptor(ring ?? new FileKekRing(Encryption)).Encrypt(file, AttachmentService.Context(c.Id, id)) : file;
            if (sensitive) keyId = EnvelopeEncryptor.KeyIdOf(bytes);
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        }
        keyId = rowKeyId ?? keyId ?? (sensitive ? "k1" : null);
        await Exec(Db(c.PhysicalDatabaseName), $"INSERT INTO {AT.Table} ({AT.Id}, {AT.SubjectKind}, {AT.SubjectId}, {AT.FileName}, {AT.ContentType}, {AT.SizeBytes}, {AT.Sha256}, {AT.Sensitivity}, {AT.StorageName}, {AT.KeyId}, {AT.State}, {AT.Revision}, {AT.CreatedBy}, {AT.CreatedAtUtc}, {AT.UpdatedAtUtc}) " +
            $"VALUES ('{id}', 'local_party', '{subject}', 'f.pdf', 'application/pdf', {content.Length}, '{H(content)}', {(sensitive ? 2 : 1)}, '{name}', {(keyId is null ? "NULL" : $"'{keyId}'")}, 1, 1, '{Guid.NewGuid()}', now(), now())");
    }

    private static string Codes(IEnumerable<BackupFinding> f) => string.Join(",", f.Select(x => $"{x.Code}={x.Count}").OrderBy(x => x, StringComparer.Ordinal));

    // ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_registered_ready_company_is_backed_up_into_a_clean_set_bound_to_its_id_and_nothing_else_changes()
    {
        await Init();
        var c = await NewCompany(); var other = await NewCompany();
        var p = await Party(c, "P1");
        await Attachment(c, p, "%PDF-1.4 a"u8.ToArray(), "%PDF-1.4 a"u8.ToArray());
        await Party(other, "O1");
        var before = (await Digest(Db(c.PhysicalDatabaseName)), await Digest(Db(other.PhysicalDatabaseName)), await Digest(SystemConnection));

        var r = await Service().BackupCompanyAsync(c.Id, Path.Combine(work, "set"));

        Assert.Equal(c.Id, r.Manifest.CompanyId);
        Assert.Single(r.Manifest.Attachments);
        Assert.Equal(MigrationScript.LoadEmbeddedCompany().Max(s => s.Version), r.Manifest.CompanyMigrationVersion);
        Assert.NotNull(r.Manifest.DatabaseLocale);
        Assert.Empty(r.MissingKeyIds);
        Assert.True(BackupSet.Verify(Path.Combine(work, "set")).IsClean);
        Assert.Equal(before, (await Digest(Db(c.PhysicalDatabaseName)), await Digest(Db(other.PhysicalDatabaseName)), await Digest(SystemConnection)));
        Assert.Empty(Directory.GetFileSystemEntries(work, "set.staging-*"));                                       // the private staging folder is gone
    }

    [PostgresFact]
    public async Task Refusals_come_before_any_dump_and_leave_nothing_and_only_stale_staging_files_are_swept()
    {
        await Init();
        var c = await NewCompany();
        var pending = await registry.RegisterPendingAsync();
        var dest = Path.Combine(work, "set");
        Assert.Equal("company_not_found", (await Assert.ThrowsAsync<BackupException>(() => Service().BackupCompanyAsync(Guid.NewGuid(), dest))).Code);
        Assert.Equal("company_not_ready", (await Assert.ThrowsAsync<BackupException>(() => Service().BackupCompanyAsync(pending.Id, dest))).Code);
        Assert.Equal("attachment_storage_not_configured", (await Assert.ThrowsAsync<BackupException>(() => Service(storage: null).BackupCompanyAsync(c.Id, dest))).Code);
        Assert.Equal("destination_overlaps_source", (await Assert.ThrowsAsync<BackupException>(() => Service().BackupCompanyAsync(c.Id, Path.Combine(Storage, "set")))).Code);
        var full = Path.Combine(work, "full"); Directory.CreateDirectory(full); File.WriteAllText(Path.Combine(full, "x"), "x");
        Assert.Equal("destination_not_empty", (await Assert.ThrowsAsync<BackupException>(() => Service().BackupCompanyAsync(c.Id, full))).Code);
        Assert.False(Directory.Exists(dest));
        Assert.Empty(Directory.GetFileSystemEntries(work, "*.staging-*"));

        var stale = Path.Combine(work, "set.staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stale); File.WriteAllText(Path.Combine(stale, "database.dump"), "x"); Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-25));
        var young = Path.Combine(work, "set.staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(young);
        var r = await Service().BackupCompanyAsync(c.Id, dest);
        Assert.Equal(1, r.StagingFoldersSwept);
        Assert.False(Directory.Exists(stale)); Assert.True(Directory.Exists(young));
    }

    [PostgresFact]
    public async Task An_icu_company_database_is_refused_up_front_and_no_set_or_staging_file_is_written()
    {
        await Init();
        var name = NewTestName();
        await Exec(Options.BuildMaintenanceConnectionString(), $"CREATE DATABASE {name} TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER icu ICU_LOCALE 'und' LOCALE 'C'");
        var id = Guid.NewGuid();
        await Exec(SystemConnection, $"INSERT INTO {Co.Table} ({Co.Id}, {Co.PhysicalDatabaseName}, {Co.ProvisioningState}, {Co.CreatedAtUtc}, {Co.StateChangedAtUtc}) VALUES ('{id}', '{name}', {(short)ProvisioningState.Ready}, now(), now())");
        var dest = Path.Combine(work, "set");
        Assert.Equal("locale_unsupported", (await Assert.ThrowsAsync<BackupException>(() => Service().BackupCompanyAsync(id, dest))).Code);
        Assert.False(Directory.Exists(dest));
        Assert.Empty(Directory.GetFileSystemEntries(work, "*.staging-*"));
    }

    [PostgresFact]
    public async Task A_trial_restore_passes_every_check_and_drops_its_database_and_the_registry_and_system_database_are_unchanged()
    {
        await Init();
        var c = await NewCompany();
        var user = await User(); var g = await Global(user);
        var p = await Party(c, "P1", g); await Link(c, p, g, user);
        await Attachment(c, p, "%PDF-1.4 plain"u8.ToArray(), "%PDF-1.4 plain"u8.ToArray());
        await Attachment(c, p, "salary"u8.ToArray(), "salary"u8.ToArray(), sensitive: true);
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(c.Id, set);
        var system = await Digest(SystemConnection);

        var r = await Service().TrialRestoreAsync(set, c.Id, Path.Combine(work, "trial-att"), keep: false);

        Assert.Equal(TrialRestoreVerdict.Ok, r.Verdict);
        Assert.Empty(r.Failures); Assert.Empty(r.Warnings);
        Assert.Equal(2, r.AttachmentFiles); Assert.Equal(0, r.UnreferencedFiles);
        Assert.Null(r.KeptFile);
        Assert.Equal(1, trialNames);
        Assert.False(await Exists(GeneratedDatabaseNames[^1]));                                                     // the trial database was dropped
        Assert.Equal(system, await Digest(SystemConnection));                                                       // registry and Hasm_System untouched
        Assert.True(await Exists(c.PhysicalDatabaseName));
    }

    [PostgresFact]
    public async Task Trial_restore_refusals_create_nothing()
    {
        await Init();
        var c = await NewCompany(); var other = await NewCompany();
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(c.Id, set);
        var att = Path.Combine(work, "trial-att");
        Assert.Equal("company_mismatch", (await Assert.ThrowsAsync<BackupException>(() => Service().TrialRestoreAsync(set, other.Id, att, false))).Code);
        Assert.Equal("target_in_live_storage", (await Assert.ThrowsAsync<BackupException>(() => Service().TrialRestoreAsync(set, c.Id, Path.Combine(Storage, "x"), false))).Code);

        // a set of a company this installation does not know (its registry row is gone in a copy of the set with another id)
        var foreign = Path.Combine(work, "foreign");
        await Service().BackupCompanyAsync(other.Id, foreign);
        var manifestPath = Path.Combine(foreign, BackupSet.ManifestFileName);
        var unknown = Guid.NewGuid();
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(other.Id.ToString(), unknown.ToString()));
        Assert.Equal("company_not_found", (await Assert.ThrowsAsync<BackupException>(() => Service().TrialRestoreAsync(foreign, unknown, att, false))).Code);

        // the manifest locale edited to another VALID libc locale while the source exists
        manifestPath = Path.Combine(set, BackupSet.ManifestFileName);
        var json = File.ReadAllText(manifestPath);
        var edited = System.Text.RegularExpressions.Regex.Replace(json, "\"collate\":\\s*\"[^\"]*\"", "\"collate\": \"POSIX\"");
        Assert.NotEqual(json, edited);
        File.WriteAllText(manifestPath, edited);
        Assert.Equal("locale_mismatch", (await Assert.ThrowsAsync<BackupException>(() => Service().TrialRestoreAsync(set, c.Id, att, false))).Code);

        Assert.Equal(0, trialNames);                                                                                // no trial database was ever named, so none was created
        Assert.False(Directory.Exists(att));
    }

    [PostgresFact]
    public async Task Disaster_recovery_the_company_database_is_gone_and_the_trial_restore_uses_the_manifest_locale()
    {
        await Init();
        var c = await NewCompany();
        await Party(c, "P1");
        var digest = await Digest(Db(c.PhysicalDatabaseName));
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(c.Id, set);
        await Exec(Options.BuildMaintenanceConnectionString(), $"DROP DATABASE {c.PhysicalDatabaseName} WITH (FORCE)");

        var r = await Service().TrialRestoreAsync(set, c.Id, Path.Combine(work, "trial-att"), keep: true);

        Assert.Equal(TrialRestoreVerdict.Ok, r.Verdict);
        var kept = CompanyBackupService.ReadKeptFile(r.KeptFile!)!;
        Assert.Equal(digest, await Digest(Db(kept.Database)));
        await Service().DropTrialRestoreAsync(r.KeptFile!);
        Assert.False(await Exists(kept.Database));
    }

    [PostgresFact]
    public async Task Check_a_unresolved_shared_links_fail_and_registry_links_missing_locally_are_reported_read_only()
    {
        await Init();
        var c = await NewCompany();
        var user = await User();
        Guid g1 = await Global(user), g2 = await Global(user), g3 = await Global(user), g4 = await Global(user), g5 = await Global(user);
        var ok = await Party(c, "P1", g1); await Link(c, ok, g1, user);
        await Party(c, "P2", g2);                                                                                   // linked locally, no registry link
        var other = await Party(c, "P3", g3); await Link(c, other, g4, user);                                       // the registry links another global party
        await Link(c, Guid.NewGuid(), g5, user);                                                                    // a registry link whose local party this copy lacks
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(c.Id, set);
        var system = await Digest(SystemConnection);

        var r = await Service().TrialRestoreAsync(set, c.Id, Path.Combine(work, "trial-att"), keep: false);

        Assert.Equal(TrialRestoreVerdict.Failed, r.Verdict);
        Assert.Equal("shared_links_unresolved=2", Codes(r.Failures));
        Assert.Equal("shared_links_missing_locally=1", Codes(r.Warnings));
        Assert.Equal(system, await Digest(SystemConnection));
    }

    [PostgresFact]
    public async Task Check_b_attachment_rows_against_files_failures_and_key_warnings()
    {
        await Init();
        var c = await NewCompany();
        var p = await Party(c, "P1");
        await Attachment(c, p, "missing"u8.ToArray(), file: null);                                                  // a row without its file
        await Attachment(c, p, "expected"u8.ToArray(), "different"u8.ToArray());                                     // a file that does not match the row hash
        var otherKeys = Path.Combine(work, "other-keys"); FileKekRing.Generate(otherKeys, "k9");
        await Attachment(c, p, "secret9"u8.ToArray(), "secret9"u8.ToArray(), sensitive: true, ring: new FileKekRing(new EncryptionOptions { KeyDirectory = otherKeys, ActiveKeyId = "k9", AllowInsecureFilePermissions = true }));
        await Attachment(c, p, "rewrapped"u8.ToArray(), "rewrapped"u8.ToArray(), sensitive: true, rowKeyId: "k0");  // the row names an older key; the envelope is under k1
        var set = Path.Combine(work, "set");
        var backup = await Service().BackupCompanyAsync(c.Id, set);
        Assert.Equal(new[] { "k9" }, backup.MissingKeyIds);

        var r = await Service().TrialRestoreAsync(set, c.Id, Path.Combine(work, "trial-att"), keep: false);

        Assert.Equal(TrialRestoreVerdict.Failed, r.Verdict);
        Assert.Equal("attachment_file_missing=1,attachment_hash_mismatch=1", Codes(r.Failures));
        Assert.Equal("attachment_key_id_stale=1,attachment_key_missing=1,key_ids_missing=1", Codes(r.Warnings));
    }

    [PostgresFact]
    public async Task Check_c_a_newer_set_is_refused_before_any_database_and_a_diverged_checksum_fails()
    {
        await Init();
        var c = await NewCompany();
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(c.Id, set);
        var manifestPath = Path.Combine(set, BackupSet.ManifestFileName);
        var json = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, System.Text.RegularExpressions.Regex.Replace(json, "\"companyMigrationVersion\":\\s*\\d+", "\"companyMigrationVersion\": 9999"));
        Assert.Equal("set_newer_than_installation", (await Assert.ThrowsAsync<BackupException>(() => Service().TrialRestoreAsync(set, c.Id, Path.Combine(work, "t1"), false))).Code);
        Assert.Equal(0, trialNames);

        // a source whose journal records another checksum for version 1 (a diverged build)
        await Exec(Db(c.PhysicalDatabaseName), $"UPDATE {SystemSchema.MigrationJournalTable} SET checksum = repeat('0', 64) WHERE version = 1");
        var diverged = Path.Combine(work, "diverged");
        await Service().BackupCompanyAsync(c.Id, diverged);
        var r = await Service().TrialRestoreAsync(diverged, c.Id, Path.Combine(work, "t2"), false);
        Assert.Equal(TrialRestoreVerdict.Failed, r.Verdict);
        Assert.Equal("migration_checksum_mismatch=1", Codes(r.Failures));

        // this installation one script behind the set: refused up front as well
        var older = MigrationScript.LoadEmbeddedCompany().Where(s => s.Version < MigrationScript.LoadEmbeddedCompany().Max(x => x.Version)).ToList();
        Assert.Equal("set_newer_than_installation", (await Assert.ThrowsAsync<BackupException>(() => Service(scripts: older).TrialRestoreAsync(diverged, c.Id, Path.Combine(work, "t3"), false))).Code);
    }

    [PostgresFact]
    public async Task A_kept_trial_database_is_dropped_only_through_its_own_file_and_token()
    {
        await Init();
        var c = await NewCompany();
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(c.Id, set);
        var r = await Service().TrialRestoreAsync(set, c.Id, Path.Combine(work, "trial-att"), keep: true);
        var keptPath = r.KeptFile!;
        Assert.StartsWith(set + ".trial-", keptPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, OperatingSystem.IsWindows() ? UnixFileMode.UserRead | UnixFileMode.UserWrite : File.GetUnixFileMode(keptPath));
        var kept = CompanyBackupService.ReadKeptFile(keptPath)!;
        Assert.True(await Exists(kept.Database));

        string Forge(CompanyBackupService.KeptTrial k) => CompanyBackupService.WriteKeptFile(Path.Combine(work, "forged-" + Guid.NewGuid().ToString("N")), k);
        Assert.Equal("not_a_trial_restore", (await Assert.ThrowsAsync<BackupException>(() => Service().DropTrialRestoreAsync(Forge(kept with { Database = c.PhysicalDatabaseName })))).Code);
        Assert.Equal("not_a_trial_restore", (await Assert.ThrowsAsync<BackupException>(() => Service().DropTrialRestoreAsync(Forge(kept with { Token = Guid.NewGuid().ToString("N") })))).Code);
        Assert.Equal("not_a_trial_restore", (await Assert.ThrowsAsync<BackupException>(() => Service().DropTrialRestoreAsync(Forge(kept with { Database = Options.SystemDatabaseName! })))).Code);
        Assert.True(await Exists(c.PhysicalDatabaseName));
        Assert.True(await Exists(kept.Database));

        await Service().DropTrialRestoreAsync(keptPath);
        Assert.False(await Exists(kept.Database));
        Assert.False(File.Exists(keptPath));
    }

    [PostgresFact]
    public async Task The_admin_commands_back_up_trial_restore_keep_and_drop_and_print_neither_names_nor_secrets()
    {
        await Init();
        var c = await NewCompany();
        var env = new Dictionary<string, string?>
        {
            ["Hasm__Attachments__StorageDirectory"] = Storage, ["Hasm__Encryption__KeyDirectory"] = Keys, ["Hasm__Encryption__ActiveKeyId"] = "k1", ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
        };
        var set = Path.Combine(work, "set");
        var outputs = new List<string>();
        string? keptDatabase = null;
        try
        {
            var backup = await AdminTool.RunWith(Options, env, "backup-company", c.Id.ToString(), set); outputs.Add(backup.All);
            Assert.True(backup.Exit == 0, backup.All);
            var trial = await AdminTool.RunWith(Options, env, "trial-restore-company", set, c.Id.ToString(), Path.Combine(work, "t1")); outputs.Add(trial.All);
            Assert.True(trial.Exit == 0, trial.All);
            Assert.Contains("OK", trial.Out);
            var keep = await AdminTool.RunWith(Options, env, "trial-restore-company", set, c.Id.ToString(), Path.Combine(work, "t2"), "--keep"); outputs.Add(keep.All);
            Assert.True(keep.Exit == 0, keep.All);
            var keptFile = Assert.Single(Directory.GetFiles(work, "set.trial-*.json"));
            keptDatabase = CompanyBackupService.ReadKeptFile(keptFile)!.Database;
            Assert.True(await Exists(keptDatabase));
            var drop = await AdminTool.RunWith(Options, env, "drop-trial-restore", keptFile); outputs.Add(drop.All);
            Assert.True(drop.Exit == 0, drop.All);
            Assert.False(await Exists(keptDatabase));

            Assert.Equal(2, (await AdminTool.RunWith(Options, env, "backup-company", "not-a-guid", set)).Exit);
            Assert.Equal(2, (await AdminTool.RunWith(Options, env, "trial-restore-company", "relative", c.Id.ToString(), Path.Combine(work, "t3"))).Exit);
            var noStorage = new Dictionary<string, string?>(env) { ["Hasm__Attachments__StorageDirectory"] = null };
            var unconfigured = await AdminTool.RunWith(Options, noStorage, "backup-company", c.Id.ToString(), Path.Combine(work, "set2")); outputs.Add(unconfigured.All);
            Assert.Equal(3, unconfigured.Exit);

            foreach (var o in outputs)
            {
                Assert.DoesNotContain(Options.AdminPassword!, o);
                Assert.DoesNotContain(c.PhysicalDatabaseName, o);
                Assert.DoesNotContain(keptDatabase, o);
                Assert.DoesNotContain("pg_dump:", o); Assert.DoesNotContain("pg_restore:", o);
            }
        }
        finally
        {
            if (keptDatabase is not null) await Exec(Options.BuildMaintenanceConnectionString(), $"DROP DATABASE IF EXISTS {keptDatabase} WITH (FORCE)");
        }
    }
}

/// <summary>Runs <see cref="CompanyBackupServiceTests"/> alone (no other test class in parallel); see its summary.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CompanyBackupSerialCollection
{
    public const string Name = "company-backup-serial";
}
