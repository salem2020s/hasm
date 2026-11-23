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
    public async Task The_database_refuses_an_anchor_that_is_not_for_the_current_revision_so_no_junk_can_exist_above_a_record_and_junk_never_locks_anybody_out()
    {
        // review H1 of PR 239: the bound is protected in the database, not by the size of a scan window
        await StartRestricted();
        var admin = await NewUser("root", admin: true);
        var user = await NewUser("alice");
        using var api = Api(); using var client = NewClient(api);
        foreach (var target in new[] { admin, user })
        {
            // a healthy subject: its current revision already has its legitimate anchor, so every junk row is refused: above, below, equal (a duplicate), unknown key id, malformed
            foreach (var (revision, keyId) in new[] { (long.MaxValue / 2, "k1"), (1_000_000L, "unknown-key"), (5L, "k1"), (0L, "k1") })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAnchor(target.Id, revision, keyId, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
                Assert.True(ex.SqlState is PostgresErrorCodes.RaiseException or PostgresErrorCodes.UniqueViolation, ex.MessageText);
            }
            await Assert.ThrowsAsync<PostgresException>(() => InsertAnchor(target.Id, 0, "k1", new byte[3]));                                // malformed: refused by the shape check
            Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = target.Login, password = Pw, client = "web" })).Status);   // nothing changed for anybody
            Assert.Equal(1L, await Anchors(target.Id));
        }
        // the only junk that can exist is one for the revision of a record the attacker has already made unverifiable (revision +1 invalidates its tag): it is ignored, reported and moves nothing
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.SecurityRevision} = {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.SecurityRevision} + 1 WHERE {U.Id} = @u", ("u", user.Id)));
        await InsertAnchor(user.Id, 1, "k1", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "alice", password = Pw, client = "web" })).Status);   // the row does not verify anyway
        await Assert.ThrowsAsync<PostgresException>(() => InsertAnchor(user.Id, 2, "k1", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));   // and no anchor above the row exists
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "root", password = Pw, client = "web" })).Status);
    }

    [PostgresRoleFact]
    public async Task A_window_filled_with_junk_is_an_unknown_bound_and_fails_closed_never_no_bound()
    {
        // the database no longer lets the flood happen; if it ever did (an owner-level write, an older database), the verifier must refuse, not forget the bound
        await StartRestricted();
        var user = await NewUser("alice");
        using var api = Api(); using var client = NewClient(api);
        await using (var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"ALTER TABLE {A.Table} DISABLE TRIGGER {A.TrCurrentRevision}; " +
                $"INSERT INTO {A.Table} ({A.Id},{A.Kind},{A.SubjectId},{A.Revision},{A.KeyId},{A.Tag},{A.CreatedAtUtc}) SELECT gen_random_uuid(),1,@s,g,'k1',decode(repeat('ab',32),'hex'),now() FROM generate_series(1,{IdentityAnchors.ScanLimit + 5}) g; " +
                $"ALTER TABLE {A.Table} ENABLE TRIGGER {A.TrCurrentRevision};", conn);
            cmd.Parameters.AddWithValue("s", user.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await conn.OpenAsync();
            var bound = await IdentityAnchors.ReadBoundAsync(conn, null, Keys, IdentityAnchors.KindUser, user.Id, default);
            Assert.True(bound.Unknown);
            Assert.Null(bound.MaxValidRevision);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "alice", password = Pw, client = "web" })).Status);
        Assert.Contains("anchor scan limit", await Scalar<string>($"SELECT {Ev.Detail} FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected} ORDER BY {Ev.Seq} DESC LIMIT 1"));
    }

    [PostgresRoleFact]
    public async Task A_user_row_rolled_back_below_its_valid_anchors_is_refused_even_when_the_old_row_and_its_tag_are_genuine()
    {
        // what an older once-valid row written back looks like to the verifier: the history holds valid anchors above the row's revision (here the rollback is made as the owner, with the guard of the revision disabled:
        // the runtime role cannot do it, the database refuses a lower revision; this is the offline, database-only manipulation OD-4 asks to be impossible to exploit)
        await StartRestricted();
        await using var store = RuntimeStore();
        var user = await NewUser("alice");
        using var api = Api(); using var client = NewClient(api);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "alice", password = Pw, client = "web" })).Status);
        for (var i = 0; i < 7; i++) await store.RevokeUserSessionsAsync(user.Id, SessionRevocation.Administrative);       // revisions 1 to 7, an anchor each
        Assert.Equal(8L, await Anchors(user.Id));
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "alice", password = Pw, client = "web" })).Status);
        await using (var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var off = new NpgsqlCommand($"ALTER TABLE {U.Table} DISABLE TRIGGER {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.TrRevisionGuard}; UPDATE {U.Table} SET {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.SecurityRevision} = 1 WHERE {U.Id} = @u; ALTER TABLE {U.Table} ENABLE TRIGGER {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.TrRevisionGuard};", conn, tx))
            {
                off.Parameters.AddWithValue("u", user.Id);
                await off.ExecuteNonQueryAsync();
            }
            var row = (await UserRow.LoadAsync(conn, tx, user.Id, false, default))!;
            var key = Keys.Active();
            row = row with { KeyId = key.Id, Tag = null };
            await using (var tag = new NpgsqlCommand($"UPDATE {U.Table} SET {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag.KeyId} = @k, {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag.Tag} = @t WHERE {U.Id} = @u", conn, tx))
            {
                tag.Parameters.AddWithValue("k", key.Id); tag.Parameters.AddWithValue("t", row.ComputeTag(Keys, key)); tag.Parameters.AddWithValue("u", user.Id);
                await tag.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        Assert.True(await UserTagIsValidAsync(user.Id));                                                                  // the rolled-back row is a perfectly genuine, validly tagged row
        var attempt = await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login = "alice", password = Pw, client = "web" });
        Assert.Equal(HttpStatusCode.Unauthorized, attempt.Status);
        Assert.DoesNotContain("anchor", attempt.Body, StringComparison.OrdinalIgnoreCase);                          // neutral
        Assert.Contains("rejected: anchor", await Scalar<string>($"SELECT {Ev.Detail} FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected} ORDER BY {Ev.Seq} DESC LIMIT 1"));
    }

    private async Task<bool> UserTagIsValidAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        return (await UserRow.LoadAsync(conn, tx, id, false, default))!.IsValid(Keys);
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
        Assert.NotEqual(first.Hash, (await Report()).Hash);                                                           // a rehash writes a new password hash: the hash covers it (review M1 of PR 239), so the review must be repeated
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

    [PostgresRoleFact]
    public async Task The_report_hash_covers_every_field_the_user_tag_covers()
    {
        // review M1 of PR 239: the tagging of part 2 is bound to this hash, so a change after the review must change it
        await StartRestricted();
        await using var mfa = RuntimeMfa();
        var users = new List<UserRecord>();
        foreach (var name in new[] { "usera", "userb", "userc", "userd", "usere", "userf" }) users.Add(await NewUser(name));
        foreach (var u in users) await mfa.IssueEnrolmentCodeAsync(u.Id, TimeSpan.FromHours(1), null);               // every user holds a code, so each code column can change
        var clean = (await Report(withKeys: false)).Hash;                                                   // without keys the tag status is constant, so only the digest of the covered fields can move the hash
        var changes = new (string Name, string Set)[]
        {
            ("password hash", $"{U.PasswordHash} = {U.PasswordHash} || ''"),                                           // placeholder replaced below
            ("security revision", $"{Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.SecurityRevision} = {Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity.SecurityRevision} + 1"),
            ("enrolment code hash", $"{M.EnrolmentCodeHash} = decode(repeat('cd', 32), 'hex')"),
            ("enrolment code expiry", $"{M.EnrolmentCodeExpiresAtUtc} = {M.EnrolmentCodeExpiresAtUtc} + interval '1 hour'"),
            ("enrolment code consumed", $"{M.EnrolmentCodeConsumedAtUtc} = now()"),
            ("key id", $"{Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag.KeyId} = 'k2'"),
        };
        var seen = new HashSet<string> { clean };
        for (var i = 0; i < changes.Length; i++)
        {
            var (name, set) = changes[i];
            if (name == "password hash") Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {U.PasswordHash} = @h WHERE {U.Id} = @u", ("h", Passwords.Hash("Attacker-Chosen-Password-1")), ("u", users[i].Id)));
            else Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {set} WHERE {U.Id} = @u", ("u", users[i].Id)));
            var hash = (await Report(withKeys: false)).Hash;
            Assert.True(seen.Add(hash), $"the hash did not change for: {name}");
        }
    }

    [PostgresRoleFact]
    public async Task The_report_replays_only_what_the_verification_covered()
    {
        // review M2 of PR 239: an event appended between the verification and the replay is never replayed (the runtime role holds INSERT on the audit table)
        await StartRestricted();
        await using var store = RuntimeStore();
        var auth = RuntimeAuth(store);
        await auth.CreateUserAsync("boss", "Boss", Pw, true);
        var minted = Guid.NewGuid();
        await Attack($"INSERT INTO {U.Table} ({U.Id},{U.Login},{U.DisplayName},{U.PasswordHash},{U.State},{U.IsIdentityAdministrator},{U.CreatedAtUtc},{U.StateChangedAtUtc},{U.PasswordChangedAtUtc}) VALUES (@i,'mallory','M',@h,0,true,now(),now(),now())", ("i", minted), ("h", Passwords.Hash(Pw)));
        var connection = Options.BuildSystemConnectionString(admin: true);
        var report = await IdentityAuditReport.BuildAsync(connection, new AuditVerifier(connection, AuditKeys, AuditCheckpoints), Keys, async () =>
        {
            // the forged creation event for the minted administrator, appended after the verification, with an arbitrary chain
            var map = AuditTableMap.System;
            var last = await Scalar<long>($"SELECT max({Ev.Seq}) FROM {Ev.Table} WHERE {Ev.StreamKey} = 'system'");
            await using var conn = new NpgsqlConnection(RuntimeConnection);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(map.InsertSql, conn);
            cmd.Parameters.AddWithValue("s", "system"); cmd.Parameters.AddWithValue("q", last + 1); cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("t", DateTimeOffset.UtcNow); cmd.Parameters.AddWithValue("e", (short)AuditEventTypes.UserCreated);
            cmd.Parameters.AddWithValue("c", DBNull.Value); cmd.Parameters.AddWithValue("a", DBNull.Value); cmd.Parameters.AddWithValue("tu", minted); cmd.Parameters.AddWithValue("su", DBNull.Value);
            cmd.Parameters.AddWithValue("l", DBNull.Value); cmd.Parameters.AddWithValue("d", "administrator: yes"); cmd.Parameters.AddWithValue("v", (short)1); cmd.Parameters.AddWithValue("k", "k1");
            cmd.Parameters.AddWithValue("p", new byte[32]); cmd.Parameters.AddWithValue("h", new byte[32]); cmd.Parameters.AddWithValue("m", new byte[32]);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.False(report.Refused, report.Refusal);
        Assert.Equal(IdentityAuditReport.OrphanNoCreationEvent, report.Entries.Single(e => e.Login == "mallory").Disposition);   // the forged event is beyond the verified sequence
    }

    [PostgresRoleFact]
    public async Task The_report_reconciles_the_enrolment_code_columns_and_marks_every_password_hash_as_needing_a_disposition()
    {
        await StartRestricted();
        await using var mfa = RuntimeMfa();
        var issued = await NewUser("issued"); var planted = await NewUser("planted"); var spent = await NewUser("spent");
        await mfa.IssueEnrolmentCodeAsync(issued.Id, TimeSpan.FromHours(1), null);
        // a code written straight into a row (what a pre-rollout tamper would do): no issue event supports it
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {M.EnrolmentCodeHash} = decode(repeat('ab', 32), 'hex'), {M.EnrolmentCodeExpiresAtUtc} = now() + interval '1 hour' WHERE {U.Id} = @u", ("u", planted.Id)));
        await mfa.IssueEnrolmentCodeAsync(spent.Id, TimeSpan.FromHours(1), null);
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {M.EnrolmentCodeConsumedAtUtc} = now() WHERE {U.Id} = @u", ("u", spent.Id)));   // marked spent with no activation after its issue
        var report = await Report();
        Assert.Equal(IdentityAuditReport.Matched, report.Entries.Single(e => e.Login == "issued").Disposition);
        Assert.Equal(IdentityAuditReport.MismatchCode, report.Entries.Single(e => e.Login == "planted").Disposition);
        Assert.Equal(IdentityAuditReport.MismatchCode, report.Entries.Single(e => e.Login == "spent").Disposition);
        var text = IdentityAuditReport.Render(report);
        Assert.Contains(IdentityReportEntry.PasswordHashSupport, text);
        Assert.Contains("password reset", text);
        Assert.DoesNotContain(Pw, text);
    }
}
