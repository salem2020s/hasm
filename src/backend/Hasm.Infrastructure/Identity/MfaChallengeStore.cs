using Npgsql;
using C = Hasm.Infrastructure.SystemDatabase.SystemSchema.MfaChallenge;

namespace Hasm.Infrastructure.Identity;

/// <summary>What a challenge is for. Numeric values are persisted; never renumber.</summary>
public enum MfaChallengePurpose : short { Login = 0, Enrol = 1, RecoveryRequest = 2 }

/// <summary>Challenge validity and attempt budget (D-103: five minutes; a small budget).</summary>
public sealed record MfaChallengeOptions(int ValidityMinutes = 5, int MaxAttempts = 5);

public sealed record MfaChallengeIssue(Guid Id, string Token, DateTimeOffset ExpiresAtUtc);
public sealed record MfaChallengeInfo(Guid Id, Guid UserId, MfaChallengePurpose Purpose);

/// <summary>
/// The short-lived, single-purpose, single-use challenge that follows the PASSWORD step of a login that needs a second factor (Phase 4.1 section 5). A challenge is NOT a session. Only the SHA-256 of its 256-bit token is stored;
/// it is bound to the user, the client kind and the source address, expires after <see cref="MfaChallengeOptions.ValidityMinutes"/>, and allows <see cref="MfaChallengeOptions.MaxAttempts"/> verification attempts. Every refusal
/// (unknown, malformed, expired, used, wrong client, wrong source, wrong purpose, budget spent) looks the same to the caller: <c>null</c>.
/// The client-kind and source bindings are CONSISTENCY checks (a challenge issued to the Web client is not accepted by the Desktop client or from another address): the client kind is chosen by the caller and the address can change behind a proxy,
/// so neither is an authentication factor. The factors are the password (before) and the single-use token plus the TOTP code (after).
/// </summary>
public sealed class MfaChallengeStore(Func<string> connectionStringFactory, TimeProvider clock, MfaChallengeOptions? options = null) : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> dataSource = new(() => NpgsqlDataSource.Create(connectionStringFactory()));
    private readonly MfaChallengeOptions o = options ?? new MfaChallengeOptions();

    public MfaChallengeStore(string connectionString, TimeProvider clock, MfaChallengeOptions? options = null) : this(() => connectionString, clock, options) { }

    public ValueTask DisposeAsync() => dataSource.IsValueCreated ? dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    private static byte[] SourceHash(string? source) => SessionToken.HashKey("mfa-source:" + (string.IsNullOrWhiteSpace(source) ? SourceAddress.Unknown : source));

    /// <summary>Issues a challenge. The plaintext token is returned once and never stored.</summary>
    public async Task<MfaChallengeIssue> IssueAsync(Guid userId, MfaChallengePurpose purpose, SessionClient client, string? source, CancellationToken ct = default)
    {
        var token = SessionToken.Generate();
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(o.ValidityMinutes);
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {C.Table} ({C.Id}, {C.UserId}, {C.TokenHash}, {C.Purpose}, {C.ClientKind}, {C.SourceHash}, {C.CreatedAtUtc}, {C.ExpiresAtUtc}) VALUES (@id, @u, @h, @p, @c, @s, @n, @e)", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("h", SessionToken.Hash(token));
        cmd.Parameters.AddWithValue("p", (short)purpose);
        cmd.Parameters.AddWithValue("c", (short)client);
        cmd.Parameters.AddWithValue("s", SourceHash(source));
        cmd.Parameters.AddWithValue("n", now);
        cmd.Parameters.AddWithValue("e", expires);
        await cmd.ExecuteNonQueryAsync(ct);
        return new MfaChallengeIssue(id, token, expires);
    }

    /// <summary>
    /// Starts ONE verification attempt with a presented token: in a single atomic statement it checks every binding and the budget and counts the attempt. Returns null for every refusal. The caller verifies the second factor and,
    /// on success, calls <see cref="ConsumeAsync"/>; a failed factor simply leaves the attempt counted.
    /// </summary>
    public async Task<MfaChallengeInfo?> TryBeginAttemptAsync(string? token, MfaChallengePurpose purpose, SessionClient client, string? source, CancellationToken ct = default)
    {
        if (!SessionToken.IsWellFormed(token)) return null;
        await using var conn = await dataSource.Value.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {C.Table} SET {C.Attempts} = {C.Attempts} + 1 WHERE {C.TokenHash} = @h AND {C.ConsumedAtUtc} IS NULL AND {C.ExpiresAtUtc} > @n AND {C.Purpose} = @p AND {C.ClientKind} = @c AND {C.SourceHash} = @s AND {C.Attempts} < @max " +
            $"RETURNING {C.Id}, {C.UserId}", conn);
        cmd.Parameters.AddWithValue("h", SessionToken.Hash(token!));
        cmd.Parameters.AddWithValue("n", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("p", (short)purpose);
        cmd.Parameters.AddWithValue("c", (short)client);
        cmd.Parameters.AddWithValue("s", SourceHash(source));
        cmd.Parameters.AddWithValue("max", o.MaxAttempts);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new MfaChallengeInfo(r.GetGuid(0), r.GetGuid(1), purpose) : null;
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
