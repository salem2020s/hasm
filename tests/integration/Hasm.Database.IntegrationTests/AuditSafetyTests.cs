using System.Security.Cryptography;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Identity;
using Npgsql;
using Au = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using Mem = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess;
using Ro = Hasm.Infrastructure.SystemDatabase.SystemSchema.Role;
using Us = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Database.IntegrationTests;

/// <summary>An audited mutation either commits together with its event or does not happen; a damaged or unanchorable stream is never extended.</summary>
public class AuditAtomicityTests : AuditTestBase
{
    private static readonly string[] View = [PermissionCatalog.Codes.MembersView];

    private Task<long> Memberships(Guid user) => Scalar<long>($"SELECT count(*) FROM {Mem.Table} WHERE {Mem.UserId} = @u", ("u", user));
    private Task<long> RolesNamed(string name) => Scalar<long>($"SELECT count(*) FROM {Ro.Table} WHERE {Ro.Name} = @n", ("n", name));

    [PostgresFact]
    public async Task A_failing_audit_write_leaves_no_access_or_identity_mutation_behind()
    {
        var (company, admin, stream) = await Scenario(1);
        var target = await NewPerson("target");
        var projection = await AuditCount(company);
        var events = await EventCount();
        var hash = await Scalar<string>($"SELECT {Us.PasswordHash} FROM {Us.Table} WHERE {Us.Id} = @u", ("u", target.Id));
        AuditKeys.Unavailable = true;

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => Access.GrantMembershipAsync(admin.Id, company, target.Login));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Access.CreateRoleAsync(admin.Id, company, "Lost role", View));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Auth.CreateUserAsync("carol", "Carol", Pw, false));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Store.SetStateAsync(admin.Id, target.Id, UserState.Disabled));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Store.UpdatePasswordHashAsync(target.Id, Passwords.Hash("Brand-New-Passphrase-5"), passwordChanged: true));

        Assert.Equal("audit_key_unavailable", ex.Code);
        Assert.Equal(0L, await Memberships(target.Id));                                                    // the protected mutation did not happen
        Assert.Equal(0L, await RolesNamed("Lost role"));
        Assert.Null(await Store.FindUserByLoginAsync("carol"));
        Assert.Equal(UserState.Active, (await Store.FindUserByIdAsync(target.Id))!.State);
        Assert.Equal(hash, await Scalar<string>($"SELECT {Us.PasswordHash} FROM {Us.Table} WHERE {Us.Id} = @u", ("u", target.Id)));
        Assert.Equal(projection, await AuditCount(company));                                                // and no half-written audit row either
        Assert.Equal(events, await EventCount());

        AuditKeys.Unavailable = false;
        Ok(await Access.GrantMembershipAsync(admin.Id, company, target.Login));                             // service resumes once the key is back
        AssertVerified(await Verify(stream), 4);
    }

    [PostgresFact]
    public async Task An_unreadable_checkpoint_store_refuses_the_next_mutation_and_changes_nothing()
    {
        var (company, admin, stream) = await Scenario(1);
        var target = await NewPerson("target");
        AuditCheckpoints.Fail = true;

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => Access.GrantMembershipAsync(admin.Id, company, target.Login));

        Assert.Equal("audit_checkpoint_unavailable", ex.Code);
        Assert.Equal(0L, await Memberships(target.Id));
        Assert.Equal(3L, await EventCount(stream));
        AuditCheckpoints.Fail = false;
        Ok(await Access.GrantMembershipAsync(admin.Id, company, target.Login));
        AssertVerified(await Verify(stream), 4);
    }

    [PostgresFact]
    public async Task A_failure_to_anchor_after_commit_keeps_the_mutation_but_blocks_further_ones_until_it_recovers()
    {
        var (company, admin, stream) = await Scenario(1);
        var first = await NewPerson("first"); var second = await NewPerson("second");
        AuditCheckpoints.FailAppend = true;

        Ok(await Access.GrantMembershipAsync(admin.Id, company, first.Login));                              // committed together with its event
        Assert.Equal(1L, await Memberships(first.Id));
        var pending = await Verify(stream);
        Assert.Equal(AuditVerdict.Incomplete, pending.Verdict);                                            // not Verified: the newest event is unanchored
        Assert.Equal(1, pending.UnanchoredEvents);
        Assert.Empty(Codes(pending, FindingSeverity.Failure));

        await Assert.ThrowsAsync<AuditUnavailableException>(() => Access.GrantMembershipAsync(admin.Id, company, second.Login));   // fail-safe
        Assert.Equal(0L, await Memberships(second.Id));
        Assert.Equal(4L, await EventCount(stream));

        AuditCheckpoints.FailAppend = false;
        Ok(await Access.GrantMembershipAsync(admin.Id, company, second.Login));                             // catches the checkpoint up, then proceeds
        AssertVerified(await Verify(stream), 5);
    }

    [PostgresFact]
    public async Task A_rolled_back_transaction_leaves_no_gap_no_event_and_no_checkpoint()
    {
        await StartAccess();
        var stream = AuditStream.ForCompany(await NewCompany());
        await using (var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await Audit.AppendAsync(conn, tx, stream, Clock.GetUtcNow(), new AuditEventData(FutureEvent, Detail: "rolled back"));
            await tx.RollbackAsync();
        }
        Assert.Equal(0L, await EventCount(stream));
        Assert.Null(await AuditCheckpoints.ReadLastAsync(stream.Key));

        await using var conn2 = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn2.OpenAsync();
        await using var tx2 = await conn2.BeginTransactionAsync();
        var receipt = await Audit.AppendAsync(conn2, tx2, stream, Clock.GetUtcNow(), new AuditEventData(FutureEvent, Detail: "committed"));
        await Audit.CommitAsync(tx2, receipt);

        Assert.Equal(1, receipt.Seq);                                                                       // the rolled-back attempt did not consume a sequence number
        AssertVerified(await Verify(stream), 1);
    }

    [PostgresFact]
    public async Task A_cancelled_operation_leaves_nothing_behind()
    {
        var (company, admin, stream) = await Scenario(1);
        var target = await NewPerson("target");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Access.GrantMembershipAsync(admin.Id, company, target.Login, cts.Token));

        Assert.Equal(0L, await Memberships(target.Id));
        AssertVerified(await Verify(stream), 3);
    }

    [PostgresFact]
    public async Task A_truncated_table_is_never_silently_restarted()
    {
        var (company, admin, stream) = await Scenario(1);
        await Exec($"TRUNCATE {Ev.Table}");

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Access.CreateRoleAsync(admin.Id, company, "After truncate", View));

        Assert.Equal("audit_stream_missing", ex.Code);
        Assert.Equal(0L, await EventCount());                                                               // no new chain was started over the old checkpoints
        Assert.Equal(0L, await RolesNamed("After truncate"));
    }

    [PostgresFact]
    public async Task A_damaged_head_stops_the_chain_from_being_extended()
    {
        var (company, admin, stream) = await Scenario(1);
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'x' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3", ("s", stream.Key));

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Access.CreateRoleAsync(admin.Id, company, "After tamper", View));

        Assert.Equal("audit_head_corrupt", ex.Code);
        Assert.Equal(3L, await EventCount(stream));
        Assert.Equal(0L, await RolesNamed("After tamper"));
    }

    [PostgresFact]
    public async Task A_database_rolled_back_behind_its_checkpoint_refuses_new_events()
    {
        var (company, admin, stream) = await Scenario(2);
        var removed = (await Events(stream)).Where(e => e.Seq >= 4).Select(e => e.LegacyRef!.Value).ToArray();
        await TamperLegacy($"DELETE FROM {Au.Table} WHERE {Au.Id} = ANY(@ids)", ("ids", removed));
        await Tamper($"DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s AND {Ev.Seq} >= 4", ("s", stream.Key));      // e.g. an old backup restored

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Access.CreateRoleAsync(admin.Id, company, "After restore", View));

        Assert.Equal("audit_stream_truncated", ex.Code);
        Assert.Equal(3L, await EventCount(stream));
    }

    [PostgresFact]
    public async Task A_rewritten_head_contradicting_its_checkpoint_is_a_fork_even_with_a_valid_code()
    {
        var (company, admin, stream) = await Scenario(1);
        var head = (await Events(stream))[^1];
        var key = AuditKeys.GetActive();                                                                    // an insider who even holds the key
        var forged = AuditCanonical.Seal(head with { Detail = "alternate history", Hash = [], Mac = [] }, key);
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = @d, {Ev.Hash} = @h, {Ev.Mac} = @m WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3",
            ("d", forged.Detail!), ("h", forged.Hash), ("m", forged.Mac), ("s", stream.Key));

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Access.CreateRoleAsync(admin.Id, company, "After fork", View));

        Assert.Equal("audit_fork", ex.Code);
        var report = await Verify(stream);
        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "checkpoint_mismatch" && f.Seq == 3);
    }
}

