using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Npgsql;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using TO = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTotp;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.1 slice S1 against real PostgreSQL: the MFA state on the user row, the TOTP secret store with its constraints and guard trigger, the replay compare-and-set, rotation, and the restore fix-up.
/// Disposable cluster only (same safety gate as every PostgreSQL test).
/// </summary>
public class MfaStoreTests : IdentityTestBase
{
    private readonly InMemoryKekRing ring = new("k1", TestIdentityKeys.Master);
    private MfaStore Mfa = null!;

    private async Task Setup()
    {
        await Start();
        Mfa = new MfaStore(Options.BuildSystemConnectionString(admin: true), Clock, Audit, new TotpSecretProtector(ring), keys: TestIdentityKeys.Instance);
    }

    private long Step => Totp.StepFor(Clock.GetUtcNow());

    private async Task<(UserRecord User, byte[] Secret, Guid SecretId)> Enrolled(string login = "alice", long? step = null)
    {
        var user = await NewUser(login);
        var secret = Totp.GenerateSecret();
        var id = await Mfa.CreatePendingAsync(user.Id, secret, null);
        Assert.True(await Mfa.ActivatePendingAsync(user.Id, id, step ?? Step, null));
        return (user, secret, id);
    }

    private Task<long> Count(string where) => Scalar<long>($"SELECT count(*) FROM {TO.Table} WHERE {where}");
    private Task<long> Events(short type) => Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.EventType} = @t", ("t", type));

    [PostgresFact]
    public async Task A_pending_secret_activates_the_user_to_Enrolled_and_is_stored_only_encrypted()
    {
        await Setup();
        var user = await NewUser();
        var secret = Totp.GenerateSecret();
        var id = await Mfa.CreatePendingAsync(user.Id, secret, null);
        Assert.Equal(MfaState.None, await Mfa.GetStateAsync(user.Id));
        Assert.Null(await Mfa.FindActiveSecretAsync(user.Id, default));                                        // pending is not usable for sign-in
        Assert.NotEqual(secret, await Scalar<byte[]>($"SELECT {TO.Cipher} FROM {TO.Table} WHERE {TO.SecretId} = @i", ("i", id)));

        Assert.True(await Mfa.ActivatePendingAsync(user.Id, id, Step, null));
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(user.Id));
        using var active = (await Mfa.FindActiveSecretAsync(user.Id, default))!;
        Assert.Equal(secret, active.Secret);
        Assert.Equal(Step, active.LastAcceptedStep);
        Assert.Equal(1L, await Count($"{TO.State} = 1"));
    }

    [PostgresFact]
    public async Task A_user_who_is_RecoveryRequired_can_never_activate_a_secret()
    {
        await Setup();
        var user = await NewUser();
        await Exec($"UPDATE {U.Table} SET {M.State} = 2 WHERE {U.Id} = @u", ("u", user.Id));
        await Exec($"UPDATE {U.Table} SET {M.State} = 3 WHERE {U.Id} = @u", ("u", user.Id));                     // along the allowed path None -> Enrolled -> RecoveryRequired
        var id = await Mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null);
        Assert.False(await Mfa.ActivatePendingAsync(user.Id, id, Step, null));
        Assert.Equal(MfaState.RecoveryRequired, await Mfa.GetStateAsync(user.Id));
        Assert.Equal(0L, await Count($"{TO.State} = 1"));
    }

    [PostgresFact]
    public async Task Re_enrolment_replaces_the_active_secret_and_a_second_pending_revokes_the_first()
    {
        await Setup();
        var (user, first, _) = await Enrolled();
        var p1 = await Mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null);
        var secret2 = Totp.GenerateSecret();
        var p2 = await Mfa.CreatePendingAsync(user.Id, secret2, null);                                           // replaces p1
        Assert.Equal(1L, await Count($"{TO.State} = 0"));
        Assert.Equal(1L, await Count($"{TO.SecretId} = '{p1}' AND {TO.State} = 2"));
        Assert.False(await Mfa.ActivatePendingAsync(user.Id, p1, Step + 1, null));                               // the revoked pending cannot be activated
        Assert.True(await Mfa.ActivatePendingAsync(user.Id, p2, Step + 1, null));
        Assert.Equal(1L, await Count($"{TO.State} = 1"));
        using var active = (await Mfa.FindActiveSecretAsync(user.Id, default))!;
        Assert.Equal(secret2, active.Secret);
        Assert.NotEqual(first, active.Secret);
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(user.Id));
    }

    [PostgresFact]
    public async Task The_database_refuses_a_second_active_and_a_second_pending_secret_for_one_user()
    {
        await Setup();
        var (user, _, _) = await Enrolled();
        string Insert(int state) =>
            $"INSERT INTO {TO.Table} ({TO.SecretId}, {TO.UserId}, {TO.Cipher}, {TO.Nonce}, {TO.Tag}, {TO.KekId}, {TO.State}, {TO.CreatedAtUtc}, {TO.ConfirmedAtUtc}) " +
            $"SELECT gen_random_uuid(), {TO.UserId}, {TO.Cipher}, {TO.Nonce}, {TO.Tag}, {TO.KekId}, {state}, {TO.CreatedAtUtc}, {(state == 1 ? "now()" : "NULL")} FROM {TO.Table} WHERE {TO.UserId} = @u AND {TO.State} = 1 LIMIT 1";
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert(1), ("u", user.Id)));
        Assert.Equal("23505", ex.SqlState);
        await Exec(Insert(0), ("u", user.Id));                                                                  // one pending is fine
        var ex2 = await Assert.ThrowsAsync<PostgresException>(() => Exec(Insert(0), ("u", user.Id)));
        Assert.Equal("23505", ex2.SqlState);
    }

    [PostgresFact]
    public async Task The_guard_trigger_forbids_deleting_truncating_rewriting_or_regressing_a_secret()
    {
        await Setup();
        var (user, _, id) = await Enrolled();
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {TO.Table} WHERE {TO.SecretId} = @i", ("i", id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"TRUNCATE {TO.Table}"));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {TO.Table} SET {TO.Cipher} = {TO.Cipher} || ''::bytea, {TO.Nonce} = decode('000000000000000000000001', 'hex') WHERE {TO.SecretId} = @i", ("i", id)));   // material changes only through rewrap
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {TO.Table} SET {TO.UserId} = gen_random_uuid() WHERE {TO.SecretId} = @i", ("i", id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {TO.Table} SET {TO.LastStep} = {TO.LastStep} - 1 WHERE {TO.SecretId} = @i", ("i", id)));   // the accepted step never decreases
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {TO.Table} SET {TO.LastStep} = NULL WHERE {TO.SecretId} = @i", ("i", id)));                 // and is never cleared (review L1)
        await Exec($"UPDATE {TO.Table} SET {TO.State} = 2 WHERE {TO.SecretId} = @i", ("i", id));               // Active -> Revoked is allowed
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {TO.Table} SET {TO.State} = 1 WHERE {TO.SecretId} = @i", ("i", id)));                // Revoked is terminal
    }

    /// <summary>Review M1: the database refuses every MFA state change the graph does not allow, and the SQL guard and <see cref="MfaStateGraph"/> agree on every one of the 16 combinations.</summary>
    [PostgresFact]
    public async Task The_database_guard_on_the_user_row_allows_exactly_the_transitions_of_the_graph()
    {
        await Setup();
        // the states are reached only along allowed transitions: None -> EnrolmentRequired -> Enrolled -> RecoveryRequired
        async Task<Guid> UserIn(MfaState target, string login)
        {
            var u = await NewUser(login);
            foreach (var step in new[] { MfaState.EnrolmentRequired, MfaState.Enrolled, MfaState.RecoveryRequired })
            {
                if (target == MfaState.None) break;
                await Exec($"UPDATE {U.Table} SET {M.State} = @s WHERE {U.Id} = @u", ("s", (short)step), ("u", u.Id));
                if (step == target) break;
            }
            Assert.Equal(target, await Mfa.GetStateAsync(u.Id));
            return u.Id;
        }
        var n = 0;
        foreach (var from in Enum.GetValues<MfaState>())
            foreach (var to in Enum.GetValues<MfaState>())
            {
                var user = await UserIn(from, $"guard{n++}");
                var graphAllows = from == to || Enum.GetValues<MfaStateCause>().Any(c => MfaStateGraph.IsAllowed(from, to, c));
                var attempt = Exec($"UPDATE {U.Table} SET {M.State} = @s WHERE {U.Id} = @u", ("s", (short)to), ("u", user));
                if (graphAllows) { await attempt; Assert.Equal(to, await Mfa.GetStateAsync(user)); }
                else
                {
                    await Assert.ThrowsAsync<PostgresException>(() => attempt);
                    Assert.Equal(from, await Mfa.GetStateAsync(user));                                           // refused: unchanged
                }
            }
    }

    [PostgresFact]
    public async Task Concurrent_pending_creations_of_one_user_never_fail_and_leave_exactly_one_pending_secret()
    {
        await Setup();
        var user = await NewUser();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null))));   // review L3: the user row lock serializes them
        Assert.Equal(1L, await Count($"{TO.UserId} = '{user.Id}' AND {TO.State} = 0"));
        Assert.Equal(7L, await Count($"{TO.UserId} = '{user.Id}' AND {TO.State} = 2"));
    }

    [PostgresFact]
    public async Task The_replay_compare_and_set_lets_exactly_one_of_many_concurrent_requests_win_and_never_goes_back()
    {
        await Setup();
        var (user, _, id) = await Enrolled(step: 1000);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Mfa.TryAdvanceLastStepAsync(id, 1001, default))));
        Assert.Equal(1, results.Count(r => r));
        Assert.False(await Mfa.TryAdvanceLastStepAsync(id, 1001, default));                                      // the same step again
        Assert.False(await Mfa.TryAdvanceLastStepAsync(id, 1000, default));                                      // an older one
        Assert.True(await Mfa.TryAdvanceLastStepAsync(id, 1005, default));
        Assert.Equal(1005L, await Scalar<long>($"SELECT {TO.LastStep} FROM {TO.Table} WHERE {TO.SecretId} = @i", ("i", id)));
    }

    [PostgresFact]
    public async Task Rewrap_moves_secrets_to_the_active_key_idempotently_and_the_old_key_can_then_be_retired()
    {
        await Setup();
        var (user, secret, _) = await Enrolled();
        Assert.Equal(1, (await Mfa.ReferencedKeysAsync())["k1"]);
        ring.Add("k2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        ring.Activate("k2");

        var first = await Mfa.RewrapAsync("k2", 10, null);
        Assert.Equal((1, 0), (first.Rewrapped, first.Remaining));
        var again = await Mfa.RewrapAsync("k2", 10, null);
        Assert.Equal((0, 0), (again.Rewrapped, again.Remaining));
        var keys = await Mfa.ReferencedKeysAsync();
        Assert.Equal(["k2"], keys.Keys);
        ring.Remove("k1");                                                                                    // retired
        using var active = (await Mfa.FindActiveSecretAsync(user.Id, default))!;
        Assert.Equal(secret, active.Secret);
        Assert.Equal(1L, await Events(MfaAuditEvents.SecretsRewrapped) - 1);                                  // two calls, two events
    }

    [PostgresFact]
    public async Task A_missing_key_fails_closed_without_changing_any_state()
    {
        await Setup();
        var (user, _, _) = await Enrolled();
        ring.Unavailable = true;
        var ex = await Assert.ThrowsAsync<MfaUnavailableException>(() => Mfa.FindActiveSecretAsync(user.Id, default));
        Assert.Equal("totp_key_unavailable", ex.Code);
        ring.Unavailable = false;
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(user.Id));
    }

    [PostgresFact]
    public async Task State_changes_follow_the_graph_use_compare_and_set_and_are_audited()
    {
        await Setup();
        var user = await NewUser();
        var before = await Events(MfaAuditEvents.StateChanged);
        Assert.False(await Mfa.TryChangeStateAsync(user.Id, MfaState.EnrolmentRequired, MfaState.None, MfaStateCause.Recovery, null));          // never back to None
        Assert.False(await Mfa.TryChangeStateAsync(user.Id, MfaState.Enrolled, MfaState.EnrolmentRequired, MfaStateCause.Recovery, null));       // the stored state is None, not Enrolled
        Assert.True(await Mfa.TryChangeStateAsync(user.Id, MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement, null));
        Assert.False(await Mfa.TryChangeStateAsync(user.Id, MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement, null));        // already changed
        Assert.Equal(MfaState.EnrolmentRequired, await Mfa.GetStateAsync(user.Id));
        Assert.Equal(before + 1, await Events(MfaAuditEvents.StateChanged));                                                                      // only the successful change is audited
        Assert.Null(await Mfa.GetStateAsync(Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task The_restore_fix_up_marks_enrolled_users_without_a_secret_and_invalidates_unconsumed_enrolment_codes()
    {
        await Setup();
        var (withSecret, _, _) = await Enrolled("with-secret");
        var noSecret = await NewUser("no-secret");
        var required = await NewUser("required");
        var codeHolder = await NewUser("code-holder");
        var consumed = await NewUser("consumed");
        await Exec($"UPDATE {U.Table} SET {M.State} = 2 WHERE {U.Id} = @u", ("u", noSecret.Id));                 // as after a restore: the row says Enrolled, the secret store is empty
        await Exec($"UPDATE {U.Table} SET {M.State} = 1 WHERE {U.Id} = @u", ("u", required.Id));
        await Exec($"UPDATE {U.Table} SET {M.EnrolmentCodeHash} = decode(repeat('ab', 32), 'hex'), {M.EnrolmentCodeExpiresAtUtc} = now() + interval '1 day' WHERE {U.Id} = @u", ("u", codeHolder.Id));
        await Exec($"UPDATE {U.Table} SET {M.EnrolmentCodeHash} = decode(repeat('cd', 32), 'hex'), {M.EnrolmentCodeExpiresAtUtc} = now() + interval '1 day', {M.EnrolmentCodeConsumedAtUtc} = now() WHERE {U.Id} = @u", ("u", consumed.Id));

        var result = await Mfa.ApplyRestoreAsync(differentInstallation: false, actor: null);
        Assert.Equal(1, result.UsersMarkedRecoveryRequired);
        Assert.Equal(1, result.EnrolmentCodesInvalidated);                                                      // the consumed one is left as history
        Assert.Equal(MfaState.RecoveryRequired, await Mfa.GetStateAsync(noSecret.Id));
        Assert.Equal(MfaState.Enrolled, await Mfa.GetStateAsync(withSecret.Id));
        Assert.Equal(MfaState.EnrolmentRequired, await Mfa.GetStateAsync(required.Id));
        Assert.Equal(MfaState.None, await Mfa.GetStateAsync(codeHolder.Id));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {U.Table} WHERE {U.Id} = @u AND {M.EnrolmentCodeHash} IS NOT NULL", ("u", codeHolder.Id)));

        var different = await Mfa.ApplyRestoreAsync(differentInstallation: true, actor: null);                  // D-107: another installation id
        Assert.Equal(1, different.UsersMarkedRecoveryRequired);
        Assert.Equal(MfaState.RecoveryRequired, await Mfa.GetStateAsync(withSecret.Id));
        Assert.Equal(MfaState.EnrolmentRequired, await Mfa.GetStateAsync(required.Id));                         // never touches users who are not Enrolled
    }

    [PostgresFact]
    public async Task Every_mutation_writes_a_system_audit_event_and_the_stream_still_verifies()
    {
        await Setup();
        var (user, _, id) = await Enrolled();
        await Mfa.RevokeAllAsync(user.Id, null, "test");
        Assert.True(await Events(MfaAuditEvents.SecretCreated) >= 1);
        Assert.True(await Events(MfaAuditEvents.SecretActivated) >= 1);
        Assert.True(await Events(MfaAuditEvents.StateChanged) >= 1);
        Assert.True(await Events(MfaAuditEvents.SecretsRevoked) >= 1);
        Assert.Equal(0L, await Count($"{TO.State} IN (0, 1)"));
        var report = await new AuditVerifier(Options.BuildSystemConnectionString(admin: true), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.System);
        Assert.True(report.Verdict == AuditVerdict.Verified, $"{report.Verdict}: {string.Join(", ", report.Findings.Select(f => $"{f.Code}@{f.Seq}"))}");
    }

    [PostgresFact]
    public async Task A_failed_audit_write_leaves_no_secret_or_state_change_behind()
    {
        await Setup();
        var user = await NewUser();
        AuditKeys.Unavailable = true;
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Mfa.CreatePendingAsync(user.Id, Totp.GenerateSecret(), null));
        await Assert.ThrowsAsync<AuditUnavailableException>(() => Mfa.TryChangeStateAsync(user.Id, MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement, null));
        AuditKeys.Unavailable = false;
        Assert.Equal(0L, await Count("true"));
        Assert.Equal(MfaState.None, await Mfa.GetStateAsync(user.Id));
    }
}
