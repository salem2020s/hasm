using System.Net;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Npgsql;
using A = Hasm.Infrastructure.SystemDatabase.SystemSchema.IdentityAnchor;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;
using CT = Hasm.Infrastructure.SystemDatabase.SystemSchema.ChallengeTag;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Identity integrity slice S-2c part 1 (docs/IDENTITY-INTEGRITY-PLAN.md section 5, owner decision OD-4) against real PostgreSQL with the ACTUAL restricted runtime role and the real API: the append-only identity anchor
/// (a valid anchor above a record refuses it, junk anchors are ignored and reported and never lock anybody out, the table cannot be changed), the margin of the challenge delete guard (review L3 of PR 237), and the
/// read-only identity audit report (net-state reconciliation of the user rows against the verified audit chain). Role-capable disposable cluster only.
/// </summary>
public class IdentityAnchorTests : RestrictedRuntimeIdentityTestBase
{
    private Task<long> Anchors(Guid user) => Scalar<long>($"SELECT count(*) FROM {A.Table} WHERE {A.SubjectId} = @u", ("u", user));

    private async Task InsertAnchor(Guid subject, long revision, string keyId, byte[] tag) =>
        await Attack($"INSERT INTO {A.Table} ({A.Id},{A.Kind},{A.SubjectId},{A.Revision},{A.KeyId},{A.Tag},{A.CreatedAtUtc}) VALUES (@i,1,@s,@r,@k,@t,now())", ("i", Guid.NewGuid()), ("s", subject), ("r", revision), ("k", keyId), ("t", tag));