/// <summary>Safe concurrent event creation: one gap-free order per stream, independent streams, rollbacks leave no holes.</summary>
public class AuditConcurrencyTests : AuditTestBase
{
    private async Task AppendMany(AuditStream stream, int count, Func<int, bool>? rollback = null)
    {
        await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var receipt = await Audit.AppendAsync(conn, tx, stream, Clock.GetUtcNow(), new AuditEventData(FutureEvent, Detail: $"n{i}"));
            if (rollback?.Invoke(i) == true) await tx.RollbackAsync();
            else await Audit.CommitAsync(tx, receipt);
        }));
    }

    [PostgresFact]
    public async Task Parallel_appends_to_several_streams_stay_gap_free_ordered_and_independent()
    {
        await StartAccess();
        var a = AuditStream.ForCompany(await NewCompany()); var b = AuditStream.ForCompany(await NewCompany());
        const int n = 25;

        await Task.WhenAll(AppendMany(AuditStream.System, n), AppendMany(a, n), AppendMany(b, n));

        foreach (var stream in new[] { AuditStream.System, a, b })
        {
            var events = await Events(stream);
            Assert.Equal(Enumerable.Range(1, n).Select(i => (long)i), events.Select(e => e.Seq));
            Assert.Equal(Enumerable.Range(0, n).Select(i => $"n{i}").Order(), events.Select(e => e.Detail!).Order());   // every request exactly once
            AssertVerified(await Verify(stream), n);
        }
    }

    [PostgresFact]
    public async Task Parallel_appends_with_rollbacks_leave_no_holes()
    {
        await StartAccess();
        var stream = AuditStream.ForCompany(await NewCompany());

        await AppendMany(stream, 30, rollback: i => i % 3 == 0);

        var events = await Events(stream);
        Assert.Equal(20, events.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), events.Select(e => e.Seq));
        AssertVerified(await Verify(stream), 20);
    }

    [PostgresFact]
    public async Task Parallel_access_and_identity_operations_produce_valid_chains()
    {
        var (company, admin, stream) = await Scenario(0);
        var people = new List<UserRecord>();
        for (var i = 0; i < 10; i++) people.Add(await NewPerson("peer"));
        var before = await EventCount(stream);
        var systemBefore = await EventCount(AuditStream.System);

        var tasks = new List<Task>();
        tasks.AddRange(people.Select(p => (Task)Access.GrantMembershipAsync(admin.Id, company, p.Login)));
        tasks.AddRange(Enumerable.Range(0, 10).Select(i => (Task)Auth.CreateUserAsync($"par{i}", "Par", Pw, false)));
        await Task.WhenAll(tasks);

        Assert.Equal(before + 10, await EventCount(stream));
        Assert.Equal(systemBefore + 10, await EventCount(AuditStream.System));
        AssertVerified(await Verify(stream), before + 10);
        AssertVerified(await Verify(AuditStream.System), systemBefore + 10);
    }

    [PostgresFact]
    public async Task Parallel_commits_never_regress_the_checkpoint_store()
    {
        await StartAccess();
        var stream = AuditStream.ForCompany(await NewCompany());

        await AppendMany(stream, 20);

        var checkpoints = (await AuditCheckpoints.ReadAllAsync(stream.Key)).Checkpoints;
        Assert.Equal(checkpoints.Select(c => c.Seq).Order(), checkpoints.Select(c => c.Seq));              // strictly increasing, no regression
        Assert.Equal(20, checkpoints[^1].Seq);
    }
}

