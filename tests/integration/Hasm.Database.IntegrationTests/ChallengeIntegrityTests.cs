using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Npgsql;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;
using CT = Hasm.Infrastructure.SystemDatabase.SystemSchema.ChallengeTag;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Identity integrity slice S-2d (plan section 5.1): the second-factor challenge row carries a keyed token hash and a tag. A row written with the runtime credentials (a chosen token, a forged tag, a copy of a genuine row, a
/// genuine row with one covered field changed) is refused like an unknown token, consumed, and reported; the genuine flow of EACH purpose that has a caller (Login, Enrol) works; no key means fail closed. White-box where it
/// has to be: a test computes the keyed hash itself (the attacker cannot) to prove that the TAG, and not only the hash, is what refuses the row. Role-capable disposable cluster only.
/// </summary>
public class ChallengeIntegrityTests : RestrictedRuntimeIdentityTestBase
{
    private int rejected;
    private MfaChallengeStore Challenges() => new(() => RuntimeConnection, Clock, keys: Keys, onRejected: id => { Interlocked.Increment(ref rejected); return Task.CompletedTask; });

    private ChallengeRow Row(Guid user, MfaChallengePurpose purpose, string token, string? source = "10.0.0.1", SessionClient client = SessionClient.Web, bool tagged = true)
    {
        var key = Keys.Active();
        var now = MacInput.Truncate(Clock.GetUtcNow());
        var row = new ChallengeRow(Guid.NewGuid(), user, Keys.TokenHash(key, token), (short)purpose, (short)client,
            SessionToken.HashKey("mfa-source:" + source), now, now.AddMinutes(5), key.Id, null);
        return tagged ? row with { Tag = row.ComputeTag(Keys, key) } : row with { KeyId = null };
    }

    private Task<int> Insert(ChallengeRow r) => Attack(
        $"INSERT INTO {C.Table} ({C.Id},{C.UserId},{C.TokenHash},{C.Purpose},{C.ClientKind},{C.SourceHash},{C.CreatedAtUtc},{C.ExpiresAtUtc},{CT.KeyId},{CT.Tag}) VALUES (@i,@u,@h,@p,@c,@s,@n,@e,@k,@g)",
        ("i", r.Id), ("u", r.UserId), ("h", r.TokenHash), ("p", r.Purpose), ("c", r.ClientKind), ("s", r.SourceHash), ("n", r.CreatedAt), ("e", r.ExpiresAt), ("k", r.KeyId), ("g", r.Tag));

    [PostgresRoleFact]
    public async Task The_genuine_flow_of_each_purpose_with_a_caller_works_and_the_stored_hash_is_keyed()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        await using var challenges = Challenges();
        foreach (var purpose in new[] { MfaChallengePurpose.Login, MfaChallengePurpose.Enrol })
        {
            var issued = await challenges.IssueAsync(user.Id, purpose, SessionClient.Web, "10.0.0.1");
            Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {C.Table} WHERE {C.Id} = @i AND {CT.Tag} IS NOT NULL AND {CT.KeyId} = 'k1' AND {C.TokenHash} = @h", ("i", issued.Id), ("h", Keys.TokenHash(Keys.Active(), issued.Token))));
            Assert.Equal(0L, await Scalar<long>($"SELECT count(*) FROM {C.Table} WHERE {C.TokenHash} = @h", ("h", SessionToken.Hash(issued.Token))));   // never the plain SHA-256
            Assert.Null(await challenges.TryBeginAttemptAsync(issued.Token, purpose == MfaChallengePurpose.Login ? MfaChallengePurpose.Enrol : MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));   // the purpose is bound
            var info = await challenges.TryBeginAttemptAsync(issued.Token, purpose, SessionClient.Web, "10.0.0.1");
            Assert.Equal((issued.Id, user.Id, purpose), (info!.Id, info.UserId, info.Purpose));
            Assert.True(await challenges.ConsumeAsync(issued.Id));
            Assert.Null(await challenges.TryBeginAttemptAsync(issued.Token, purpose, SessionClient.Web, "10.0.0.1"));                                   // single use
        }
        Assert.Equal(0, rejected);
    }

    [PostgresRoleFact]
    public async Task A_forged_challenge_row_is_refused_for_each_purpose_whatever_the_attacker_writes()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        await using var challenges = Challenges();
        foreach (var purpose in new[] { MfaChallengePurpose.Login, MfaChallengePurpose.Enrol })
        {
            // (1) the old scheme: a chosen token and its plain SHA-256, no tag
            var plain = SessionToken.Generate();
            await Attack($"INSERT INTO {C.Table} ({C.Id},{C.UserId},{C.TokenHash},{C.Purpose},{C.ClientKind},{C.SourceHash},{C.CreatedAtUtc},{C.ExpiresAtUtc}) VALUES (@i,@u,@h,@p,0,@s,now(),now() + interval '5 minutes')",
                ("i", Guid.NewGuid()), ("u", user.Id), ("h", SessionToken.Hash(plain)), ("p", (short)purpose), ("s", SessionToken.HashKey("mfa-source:10.0.0.1")));
            Assert.Null(await challenges.TryBeginAttemptAsync(plain, purpose, SessionClient.Web, "10.0.0.1"));
            // (2) the keyed hash computed WITH the key (the attacker cannot; the test can) but a random tag: only the tag refuses it
            var noTag = SessionToken.Generate();
            var forged = Row(user.Id, purpose, noTag) with { Tag = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) };
            await Insert(forged);
            var before = rejected;
            Assert.Null(await challenges.TryBeginAttemptAsync(noTag, purpose, SessionClient.Web, "10.0.0.1"));
            Assert.Equal(before + 1, rejected);                                                                      // reported to the operator ...
            Assert.True(await Scalar<bool>($"SELECT {C.ConsumedAtUtc} IS NOT NULL FROM {C.Table} WHERE {C.Id} = @i", ("i", forged.Id)));   // ... and burned
            // (3) the same row with a valid tag works: the harness is sound, it is the tag that decides
            var good = SessionToken.Generate();
            await Insert(Row(user.Id, purpose, good));
            Assert.NotNull(await challenges.TryBeginAttemptAsync(good, purpose, SessionClient.Web, "10.0.0.1"));
        }
    }

    [PostgresRoleFact]
    public async Task Every_covered_field_of_a_genuine_challenge_is_bound_by_the_tag()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        var other = await NewUser("other");
        await using var challenges = Challenges();
        // the runtime role cannot UPDATE these columns (column grants), so the attacker's way is a forged INSERT: a row whose fields differ from the ones the genuine tag was computed over
        var foreign = Row(other.Id, MfaChallengePurpose.Login, SessionToken.Generate());
        var cases = new (string Name, Func<ChallengeRow, ChallengeRow> Alter, MfaChallengePurpose Purpose, SessionClient Client, string Source)[]
        {
            ("user", r => r with { UserId = other.Id }, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"),
            ("purpose", r => r with { Purpose = (short)MfaChallengePurpose.Enrol }, MfaChallengePurpose.Enrol, SessionClient.Web, "10.0.0.1"),
            ("client", r => r with { ClientKind = (short)SessionClient.Desktop }, MfaChallengePurpose.Login, SessionClient.Desktop, "10.0.0.1"),
            ("source", r => r with { SourceHash = SessionToken.HashKey("mfa-source:10.9.9.9") }, MfaChallengePurpose.Login, SessionClient.Web, "10.9.9.9"),
            ("expiry", r => r with { ExpiresAt = r.ExpiresAt.AddDays(1) }, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"),
            ("created", r => r with { CreatedAt = r.CreatedAt.AddMinutes(-1) }, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"),
            ("key id", r => r with { KeyId = "k2" }, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"),
            ("tag of another row", r => r with { Tag = foreign.Tag }, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"),
        };
        foreach (var (name, alter, purpose, client, source) in cases)
        {
            var token = SessionToken.Generate();
            var genuine = Row(user.Id, MfaChallengePurpose.Login, token);                                          // tag computed over the ORIGINAL fields
            Assert.Equal(1, await Insert(alter(genuine)));                                                        // stored with one field different
            Assert.Null(await challenges.TryBeginAttemptAsync(token, purpose, client, source));
            Assert.True(name.Length > 0);
        }
        // the attempt counter and the consumption time are NOT covered by the tag (S-2b derives the limits from the attempt log); the database already refuses to lower the counter
        var t2 = SessionToken.Generate(); var r2 = Row(user.Id, MfaChallengePurpose.Login, t2); await Insert(r2);
        Assert.NotNull(await challenges.TryBeginAttemptAsync(t2, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Attack($"UPDATE {C.Table} SET {C.Attempts} = 0 WHERE {C.Id} = @i", ("i", r2.Id)));
        Assert.Contains("never decreases", ex.MessageText);
    }

    [PostgresRoleFact]
    public async Task A_missing_key_fails_closed_and_a_rejected_challenge_is_audited_through_the_real_wiring()
    {
        await StartRestricted();
        var user = await NewUser("victim");
        await using var store = RuntimeStore();
        await using var wired = new MfaChallengeStore(() => RuntimeConnection, Clock, keys: Keys, onRejected: id => store.AuditRejectedChallengeAsync(id));
        var token = SessionToken.Generate();
        await Insert(Row(user.Id, MfaChallengePurpose.Enrol, token) with { Tag = new byte[32] });
        var before = await Rejections();
        Assert.Null(await wired.TryBeginAttemptAsync(token, MfaChallengePurpose.Enrol, SessionClient.Web, "10.0.0.1"));
        Assert.Equal(before + 1, await Rejections());
        Assert.Contains("challenge", await Scalar<string>($"SELECT {Ev.Detail} FROM {Ev.Table} WHERE {Ev.EventType} = {MfaAuditEvents.IdentityRecordRejected} ORDER BY {Ev.Seq} DESC LIMIT 1"));
        ring.Unavailable = true;
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => wired.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => wired.TryBeginAttemptAsync(SessionToken.Generate(), MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));
        await using var keyless = new MfaChallengeStore(() => RuntimeConnection, Clock);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keyless.IssueAsync(user.Id, MfaChallengePurpose.Login, SessionClient.Web, "10.0.0.1"));
    }
}
