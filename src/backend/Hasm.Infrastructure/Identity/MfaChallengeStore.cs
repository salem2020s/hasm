using Hasm.Infrastructure.Security;
using Npgsql;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;
using CT = Hasm.Infrastructure.SystemDatabase.SystemSchema.ChallengeTag;

namespace Hasm.Infrastructure.Identity;

/// <summary>What a challenge is for. Numeric values are persisted; never renumber.</summary>
public enum MfaChallengePurpose : short { Login = 0, Enrol = 1, RecoveryRequest = 2 }

/// <summary>Challenge validity and attempt budget (D-103: five minutes; a small budget).</summary>
public sealed record MfaChallengeOptions(int ValidityMinutes = 5, int MaxAttempts = 5);

public sealed record MfaChallengeIssue(Guid Id, string Token, DateTimeOffset ExpiresAtUtc);
public sealed record MfaChallengeInfo(Guid Id, Guid UserId, MfaChallengePurpose Purpose);

/// <summary>
/// The short-lived, single-purpose, single-use challenge that follows the PASSWORD step of a login that needs a second factor (Phase 4.1 section 5). A challenge is NOT a session. Only the KEYED hash (HMAC under the identity token key) of its 256-bit token is stored, and the row carries a key id and a tag over its security fields (identity integrity S-2d: a row written with the runtime credentials is no challenge, it is refused as an unknown token and reported through <c>onRejected</c>; no key means fail closed);
/// it is bound to the user, the client kind and the source address, expires after <see cref="MfaChallengeOptions.ValidityMinutes"/>, and allows <see cref="MfaChallengeOptions.MaxAttempts"/> verification attempts. Every refusal
/// (unknown, malformed, expired, used, wrong client, wrong source, wrong purpose, budget spent) looks the same to the caller: <c>null</c>.
/// The client-kind and source bindings are CONSISTENCY checks (a challenge issued to the Web client is not accepted by the Desktop client or from another address): the client kind is chosen by the caller and the address can change behind a proxy,
/// so neither is an authentication factor. The factors are the password (before) and the single-use token plus the TOTP code (after).
/// </summary>
public sealed class MfaChallengeStore(Func<string> connectionStringFactory, TimeProvider clock, MfaChallengeOptions? options = null, IdentityKeys? keys = null, Func<Guid, Task>? onRejected = null) : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));
    private readonly MfaChallengeOptions o = options ?? new MfaChallengeOptions();

    public MfaChallengeStore(string connectionString, TimeProvider clock, MfaChallengeOptions? options = null, IdentityKeys? keys = null, Func<Guid, Task>? onRejected = null) : this(() => connectionString, clock, options, keys, onRejected) { }

    private IdentityKeys RequireKeys() => keys ?? throw new EncryptionUnavailableException("encryption_key_unavailable", "No identity key ring is configured.");

    public ValueTask DisposeAsync() => dataSource.IsValueCreated ? dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    private static byte[] SourceHash(string? source) => SessionToken.HashKey("mfa-source:" + (string.IsNullOrWhiteSpace(source) ? SourceAddress.Unknown : source));

    /// <summary>Issues a challenge. The plaintext token is returned once and never stored; the row is written with its keyed token hash and its tag (a missing key fails closed: no challenge without it).</summary>
    public async Task<MfaChallengeIssue> IssueAsync(Guid userId, MfaChallengePurpose purpose, SessionClient client, string? source, CancellationToken ct = default)
    {
        var k = RequireKeys(); var key = k.Active();
        var token = SessionToken.Generate();
        var now = MacInput.Truncate(clock.GetUtcNow());
        var row = new ChallengeRow(Guid.NewGuid(), userId, k.TokenHash(key, token), (short)purpose, (short)client, SourceHash(source), now, MacInput.Truncate(now.AddMinutes(o.ValidityMinutes)), key.Id, null);
        row = row with { Tag = row.ComputeTag(k, key) };
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {C.Table} ({C.Id}, {C.UserId}, {C.TokenHash}, {C.Purpose}, {C.ClientKind}, {C.SourceHash}, {C.CreatedAtUtc}, {C.ExpiresAtUtc}, {CT.KeyId}, {CT.Tag}) VALUES (@id, @u, @h, @p, @c, @s, @n, @e, @k, @g)", conn);
        cmd.Parameters.AddWithValue("id", row.Id);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("h", row.TokenHash);
        cmd.Parameters.AddWithValue("p", row.Purpose);
        cmd.Parameters.AddWithValue("c", row.ClientKind);
        cmd.Parameters.AddWithValue("s", row.SourceHash);
        cmd.Parameters.AddWithValue("n", row.CreatedAt);
        cmd.Parameters.AddWithValue("e", row.ExpiresAt);
        cmd.Parameters.AddWithValue("k", key.Id);
        cmd.Parameters.AddWithValue("g", row.Tag!);
        await cmd.ExecuteNonQueryAsync(ct);
        return new MfaChallengeIssue(row.Id, token, row.ExpiresAt);
    }

    /// <summary>
    /// Starts ONE verification attempt with a presented token: under the row lock it checks every binding, the budget AND the tag, and counts the attempt. Returns null for every refusal (a row whose tag does not verify is
    /// refused as an unknown token, consumed so it cannot be tried again, and reported through <c>onRejected</c> without any detail). The caller verifies the second factor and, on success, calls <see cref="ConsumeAsync"/>;
    /// a failed factor simply leaves the attempt counted. The attempt counter itself is advisory (it is not covered by the tag; the attempt log of slice S-2b will derive the limits).
    /// </summary>
    public async Task<MfaChallengeInfo?> TryBeginAttemptAsync(string? token, MfaChallengePurpose purpose, SessionClient client, string? source, CancellationToken ct = default)
    {
        if (!SessionToken.IsWellFormed(token)) return null;
        var k = RequireKeys();
        var now = clock.GetUtcNow();
        foreach (var key in k.Accepted())
        {
            await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            ChallengeRow row;
            await using (var sel = new NpgsqlCommand(
                $"SELECT {ChallengeRow.Columns("c")} FROM {C.Table} c WHERE c.{C.TokenHash} = @h AND c.{C.ConsumedAtUtc} IS NULL AND c.{C.ExpiresAtUtc} > @n AND c.{C.Purpose} = @p AND c.{C.ClientKind} = @c AND c.{C.SourceHash} = @s AND c.{C.Attempts} < @max FOR UPDATE", conn, tx))
            {
                sel.Parameters.AddWithValue("h", k.TokenHash(key, token!));
                sel.Parameters.AddWithValue("n", now);
                sel.Parameters.AddWithValue("p", (short)purpose);
                sel.Parameters.AddWithValue("c", (short)client);
                sel.Parameters.AddWithValue("s", SourceHash(source));
                sel.Parameters.AddWithValue("max", o.MaxAttempts);
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) continue;
                row = ChallengeRow.Read(r);
            }
            if (!row.IsValid(k))
            {
                await using (var kill = new NpgsqlCommand($"UPDATE {C.Table} SET {C.ConsumedAtUtc} = @n WHERE {C.Id} = @id", conn, tx))
                {
                    kill.Parameters.AddWithValue("n", now);
                    kill.Parameters.AddWithValue("id", row.Id);
                    await kill.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                if (onRejected is not null) await onRejected(row.Id);
                return null;
            }
            await using (var count = new NpgsqlCommand($"UPDATE {C.Table} SET {C.Attempts} = {C.Attempts} + 1 WHERE {C.Id} = @id", conn, tx))
            {
                count.Parameters.AddWithValue("id", row.Id);
                await count.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return new MfaChallengeInfo(row.Id, row.UserId, purpose);
        }
        return null;
    }

    /// <summary>Consumes the challenge (single use). Exactly one caller can succeed.</summary>
    public async Task<bool> ConsumeAsync(Guid challengeId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"UPDATE {C.Table} SET {C.ConsumedAtUtc} = @n WHERE {C.Id} = @id AND {C.ConsumedAtUtc} IS NULL AND {C.ExpiresAtUtc} > @n", conn);
        cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("id", challengeId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Housekeeping: removes challenges that expired before <paramref name="olderThan"/>. Returns rows removed.</summary>
    public async Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"DELETE FROM {C.Table} WHERE {C.ExpiresAtUtc} < @t", conn);
        cmd.Parameters.AddWithValue("t", olderThan);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>A challenge row as the tag sees it (identity integrity S-2d, plan section 5.1): identity, user, purpose, client, source hash, creation and expiry, the keyed token hash and the key id. The attempt counter and the consumption time are NOT covered.</summary>
internal sealed record ChallengeRow(Guid Id, Guid UserId, byte[] TokenHash, short Purpose, short ClientKind, byte[] SourceHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? KeyId, byte[]? Tag)
{
    public const string Domain = "hasm.challenge.v1";

    public static string Columns(string alias) => string.Join(",", new[] { C.Id, C.UserId, C.TokenHash, C.Purpose, C.ClientKind, C.SourceHash, C.CreatedAtUtc, C.ExpiresAtUtc, CT.KeyId, CT.Tag }.Select(c => alias + "." + c));

    public static ChallengeRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), (byte[])r.GetValue(2), r.GetInt16(3), r.GetInt16(4), (byte[])r.GetValue(5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : (byte[])r.GetValue(9));

    public byte[] ComputeTag(IdentityKeys keys, KekHandle key) => keys.Mac(key, IdentityKeys.SessionPurpose, new MacInput(Domain)
        .Add(Id).Add(UserId).Add((long)Purpose).Add((long)ClientKind).Add(SourceHash).Add(CreatedAt).Add(ExpiresAt).Add(TokenHash).Add(key.Id));

    public bool IsValid(IdentityKeys keys)
    {
        if (KeyId is null || Tag is null || keys.FindAccepted(KeyId) is not { } key) return false;
        return IdentityKeys.Equal(ComputeTag(keys, key), Tag);
    }
}