/// <summary>Controlled key rotation: history stays verifiable; missing or wrong keys are never read as valid.</summary>
public class AuditKeyRotationTests : AuditTestBase
{
    private static readonly string[] View = [PermissionCatalog.Codes.MembersView];

    [PostgresFact]
    public async Task After_rotation_records_of_both_keys_verify_and_checkpoints_follow_the_active_key()
    {
        var (company, admin, stream) = await Scenario(2);                                                   // 4 events under k1
        AuditKeys.Add("k2", RandomNumberGenerator.GetBytes(32));
        AuditKeys.Activate("k2");
        Ok(await Access.CreateRoleAsync(admin.Id, company, "After rotation 1", View));
        Ok(await Access.CreateRoleAsync(admin.Id, company, "After rotation 2", View));

        var events = await Events(stream);

        Assert.Equal(["k1", "k1", "k1", "k1", "k2", "k2"], events.Select(e => e.KeyId));
        AssertVerified(await Verify(stream), 6);
        Assert.Equal(["k1", "k2"], (await AuditCheckpoints.ReadAllAsync(stream.Key)).Checkpoints.Select(c => c.KeyId).Distinct().Order());
    }

    [PostgresFact]
    public async Task Retiring_an_old_key_without_keeping_it_makes_old_evidence_incomplete_but_writing_continues()
    {
        var (company, admin, stream) = await Scenario(1);
        AuditKeys.Add("k2", RandomNumberGenerator.GetBytes(32));
        AuditKeys.Activate("k2");
        Ok(await Access.CreateRoleAsync(admin.Id, company, "Under k2", View));
        AuditKeys.Remove("k1");

        var report = await Verify(stream);
        Assert.Equal(AuditVerdict.Incomplete, report.Verdict);
        Assert.Contains("key_missing", Codes(report));
        Assert.Empty(Codes(report, FindingSeverity.Failure));
        Assert.Equal(0, report.VerifiedThroughSeq);                                                        // records 1-3 cannot be authenticated, so the verified range does not advance

        Ok(await Access.CreateRoleAsync(admin.Id, company, "Still works", View));                           // head is under k2: writing is safe
    }

