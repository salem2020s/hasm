using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Hasm.Infrastructure.Identity;

public enum PasswordVerification { Failed, Success, SuccessRehashNeeded }

/// <summary>
/// Password hashing through the framework's <see cref="PasswordHasher{TUser}"/> (ASP.NET Core Identity v3 format:
/// PBKDF2-HMAC-SHA512, per-hash random 128-bit salt, iteration count stored in the hash, fixed-time comparison).
/// No custom cryptography. Hashes whose iteration count is below the configured one report
/// <see cref="PasswordVerification.SuccessRehashNeeded"/> so they are upgraded at the next login.
/// </summary>
public sealed class PasswordService
{
    public const int DefaultIterations = 210_000;
    private static readonly object NoUser = new();
    private readonly PasswordHasher<object> hasher;
    private readonly Lazy<string> dummyHash;

    public PasswordService(int iterations = DefaultIterations)
    {
        hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterations,
        }));
        // Same cost as real hashes; used so unknown accounts take the same time as known ones.
        dummyHash = new Lazy<string>(() => hasher.HashPassword(NoUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));
    }

    public string Hash(string password) => hasher.HashPassword(NoUser, password);

    public PasswordVerification Verify(string hash, string password)
    {
        try
        {
            return hasher.VerifyHashedPassword(NoUser, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordVerification.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
                _ => PasswordVerification.Failed,
            };
        }
        catch (FormatException)
        {
            return PasswordVerification.Failed; // corrupt stored value: never verifies, never crashes the request
        }
    }

    /// <summary>Spends the cost of a verification against a throw-away hash (account-enumeration timing defence).</summary>
    public void BurnVerification(string password) => hasher.VerifyHashedPassword(NoUser, dummyHash.Value, password);
}
