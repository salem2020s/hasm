using System.Security.Cryptography;

namespace Hasm.Infrastructure.Identity;

/// <summary>A stored TOTP secret opened for one verification. The plaintext is zeroed when disposed.</summary>
public sealed class MfaSecret(Guid secretId, byte[] secret, long? lastAcceptedStep) : IDisposable
{
    public Guid SecretId { get; } = secretId;
    public long? LastAcceptedStep { get; } = lastAcceptedStep;
    internal byte[] Secret { get; } = secret;
    public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
}

/// <summary>Raised by a secret store when the key that protects the secrets is not available (fail closed: the user is neither accepted nor downgraded).</summary>
public sealed class MfaUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Storage seam of the verifier. The implementation decrypts the ACTIVE secret of a user and performs the replay compare-and-set in ONE atomic statement.</summary>
public interface IMfaSecretStore
{
    /// <summary>The user's Active secret, or null when none. Throws <see cref="MfaUnavailableException"/> when it cannot be decrypted.</summary>
    Task<MfaSecret?> FindActiveSecretAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Atomically advances the stored last accepted step to <paramref name="matchedStep"/> only if it is still lower (for example
    /// <c>UPDATE ... SET last_step = @s WHERE secret_id = @id AND (last_step IS NULL OR last_step &lt; @s)</c>) and returns whether exactly one row changed.
    /// </summary>
    Task<bool> TryAdvanceLastStepAsync(Guid secretId, long matchedStep, CancellationToken ct);
}

/// <summary>Failure counters of the second factor (<see cref="ThrottleScope.Mfa"/>): bounded per (user, source) and per user, time-based blocks only.</summary>
public interface IMfaAttemptLimiter
{
    /// <summary>The remaining block for this user from this source, or null when not blocked.</summary>
    Task<TimeSpan?> BlockedForAsync(Guid userId, string source, CancellationToken ct);

    /// <summary>Records one failure; returns true when this failure newly reached the PER-USER bound (the caller raises the lockout alert).</summary>
    Task<bool> RecordFailureAsync(Guid userId, string source, CancellationToken ct);

    /// <summary>A success clears only this (user, source) pair counter; the per-user counter is deliberately kept.</summary>
    Task ResetPairAsync(Guid userId, string source, CancellationToken ct);
}

/// <summary>Bounds of the second-factor throttle (same family as the password step: 5 per user and source, 100 per user, in a 15 minute window, then a 15 minute block).</summary>
public sealed record MfaThrottleOptions(int MaxFailuresPerUserSource = 5, int MaxFailuresPerUser = 100, int WindowMinutes = 15, int BlockMinutes = 15);

public abstract record MfaResult
{
    public sealed record Verified(long Step) : MfaResult;
    /// <summary>Wrong, replayed, malformed or no secret: every failure looks the same to the caller.</summary>
    public sealed record Invalid(bool UserLockoutReached = false) : MfaResult;
    public sealed record Throttled(TimeSpan RetryAfter) : MfaResult;
    public sealed record Unavailable(string Code) : MfaResult;
}

/// <summary>
/// THE one place in the product that verifies a TOTP code (Phase 4.1, review M1): window 1, the atomic replay compare-and-set through the store, and the second-factor throttle. No other code may call
/// <see cref="Totp.Verify"/> (a unit test scans the solution). The window is fixed and not a parameter.
/// </summary>
public sealed class MfaVerifier(IMfaSecretStore store, IMfaAttemptLimiter limiter, TimeProvider clock)
{
    public const int Window = 1;
    private static readonly byte[] BurnSecret = new byte[Totp.SecretBytes];

    public async Task<MfaResult> VerifyAsync(Guid userId, string? code, string? sourceAddress, CancellationToken ct = default)
    {
        var source = string.IsNullOrWhiteSpace(sourceAddress) ? SourceAddress.Unknown : sourceAddress;
        if (await limiter.BlockedForAsync(userId, source, ct) is { } retry) return new MfaResult.Throttled(retry);

        MfaSecret? secret;
        try { secret = await store.FindActiveSecretAsync(userId, ct); }
        catch (MfaUnavailableException ex) { return new MfaResult.Unavailable(ex.Code); }                    // fail closed, and not counted against the user

        using (secret)
        {
            var now = clock.GetUtcNow();
            long? matched;
            if (secret is null)
            {
                Totp.Verify(BurnSecret, code, now, null, Window);                                              // equalize timing with the enrolled path
                matched = null;
            }
            else
            {
                matched = Totp.Verify(secret.Secret, code, now, secret.LastAcceptedStep, Window);
            }

            if (matched is { } step && secret is not null && await store.TryAdvanceLastStepAsync(secret.SecretId, step, ct))
            {
                await limiter.ResetPairAsync(userId, source, ct);
                return new MfaResult.Verified(step);
            }
            // wrong, replayed (the compare-and-set lost) or no secret
            return new MfaResult.Invalid(await limiter.RecordFailureAsync(userId, source, ct));
        }
    }
}
