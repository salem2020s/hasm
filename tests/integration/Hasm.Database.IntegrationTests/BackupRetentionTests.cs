using System.Text;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using E = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using FP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using FY = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using H = Hasm.Infrastructure.SystemDatabase.SystemSchema.BackupLegalHold;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LegalHold;
using P = Hasm.Infrastructure.SystemDatabase.SystemSchema.BackupRetentionPin;
using ST = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Settings;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 slice 6 on the disposable PostgreSQL server (D-086; D-130..D-139): the retention preview over real encrypted runs classified per company by its own fiscal calendar and time zone, backup
/// legal holds (audited, append-only, never released), automatic deletion INACTIVE through every non-test entry point, the deletion engine through the internal test seam only (exactly the eligible runs,
/// audit-first, tombstones, a remaining run still recovers), and the run and restore locks. Runs use a controllable clock; the recovery tests name their disposable databases with the test prefix, the
/// Hasm.Admin commands restore nothing. Runs in the backup collection (no parallel class).
/// </summary>
[Collection(CompanyBackupSerialCollection.Name)]
public class BackupRetentionTests : PostgresTestBase
{
    private const string Pass = "an offline recovery passphrase for tests";
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-brr-" + Guid.NewGuid().ToString("N"));
    private string Storage => Path.Combine(work, "storage");
    private string Keys => Path.Combine(work, "keys");
    private string AuditKeys => Path.Combine(work, "audit-keys");
    private string SigningDir => Path.Combine(work, "signing");
    private string Root => Path.Combine(work, "runs");
    private CompanyRegistry registry = null!;
    private RecoveryKeys.GeneratedPair pair = null!;
    private string publicKeyFile = null!, signingId = null!, signingPem = null!;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public override async Task DisposeAsync()
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private EncryptionOptions Encryption => new() { KeyDirectory = Keys, ActiveKeyId = "k1", AllowInsecureFilePermissions = true };
    private AttachmentOptions Attachments => new() { StorageDirectory = Storage };
    private SigningTrust Trust => new([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Active)]);
    private BackupMaterial Material() => new(RecoveryPublicKey.FromPem(pair.PublicKeyPem), BackupSigningKeys.Load(SigningDir, signingId, true));
    private BackupOptions Backup => new() { RecoveryPublicKeyFile = publicKeyFile, SigningKeyDirectory = SigningDir, SigningKeyId = signingId, AllowInsecureFilePermissions = true };
    private static RetentionOptions Retention => new() { InstallationTimeZone = "UTC" };
    private string SystemConnection => Options.BuildSystemConnectionString(admin: true);
    private AuditTrail Trail() => AuditTrail.FromOptions(new AuditOptions { KeyDirectory = AuditKeys, ActiveKeyId = "a1", CheckpointDirectory = Path.Combine(work, "checkpoints"), AllowInsecureFilePermissions = true }, TimeProvider.System);

    private Dictionary<string, string?> Env() => new()
    {
        ["Hasm__Attachments__StorageDirectory"] = Storage, ["Hasm__Encryption__KeyDirectory"] = Keys, ["Hasm__Encryption__ActiveKeyId"] = "k1", ["Hasm__Encryption__AllowInsecureFilePermissions"] = "true",
        ["Hasm__Audit__KeyDirectory"] = AuditKeys, ["Hasm__Audit__ActiveKeyId"] = "a1", ["Hasm__Audit__CheckpointDirectory"] = Path.Combine(work, "checkpoints"),
        ["Hasm__Backup__RecoveryPublicKeyFile"] = publicKeyFile, ["Hasm__Backup__SigningKeyDirectory"] = SigningDir, ["Hasm__Backup__SigningKeyId"] = signingId,
        ["Hasm__Backup__AllowInsecureFilePermissions"] = "true", ["Hasm__Backup__Retention__InstallationTimeZone"] = "UTC",
    };

    private async Task Init()
    {
        Directory.CreateDirectory(work); Directory.CreateDirectory(Storage);
        FileKekRing.Generate(Keys, "k1");
        FileAuditKeyRing.Generate(AuditKeys, "a1");
        pair = RecoveryKeys.Generate(Pass);
        RecoveryKit.Write(Path.Combine(work, "recovery"), pair);
        publicKeyFile = Path.Combine(work, "recovery", pair.KeyId + ".pub.pem");
        (signingId, signingPem) = BackupSigningKeys.Generate(SigningDir);
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        registry = new CompanyRegistry(SystemConnection, NewTestName);
    }

    /// <summary>A Ready company with, optionally, a configured time zone and a calendar fiscal year 2026 (monthly periods) in that zone.</summary>
    private async Task<CompanyRecord> NewCompany(string? zone)
    {
        var c = await registry.RegisterPendingAsync();
        await new CompanyProvisioner(Options, registry).ProvisionAsync(c.Id);
        c = (await registry.GetAsync(c.Id))!;
        if (zone is null) return c;
        var db = Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName);
        await Exec(db, $"INSERT INTO {ST.Table} ({ST.TimeZone}, {ST.UpdatedAtUtc}) VALUES ('{zone}', now()) ON CONFLICT ({ST.Singleton}) DO UPDATE SET {ST.TimeZone} = EXCLUDED.{ST.TimeZone}, {ST.Revision} = {ST.Table}.{ST.Revision} + 1, {ST.UpdatedAtUtc} = now()");
        // a fiscal year and its periods in ONE transaction (the calendar is checked at commit)
        var year = Guid.NewGuid();
        var sql = new StringBuilder("BEGIN;\n");
        sql.Append($"INSERT INTO {FY.Table} ({FY.Id}, {FY.Name}, {FY.StartDate}, {FY.EndDate}, {FY.PeriodType}, {FY.TimeZone}, {FY.CreatedAtUtc}, {FY.CreatedBy}) VALUES ('{year}', 'FY2026', '2026-01-01', '2026-12-31', 1, '{zone}', now(), gen_random_uuid());\n");
        for (var m = 1; m <= 12; m++)
        {
            var start = new DateOnly(2026, m, 1);
            sql.Append($"INSERT INTO {FP.Table} ({FP.YearId}, {FP.Number}, {FP.StartDate}, {FP.EndDate}, {FP.CreatedAtUtc}) VALUES ('{year}', {m}, '{start:yyyy-MM-dd}', '{start.AddMonths(1).AddDays(-1):yyyy-MM-dd}', now());\n");
        }
        sql.Append("COMMIT;");
        await Exec(db, sql.ToString());
        return c;
    }

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

    private async Task<string?> Refused(string sql)
    {
        try { await Exec(SystemConnection, sql); return null; }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    private async Task<long> AuditEvents(short type) => await Scalar<long>(SystemConnection, $"SELECT count(*) FROM {E.Table} WHERE {E.EventType} = {type}");

    /// <summary>One encrypted run at the clock's time.</summary>
    private async Task<string> RunAt(Clock clock, DateTimeOffset at)
    {
        clock.Now = at;
        var (report, dir) = await new BackupRunService(Options, Attachments, Encryption, clock).RunAsync(Root, Material());
        Assert.Equal("ok", report.Verdict);
        return Path.GetFileName(dir);
    }

    /// <summary>A passed recovery test of one run (its proof makes the recovery key tested, D-133).</summary>
    private async Task Prove(string runId)
    {
        using var priv = RecoveryPrivateKey.FromEncryptedPem(pair.EncryptedPrivateKeyPem, Pass);
        var t = await new RecoveryTestService(Options, Attachments, Encryption, nameGenerator: NewTestName).RunAsync(Path.Combine(Root, runId), new RestoreKeys(priv, Trust), null, Path.Combine(work, "rt-" + runId));
        Assert.True(t.Passed, string.Join(", ", t.Failures.Select(f => f.Code)));
        RecoveryTestService.WriteProof(Path.Combine(Root, runId), t, DateTimeOffset.UtcNow);
    }

    [PostgresFact]
    public async Task The_preview_classifies_each_company_by_its_own_fiscal_calendar_and_time_zone_and_carries_the_key_inventory()
    {
        await Init();
        var riyadh = await NewCompany("Asia/Riyadh");                                                                  // UTC+3
        var unset = await NewCompany(null);                                                                           // no time zone configured (review L1 of #130)
        var clock = new Clock();
        var r1 = await RunAt(clock, new DateTimeOffset(2026, 3, 30, 22, 30, 0, TimeSpan.Zero));                       // Riyadh 31 March 01:30
        var r2 = await RunAt(clock, new DateTimeOffset(2026, 3, 31, 21, 30, 0, TimeSpan.Zero));                       // Riyadh 1 April 00:30: after the March period end
        var r3 = await RunAt(clock, new DateTimeOffset(2026, 4, 2, 2, 0, 0, TimeSpan.Zero));
        await Prove(r3);
        Assert.Contains("\"version\": 2", File.ReadAllText(Path.Combine(Root, r3, SignedDocuments.RunStatusFileName)));   // D-138 key inventory

        var (plan, inputs) = await new RetentionService(Options, Retention, clock).PlanAsync(Root, Trust);
        RunDecision D(string id) => plan.Runs.Single(r => r.RunId == id);
        var part = riyadh.Id.ToString("D");
        Assert.Contains("period_end", D(r1).Tags[part]);                                                               // the last run before the end of March in Riyadh
        Assert.Contains("period_end", D(r2).Tags[part]);                                                               // the first run after it
        Assert.All(new[] { r1, r2, r3 }, id => Assert.Contains("unknown", D(id).Tags[unset.Id.ToString("D")]));
        Assert.All(new[] { r1, r2, r3 }, id => Assert.Contains("classification_unknown", D(id).Reasons));
        Assert.Empty(plan.Eligible);
        Assert.True(plan.KeyInventoryComplete);
        Assert.Equal([pair.KeyId], plan.RecoveryKeysNeeded);
        Assert.True(inputs.Companies.Single(c => c.CompanyId == riyadh.Id) is { Readable: true, TimeZoneId: "Asia/Riyadh", HoldCode: null });   // no attachment-hold tables, no holds

        // the command: audited 2014, prints run ids and reasons, never a physical name
        var preview = await AdminTool.RunWith(Options, Env(), "backup-retention-plan", Root, "--json", Path.Combine(work, "plan.json"));
        Assert.True(preview.Exit == 0, preview.All);
        Assert.Contains("classification_unknown", preview.Out);
        Assert.DoesNotContain(riyadh.PhysicalDatabaseName, preview.All);
        Assert.Equal(1, await AuditEvents(BackupAuditEvents.RetentionEvaluated));
        Assert.Contains("\"planHash\"", File.ReadAllText(Path.Combine(work, "plan.json")));
        var below = await AdminTool.RunWith(Options, new Dictionary<string, string?>(Env()) { ["Hasm__Backup__Retention__DailyKeep"] = "6" }, "backup-retention-plan", Root);
        Assert.Equal(3, below.Exit);
        Assert.Contains("retention_below_minimum", below.Err);
    }

    [PostgresFact]
    public async Task Backup_legal_holds_are_audited_append_only_never_released_by_the_tool_and_honoured()
    {
        await Init();
        var c = await NewCompany("UTC");
        var clock = new Clock();
        var r1 = await RunAt(clock, new DateTimeOffset(2026, 3, 2, 2, 0, 0, TimeSpan.Zero));
        var place = await AdminTool.RunWith(Options, Env(), "backup-hold-place", "--company", c.Id.ToString(), "--from", "2026-03-01", "--to", "2026-03-31", "--reason", "litigation 2026/17");
        Assert.True(place.Exit == 0, place.All);
        Assert.Equal(1, await AuditEvents(BackupAuditEvents.LegalHoldPlaced));
        Assert.Equal(0L, await Scalar<long>(SystemConnection, $"SELECT count(*) FROM {E.Table} WHERE {E.Detail} LIKE '%litigation%'"));    // the reason is in the hold row, not in the event
        var list = await AdminTool.RunWith(Options, Env(), "backup-hold-list");
        Assert.Contains("ACTIVE", list.Out);
        Assert.Equal(2, (await AdminTool.RunWith(Options, Env(), "backup-hold-release", "x")).Exit);                // no release command (D-134)
        Assert.Equal(2, (await AdminTool.RunWith(Options, Env(), "backup-hold-place", "--company", c.Id.ToString(), "--run", r1, "--reason", "both scopes")).Exit);

        var (plan, _) = await new RetentionService(Options, Retention, clock).PlanAsync(Root, Trust);
        Assert.Contains("legal_hold", plan.Runs.Single(r => r.RunId == r1).Reasons);

        // the database guards: never removed, nothing but a one-time release changes, pins append-only
        const string Raised = "P0001";
        Assert.Equal(Raised, await Refused($"UPDATE {H.Table} SET {H.Reason} = 'other'"));
        Assert.Equal(Raised, await Refused($"DELETE FROM {H.Table}"));
        Assert.Equal(Raised, await Refused($"TRUNCATE {H.Table}"));
        Assert.Equal(Raised, await Refused($"INSERT INTO {H.Table} ({H.Id}, {H.Scope}, {H.RunId}, {H.Reason}, {H.PlacedBy}, {H.PlacedAtUtc}, {H.ReleasedBy}, {H.ReleasedAtUtc}, {H.ReleaseReason}) VALUES (gen_random_uuid(), 1, '{r1}', 'why', 'os:x', now(), 'os:y', now(), 'gone')"));
        Assert.Null(await Refused($"UPDATE {H.Table} SET {H.ReleasedBy} = 'os:approver', {H.ReleasedAtUtc} = now(), {H.ReleaseReason} = 'matter closed'"));
        Assert.Equal(Raised, await Refused($"UPDATE {H.Table} SET {H.ReleaseReason} = 'changed'"));
        Assert.Null(await Refused($"INSERT INTO {P.Table} ({P.Id}, {P.RunId}, {P.Kind}, {P.Reason}, {P.CreatedBy}, {P.CreatedAtUtc}, {P.ExpiresAtUtc}) VALUES (gen_random_uuid(), '{r1}', 1, 'cutover safety', 'os:x', now(), now() + interval '1 day')"));
        Assert.Equal(Raised, await Refused($"UPDATE {P.Table} SET {P.ExpiresAtUtc} = now()"));
        Assert.Equal(Raised, await Refused($"DELETE FROM {P.Table}"));
        Assert.Equal(Raised, await Refused($"TRUNCATE {P.Table}"));
        var (pinned, _) = await new RetentionService(Options, Retention, clock).PlanAsync(Root, Trust);
        Assert.DoesNotContain("legal_hold", pinned.Runs.Single(r => r.RunId == r1).Reasons);                         // released
        Assert.Contains("recovery_chain", pinned.Runs.Single(r => r.RunId == r1).Reasons);                           // pinned (clock in March, pin expires tomorrow)
    }

    [PostgresFact]
    public async Task An_active_phase_r_attachment_legal_hold_of_any_scope_keeps_every_run_of_the_company()
    {
        // review H2 of #134 (D-134, D-114): R1's company LegalHold table is read; any ACTIVE hold, whatever its scope, keeps every run containing the company (fail closed, never ignored)
        await Init();
        var held = await NewCompany("UTC");
        var free = await NewCompany("UTC");
        var clock = new Clock();
        var runs = new List<string>();
        for (var d = 2; d <= 13; d++) runs.Add(await RunAt(clock, new DateTimeOffset(2026, 3, d, 2, 0, 0, TimeSpan.Zero)));
        await Prove(runs[^1]);
        clock.Now = new DateTimeOffset(2026, 3, 13, 3, 0, 0, TimeSpan.Zero);
        var service = new RetentionService(Options, Retention, clock);
        var (before, _) = await service.PlanAsync(Root, Trust);
        Assert.NotEmpty(before.Eligible);
        Assert.All(before.Runs, r => Assert.DoesNotContain("attachment_hold", r.Reasons));

        // a RECORD-scoped hold (scope 2) in one company, placed with valid SQL as R1's own tests do
        var db = Options.BuildCompanyAdminConnectionString(held.PhysicalDatabaseName);
        await Exec(db, $"INSERT INTO {LH.Table} ({LH.Id}, {LH.Scope}, {LH.SubjectKind}, {LH.SubjectId}, {LH.Reason}, {LH.PlacedBy}, {LH.PlacedAtUtc}, {LH.Revision}, {LH.UpdatedAtUtc}) " +
                       $"VALUES (gen_random_uuid(), 2, 'local_party', gen_random_uuid(), 'pending litigation', gen_random_uuid(), now(), 1, now())");
        var (after, inputs) = await service.PlanAsync(Root, Trust);
        Assert.Equal("attachment_hold", inputs.Companies.Single(c => c.CompanyId == held.Id).HoldCode);
        Assert.Null(inputs.Companies.Single(c => c.CompanyId == free.Id).HoldCode);
        Assert.All(after.Runs, r => Assert.Contains("attachment_hold", r.Reasons));                                  // every run contains the held company
        Assert.Empty(after.Eligible);

        // and the deletion engine (through the test seam) deletes nothing while the hold is active
        var result = await service.ApplyCoreAsync(Root, Trust, Backup, (_, _, _) => Task.CompletedTask, "os:test", RetentionGateOverride.ForTests(), default);
        Assert.Empty(result.Deleted);
        Assert.All(runs, id => Assert.True(Directory.Exists(Path.Combine(Root, id))));

        // review L5 of #134: a hold table that is missing at an R1+ migration level (dropped or renamed) is never read as "no hold"
        await Exec(Options.BuildCompanyAdminConnectionString(free.PhysicalDatabaseName), $"ALTER TABLE {LH.Table} RENAME TO t_renamed_hold");
        var (drift, driftInputs) = await service.PlanAsync(Root, Trust);
        Assert.Equal("hold_check_failed", driftInputs.Companies.Single(c => c.CompanyId == free.Id).HoldCode);
        Assert.All(drift.Runs, r => Assert.Contains("hold_check_failed", r.Reasons));
    }

    [PostgresFact]
    public async Task Automatic_deletion_is_inactive_audited_and_deletes_nothing()
    {
        await Init();
        await NewCompany("UTC");
        var clock = new Clock();
        var r1 = await RunAt(clock, new DateTimeOffset(2026, 3, 2, 2, 0, 0, TimeSpan.Zero));
        foreach (var env in new[] { Env(), new Dictionary<string, string?>(Env()) { ["Hasm__Backup__Retention__AutomaticDeletion"] = RetentionOptions.OptInValue } })
        {
            var apply = await AdminTool.RunWith(Options, env, "backup-retention-apply", Root);
            Assert.Equal(3, apply.Exit);
            Assert.Contains("retention_deletion_inactive", apply.Err);
        }
        Assert.Equal(2, await AuditEvents(BackupAuditEvents.DeletionRefused));
        Assert.Equal(0, await AuditEvents(BackupAuditEvents.DeletionStarted));
        Assert.True(Directory.Exists(Path.Combine(Root, r1)));
        var service = new RetentionService(Options, Retention, clock);
        Assert.Equal("retention_deletion_inactive", (await Assert.ThrowsAsync<BackupException>(() => service.ApplyAsync(Root, Trust, Backup, Trail(), "os:test"))).Code);
        Assert.True(Directory.Exists(Path.Combine(Root, r1)));
    }

    [PostgresFact]
    public async Task Through_the_test_seam_exactly_the_eligible_runs_are_deleted_audit_first_and_a_remaining_run_still_recovers()
    {
        await Init();
        await NewCompany("UTC");
        var clock = new Clock();
        var runs = new List<string>();
        for (var d = 2; d <= 13; d++) runs.Add(await RunAt(clock, new DateTimeOffset(2026, 3, d, 2, 0, 0, TimeSpan.Zero)));
        await Prove(runs[^1]);
        clock.Now = new DateTimeOffset(2026, 3, 13, 3, 0, 0, TimeSpan.Zero);
        var service = new RetentionService(Options, Retention, clock);
        var (plan, _) = await service.PlanAsync(Root, Trust);
        var eligible = plan.Eligible.Select(e => e.RunId).ToList();
        Assert.Equal(runs[1..5], eligible.Order(StringComparer.Ordinal).ToList());                                   // 3..6 March: not daily (7..13), weekly (8, 13), period-end (2) or last
        var trail = Trail();
        var result = await service.ApplyCoreAsync(Root, Trust, Backup, (code, detail, ct) => BackupAudit.WriteAsync(SystemConnection, trail, code, detail, null, ct), "os:test",
            RetentionGateOverride.ForTests(), default);
        Assert.Equal(eligible.Order(StringComparer.Ordinal), result.Deleted.Order(StringComparer.Ordinal));
        Assert.All(runs, id => Assert.Equal(!eligible.Contains(id), Directory.Exists(Path.Combine(Root, id))));
        Assert.Equal(4, await AuditEvents(BackupAuditEvents.DeletionStarted));
        Assert.Equal(4, await AuditEvents(BackupAuditEvents.Deleted));
        foreach (var id in eligible)
        {
            var tomb = Path.Combine(Root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder, id);
            Assert.Equal(id, SignedDocuments.ReadRunStatus(tomb, Trust).RunId);                                    // the deleted run's signed status stays verifiable
        }
        // the tombstone and the remaining runs stay verifiable, and an older remaining run still passes its recovery test
        Assert.Equal((0, "ok"), BackupRunStatus.Evaluate(BackupRunStatus.List(Root, Trust), clock.Now, BackupRunStatus.DefaultMaxAge) is var st ? (st.ExitCode, st.Code) : default);
        await Prove(runs[0]);
    }

    [PostgresFact]
    public async Task A_completion_event_is_recognised_only_for_its_own_run()
    {
        // review L-b of #156: the real stream check behind the exactly-once recovery of audit 2016 (review L2 of #134)
        await Init();
        var service = new RetentionService(Options, Retention);
        var trail = Trail();
        const string Run = "20260301T020000Z-0000abcd", Neighbour = "20260301T020000Z-0000abce";
        Task Write(short code, string detail) => BackupAudit.WriteAsync(SystemConnection, trail, code, detail, null, default);
        Assert.False(await service.DeletedRecordedAsync(Run, default));
        await Write(BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:test run={Run} status=x reasons=none");
        Assert.False(await service.DeletedRecordedAsync(Run, default));                                           // a 2015 is not a completion
        await Write(BackupAuditEvents.Deleted, $"backup deleted operator=os:test run={Run} status=x files=1 bytes=1 tombstone=yes resumed=no");
        Assert.True(await service.DeletedRecordedAsync(Run, default));
        Assert.False(await service.DeletedRecordedAsync(Neighbour, default));                                    // a run id one hex digit away does not match
        await Write(BackupAuditEvents.Deleted, $"backup deleted operator=os:test run={Neighbour} resumed=yes recovered=audit folder=absent");
        Assert.True(await service.DeletedRecordedAsync(Neighbour, default));
        Assert.Equal("invalid_run_id", (await Assert.ThrowsAsync<BackupException>(() => service.DeletedRecordedAsync("x%", default))).Code);
    }

    [PostgresFact]
    public async Task Deletion_never_overlaps_a_running_backup_or_a_restore()
    {
        await Init();
        var service = new RetentionService(Options, Retention);
        Directory.CreateDirectory(Root);
        async Task<string> Code() => (await Assert.ThrowsAsync<BackupException>(() => service.ApplyCoreAsync(Root, Trust, Backup, (_, _, _) => Task.CompletedTask, "os:test", RetentionGateOverride.ForTests(), default))).Code;
        await using (var run = new NpgsqlConnection(SystemConnection))
        {
            await run.OpenAsync();
            await using (var lk = new NpgsqlCommand($"SELECT pg_advisory_lock({BackupRunService.RunLockNamespace}, 0)", run)) await lk.ExecuteNonQueryAsync();
            Assert.Equal("run_in_progress", await Code());
            // release explicitly: closing an unpooled session frees its advisory locks only when the server backend has exited, which is asynchronous
            await using (var un = new NpgsqlCommand($"SELECT pg_advisory_unlock({BackupRunService.RunLockNamespace}, 0)", run)) await un.ExecuteNonQueryAsync();
        }
        // review L6 of #134 (and L-a of #156, timing-free): on the holder's OWN session, after its explicit unlock and before the close, no advisory lock remains
        long? heldBeforeClose = null;
        await using (var restore = await RetentionService.AcquireRestoreLockAsync(Options, default))
        {
            Assert.Equal("restore_in_progress", await Code());
            restore.BeforeCloseForTests = async c =>
            {
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid()", c);
                heldBeforeClose = (long)(await cmd.ExecuteScalarAsync())!;
            };
        }
        Assert.Equal(0L, heldBeforeClose);
        // and a restore cannot start while a deletion holds the restore lock exclusively
        await using var deletion = new NpgsqlConnection(SystemConnection);
        await deletion.OpenAsync();
        await using (var lk = new NpgsqlCommand($"SELECT pg_advisory_lock({BackupRunService.RunLockNamespace}, 1)", deletion)) await lk.ExecuteNonQueryAsync();
        Assert.Equal("retention_in_progress", (await Assert.ThrowsAsync<BackupException>(() => RetentionService.AcquireRestoreLockAsync(Options, default))).Code);
    }
}