    [PostgresFact]
    public async Task A_head_authenticated_by_an_unavailable_key_refuses_new_events()
    {
        var (company, admin, stream) = await Scenario(1);
        AuditKeys.Add("k2", RandomNumberGenerator.GetBytes(32));
        AuditKeys.Activate("k2");
        AuditKeys.Remove("k1");                                                                              // head (seq 3) was written under k1

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => Access.CreateRoleAsync(admin.Id, company, "Blocked", View));

        Assert.Equal("audit_key_unavailable", ex.Code);
        Assert.Equal(3L, await EventCount(stream));
    }

    [PostgresFact]
    public async Task A_different_secret_under_an_existing_key_id_is_reported_as_failure()
    {
        var (company, admin, stream) = await Scenario(1);
        AuditKeys.Add("k1", RandomNumberGenerator.GetBytes(32));                                            // wrong secret, same identifier

        var report = await Verify(stream);
        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => Access.CreateRoleAsync(admin.Id, company, "Wrong key", View));

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Equal([1L, 2L, 3L], report.Findings.Where(f => f.Code == "mac_invalid").Select(f => f.Seq!.Value).Order());
        Assert.Contains("checkpoint_mac_invalid", Codes(report));
        Assert.Equal("audit_head_corrupt", ex.Code);
    }
}

