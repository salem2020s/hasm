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
using GP = Hasm.Infrastructure.SystemDatabase.SystemSchema.GlobalParty;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PL = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyLink;
using PP = Hasm.Infrastructure.SystemDatabase.SystemSchema.PartyProposal;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Release 1 acceptance, SRS 21 scenario 21.8 (single-company restore without rolling back the other companies or the system database; links and attachments verified), on the TRIAL restore path of
/// Phase 4.3 slice 3 (the live cutover of slice 7 is a different path and is NOT covered here). Two companies with linked parties and plain and sensitive attachments are provisioned; one is backed up and
/// trial-restored. The proof is a before and after comparison of per-database CONTENT HASHES (every public table, exact row counts and row hashes) of the other company, the restored company's live
/// database and the system database, plus a hash of every file of the live attachment storage: all must be byte-identical, the restored copy must equal its source and contain nothing of the other
/// company, and an inconsistent set fails closed with the exact findings and changes nothing anywhere. Evidence only: it does not claim release acceptance of group G. Needs pg_dump and pg_restore of the
/// server's major version on PATH. The sets are UNENCRYPTED v1 sets (DEV/TEST path, allowUnencrypted); an encrypted-set variant is not covered. Non-parallel collection, like <see cref="CompanyBackupServiceTests"/>, because a trial database must not exist while another test class starts.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class CompanyRestoreIsolationTests : PostgresTestBase
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-cri-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private CompanyRegistry registry = null!;

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private EncryptionOptions Encryption => new() { KeyDirectory = Keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true };

    private CompanyBackupService Service() =>
        new(Options, new AttachmentOptions { StorageDirectory = Storage }, Encryption, nameGenerator: NewTestName);

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
        await new CompanyProvisioner(Options, registry).ProvisionAsync(c.Id);
        return (await registry.GetAsync(c.Id))!;
    }

    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);
    private string Db(string name) => Options.BuildCompanyAdminConnectionString(name);

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

    /// <summary>
    /// Every public table with its exact row count and a hash of its rows, every sequence with its last value, and a fingerprint of the schema objects (trigger, function and constraint definitions of
    /// public): any change to data, counters or schema shows. <paramref name="constraintText"/> false compares constraint names, kinds and structural attributes (columns, deferrability, referenced table, validity) but not the rendered text (needed against a pg_restore copy, which renders CHECK expressions differently).
    /// </summary>
    private static async Task<string> Digest(string connection, bool constraintText = true)
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
        sb.Append("sequences=").Append(await Text(conn, "SELECT coalesce(string_agg(schemaname || '.' || sequencename || ':' || coalesce(last_value::text, 'null'), ',' ORDER BY schemaname, sequencename), '') FROM pg_sequences")).Append(';');
        sb.Append("triggers=").Append(await Text(conn, "SELECT md5(coalesce(string_agg(pg_get_triggerdef(t.oid), E'\\n' ORDER BY c.relname, t.tgname), '')) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND NOT t.tgisinternal")).Append(';');
        sb.Append("functions=").Append(await Text(conn, "SELECT md5(coalesce(string_agg(pg_get_functiondef(p.oid), E'\\n' ORDER BY p.proname, p.oid::text), '')) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.prokind IN ('f', 'p')")).Append(';');
        sb.Append("constraints=").Append(await Text(conn, "SELECT md5(coalesce(string_agg(c.conrelid::regclass::text || '.' || c.conname || ':' || c.contype::text || ':' || coalesce(c.conkey::text, '') || ':' || c.condeferrable::text || ':' || c.condeferred::text || ':' || coalesce(c.confrelid::regclass::text, '') || ':' || c.convalidated::text || (CASE WHEN " + (constraintText ? "true" : "false") + " THEN pg_get_constraintdef(c.oid) ELSE '' END), E'\\n' ORDER BY c.conrelid::regclass::text, c.conname), '')) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'public'")).Append(';');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static async Task<string> Text(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static string H(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Relative path and SHA-256 of every file under a folder (empty when it does not exist).</summary>
    private static IReadOnlyDictionary<string, string> Files(string root) =>
        !Directory.Exists(root) ? new Dictionary<string, string>() :
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), f => H(File.ReadAllBytes(f)), StringComparer.Ordinal);

    private async Task<string> DatabaseNames() =>
        string.Join(",", await ListDatabases());

    private async Task<List<string>> ListDatabases()
    {
        var names = new List<string>();
        await using var conn = new NpgsqlConnection(Options.BuildMaintenanceConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT datname FROM pg_database WHERE datname LIKE '{PostgresTestEnvironment.DatabasePrefix}%' ORDER BY 1", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    private async Task<Guid> Party(CompanyRecord c, string code, Guid global)
    {
        var id = Guid.NewGuid();
        await Exec(Db(c.PhysicalDatabaseName), $"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) " +
                                               $"VALUES ('{id}', '{code}', 'p', true, 1, 1, '{Guid.NewGuid()}', now(), now())");
        await Exec(Db(c.PhysicalDatabaseName), $"UPDATE {LP.Table} SET {LP.LinkState} = 2, {LP.GlobalPartyId} = '{global}', {LP.Revision} = 2 WHERE {LP.Id} = '{id}'");
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

    private async Task Attachment(CompanyRecord c, Guid subject, byte[] content, byte[]? file, bool sensitive = false)
    {
        var id = Guid.NewGuid(); var name = Guid.NewGuid().ToString("N") + ".att";
        var dir = Path.Combine(Storage, c.Id.ToString("N")); Directory.CreateDirectory(dir);
        string? keyId = null;
        if (file is not null)
        {
            var bytes = sensitive ? new EnvelopeEncryptor(new FileKekRing(Encryption)).Encrypt(file, AttachmentService.Context(c.Id, id)) : file;
            if (sensitive) keyId = EnvelopeEncryptor.KeyIdOf(bytes);
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        }
        keyId ??= sensitive ? "k1" : null;
        await Exec(Db(c.PhysicalDatabaseName), $"INSERT INTO {AT.Table} ({AT.Id}, {AT.SubjectKind}, {AT.SubjectId}, {AT.FileName}, {AT.ContentType}, {AT.SizeBytes}, {AT.Sha256}, {AT.Sensitivity}, {AT.StorageName}, {AT.KeyId}, {AT.State}, {AT.Revision}, {AT.CreatedBy}, {AT.CreatedAtUtc}, {AT.UpdatedAtUtc}) " +
            $"VALUES ('{id}', 'local_party', '{subject}', 'f.pdf', 'application/pdf', {content.Length}, '{H(content)}', {(sensitive ? 2 : 1)}, '{name}', {(keyId is null ? "NULL" : $"'{keyId}'")}, 1, 1, '{Guid.NewGuid()}', now(), now())");
    }

    private static string Codes(IEnumerable<BackupFinding> f) => string.Join(",", f.Select(x => $"{x.Code}={x.Count}").OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Two companies, each with a party linked in the registry and one plain and one sensitive attachment, with content that differs between them.</summary>
    private async Task<(CompanyRecord A, CompanyRecord B)> TwoCompanies()
    {
        await Init();
        var a = await NewCompany(); var b = await NewCompany();
        var user = await User();
        foreach (var (c, tag) in new[] { (a, "A"), (b, "B") })
        {
            var g = await Global(user);
            var p = await Party(c, tag + "-P1", g); await Link(c, p, g, user);
            await Attachment(c, p, Encoding.UTF8.GetBytes("%PDF plain " + tag), Encoding.UTF8.GetBytes("%PDF plain " + tag));
            await Attachment(c, p, Encoding.UTF8.GetBytes("secret " + tag), Encoding.UTF8.GetBytes("secret " + tag), sensitive: true);
        }
        return (a, b);
    }

    [PostgresFact]
    public async Task A_trial_restore_of_one_company_leaves_the_other_company_the_system_database_and_every_live_file_byte_identical_and_the_copy_holds_only_its_own_company()
    {
        var (a, b) = await TwoCompanies();
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(a.Id, set);
        var before = (A: await Digest(Db(a.PhysicalDatabaseName)), B: await Digest(Db(b.PhysicalDatabaseName)), System: await Digest(SystemConnection), Live: Files(Storage), Databases: await DatabaseNames());
        var attachmentsOfA = Files(Path.Combine(Storage, a.Id.ToString("N")));
        Assert.Equal(2, attachmentsOfA.Count);

        var trialAtt = Path.Combine(work, "trial-att");
        var r = await Service().TrialRestoreAsync(set, a.Id, trialAtt, keep: true, allowUnencrypted: true);

        // the checks passed: links resolved against the registry, attachment rows against their files
        Assert.Equal(TrialRestoreVerdict.Ok, r.Verdict);
        Assert.Empty(r.Failures); Assert.Empty(r.Warnings);
        Assert.Equal(2, r.AttachmentFiles); Assert.Equal(0, r.UnreferencedFiles);
        var kept = CompanyBackupService.ReadKeptFile(r.KeptFile!)!;

        // the restored copy equals its source and holds nothing of the other company
        Assert.Equal(await Digest(Db(a.PhysicalDatabaseName), constraintText: false), await Digest(Db(kept.Database), constraintText: false));   // pg_restore re-renders CHECK expressions (same meaning, different text): constraint names and kinds are compared, not their text
        // review F-L2 of #153 (control): the constraint comparison of the restored copy sees a structural change that keeps name and kind (a foreign key made deferrable), then sees it reverted
        var keptDb = Db(kept.Database);
        var fk = (await Scalar<string>(keptDb, "SELECT c.conrelid::regclass::text || '|' || c.conname FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'public' AND c.contype = 'f' AND NOT c.condeferrable ORDER BY 1 LIMIT 1")).Split('|');
        await Exec(keptDb, $"ALTER TABLE {fk[0]} ALTER CONSTRAINT \"{fk[1]}\" DEFERRABLE");
        Assert.NotEqual(await Digest(Db(a.PhysicalDatabaseName), constraintText: false), await Digest(keptDb, constraintText: false));
        await Exec(keptDb, $"ALTER TABLE {fk[0]} ALTER CONSTRAINT \"{fk[1]}\" NOT DEFERRABLE");
        Assert.Equal(await Digest(Db(a.PhysicalDatabaseName), constraintText: false), await Digest(keptDb, constraintText: false));
        Assert.Equal(0L, await Scalar<long>(Db(kept.Database), $"SELECT count(*) FROM {LP.Table} WHERE {LP.Code} LIKE 'B-%'"));
        Assert.Equal(1L, await Scalar<long>(Db(kept.Database), $"SELECT count(*) FROM {LP.Table} WHERE {LP.Code} LIKE 'A-%'"));
        Assert.Equal(attachmentsOfA.Values.Order(StringComparer.Ordinal).ToArray(), Files(trialAtt).Values.Order(StringComparer.Ordinal).ToArray());      // the restored files are exactly A's bytes (plain and sensitive), nothing of B

        // while the trial copy exists: every other database, the live company, the registry and every live file are unchanged
        Assert.Equal(before.A, await Digest(Db(a.PhysicalDatabaseName)));
        Assert.Equal(before.B, await Digest(Db(b.PhysicalDatabaseName)));
        Assert.Equal(before.System, await Digest(SystemConnection));
        Assert.Equal(before.Live, Files(Storage));
        Assert.StartsWith("..", Path.GetRelativePath(Storage, trialAtt));                                           // the trial files lie OUTSIDE the live storage (relative path leaves it)

        await Service().DropTrialRestoreAsync(r.KeptFile!);

        // after the drop nothing of the trial remains and the installation is exactly as before
        Assert.Equal(before.Databases, await DatabaseNames());
        Assert.Equal(before.A, await Digest(Db(a.PhysicalDatabaseName)));
        Assert.Equal(before.B, await Digest(Db(b.PhysicalDatabaseName)));
        Assert.Equal(before.System, await Digest(SystemConnection));
        Assert.Equal(before.Live, Files(Storage));

        // sensitivity: each kind of change the proof is meant to catch really moves the matching hash, and reverting restores it exactly
        var bDb = Db(b.PhysicalDatabaseName);
        var seq = await Scalar<string>(bDb, "SELECT sequencename FROM pg_sequences WHERE schemaname = 'public' ORDER BY 1 LIMIT 1");   // one live sequence
        var lastText = await Scalar<string>(bDb, $"SELECT coalesce(last_value::text, '') FROM pg_sequences WHERE schemaname = 'public' AND sequencename = '{seq}'");
        long? last = lastText == "" ? null : long.Parse(lastText);
        await Exec(bDb, $"SELECT setval('public.\"{seq}\"', {(last ?? 0) + 1000}, true)");
        Assert.NotEqual(before.B, await Digest(bDb));
        await Exec(bDb, last is null ? $"SELECT setval('public.\"{seq}\"', 1, false)" : $"SELECT setval('public.\"{seq}\"', {last}, true)");
        Assert.Equal(before.B, await Digest(bDb));

        // review F-L3 of #153: a changed trigger, function or constraint of a live database also changes its digest (each is reverted and the digest must return)
        var trigger = (await Scalar<string>(bDb, "SELECT c.relname || '|' || t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND NOT t.tgisinternal ORDER BY 1 LIMIT 1")).Split('|');
        await Exec(bDb, $"ALTER TRIGGER \"{trigger[1]}\" ON \"{trigger[0]}\" RENAME TO zz_mutated_trigger");
        Assert.NotEqual(before.B, await Digest(bDb));
        await Exec(bDb, $"ALTER TRIGGER zz_mutated_trigger ON \"{trigger[0]}\" RENAME TO \"{trigger[1]}\"");
        Assert.Equal(before.B, await Digest(bDb));

        var function = await Scalar<string>(bDb, "SELECT p.oid::regprocedure::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.prokind = 'f' AND p.proconfig IS NULL ORDER BY 1 LIMIT 1");
        await Exec(bDb, $"ALTER FUNCTION {function} SET statement_timeout = '7s'");                                    // a function-level setting is part of pg_get_functiondef
        Assert.NotEqual(before.B, await Digest(bDb));
        await Exec(bDb, $"ALTER FUNCTION {function} RESET statement_timeout");
        Assert.Equal(before.B, await Digest(bDb));

        var constraint = (await Scalar<string>(bDb, "SELECT c.conrelid::regclass::text || '|' || c.conname FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'public' AND c.contype = 'c' ORDER BY 1 LIMIT 1")).Split('|');
        await Exec(bDb, $"ALTER TABLE {constraint[0]} RENAME CONSTRAINT \"{constraint[1]}\" TO zz_mutated_constraint");
        Assert.NotEqual(before.B, await Digest(bDb));
        await Exec(bDb, $"ALTER TABLE {constraint[0]} RENAME CONSTRAINT zz_mutated_constraint TO \"{constraint[1]}\"");
        Assert.Equal(before.B, await Digest(bDb));

        var liveFile = Directory.EnumerateFiles(Path.Combine(Storage, a.Id.ToString("N"))).First();                    // one byte of a live attachment
        var original = File.ReadAllBytes(liveFile);
        var changed = (byte[])original.Clone(); changed[^1] ^= 0x01;
        File.WriteAllBytes(liveFile, changed);
        Assert.NotEqual(before.Live, Files(Storage));
        File.WriteAllBytes(liveFile, original);
        Assert.Equal(before.Live, Files(Storage));

        var partyB = await Scalar<Guid>(bDb, $"SELECT {LP.Id} FROM {LP.Table} LIMIT 1");                               // one row of B (last: the revision trigger allows only an advance by one, so this one is not reverted; B is throwaway)
        await Exec(bDb, $"UPDATE {LP.Table} SET {LP.Notes} = 'mutated', {LP.Revision} = {LP.Revision} + 1 WHERE {LP.Id} = '{partyB}'");
        Assert.NotEqual(before.B, await Digest(bDb));
    }

    [PostgresFact]
    public async Task A_trial_restore_of_an_inconsistent_set_fails_closed_with_the_exact_findings_and_nothing_anywhere_changes()
    {
        var (a, b) = await TwoCompanies();
        // make A's data inconsistent BEFORE the backup: a party linked locally to a global party the registry does not link, an attachment row without its file, and a file that no longer matches its row hash
        var user = await User();
        var orphanGlobal = await Global(user);
        var p = await Party(a, "A-P2", orphanGlobal);                                                                // linked locally, no registry link
        await Attachment(a, p, "missing"u8.ToArray(), file: null);
        await Attachment(a, p, "expected"u8.ToArray(), "different"u8.ToArray());
        var set = Path.Combine(work, "set");
        await Service().BackupCompanyAsync(a.Id, set);
        var before = (A: await Digest(Db(a.PhysicalDatabaseName)), B: await Digest(Db(b.PhysicalDatabaseName)), System: await Digest(SystemConnection), Live: Files(Storage), Databases: await DatabaseNames());

        var r = await Service().TrialRestoreAsync(set, a.Id, Path.Combine(work, "trial-att"), keep: false, allowUnencrypted: true);

        Assert.Equal(TrialRestoreVerdict.Failed, r.Verdict);                                                         // the inconsistent set is refused by the pre-checks
        Assert.Equal("attachment_file_missing=1,attachment_hash_mismatch=1,shared_links_unresolved=1", Codes(r.Failures));
        Assert.Null(r.KeptFile);
        Assert.Equal("", Codes(r.Warnings));                                         // exact warnings (none expected)

        Assert.Equal(before.Databases, await DatabaseNames());                                                       // the trial database was dropped even though the checks failed
        Assert.Equal(before.A, await Digest(Db(a.PhysicalDatabaseName)));
        Assert.Equal(before.B, await Digest(Db(b.PhysicalDatabaseName)));
        Assert.Equal(before.System, await Digest(SystemConnection));
        Assert.Equal(before.Live, Files(Storage));                                                                  // no live file written, changed or removed
    }
}
