using System.Net;
using System.Text.Json;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Npgsql;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using M = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserMfa;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using UI = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserIntegrity;
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Identity integrity slice S-2a (docs/IDENTITY-INTEGRITY-PLAN.md section 5, vector B: the user row) against real PostgreSQL with the ACTUAL restricted runtime role and the REAL API: a user row written by that role
/// (minted, or an existing one changed in a covered column, the enrolment-code columns included) is no user: login, the session lookup and the enrolment checks refuse it neutrally and audit it; legitimate writers
/// keep the tag valid; the security-reducing writers still work on a row that does not verify and never bless it; no key means 503 and no user; the one-time tagging command; rotation; no deadlock between the writers.
/// Role-capable disposable cluster only.
/// </summary>
public class UserIntegrityTests : RestrictedRuntimeIdentityTestBase
{
    private async Task<(HttpStatusCode Status, string Body)> WebLogin(HttpClient client, string login, string password = Pw) =>
        await Send(client, HttpMethod.Post, "/api/v1/auth/login", null, new { login, password, client = "web" });

    private async Task<bool> UserTagIsValid(Guid id)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var row = await UserRow.LoadAsync(conn, tx, id, false, default);
        return row is not null && row.IsValid(Keys);
    }

    [PostgresRoleFact]
    public async Task A_user_minted_by_the_runtime_role_cannot_sign_in_and_is_audited()
    {
        await StartRestricted();
        await NewUser("root", admin: true);
        using var api = Api(); using var client = NewClient(api);
        var hash = Passwords.Hash(Pw);
        var before = await Rejections();
        // the attack of the plan: an identity administrator with a password of the attacker's choice, no key needed to write the row
        await Attack($"INSERT INTO {U.Table} ({U.Id},{U.Login},{U.DisplayName},{U.PasswordHash},{U.State},{U.IsIdentityAdministrator},{U.CreatedAtUtc},{U.StateChangedAtUtc},{U.PasswordChangedAtUtc}) VALUES (@i,'mallory','M',@h,0,true,now(),now(),now())", ("i", Guid.NewGuid()), ("h", hash));
        var (status, body) = await WebLogin(client, "mallory");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        var unknown = await WebLogin(client, "nobody-at-all");
        Assert.Equal((HttpStatusCode.Unauthorized, body), (unknown.Status, unknown.Body));                                   // the same neutral answer as an unknown login, byte for byte
        Assert.False(body.Contains("tag", StringComparison.OrdinalIgnoreCase) || body.Contains("integrity", StringComparison.OrdinalIgnoreCase));
        Assert.True(await Rejections() > before);
        // the same with a forged key id and a random tag: the best he can write without the key
        await Attack($"UPDATE {U.Table} SET {UT.KeyId} = 'k1', {UT.Tag} = @t WHERE {U.Login} = 'mallory'", ("t", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebLogin(client, "mallory")).Status);
    }

    [PostgresRoleFact]
    public async Task Every_tampered_covered_column_makes_the_user_refused_at_login_and_in_the_session_lookup_and_an_uncovered_column_does_not()
    {
        await StartRestricted();
        var knownHash = Passwords.Hash("Attacker-Chosen-Password-1");
        var cases = new (string Name, string Set, (string, object?)[] Params)[]
        {
            ("password hash", $"{U.PasswordHash} = @v", [("v", knownHash)]),
            ("lifecycle state", $"{U.State} = 2", []),
            ("identity administrator flag", $"{U.IsIdentityAdministrator} = true", []),
            ("mfa state", $"{M.State} = 1", []),
            ("enrolment code hash", $"{M.EnrolmentCodeHash} = @v", [("v", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))]),
            ("enrolment code expiry", $"{M.EnrolmentCodeExpiresAtUtc} = {M.EnrolmentCodeExpiresAtUtc} + interval '1 day'", []),
            ("enrolment code consumed", $"{M.EnrolmentCodeConsumedAtUtc} = now()", []),
            ("security revision (+1)", $"{UI.SecurityRevision} = {UI.SecurityRevision} + 1", []),
            ("key id", $"{UT.KeyId} = 'k2'", []),
            ("tag removed", $"{UT.Tag} = NULL, {UT.KeyId} = NULL", []),
        };
        using var api = Api(); using var client = NewClient(api);
        var n = 0;
        foreach (var (name, set, parameters) in cases)
        {
            var login = "victim" + n++;
            var user = await NewUser(login);
            await using (var legit = RuntimeMfa()) await legit.IssueEnrolmentCodeAsync(user.Id, TimeSpan.FromHours(1), null);   // every user holds a one-time code, so the code columns can be tampered one by one
            await using var store = RuntimeStore();
            var session = Assert.IsType<LoginOutcome.Success>(await RuntimeAuth(store).LoginAsync(login, Pw, SessionClient.Web, "10.0.0.1"));
            Assert.True(await IsAuthenticated(client, session.Token), name + " (baseline session)");
            Assert.Equal(HttpStatusCode.OK, (await WebLogin(client, login)).Status);
            var before = await Rejections();
            Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {set} WHERE {U.Id} = @id", parameters.Append(("id", (object?)user.Id)).ToArray()));
            var attempt = await WebLogin(client, login);
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.Status);                                           // neutral, whatever the attacker set (he cannot sign in as the victim with his own password either)
            Assert.DoesNotContain("tag", attempt.Body, StringComparison.OrdinalIgnoreCase);
            Assert.False(await IsAuthenticated(client, session.Token), name + " (existing session)");           // the per-request lookup checks the user too
            if (name != "lifecycle state") Assert.True(await IsRevoked(session.SessionId), name);                 // (a Locked user's session is not even found: the lookup requires an Active user)
            Assert.True(await Rejections() > before, name);
        }
        // a column the tag does not cover changes freely and nothing breaks
        var plain = await NewUser("plain");
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {U.DisplayName} = 'Someone Else', {U.LastLoginAtUtc} = now() WHERE {U.Id} = @i", ("i", plain.Id)));
        Assert.Equal(HttpStatusCode.OK, (await WebLogin(client, "plain")).Status);
    }

    [PostgresRoleFact]
    public async Task The_enrolment_code_columns_are_covered_so_a_runtime_role_cannot_enrol_its_own_authenticator()
    {
        // review H1 of the plan: for a user who must enrol, the runtime role writes its own code and a forged enrol challenge; the code columns are in the tag, so the enrolment is refused
        await StartRestricted();
        var user = await NewUser("needsmfa");
        await using var mfa = RuntimeMfa();
        Assert.True(await mfa.TryChangeStateAsync(user.Id, MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement, null));
        using var api = Api(); using var client = NewClient(api);

        async Task<(HttpStatusCode Status, string Body)> ForgedBegin(string enrolmentCode)
        {
            var token = SessionToken.Generate();
            var source = SessionToken.HashKey("mfa-source:unknown");
            await Attack($"INSERT INTO {C.Table} ({C.Id},{C.UserId},{C.TokenHash},{C.Purpose},{C.ClientKind},{C.SourceHash},{C.CreatedAtUtc},{C.ExpiresAtUtc}) VALUES (@i,@u,@h,1,0,@s,now(),now() + interval '10 minutes')",
                ("i", Guid.NewGuid()), ("u", user.Id), ("h", SessionToken.Hash(token)), ("s", source));
            return await Send(client, HttpMethod.Post, "/api/v1/auth/mfa/enrol/begin", null, new { challenge = token, enrolmentCode, client = "web" });
        }

        // control 1: the genuine flow works (a real login yields a real, tagged challenge; the legitimately issued code begins the enrolment)
        var legit = await mfa.IssueEnrolmentCodeAsync(user.Id, TimeSpan.FromHours(1), null);
        var real = await WebLogin(client, "needsmfa");
        var realToken = JsonDocument.Parse(real.Body).RootElement.GetProperty("challenge").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/v1/auth/mfa/enrol/begin", null, new { challenge = realToken, enrolmentCode = legit, client = "web" })).Status);
        // control 2 (slice S-2d closes the gap S-2a left): a FORGED challenge row (the attacker chose the token, the row has no valid tag) is refused even with the legitimately issued code
        legit = await mfa.IssueEnrolmentCodeAsync(user.Id, TimeSpan.FromHours(1), null);
        Assert.NotEqual(HttpStatusCode.OK, (await ForgedBegin(legit)).Status);
        // the attack: the attacker's own code, written straight into the user row
        const string attackerCode = "ATTACKERCODE1234";
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {M.EnrolmentCodeHash} = @h, {M.EnrolmentCodeExpiresAtUtc} = now() + interval '1 hour', {M.EnrolmentCodeConsumedAtUtc} = NULL WHERE {U.Id} = @u", ("h", MfaStore.EnrolmentCodeHash(attackerCode)), ("u", user.Id)));
        var attack = await ForgedBegin(attackerCode);
        Assert.NotEqual(HttpStatusCode.OK, attack.Status);
        Assert.False(attack.Body.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.False(await mfa.CheckEnrolmentCodeAsync(user.Id, attackerCode));                                  // the check itself refuses a row that does not verify
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {S.Table} WHERE {S.UserId} = @u AND {S.RevokedAtUtc} IS NULL", ("u", user.Id)));   // and no session was issued
    }

    [PostgresRoleFact]
    public async Task A_row_that_does_not_verify_reads_as_the_strictest_mfa_state_and_requirement()
    {
        // review L1 of PR 235 (follow-up pin): the decisions taken from the user row come from the VERIFIED row; a row that does not verify is the strictest value, never the weaker one
        await StartRestricted();
        var user = await NewUser("plainuser");                                                                     // no MFA, no administrator
        await using var store = RuntimeStore();
        await using var mfa = RuntimeMfa();
        Assert.Equal(MfaState.None, await store.GetMfaStateAsync(user.Id));
        Assert.Equal(MfaState.None, await mfa.GetStateAsync(user.Id));
        Assert.Equal(MfaMandatoryReason.None, (await store.GetMfaRequirementAsync(user.Id)).Reasons);
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {UI.SecurityRevision} = {UI.SecurityRevision} + 1 WHERE {U.Id} = @u", ("u", user.Id)));    // any covered change makes the row unverifiable
        Assert.Equal(MfaState.RecoveryRequired, await store.GetMfaStateAsync(user.Id));
        Assert.Equal(MfaState.RecoveryRequired, await mfa.GetStateAsync(user.Id));
        Assert.True((await store.GetMfaRequirementAsync(user.Id)).Reasons.HasFlag(MfaMandatoryReason.IdentityAdministrator));
        Assert.Null(await mfa.GetStateAsync(Guid.NewGuid()));                                                      // a user that does not exist is still "no state"
    }

    [PostgresRoleFact]
    public async Task Restoring_an_older_user_row_is_refused_by_the_database_because_the_revision_cannot_go_back()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        var old = await Scalar<string>($"SELECT encode({UT.Tag}, 'hex') FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id));
        await using var store = RuntimeStore();
        await store.UpdatePasswordHashAsync(user.Id, Passwords.Hash("Replacement-Passphrase-3"), passwordChanged: true);   // a legitimate change: revision 1, new tag
        var oldHash = Passwords.Hash(Pw);
        // the attacker writes the ORIGINAL password hash, the original tag AND the original revision back: the old row is perfectly valid as a row, but the revision would decrease
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Attack($"UPDATE {U.Table} SET {U.PasswordHash} = @h, {UT.Tag} = decode(@t, 'hex'), {UI.SecurityRevision} = 0 WHERE {U.Id} = @u", ("h", oldHash), ("t", old), ("u", user.Id)));
        Assert.Contains("exactly one", ex.MessageText);
        Assert.True(await UserTagIsValid(user.Id));
    }

    [PostgresRoleFact]
    public async Task The_legitimate_user_writers_keep_the_tag_valid_under_the_restricted_role()
    {
        await StartRestricted();
        await using var store = RuntimeStore();
        await using var mfa = RuntimeMfa();
        var auth = RuntimeAuth(store);
        var created = await auth.CreateUserAsync("alice", "Alice", Pw, false);
        var user = created.User!;
        Assert.True(await UserTagIsValid(user.Id));                                                              // created tagged
        var session = Assert.IsType<LoginOutcome.Success>(await auth.LoginAsync("alice", Pw, SessionClient.Web, "10.0.0.1"));
        Assert.True(await UserTagIsValid(user.Id));                                                              // the login time is not covered
        await store.UpdatePasswordHashAsync(user.Id, Passwords.Hash(Pw), passwordChanged: false);                // transparent rehash: re-tag, no bump
        Assert.Equal(0L, await Scalar<long>($"SELECT {UI.SecurityRevision} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));
        Assert.NotNull(await auth.AuthenticateAsync(session.Token));                                              // the rehash did not sign anybody out
        var changer = (await auth.AuthenticateAsync(session.Token))!;
        Assert.Equal(ChangePasswordStatus.Changed, (await auth.ChangePasswordAsync(changer, Pw, "Replacement-Passphrase-3")).Status);
        Assert.True(await UserTagIsValid(user.Id));
        Assert.NotNull(await auth.AuthenticateAsync(session.Token));                                              // the changing session stays
        Assert.True(await mfa.TryChangeStateAsync(user.Id, MfaState.None, MfaState.EnrolmentRequired, MfaStateCause.Requirement, null));
        Assert.True(await UserTagIsValid(user.Id));
        var code = await mfa.IssueEnrolmentCodeAsync(user.Id, TimeSpan.FromHours(1), null);
        Assert.True(await UserTagIsValid(user.Id));
        Assert.True(await mfa.CheckEnrolmentCodeAsync(user.Id, code));
        var admin = (await auth.CreateUserAsync("boss", "Boss", Pw, true)).User!;
        Assert.Equal(SetStateOutcome.Changed, await store.SetStateAsync(admin.Id, user.Id, UserState.Disabled));
        Assert.True(await UserTagIsValid(user.Id));
        Assert.Equal(SetStateOutcome.Changed, await store.SetStateAsync(admin.Id, user.Id, UserState.Active));
        Assert.True(await UserTagIsValid(user.Id));
        var restore = await mfa.ApplyRestoreAsync(differentInstallation: true, null);                              // restore fix-up: per row through the primitive
        Assert.True(await UserTagIsValid(user.Id) && await UserTagIsValid(admin.Id));
        Assert.Equal(0, restore.UsersMarkedRecoveryRequired);                                                      // nobody was Enrolled
        Assert.Equal(1, restore.EnrolmentCodesInvalidated);
    }

    [PostgresRoleFact]
    public async Task Deactivation_and_revoke_all_work_on_a_row_that_does_not_verify_and_never_bless_it()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        var admin = await NewUser("boss", admin: true);
        await using var store = RuntimeStore();
        var session = Assert.IsType<LoginOutcome.Success>(await RuntimeAuth(store).LoginAsync("victim", Pw, SessionClient.Web, "10.0.0.1"));
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {U.IsIdentityAdministrator} = true WHERE {U.Id} = @u", ("u", user.Id)));      // tampered
        Assert.Equal(SetStateOutcome.Changed, await store.SetStateAsync(admin.Id, user.Id, UserState.Disabled));                              // the operator can still switch it off
        Assert.False(await UserTagIsValid(user.Id));                                                              // and the write did not re-tag (bless) the tampered row
        Assert.Equal((short)UserState.Disabled, await Scalar<short>($"SELECT {U.State} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));
        Assert.Equal(SetStateOutcome.NotFound, await store.SetStateAsync(admin.Id, user.Id, UserState.Active));                              // re-activating needs an authentic row
        Assert.Null(await store.FindActiveSessionAsync(session.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdatePasswordHashAsync(user.Id, Passwords.Hash("Whatever-Passphrase-9"), true));   // a tampered row takes no password change
    }

    [PostgresRoleFact]
    public async Task A_missing_key_is_service_unavailable_at_login_and_no_user_is_created_without_it()
    {
        await StartRestricted();
        await NewUser("victim");
        using var api = Api(); using var client = NewClient(api);
        await using var store = RuntimeStore();
        var users = await Scalar<long>($"SELECT count(*) FROM {U.Table}");
        ring.Unavailable = true;
        var login = await WebLogin(client, "victim");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.Status);
        Assert.Contains("encryption_unavailable", login.Body);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => RuntimeAuth(store).CreateUserAsync("ghost", "Ghost", Pw, false));
        await using var keyless = new IdentityStore(() => RuntimeConnection, Clock, Audit);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => RuntimeAuth(keyless).CreateUserAsync("ghost2", "Ghost", Pw, false));
        Assert.Equal(users, await Scalar<long>($"SELECT count(*) FROM {U.Table}"));
        ring.Unavailable = false;
        Assert.Equal(HttpStatusCode.OK, (await WebLogin(client, "victim")).Status);
    }

    [PostgresRoleFact]
    public async Task The_owner_tagging_tags_only_untagged_users_and_never_blesses_a_tampered_tagged_row()
    {
        await StartRestricted();
        var tampered = await NewUser("tampered");
        var tagged = await NewUser("tagged");
        // users that exist without a tag (as after the upgrade): written by the owner account without one
        await Attack($"INSERT INTO {U.Table} ({U.Id},{U.Login},{U.DisplayName},{U.PasswordHash},{U.State},{U.IsIdentityAdministrator},{U.CreatedAtUtc},{U.StateChangedAtUtc},{U.PasswordChangedAtUtc}) VALUES (@i,'legacy','L',@h,0,false,now(),now(),now())", ("i", Guid.NewGuid()), ("h", Passwords.Hash(Pw)));
        Assert.Equal(1, await Attack($"UPDATE {U.Table} SET {U.IsIdentityAdministrator} = true WHERE {U.Id} = @u", ("u", tampered.Id)));
        using var api = Api(); using var client = NewClient(api);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebLogin(client, "legacy")).Status);                    // no compatibility mode: untagged, no sign-in
        var result = await IdentityTagging.TagUntaggedUsersAsync(Options.BuildSystemConnectionString(admin: true), Keys, Clock, Audit);
        Assert.Equal(1, result.Tagged);
        Assert.Equal(2, result.AlreadyTagged);
        Assert.Equal(HttpStatusCode.OK, (await WebLogin(client, "legacy")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebLogin(client, "tampered")).Status);                  // a tampered TAGGED row is not blessed by the tagging
        Assert.Equal(HttpStatusCode.OK, (await WebLogin(client, "tagged")).Status);
        Assert.Equal(0, (await IdentityTagging.TagUntaggedUsersAsync(Options.BuildSystemConnectionString(admin: true), Keys, Clock, Audit)).Tagged);   // idempotent
    }

    [PostgresRoleFact]
    public async Task Key_rotation_honours_the_previous_key_only_while_listed_and_a_write_re_tags_under_the_active_key()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        ring.Add("k2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        ring.Activate("k2");
        await using var rotating = RuntimeStore(new IdentityKeys(ring, ["k1"]));
        await using var strict = RuntimeStore(new IdentityKeys(ring));
        Assert.NotNull(await rotating.FindUserByIdAsync(user.Id));                                                // grace period: the k1 tag is honoured
        Assert.Null(await strict.FindUserByIdAsync(user.Id));                                                     // the grace period ended: it is not
        await rotating.UpdatePasswordHashAsync(user.Id, Passwords.Hash(Pw), passwordChanged: false);
        Assert.Equal("k2", await Scalar<string>($"SELECT {UT.KeyId} FROM {U.Table} WHERE {U.Id} = @u", ("u", user.Id)));
        Assert.NotNull(await strict.FindUserByIdAsync(user.Id));                                                  // re-tagged under the active key: valid without the previous key
    }

    [PostgresRoleFact]
    public async Task The_writers_never_deadlock_login_password_change_revoke_all_and_state_changes_run_together()
    {
        // lock order: the user row first (FOR NO KEY UPDATE), then the session rows, everywhere; the session insert takes only a key-share lock on the user
        await StartRestricted();
        var user = await NewUser("victim");
        var admin = await NewUser("boss", admin: true);
        await using var store = RuntimeStore();
        await using var mfa = RuntimeMfa();
        var auth = RuntimeAuth(store);
        for (var round = 0; round < 10; round++)
        {
            var login = Assert.IsType<LoginOutcome.Success>(await auth.LoginAsync("victim", Pw, SessionClient.Web, "10.0.0." + (round + 1)));
            var tasks = new List<Task>
            {
                auth.LoginAsync("victim", Pw, SessionClient.Web, "10.1.0." + (round + 1)),
                store.RevokeUserSessionsAsync(user.Id, SessionRevocation.Administrative),
                mfa.RecordStepUpAsync(login.SessionId, user.Id),
                store.UpdatePasswordHashAsync(user.Id, Passwords.Hash(Pw), passwordChanged: false),
                store.SetStateAsync(admin.Id, user.Id, UserState.Active),
                store.UpdatePasswordHashAsync(user.Id, Passwords.Hash(Pw), passwordChanged: false),
            };
            try { await Task.WhenAll(tasks); } catch (Exception) { }                                                // a refusal of a concurrent change is not a failure of this test ...
            foreach (var task in tasks.Where(t => t.IsFaulted)) Assert.DoesNotContain(task.Exception!.Flatten().InnerExceptions, e => e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected });   // ... a deadlock is
            Assert.True(await UserTagIsValid(user.Id));                                                              // and the row is never half written: tag and fields agree
        }
        _ = admin;
    }
}
