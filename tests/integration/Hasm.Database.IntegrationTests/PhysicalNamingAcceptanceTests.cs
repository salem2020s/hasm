using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Naming;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Br = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using BA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using Cur = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Currency;
using Id = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Identity;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using Pr = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Profile;
using St = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Settings;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Acceptance scenario SRS 21.10 and the "upgrade" test category of SRS 18.4: non-descriptive PostgreSQL physical names are tested on (a) a NEW
/// database, (b) a schema UPGRADE of an existing, populated company database and (c) a backup RESTORE (an encrypted, signed company set restored on the
/// trial path with the offline recovery key), and in each case the schema's invariants stay intact. Scope (review M1 of #151): the system database is
/// checked for names on a new installation only; its upgrade compared with a new database, and the full-installation (system set) restore, are not
/// covered here, so scenario 21.10 stays PARTIAL. For every case the test proves, against the live PostgreSQL catalog (not the migration source):
/// <list type="bullet">
/// <item>every identifier in the public schema (relations, columns, constraints, indexes, triggers, functions, standalone types, sequences) is
/// opaque (<c>[tcfkr]_</c> + 8 hex, or an identity sequence composed only of such tokens) and every opaque token is a value of the code's schema map,
/// so no unmapped object exists; the only descriptive identifiers are the migration journal's (infrastructure, no business data);</item>
/// <item>the structural catalog (columns, types, collations, defaults, constraints with their parse trees, indexes, triggers, function bodies,
/// sequences) of an upgraded database is IDENTICAL to a freshly created database's, and a restored database's is identical to its source's after
/// the dump's own parse round trip, so neither is an approximation; the catalog holds no object kind this scenario does not inspect;</item>
/// <item>the runtime schema gate accepts the database (every migration applied unchanged, identity bound, exactly one active main branch);</item>
/// <item>the integrity rules are enforced by PostgreSQL even for a principal who knows every physical name: opacity is NOT treated as protection
/// against DBA analysis (SRS 21.10), so the probes below use the mapped names directly with the owner connection and are still refused.</item>
/// </list>
/// Restore runs in the backup collection (pg_dump/pg_restore of the server's major version on PATH); every database carries the disposable prefix.
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public partial class PhysicalNamingAcceptanceTests : CompanyRuntimeTestBase
{
    private const string Token = "[tcfkr]_[0-9a-f]{8}";
    private static readonly Regex OpaqueName = new($"^{Token}$", RegexOptions.CultureInvariant);
    private static readonly Regex IdentitySequenceName = new($"^({Token})_({Token})_seq$", RegexOptions.CultureInvariant);
    private static readonly Regex TokenPattern = new(Token, RegexOptions.CultureInvariant);

    /// <summary>The migration journal (MigrationRunner) is infrastructure shared by the system and every company database; it holds no business data.</summary>
    private static readonly HashSet<string> JournalIdentifiers = new(StringComparer.Ordinal)
        { SystemSchema.MigrationJournalTable, SystemSchema.MigrationJournalTable + "_pkey", "version", "name", "checksum", "applied_at" };

    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-pna-" + Guid.NewGuid().ToString("N"));

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private string Db(CompanyRecord c) => Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName);

    // ------------------------------------------------------------------------------------------ catalog helpers

    /// <summary>Every identifier the public schema holds, with its kind (system catalogs only; nothing is read from migration files).</summary>
    private static async Task<List<(string Kind, string Name)>> Identifiers(string connection)
    {
        const string sql = """
            SELECT 'relation:' || c.relkind::text, c.relname FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r','p','v','m','S','i','I','f','c')
            UNION ALL SELECT 'column', a.attname FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
                WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r','p','v','m','f') AND a.attnum > 0 AND NOT a.attisdropped
            UNION ALL SELECT 'constraint:' || co.contype::text, co.conname FROM pg_constraint co WHERE co.connamespace = 'public'::regnamespace
            UNION ALL SELECT 'trigger', t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relnamespace = 'public'::regnamespace AND NOT t.tgisinternal
            UNION ALL SELECT 'function', p.proname FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace
            UNION ALL SELECT 'type:' || t.typtype::text, t.typname FROM pg_type t WHERE t.typnamespace = 'public'::regnamespace AND t.typrelid = 0 AND t.typelem = 0
            """;
        var list = new List<(string, string)>();
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>
    /// The structural catalog of the public schema as sorted lines: relations, columns (by position among live columns, so a dump/restore that
    /// compacts dropped columns compares equal), types, collations, nullability, defaults, identity, constraints (text AND parse tree), indexes (and
    /// their validity), triggers, function definitions and sequences. Ownership, privileges, statistics and sequence positions are deliberately
    /// excluded (they are not schema). A CHECK constraint is compared by its deparsed text and by its stored parse tree (<c>conbin</c>, source
    /// positions removed), so a change of precedence, a cast or a constant is never hidden.
    /// <paramref name="roundTripChecks"/>: pg_dump writes each CHECK expression as PostgreSQL deparses it and pg_restore parses that text again; the
    /// deparser is not a fixed point (a BETWEEN becomes two comparisons, IN-list elements gain casts), so a RESTORED database is compared with the
    /// source's CHECK expressions after the SAME round trip: each source definition is parsed again (added as a NOT VALID constraint inside a
    /// transaction that is rolled back) and its text and parse tree are taken from that. The comparison stays exact.
    /// </summary>
    private static async Task<List<string>> Structure(string connection, bool roundTripChecks = false)
    {
        const string sql = """
            SELECT 'rel|' || c.relkind::text || '|' || c.relname FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r','p','v','m','S','i','I','f','c')
            UNION ALL SELECT 'col|' || c.relname || '|' || row_number() OVER (PARTITION BY c.oid ORDER BY a.attnum) || '|' || a.attname || '|' || format_type(a.atttypid, a.atttypmod)
                    || '|' || a.attnotnull || '|' || coalesce(pg_get_expr(d.adbin, d.adrelid), '') || '|' || a.attidentity::text || '|' || a.attgenerated::text
                    || '|' || coalesce(a.attcollation::regcollation::text, '')
                FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r','p','v','m','f') AND a.attnum > 0 AND NOT a.attisdropped
            UNION ALL SELECT 'con|' || coalesce(cl.relname, '') || '|' || co.conname || '|' || co.contype::text || '|' || co.condeferrable || '|' || co.condeferred || '|' || co.convalidated
                    || '|' || coalesce(co.conkey::text, '') || '|' || pg_get_constraintdef(co.oid) || '|' || coalesce(regexp_replace(co.conbin::text, ' :location -?[0-9]+', '', 'g'), '')
                FROM pg_constraint co LEFT JOIN pg_class cl ON cl.oid = co.conrelid WHERE co.connamespace = 'public'::regnamespace
            UNION ALL SELECT 'idx|' || pg_get_indexdef(i.indexrelid) || '|' || i.indisvalid || '|' || i.indisready FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid WHERE c.relnamespace = 'public'::regnamespace
            UNION ALL SELECT 'trg|' || pg_get_triggerdef(t.oid) || '|' || t.tgenabled::text FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                WHERE c.relnamespace = 'public'::regnamespace AND NOT t.tgisinternal
            UNION ALL SELECT 'fn|' || pg_get_functiondef(p.oid) FROM pg_proc p WHERE p.pronamespace = 'public'::regnamespace
            UNION ALL SELECT 'typ|' || t.typname || '|' || t.typtype::text FROM pg_type t WHERE t.typnamespace = 'public'::regnamespace AND t.typrelid = 0 AND t.typelem = 0
            UNION ALL SELECT 'seq|' || c.relname || '|' || s.seqtypid::regtype || '|' || s.seqincrement || '|' || s.seqmin || '|' || s.seqmax || '|' || s.seqcycle
                FROM pg_sequence s JOIN pg_class c ON c.oid = s.seqrelid WHERE c.relnamespace = 'public'::regnamespace
            """;
        var lines = new List<string>();
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) lines.Add(r.GetString(0));
        if (roundTripChecks)
        {
            var checks = new List<(string Table, string Name, string Expression)>();
            await using (var list = new NpgsqlCommand("""
                SELECT cl.relname, co.conname, pg_get_constraintdef(co.oid) FROM pg_constraint co JOIN pg_class cl ON cl.oid = co.conrelid
                WHERE co.connamespace = 'public'::regnamespace AND co.contype = 'c'
                """, conn))
            await using (var r = await list.ExecuteReaderAsync())
                while (await r.ReadAsync()) checks.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            foreach (var (table, name, definition) in checks)
            {
                var expression = definition.StartsWith("CHECK ", StringComparison.Ordinal) && definition.EndsWith(')') ? definition[6..] : throw new InvalidOperationException(definition);
                await using var tx = await conn.BeginTransactionAsync();
                await using (var add = new NpgsqlCommand($"ALTER TABLE {table} ADD CONSTRAINT zz_round_trip CHECK {expression} NOT VALID", conn, tx)) await add.ExecuteNonQueryAsync();
                await using var read = new NpgsqlCommand(
                    "SELECT pg_get_constraintdef(oid) || '|' || regexp_replace(conbin::text, ' :location -?[0-9]+', '', 'g') FROM pg_constraint WHERE conname = 'zz_round_trip'", conn, tx);
                var parsed = (string)(await read.ExecuteScalarAsync())!;
                await tx.RollbackAsync();
                var at = lines.FindIndex(l => l.StartsWith($"con|{table}|{name}|c|", StringComparison.Ordinal));
                var line = lines[at];
                var head = string.Join('|', line.Split('|')[..8]);                                                 // con|table|name|type|deferrable|deferred|validated|conkey
                lines[at] = head + "|" + parsed.Replace(" NOT VALID|", "|", StringComparison.Ordinal);
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>
    /// The catalog holds no object kind that <see cref="Identifiers"/> and <see cref="Structure"/> do not inspect (review L1 of #151): no extra schema, no comment on a
    /// public object, no row-level security policy, no rule other than a view's, no event trigger, no extended statistics, no extension other than plpgsql.
    /// A new kind fails here first, so the scenario's coverage can never silently shrink.
    /// </summary>
    private static async Task AssertNoUncoveredObjectKinds(string connection, string what)
    {
        (string Kind, string Sql)[] checks =
        [
            ("schema", "SELECT count(*) FROM pg_namespace WHERE nspname NOT IN ('public', 'pg_catalog', 'information_schema') AND nspname NOT LIKE 'pg\\_toast%' AND nspname NOT LIKE 'pg\\_temp%'"),
            ("comment", """
                SELECT count(*) FROM pg_description d WHERE d.objoid IN (SELECT oid FROM pg_class WHERE relnamespace = 'public'::regnamespace UNION SELECT oid FROM pg_proc WHERE pronamespace = 'public'::regnamespace
                    UNION SELECT oid FROM pg_constraint WHERE connamespace = 'public'::regnamespace UNION SELECT oid FROM pg_type WHERE typnamespace = 'public'::regnamespace
                    UNION SELECT t.oid FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relnamespace = 'public'::regnamespace)
                """),
            ("policy", "SELECT count(*) FROM pg_policy"),
            ("rule", "SELECT count(*) FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class WHERE c.relnamespace = 'public'::regnamespace AND r.rulename <> '_RETURN'"),
            ("event trigger", "SELECT count(*) FROM pg_event_trigger"),
            ("extended statistics", "SELECT count(*) FROM pg_statistic_ext WHERE stxnamespace = 'public'::regnamespace"),
            ("extension", "SELECT count(*) FROM pg_extension WHERE extname <> 'plpgsql'"),
        ];
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        foreach (var (kind, sql) in checks)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            Assert.True((long)(await cmd.ExecuteScalarAsync())! == 0, $"{what}: the catalog holds object kind '{kind}', which this scenario does not inspect yet");
        }
    }

    /// <summary>Equal structure, reported as the first lines that differ (never as two opaque hashes).</summary>
    private static void AssertSameStructure(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what, string reference = "a freshly created database")
    {
        var missing = expected.Except(actual, StringComparer.Ordinal).Take(5).ToList();
        var extra = actual.Except(expected, StringComparer.Ordinal).Take(5).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0 && expected.Count == actual.Count,
            $"{what}: catalog differs from {reference}. Missing: [{string.Join(" ; ", missing)}] Extra: [{string.Join(" ; ", extra)}]");
    }

    /// <summary>
    /// Every identifier is opaque and mapped: a plain opaque token, or an identity sequence named by PostgreSQL from two opaque tokens; every token
    /// is a value of <paramref name="map"/>; the journal's identifiers are the only descriptive ones. Returns the opaque tokens found.
    /// </summary>
    private static HashSet<string> AssertOpaqueAndMapped(IEnumerable<(string Kind, string Name)> identifiers, IReadOnlyDictionary<string, string> map, string what)
    {
        var mapped = map.Values.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var descriptive = new List<string>();
        var unmapped = new List<string>();
        foreach (var (kind, name) in identifiers)
        {
            if (JournalIdentifiers.Contains(name)) continue;
            var sequence = kind == "relation:S" ? IdentitySequenceName.Match(name) : Match.Empty;
            if (!OpaqueName.IsMatch(name) && !sequence.Success) { descriptive.Add($"{kind} {name}"); continue; }
            foreach (Match m in TokenPattern.Matches(name))
            {
                found.Add(m.Value);
                if (!mapped.Contains(m.Value)) unmapped.Add($"{kind} {name}");
            }
        }
        Assert.True(descriptive.Count == 0, $"{what}: descriptive physical identifiers: {string.Join(", ", descriptive.Take(10))}");
        Assert.True(unmapped.Count == 0, $"{what}: opaque identifiers that the schema map does not know: {string.Join(", ", unmapped.Take(10))}");
        return found;
    }

    /// <summary>
    /// Integrity rules hold for a principal who knows every physical name (the owner connection, using the mapped names directly): opacity is not
    /// the protection. Each probe runs in its own transaction and must be refused by the database with the rule's own message.
    /// </summary>
    private static async Task AssertIntegrityRulesEnforced(string connection, string what)
    {
        (string Sql, string Message)[] probes =
        [
            ($"UPDATE {Id.Table} SET {Id.CreatedAtUtc} = now()", "company identity is immutable"),
            ($"INSERT INTO {BA.Table} ({BA.StreamKey}, {BA.Seq}, {BA.CompanyId}) VALUES ('business:forged', 1, gen_random_uuid())", "audit event does not belong to this company"),
            ($"DELETE FROM {Br.Table}", "branches cannot be deleted"),
            ($"UPDATE {Br.Table} SET {Br.NameEn} = 'renamed' WHERE {Br.IsMain}", "revision must advance by one"),
            // review L2 of #151: the main-branch rule itself (revision and epoch advanced correctly, so only the immediate CHECK can refuse it)
            ($"UPDATE {Br.Table} SET {Br.IsActive} = false, {Br.Revision} = {Br.Revision} + 1, {Br.ActivationEpoch} = {Br.ActivationEpoch} + 1 WHERE {Br.IsMain}", $"\"{Br.CheckMain}\""),
            ($"DELETE FROM {Cur.Table} WHERE {Cur.Code} = 'USD'", "currency reference rows cannot be deleted"),
        ];
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        foreach (var (sql, message) in probes)
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.True(ex.MessageText.Contains(message, StringComparison.Ordinal), $"{what}: '{sql}' was refused with '{ex.MessageText}', expected '{message}'");
            await tx.RollbackAsync();
        }
    }

    /// <summary>The runtime schema gate (every migration applied unchanged, identity bound to <paramref name="companyId"/>, exactly one active main branch).</summary>
    private static async Task AssertSchemaGateAccepts(string connection, Guid companyId)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await CompanySchemaGate.EnsureCompatibleAsync(conn, companyId, MigrationScript.LoadEmbeddedCompany(), allowOverprivileged: true);
    }

    /// <summary>Every row of the named tables (or of every public base table), as a hash per table.</summary>
    private static async Task<string> Digest(string connection, params string[] tables)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        if (tables.Length == 0)
        {
            var all = new List<string>();
            await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1", conn))
            await using (var r = await list.ExecuteReaderAsync()) while (await r.ReadAsync()) all.Add(r.GetString(0));
            tables = [.. all];
        }
        var sb = new StringBuilder();
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(x::text, ',' ORDER BY x::text)), '') FROM {table} x", conn);
            sb.Append(table).Append('=').Append((string)(await cmd.ExecuteScalarAsync())!).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Company configuration written the way the database accepts it at company level 3 (no service needed, nothing assumed of later levels).</summary>
    private static async Task Configure(string connection)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {Pr.Table} ({Pr.NameAr}, {Pr.NameEn}, {Pr.CountryCode}, {Pr.TaxNo}, {Pr.City}, {Pr.UpdatedAtUtc}) VALUES ('شركة الاختبار', 'Test Co', 'SA', '300000000000003', 'الرياض', now()); " +
            $"INSERT INTO {St.Table} ({St.DefaultLanguage}, {St.DateFormat}, {St.NumeralSystem}, {St.TimeZone}, {St.UpdatedAtUtc}) VALUES ('ar', 'dd/MM/yyyy', 'arabic-indic', 'Asia/Riyadh', now()); " +
            $"INSERT INTO {CC.Table} ({CC.Code}, {CC.IsBase}, {CC.Enabled}, {CC.UpdatedAtUtc}) VALUES ('SAR', true, true, now()), ('USD', false, true, now()); " +
            $"UPDATE {Pr.Table} SET {Pr.Phone} = '+966110000000', {Pr.Revision} = {Pr.Revision} + 1", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------------------------------ (a) new database

    [PostgresFact]
    public async Task A_new_database_has_only_opaque_mapped_physical_names_and_its_integrity_rules_hold_against_a_name_aware_principal()
    {
        await StartCompanies();

        // Names the installation generates are opaque by construction (the test databases carry the disposable prefix by injection instead).
        Assert.Matches("^d[0-9a-f]{32}$", PhysicalIdentifier.GenerateDatabaseName());
        Assert.NotEqual(PhysicalIdentifier.GenerateDatabaseName(), PhysicalIdentifier.GenerateDatabaseName());
        var c = await ReadyCompany();
        Assert.Matches("^hasm_rt_[0-9a-f]{32}$", CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName));
        Assert.DoesNotContain(c.Id.ToString("N"), CompanyRuntimeRole.NameFor(c.PhysicalDatabaseName));

        // Company database: opaque, mapped, and COMPLETE (every mapped company identifier exists in a new database).
        var company = await Identifiers(Db(c));
        var found = AssertOpaqueAndMapped(company, CompanySchema.Map, "new company database");
        var absent = CompanySchema.Map.Values.Where(v => !found.Contains(v)).Order(StringComparer.Ordinal).ToList();
        Assert.True(absent.Count == 0, $"mapped company identifiers missing from a new database: {string.Join(", ", absent.Take(10))}");

        // System database: the same rule with its own map.
        var system = await Identifiers(Options.BuildSystemConnectionString(admin: true));
        var systemFound = AssertOpaqueAndMapped(system, SystemSchema.Map, "new system database");
        var systemAbsent = SystemSchema.Map.Values.Where(v => !systemFound.Contains(v)).Order(StringComparer.Ordinal).ToList();
        Assert.True(systemAbsent.Count == 0, $"mapped system identifiers missing from a new database: {string.Join(", ", systemAbsent.Take(10))}");

        await AssertNoUncoveredObjectKinds(Db(c), "new company database");
        await AssertNoUncoveredObjectKinds(Options.BuildSystemConnectionString(admin: true), "new system database");
        await AssertSchemaGateAccepts(Db(c), c.Id);
        await AssertIntegrityRulesEnforced(Db(c), "new company database");
    }

    // ------------------------------------------------------------------------------------------ (b) schema upgrade

    [PostgresFact]
    public async Task An_existing_populated_company_database_upgraded_from_level_3_keeps_its_data_and_becomes_identical_to_a_new_one()
    {
        await StartCompanies();
        var all = MigrationScript.LoadEmbeddedCompany();
        var fresh = await ReadyCompany();
        var legacy = await ReadyCompany([.. all.Where(s => s.Version <= 3)]);
        Assert.Equal(3L, await InCompany<long>(legacy.PhysicalDatabaseName, $"SELECT count(*) FROM {SystemSchema.MigrationJournalTable}"));
        Assert.Equal(0L, await InCompany<long>(legacy.PhysicalDatabaseName, $"SELECT count(*) FROM information_schema.tables WHERE table_name = '{Br.Table}'"));

        await Configure(Db(legacy));
        AssertOpaqueAndMapped(await Identifiers(Db(legacy)), CompanySchema.Map, "company database at level 3");
        string[] level3Tables = [Id.Table, Pr.Table, St.Table, Cur.Table, CC.Table];
        var before = await Digest(Db(legacy), level3Tables);

        var outcomes = await new CompanyMigrator(Options, Registry).MigrateAsync();
        var outcome = Assert.Single(outcomes, o => o.CompanyId == legacy.Id);
        Assert.Equal(CompanyMigrationStatus.Migrated, outcome.Status);
        Assert.Equal(all.Where(s => s.Version > 3).Select(s => s.Version), outcome.Applied);
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(outcomes, o => o.CompanyId == fresh.Id).Status);

        // Data intact: every row of every level-3 table is unchanged; the upgrade added its own seed (one active main branch) and nothing else there.
        Assert.Equal(before, await Digest(Db(legacy), level3Tables));
        Assert.Equal(1L, await InCompany<long>(legacy.PhysicalDatabaseName, $"SELECT count(*) FROM {Br.Table} WHERE {Br.IsMain} AND {Br.IsActive}"));

        // Schema intact: names opaque and mapped, and the catalog is IDENTICAL to a database created new at the latest level.
        AssertOpaqueAndMapped(await Identifiers(Db(legacy)), CompanySchema.Map, "upgraded company database");
        AssertSameStructure(await Structure(Db(fresh)), await Structure(Db(legacy)), "upgraded company database");
        await AssertNoUncoveredObjectKinds(Db(legacy), "upgraded company database");
        await AssertSchemaGateAccepts(Db(legacy), legacy.Id);
        await AssertIntegrityRulesEnforced(Db(legacy), "upgraded company database");

        // A second run changes nothing.
        Assert.Equal(CompanyMigrationStatus.UpToDate, Assert.Single(await new CompanyMigrator(Options, Registry).MigrateAsync(legacy.Id)).Status);
        Assert.Equal(before, await Digest(Db(legacy), level3Tables));
    }

    // ------------------------------------------------------------------------------------------ (c) backup restore (trial path)

    [PostgresFact]
    public async Task A_backup_restored_on_the_trial_path_has_the_same_opaque_names_schema_data_and_integrity_rules()
    {
        await StartCompanies();
        // review M1 of #151: the ENCRYPTED, signed path (slice 5), restored on the trial path with the offline recovery key, as production does
        var storage = Path.Combine(work, "storage"); var keys = Path.Combine(work, "keys"); var signing = Path.Combine(work, "signing");
        Directory.CreateDirectory(storage);
        FileKekRing.Generate(keys, "k1");
        const string passphrase = "an offline recovery passphrase for tests";
        var pair = RecoveryKeys.Generate(passphrase);
        var (signingId, signingPem) = BackupSigningKeys.Generate(signing);
        var material = new BackupMaterial(RecoveryPublicKey.FromPem(pair.PublicKeyPem), BackupSigningKeys.Load(signing, signingId, true));
        var trust = new SigningTrust([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Active)]);
        var service = new CompanyBackupService(Options, new AttachmentOptions { StorageDirectory = storage },
            new EncryptionOptions { KeyDirectory = keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true }, nameGenerator: NewTestName);

        var c = await ReadyCompany();
        await Configure(Db(c));
        await InCompany(c.PhysicalDatabaseName,
            $"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) " +
            $"VALUES (gen_random_uuid(), 'C-001', 'عميل نقدي', true, 1, 1, gen_random_uuid(), now(), now()), (gen_random_uuid(), 'S-001', 'Supplier', true, 1, 1, gen_random_uuid(), now(), now())");
        var source = (Structure: await Structure(Db(c), roundTripChecks: true), Data: await Digest(Db(c)));

        var set = Path.Combine(work, "set");
        await service.BackupCompanyEncryptedAsync(c.Id, set, material);
        using var recovery = RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, passphrase);
        var r = await service.TrialRestoreAsync(set, c.Id, Path.Combine(work, "trial-att"), keep: true, new RestoreKeys(recovery, trust));
        Assert.True(r.Verdict == TrialRestoreVerdict.Ok, string.Join(", ", r.Failures.Concat(r.Warnings).Select(f => f.Code)));
        Assert.True(r.Encrypted);
        var kept = CompanyBackupService.ReadKeptFile(r.KeptFile!)!;
        Assert.NotEqual(c.PhysicalDatabaseName, kept.Database);                                                    // a NEW database, never the live one
        var restored = Options.BuildCompanyAdminConnectionString(kept.Database);

        AssertOpaqueAndMapped(await Identifiers(restored), CompanySchema.Map, "restored company database");
        AssertSameStructure(source.Structure, await Structure(restored), "restored company database", "its source after the dump's own parse round trip");
        await AssertNoUncoveredObjectKinds(restored, "restored company database");
        Assert.Equal(source.Data, await Digest(restored));
        await AssertSchemaGateAccepts(restored, c.Id);
        await AssertIntegrityRulesEnforced(restored, "restored company database");

        // review M2 of #151: a change of precedence or of a cast in a restored CHECK is detected (the comparison is exact, not normalised)
        (string Table, string Name, string Changed)[] mutations =
        [
            (Pr.Table, Pr.CheckName, $"{Pr.NameAr} IS NOT NULL OR {Pr.NameEn} IS NOT NULL AND ({Pr.NameAr} IS NULL OR char_length(btrim({Pr.NameAr})) BETWEEN 1 AND 200) " +
                                     $"AND ({Pr.NameEn} IS NULL OR char_length(btrim({Pr.NameEn})) BETWEEN 1 AND 200)"),          // precedence regrouped
            (Pr.Table, Pr.CheckRevision, $"{Pr.Revision} > 0::bigint"),                                                             // a cast added
        ];
        foreach (var (table, name, changed) in mutations)
        {
            var original = await InCompany<string>(kept.Database, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = '{name}'");
            await InCompany(kept.Database, $"ALTER TABLE {table} DROP CONSTRAINT {name}; ALTER TABLE {table} ADD CONSTRAINT {name} CHECK ({changed})");
            Assert.NotNull(await Record.ExceptionAsync(async () => AssertSameStructure(source.Structure, await Structure(restored), "mutated restored database", "its source")));
            await InCompany(kept.Database, $"ALTER TABLE {table} DROP CONSTRAINT {name}; ALTER TABLE {table} ADD CONSTRAINT {name} {original}");
        }
        AssertSameStructure(source.Structure, await Structure(restored), "restored company database after the controls", "its source after the dump's own parse round trip");

        await service.DropTrialRestoreAsync(r.KeptFile!);
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_database WHERE datname = @n", ("n", kept.Database)));
        Assert.Equal(source.Data, await Digest(Db(c)));                                                             // the live company was never touched
    }
}