/// <summary>Real file-based keys and checkpoint directory in a temporary location, shared by the file-evidence and admin-tool suites.</summary>
public abstract class FileEvidenceTestBase : AuditTestBase
{
    protected static readonly string[] View = [PermissionCatalog.Codes.MembersView];
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-audit-it-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
    protected string Root => root;
    protected string KeyDir => Path.Combine(root, "keys");
    protected string CpDir => Path.Combine(root, "checkpoints");

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    protected AuditOptions Opts(string active) => new() { KeyDirectory = KeyDir, ActiveKeyId = active, CheckpointDirectory = CpDir };

    protected async Task<(Guid Company, UserRecord Admin, AuditStream Stream, AccessService Service)> Written(string key = "k1", int roles = 3)
    {
        await StartAccess();
        FileAuditKeyRing.Generate(KeyDir, key);
        var company = await NewCompany();
        var trail = AuditTrail.FromOptions(Opts(key), Clock);
        var service = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog, trail, keys: TestIdentityKeys.Instance);
        var user = await NewPerson("fileadmin");
        Assert.Equal(AccessService.BootstrapStatus.Granted, await service.BootstrapCompanyAdministratorAsync(user.Login, company));
        for (var i = 0; i < roles; i++) Ok(await service.CreateRoleAsync(user.Id, company, $"File role {i}", View));
        return (company, user, AuditStream.ForCompany(company), service);
    }

    protected AuditVerifier FileVerifier(string? cpDir = null, string active = "k1") =>
        new(Options.BuildSystemConnectionString(admin: true), new FileAuditKeyRing(Opts(active)), new FileCheckpointStore(cpDir ?? CpDir, enforcePermissions: false));

    protected string CheckpointFile(AuditStream s) => Path.Combine(CpDir, FileCheckpointStore.FileNameFor(s.Key));

}

/// <summary>The real file-based keys and checkpoint directory (restricted storage outside the database), end to end.</summary>
public class AuditFileEvidenceTests : FileEvidenceTestBase
{
    [PostgresFact]
    public async Task Events_written_with_file_keys_verify_against_the_checkpoint_directory_and_an_offline_copy()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;

        AssertVerified(await FileVerifier().VerifyAsync(stream), 5);