    [PostgresRoleFact]
    public async Task Legitimate_changes_write_one_valid_anchor_each_and_the_table_cannot_be_changed()
    {
        await StartRestricted();
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        var user = (await auth.CreateUserAsync("alice", "Alice", Pw, false)).User!;
        Assert.Equal(1L, await Anchors(user.Id));                                                                    // revision 0 at creation
        var session = Assert.IsType<LoginOutcome.Success>(await auth.LoginAsync("alice", Pw, SessionClient.Web, "10.0.0.1"));
        await store.UpdatePasswordHashAsync(user.Id, Passwords.Hash(Pw), passwordChanged: false);                    // a transparent rehash is not a security change
        Assert.Equal(1L, await Anchors(user.Id));
        Assert.Equal(ChangePasswordStatus.Changed, (await auth.ChangePasswordAsync((await auth.AuthenticateAsync(session.Token))!, Pw, "Replacement-Passphrase-3")).Status);
        Assert.Equal(3L, await Anchors(user.Id));                                                                    // the hash change and the revoke-all of the other sessions: revisions 1 and 2
        // every anchor verifies under the key and the highest is the user's revision
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        var bound = await IdentityAnchors.ReadBoundAsync(conn, null, Keys, IdentityAnchors.KindUser, user.Id, default);
        Assert.Equal((2L, 0), (bound.MaxValidRevision, bound.Ignored));
        Assert.Equal(2L, await Scalar<long>($"SELECT {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));
        // append-only: not even the owner can change or remove an anchor, and the runtime role holds no such privilege
        var ownerEx = await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {A.Table} SET {A.Revision} = 99 WHERE {A.SubjectId} = @u", ("u", user.Id)));
        Assert.Contains("append-only", ownerEx.MessageText);
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {A.Table} WHERE {A.SubjectId} = @u", ("u", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Attack($"UPDATE {A.Table} SET {A.Revision} = 99 WHERE {A.SubjectId} = @u", ("u", user.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Attack($"DELETE FROM {A.Table} WHERE {A.SubjectId} = @u", ("u", user.Id)));
        Assert.Equal(3L, await Anchors(user.Id));
    }

    [PostgresRoleFact]
    public async Task Junk_anchors_are_ignored_and_reported_and_never_lock_a_user_or_an_administrator_out()
    {
        await StartRestricted();
        var admin = await NewUser("root", admin: true);
        var user = await NewUser("alice");
        using var api = Api(); using var client = NewClient(api);
        foreach (var target in new[] { admin, user })
        {
            var before = await Rejections();
            await InsertAnchor(target.Id, long.MaxValue / 2, "k1", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));    // a huge revision with a garbage tag
            await InsertAnchor(target.Id, 1_000_000, "unknown-key", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));      // an unknown key id
            await InsertAnchor(target.Id, 5, "k1", new byte[32]);                                                                          // a plausible one with a bad tag
            await Assert.ThrowsAsync<PostgresException>(() => Attack($"INSERT INTO {A.Table} ({A.Id},{A.Kind},{A.SubjectId},{A.Revision},{A.KeyId},{A.Tag},{A.CreatedAtUtc}) VALUES (@i,1,@s,1,'k1',@t,now())", ("i", Guid.NewGuid()), ("s", target.Id), ("t", new byte[3])));   // malformed: refused by the shape check
            Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = target.Login, password = Pw, client = "web" })).Status);   // still signs in
            Assert.True(await Rejections() > before);                                                                                      // and the operator is told
            Assert.Contains("anchors ignored", await Scalar<string>($"SELECT {Ev.Detail} FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected} ORDER BY {Ev.Seq} DESC LIMIT 1"));
        }
    }

    [PostgresRoleFact]
    public async Task A_valid_anchor_above_the_row_refuses_it_an_older_once_valid_record_cannot_be_written_back()
    {
        await StartRestricted();
        var user = await NewUser("alice");
        using var api = Api(); using var client = NewClient(api);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "alice", password = Pw, client = "web" })).Status);
        // what a rolled-back row looks like to the verifier: the history holds a VALID anchor (made here with the key) above the row's revision
        await using (var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await IdentityAnchors.WriteAsync(conn, tx, Keys, IdentityAnchors.KindUser, user.Id, 7, default);
            await tx.CommitAsync();
        }
        var attempt = await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "alice", password = Pw, client = "web" });
        Assert.Equal(HttpStatusCode.Unauthorized, attempt.Status);
        Assert.DoesNotContain("anchor", attempt.Body, StringComparison.OrdinalIgnoreCase);                          // neutral
        Assert.Contains("rejected: anchor", await Scalar<string>($"SELECT {Ev.Detail} FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected} ORDER BY {Ev.Seq} DESC LIMIT 1"));
    }

    [PostgresRoleFact]
    public async Task The_challenge_delete_guard_has_a_margin_of_an_hour_and_the_expiry_purge_still_works()
    {
        // review L3 of PR 237: a clock difference between the application and the database must never let a live challenge be deleted
        await StartRestricted();
        var user = await NewUser("victim");
        async Task<Guid> Row(DateTimeOffset expires)
        {
            var key = Keys.Active(); var created = expires.AddMinutes(-5);
            var row = new ChallengeRow(Guid.NewGuid(), user.Id, Keys.TokenHash(key, SessionToken.Generate()), 0, 0, SessionToken.HashKey("mfa-source:10.0.0.1"), MacInput.Truncate(created), MacInput.Truncate(expires), key.Id, null);
            row = row with { Tag = row.ComputeTag(Keys, key) };
            await Attack($"INSERT INTO {C.Table} ({C.Id},{C.UserId},{C.TokenHash},{C.Purpose},{C.ClientKind},{C.SourceHash},{C.CreatedAtUtc},{C.ExpiresAtUtc},{CT.KeyId},{CT.Tag}) VALUES (@i,@u,@h,@p,@c,@s,@n,@e,@k,@g)",
                ("i", row.Id), ("u", row.UserId), ("h", row.TokenHash), ("p", row.Purpose), ("c", row.ClientKind), ("s", row.SourceHash), ("n", row.CreatedAt), ("e", row.ExpiresAt), ("k", row.KeyId), ("g", row.Tag));
            return row.Id;
        }
        var live = await Row(DateTimeOffset.UtcNow.AddMinutes(5));
        var justExpired = await Row(DateTimeOffset.UtcNow.AddMinutes(-30));                                         // expired, but not for an hour yet
        var longExpired = await Row(DateTimeOffset.UtcNow.AddHours(-3));
        foreach (var id in new[] { live, justExpired })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Attack($"DELETE FROM {C.Table} WHERE {C.Id} = @i", ("i", id)));
            Assert.Contains("expired", ex.MessageText);
        }
        Assert.Equal(1, await Attack($"DELETE FROM {C.Table} WHERE {C.Id} = @i", ("i", longExpired)));              // an hour past expiry it may go
        await using var challenges = new MfaChallengeStore(() => RuntimeConnection, TimeProvider.System, keys: Keys);
        await Row(DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Equal(1, await challenges.PurgeAsync(DateTimeOffset.UtcNow.AddDays(-1)));                              // the housekeeping purge (rows expired for a day) is unaffected
        Assert.Equal(2L, await Scalar<long>($"SELECT count(*) FROM {C.Table} WHERE {C.Id} IN (@a, @b)", ("a", live), ("b", justExpired)));
    }

    // ---------------------------------------------------------------- the report

    private async Task<IdentityAuditReportResult> Report(bool withKeys = true)
    {
        var connection = Options.BuildSystemConnectionString(admin: true);
        return await IdentityAuditReport.BuildAsync(connection, new AuditVerifier(connection, AuditKeys, AuditCheckpoints), withKeys ? Keys : null);
    }

    [PostgresRoleFact]
    public async Task The_report_matches_what_the_authenticated_events_produced_and_its_hash_is_stable()
    {
        await StartRestricted();
        await using var store = RuntimeStore(); await using var mfa = RuntimeMfa();
        var auth = RuntimeAuth(store);
        var admin = (await auth.CreateUserAsync("boss", "Boss", Pw, true)).User!;
        var alice = (await auth.CreateUserAsync("alice", "Alice", Pw, false)).User!;
        var bob = (await auth.CreateUserAsync("bob", "Bob", Pw, false)).User!;
        Assert.Equal(SetStateOutcome.Changed, await store.SetStateAsync(admin.Id, bob.Id, UserState.Disabled));
        Assert.True(await mfa.TryChangeStateAsync(alice.Id, MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement, null));
        var first = await Report();
        Assert.False(first.Refused, first.Refusal);
        Assert.Equal(3, first.Entries.Count);
        Assert.All(first.Entries, e => { Assert.Equal(IdentityAuditReport.Matched, e.Disposition); Assert.Equal("valid", e.Tag); });
        Assert.Equal(first.Hash, (await Report()).Hash);                                                              // the same state, the same hash: the review has an identity
        Assert.Contains("Report hash:", IdentityAuditReport.Render(first));
        Assert.DoesNotContain(Pw, IdentityAuditReport.Render(first));
        await store.UpdatePasswordHashAsync(alice.Id, Passwords.Hash(Pw), passwordChanged: false);
        Assert.Equal(first.Hash, (await Report()).Hash);                                                              // a rehash changes no reviewed fact
    }

    [PostgresRoleFact]
    public async Task The_report_flags_every_row_the_net_state_does_not_support_and_a_broken_chain_refuses_it()
    {
        await StartRestricted();
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        await auth.CreateUserAsync("boss", "Boss", Pw, true);
        var plain = (await auth.CreateUserAsync("plain", "Plain", Pw, false)).User!;
        var gone = (await auth.CreateUserAsync("gone", "Gone", Pw, false)).User!;
        var enrolled = (await auth.CreateUserAsync("mfa", "Mfa", Pw, false)).User!;
        var clean = await Report();
        // the attacks, all written as the runtime role: a minted administrator, an escalated flag, a re-activated user, a changed MFA state
        await Attack($"INSERT INTO {U.Table} ({U.Id},{U.Login},{U.DisplayName},{U.PasswordHash},{U.State},{U.IsIdentityAdministrator},{U.CreatedAtUtc},{U.StateChangedAtUtc},{U.PasswordChangedAtUtc}) VALUES (@i,'mallory','M',@h,0,true,now(),now(),now())", ("i", Guid.NewGuid()), ("h", Passwords.Hash(Pw)));
        await Attack($"UPDATE {U.Table} SET {U.IsIdentityAdministrator} = true WHERE {U.Id} = @u", ("u", plain.Id));
        Assert.Equal(SetStateOutcome.Changed, await store.SetStateAsync(null, gone.Id, UserState.Disabled));
        await Attack($"UPDATE {U.Table} SET {U.State} = 0 WHERE {U.Id} = @u", ("u", gone.Id));                          // re-activated by SQL: the events say Disabled
        await Attack($"UPDATE {U.Table} SET {M.State} = 2 WHERE {U.Id} = @u", ("u", enrolled.Id));                      // Enrolled by SQL: the events say None
        var report = await Report();
        Assert.False(report.Refused);
        Assert.Equal(1, report.Count(IdentityAuditReport.OrphanNoCreationEvent));
        Assert.Equal(1, report.Count(IdentityAuditReport.MismatchAdministrator));
        Assert.Equal(1, report.Count(IdentityAuditReport.MismatchState));
        Assert.Equal(1, report.Count(IdentityAuditReport.MismatchMfa));
        Assert.Equal(1, report.Count(IdentityAuditReport.Matched));                                                    // only the administrator that was never touched
        Assert.Equal(["mallory"], report.Entries.Where(e => e.Disposition == IdentityAuditReport.OrphanNoCreationEvent).Select(e => e.Login));
        Assert.Equal(1, report.Entries.Count(e => e.Eligible));
        Assert.NotEqual(clean.Hash, report.Hash);
        // a stream that does not verify refuses the report
        await using (var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"ALTER TABLE {Ev.Table} DISABLE TRIGGER {Ev.GuardTrigger}; UPDATE {Ev.Table} SET {Ev.Detail} = 'tampered' WHERE {Ev.Seq} = 2; ALTER TABLE {Ev.Table} ENABLE TRIGGER {Ev.GuardTrigger};", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var refused = await Report();
        Assert.True(refused.Refused);
        Assert.Contains("does not verify", refused.Refusal);
        Assert.Empty(refused.Entries);
    }
}
