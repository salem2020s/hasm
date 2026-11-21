using Hasm.Infrastructure.Identity;
using Npgsql;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Session;
using SM = Hasm.Infrastructure.SystemDatabase.SystemSchema.SessionMfa;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 4.1 slice S2 against real PostgreSQL: the login challenge (single use, bound, budgeted, expiring, stored only as a hash) and the MFA columns of the session. Disposable cluster only.</summary>
public class MfaChallengeStoreTests : IdentityTestBase
{
    private MfaChallengeStore Challenges = null!;

    private async Task<UserRecord> Setup()
    {
        await Start();
        Challenges = new MfaChallengeStore(Options.BuildSystemConnectionString(admin: true), Clock);
        return await NewUser();
    }

    private Task<long> Attempts(Guid id) => Scalar<long>($"SELECT {C.Attempts}::bigint FROM {C.Table} WHERE {C.Id} = @i", ("i", id));

    [PostgresFact]
    public async Task A_challenge_is_stored_only_as_a_hash_and_is_usable_with_its_exact_bindings()
    {
        var user = await Setup();
        var issued = await Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1");
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {C.Table} WHERE {C.TokenHash} = @h", ("h", SessionToken.Hash(issued.Token))));
        Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {C.Table} WHERE encode({C.TokenHash}, 'escape') LIKE '%' || @t || '%'", ("t", issued.Token)));   // the plaintext token is nowhere
        Assert.Equal(Clock.GetUtcNow().AddMinutes(5), issued.ExpiresAtUtc);

        var info = await Challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1");
        Assert.Equal((issued.Id, user.Id, MfaChallengePurpose.Login), (info!.Id, info.UserId, info.Purpose));
        Assert.Equal(1L, await Attempts(issued.Id));
    }

    [PostgresFact]
    public async Task Every_refusal_looks_the_same_and_a_wrong_binding_never_counts_an_attempt()
    {
        var user = await Setup();
        var issued = await Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1");
        Assert.Null(await Challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Login, SessionClient.Desktop, "10.0.0.1"));      // another client kind
        Assert.Null(await Challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.2"));          // another source
        Assert.Null(await Challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Enrol, SessionClient.Web, "10.0.0.1"));          // another purpose
        Assert.Null(await Challenges.TryBeginAttemptAsync(SessionToken.Generate(), MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));   // unknown
        Assert.Null(await Challenges.TryBeginAttemptAsync("not-a-token", MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));         // malformed
        Assert.Null(await Challenges.TryBeginAttemptAsync(null, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));
        Assert.Equal(0L, await Attempts(issued.Id));
    }

    [PostgresFact]
    public async Task The_attempt_budget_is_five_and_an_expired_challenge_is_refused()
    {
        var user = await Setup();
        var issued = await Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, null);
        for (var i = 0; i < 5; i++) Assert.NotNull(await Challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Login, SessionClient.Web, null));
        Assert.Null(await Challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Login, SessionClient.Web, null));                // the sixth is refused
        Assert.Equal(5L, await Attempts(issued.Id));

        var fresh = await Challenges.IssueAsync(user.Id, MfaChallengePurpose.Enrol, SessionClient.Desktop, "a");
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Null(await Challenges.TryBeginAttemptAsync(fresh.Token, MfaChallengePurpose.Enrol, SessionClient.Desktop, "a"));             // expired
        Assert.False(await Challenges.ConsumeAsync(fresh.Id));                                                                                 // and cannot be consumed either
    }

    [PostgresFact]
    public async Task A_challenge_is_consumed_exactly_once_even_under_concurrency()
    {
        var user = await Setup();
        var issued = await Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "a");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Challenges.ConsumeAsync(issued.Id))));
        Assert.Equal(1, results.Count(r => r));
        Assert.Null(await Challenges.TryBeginAttemptAsync(issued.Token, MfaChallengePurpose.Login, SessionClient.Web, "a"));                 // a consumed challenge starts no new attempt
    }

    [PostgresFact]
    public async Task The_guard_trigger_protects_the_identity_of_a_challenge_and_its_single_use_and_purge_removes_only_expired_ones()
    {
        var user = await Setup();
        var a = await Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "a");
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {C.Table} SET {C.ExpiresAtUtc} = {C.ExpiresAtUtc} + interval '1 day' WHERE {C.Id} = @i", ("i", a.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {C.Table} SET {C.UserId} = gen_random_uuid() WHERE {C.Id} = @i", ("i", a.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {C.Table} SET {C.Purpose} = 1 WHERE {C.Id} = @i", ("i", a.Id)));
        Assert.True(await Challenges.ConsumeAsync(a.Id));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {C.Table} SET {C.ConsumedAtUtc} = now() + interval '1 hour' WHERE {C.Id} = @i", ("i", a.Id)));   // consumed at most once
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {C.Table} SET {C.Attempts} = -1 WHERE {C.Id} = @i", ("i", a.Id)));                                 // the check refuses a negative count

        Clock.Advance(TimeSpan.FromMinutes(10));
        var live = await Challenges.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "a");
        Assert.Equal(1, await Challenges.PurgeAsync(Clock.GetUtcNow()));                                                                      // only the expired one
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {C.Table} WHERE {C.Id} = @i", ("i", live.Id)));
    }

    [PostgresFact]
    public async Task A_session_defaults_to_password_only_and_a_totp_session_must_carry_its_verification_time()
    {
        var user = await Setup();
        var login = await Login("alice", Pw);
        var ok = Assert.IsType<LoginOutcome.Success>(login);
        Assert.Equal(1L, await Scalar<long>($"SELECT {SM.AuthMethods}::bigint FROM {S.Table} WHERE {S.Id} = @i", ("i", ok.SessionId)));
        Assert.False(await Scalar<bool>($"SELECT {SM.Emergency} FROM {S.Table} WHERE {S.Id} = @i", ("i", ok.SessionId)));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {S.Table} SET {SM.AuthMethods} = 3 WHERE {S.Id} = @i", ("i", ok.SessionId)));            // the totp bit without a verification time
        await Exec($"UPDATE {S.Table} SET {SM.AuthMethods} = 3, {SM.VerifiedAtUtc} = now() WHERE {S.Id} = @i", ("i", ok.SessionId));
        await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {S.Table} SET {SM.AuthMethods} = 0 WHERE {S.Id} = @i", ("i", ok.SessionId)));            // password is always part of it
    }
}