        var copy = Path.Combine(Root, "offline-copy");                                                      // e.g. removable media kept off the server
        Directory.CreateDirectory(copy);
        File.Copy(CheckpointFile(stream), Path.Combine(copy, FileCheckpointStore.FileNameFor(stream.Key)));
        AssertVerified(await FileVerifier(copy).VerifyAsync(stream), 5);
        Assert.Equal(4, File.ReadAllLines(CheckpointFile(stream)).Length);          // the bootstrap transaction wrote two events but is anchored once, at its newest
        Assert.DoesNotContain(stream.Key, string.Join('|', Directory.GetFiles(CpDir).Select(Path.GetFileName)));   // opaque file names
        Assert.DoesNotContain(Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(KeyDir, "k1.hkey"))), await File.ReadAllTextAsync(CheckpointFile(stream)));
    }

    [PostgresFact]
    public async Task A_deleted_checkpoint_file_makes_the_evidence_incomplete()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        File.Delete(CheckpointFile(stream));

        var report = await FileVerifier().VerifyAsync(stream);

        Assert.Equal(AuditVerdict.Incomplete, report.Verdict);
        Assert.Contains("no_checkpoints", Codes(report));
    }

    [PostgresFact]
    public async Task An_edited_checkpoint_is_rejected_by_its_authentication_code()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        var file = CheckpointFile(stream);
        var lines = File.ReadAllLines(file);
        lines[1] = lines[1].Replace("\"seq\":3", "\"seq\":9");                                              // retarget a checkpoint at another record
        File.WriteAllLines(file, lines);

        var report = await FileVerifier().VerifyAsync(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains("checkpoint_mac_invalid", Codes(report));
    }

    [PostgresFact]
    public async Task A_checkpoint_removed_from_the_middle_of_the_file_is_detected()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        var file = CheckpointFile(stream);
        File.WriteAllLines(file, File.ReadAllLines(file).Where((_, i) => i != 1));

        var report = await FileVerifier().VerifyAsync(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains("checkpoint_chain_broken", Codes(report));
    }

    [PostgresFact]
    public async Task Losing_only_the_newest_checkpoints_leaves_an_unanchored_tail_which_is_incomplete_not_verified()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        var file = CheckpointFile(stream);
        File.WriteAllLines(file, File.ReadAllLines(file).Take(2));

        var report = await FileVerifier().VerifyAsync(stream);

        Assert.Equal(AuditVerdict.Incomplete, report.Verdict);
        Assert.Equal(2, report.UnanchoredEvents);
        Assert.Contains("unanchored_tail", Codes(report));
        Assert.Empty(Codes(report, FindingSeverity.Failure));
        Assert.Equal(5, report.VerifiedThroughSeq);                                                        // the chain is intact; only the anchor is short
    }

    [PostgresFact]
    public async Task An_interrupted_checkpoint_write_is_reported_as_incomplete_and_the_next_write_repairs_the_file_layout()
    {
        var (company, admin, stream, service) = await Written();
        await using var _s = service;
        await File.AppendAllTextAsync(CheckpointFile(stream), "{\"v\":1,\"stream\":\"" + stream.Key + "\",\"seq\":6,\"ha");     // power loss mid-line

        var torn = await FileVerifier().VerifyAsync(stream);
        Assert.Equal(AuditVerdict.Incomplete, torn.Verdict);
        Assert.Contains("checkpoint_unreadable", Codes(torn));
        Assert.Empty(Codes(torn, FindingSeverity.Failure));

        Ok(await service.CreateRoleAsync(admin.Id, company, "After the crash", View));                      // appends on a fresh line
        var after = await FileVerifier().VerifyAsync(stream);
        Assert.Empty(Codes(after, FindingSeverity.Failure));
        Assert.Equal(6, after.LastCheckpointSeq);
    }

    [PostgresFact]
    public async Task Rotation_with_real_key_files_keeps_old_records_verifiable_and_a_missing_old_key_file_is_incomplete()
    {
        var (company, admin, stream, service) = await Written("k1", roles: 2);
        await service.DisposeAsync();
        FileAuditKeyRing.Generate(KeyDir, "k2");
        await using var rotated = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog, AuditTrail.FromOptions(Opts("k2"), Clock), keys: TestIdentityKeys.Instance);
        Ok(await rotated.CreateRoleAsync(admin.Id, company, "Under the new key", View));

        var events = await Events(stream);
        Assert.Equal(["k1", "k1", "k1", "k1", "k2"], events.Select(e => e.KeyId));
        AssertVerified(await FileVerifier(active: "k2").VerifyAsync(stream), 5);

        var withoutOld = Path.Combine(Root, "keys-without-k1");
        Directory.CreateDirectory(withoutOld);
        File.Copy(Path.Combine(KeyDir, "k2.hkey"), Path.Combine(withoutOld, "k2.hkey"));
        var limited = new AuditVerifier(Options.BuildSystemConnectionString(admin: true),
            new FileAuditKeyRing(new AuditOptions { KeyDirectory = withoutOld, ActiveKeyId = "k2", CheckpointDirectory = CpDir, AllowInsecureFilePermissions = true }),
            new FileCheckpointStore(CpDir, enforcePermissions: false));
        var report = await limited.VerifyAsync(stream);
        Assert.Equal(AuditVerdict.Incomplete, report.Verdict);
        Assert.Contains("key_missing", Codes(report));
    }

    [PostgresFact]
    public async Task Missing_key_or_checkpoint_configuration_fails_the_mutation_closed_with_a_safe_message()
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        var unconfigured = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog,
            AuditTrail.FromOptions(new AuditOptions(), Clock), keys: TestIdentityKeys.Instance);
        await using var _s = unconfigured;

        var ex = await Assert.ThrowsAsync<AuditConfigurationException>(() => unconfigured.CreateRoleAsync(admin.Id, company, "Unprotected", View));

        Assert.Equal("audit_not_configured", ex.Code);
        Assert.Contains("Hasm:Audit:KeyDirectory", ex.Message);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ro.Table} WHERE {Ro.Name} = 'Unprotected'"));

        // Keys configured but the checkpoint directory is not usable (a file stands where the directory should be): the first mutation
        // commits with its event but cannot be anchored, so the next one is refused.
        Directory.CreateDirectory(Root);
        FileAuditKeyRing.Generate(KeyDir, "k1");
        var blocker = Path.Combine(Root, "blocked");
        await File.WriteAllTextAsync(blocker, "x");
        var broken = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog,
            AuditTrail.FromOptions(new AuditOptions { KeyDirectory = KeyDir, ActiveKeyId = "k1", CheckpointDirectory = Path.Combine(blocker, "cp") }, Clock), keys: TestIdentityKeys.Instance);
        await using var _b = broken;
        var company2 = await NewCompany();
        var admin2 = await NewPerson("brokenadmin");
        Assert.Equal(AccessService.BootstrapStatus.Granted, await broken.BootstrapCompanyAdministratorAsync(admin2.Login, company2));

        var failure = await Assert.ThrowsAsync<AuditIntegrityException>(() => broken.CreateRoleAsync(admin2.Id, company2, "Unanchorable", View));

        // Events exist but there is not a single protected checkpoint: the system will not anchor them on its own (that would define the
        // trusted baseline); an administrator must run audit-anchor --establish-baseline once the storage works.
        Assert.Equal("audit_baseline_missing", failure.Code);
        Assert.DoesNotContain(blocker, failure.Message);
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {Ro.Table} WHERE {Ro.Name} = 'Unanchorable'"));
    }
}

/// <summary>Crash consistency, restart recovery and the rule that nothing silently establishes a new trust baseline.</summary>
public class AuditRecoveryTests : AuditTestBase
{
    private static readonly string[] View = [PermissionCatalog.Codes.MembersView];

    private AuditVerifier VerifierWith(ICheckpointStore store) => new(Options.BuildSystemConnectionString(admin: true), AuditKeys, store);

    [PostgresFact]
    public async Task After_a_restart_committed_but_unanchored_events_are_kept_reported_incomplete_and_then_caught_up()
    {
        var (company, admin, stream) = await Scenario(1);
        var member = await NewPerson("late");
        AuditCheckpoints.FailAppend = true;
        Ok(await Access.GrantMembershipAsync(admin.Id, company, member.Login));                            // committed; the process "dies" before anchoring succeeds

        var before = await Verify(stream);
        Assert.Equal(AuditVerdict.Incomplete, before.Verdict);                                              // never presented as verified
        Assert.Equal(1, before.UnanchoredEvents);
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess.Table} WHERE {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserCompanyAccess.UserId} = @u", ("u", member.Id)));

        AuditCheckpoints.FailAppend = false;                                                                // restart: a brand-new trail instance, same keys and store
        await using var restarted = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog,
            new AuditTrail(new AuditServices(AuditKeys, AuditCheckpoints, 0), Clock), keys: TestIdentityKeys.Instance);
        Ok(await restarted.CreateRoleAsync(admin.Id, company, "After restart", View));

        AssertVerified(await Verify(stream), 5);
        Assert.Equal(4L, (await Events(stream))[3].Seq);                                                    // the committed event and its evidence were preserved
    }

    [PostgresFact]
    public async Task A_replaced_or_wiped_checkpoint_store_never_lets_the_runtime_silently_set_a_new_baseline()
    {
        var (company, admin, stream) = await Scenario(2);
        var wiped = new InMemoryCheckpointStore();
        await using var service = new AccessService(Options.BuildSystemConnectionString(admin: true), Clock, TestCatalog,
            new AuditTrail(new AuditServices(AuditKeys, wiped, 0), Clock), keys: TestIdentityKeys.Instance);

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => service.CreateRoleAsync(admin.Id, company, "After wipe", View));

        Assert.Equal("audit_baseline_missing", ex.Code);
        Assert.Null(await wiped.ReadLastAsync(stream.Key));                                                 // nothing was anchored behind anyone's back
        Assert.Equal(4L, await EventCount(stream));
    }

    [PostgresFact]
    public async Task Anchoring_without_any_checkpoint_needs_an_explicit_baseline_decision()
    {
        var (_, _, stream) = await Scenario(2);
        // The attacker deleted the newest events (and their projection rows) together with the whole checkpoint store.
        var removed = (await Events(stream)).Where(e => e.Seq >= 3).Select(e => e.LegacyRef!.Value).ToArray();
        await TamperLegacy($"DELETE FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit.Table} WHERE {Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit.Id} = ANY(@ids)", ("ids", removed));
        await Tamper($"DELETE FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.Table} WHERE {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.StreamKey} = @s AND {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.Seq} >= 3", ("s", stream.Key));
        var empty = new InMemoryCheckpointStore();
        var verifier = VerifierWith(empty);

        var report = await verifier.VerifyAsync(stream);
        Assert.Equal(AuditVerdict.Incomplete, report.Verdict);                                              // looks perfect, but is not "Verified"
        var refused = await Assert.ThrowsAsync<AuditIntegrityException>(() => verifier.AnchorHeadAsync(stream, Clock));
        Assert.Equal("audit_baseline_requires_confirmation", refused.Code);
        Assert.Null(await empty.ReadLastAsync(stream.Key));

        Assert.Equal(2L, await verifier.AnchorHeadAsync(stream, Clock, establishBaseline: true));           // a deliberate, explicit act
        AssertVerified(await verifier.VerifyAsync(stream), 2);
    }

    [PostgresFact]
    public async Task Anchoring_never_blesses_an_altered_chain_even_with_the_baseline_flag()
    {
        var (_, _, stream) = await Scenario(2);
        await Tamper($"UPDATE {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.Table} SET {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.Detail} = 'x' WHERE {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.StreamKey} = @s AND {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.Seq} = 2", ("s", stream.Key));
        var empty = new InMemoryCheckpointStore();

        var ex = await Assert.ThrowsAsync<AuditIntegrityException>(() => VerifierWith(empty).AnchorHeadAsync(stream, Clock, establishBaseline: true));

        Assert.Equal("audit_verification_failed", ex.Code);
        Assert.Null(await empty.ReadLastAsync(stream.Key));
    }

    [PostgresFact]
    public async Task Wiping_events_and_checkpoints_leaves_empty_streams_that_are_incomplete_not_verified()
    {
        var (_, _, stream) = await Scenario(2);
        await Exec($"TRUNCATE {Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent.Table}");
        var verifier = VerifierWith(new InMemoryCheckpointStore());                                         // checkpoints gone too

        var company = await verifier.VerifyAsync(stream);
        var system = await verifier.VerifyAsync(AuditStream.System);

        Assert.Equal(AuditVerdict.Incomplete, company.Verdict);                                             // projection rows remain, nothing authenticates them
        Assert.Contains("no_authenticated_history", Codes(company));
        Assert.Equal(AuditVerdict.Incomplete, system.Verdict);                                              // users exist, no authenticated events
        Assert.Contains("no_authenticated_history", Codes(system));
    }

    [PostgresFact]
    public async Task The_report_flags_a_verifying_account_that_could_rewrite_audit_evidence_without_changing_the_verdict()
    {
        var (_, _, stream) = await Scenario(1);

        var report = await Verify(stream);                                                                  // the test role owns the table

        Assert.Contains("verifier_account_can_modify_audit", Codes(report, FindingSeverity.Note));
        AssertVerified(report, 3);
    }
}
