using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>The API refuses to use the system database because the runtime account or the TOTP secret store is not least-privilege (codes only).</summary>
public sealed class SystemRuntimePrivilegeException(IReadOnlyList<string> codes)
    : Exception("The system database runtime account is not least-privilege for the TOTP secret store: " + string.Join(", ", codes) + ". Run Hasm.Admin grant-system-runtime.")
{
    public IReadOnlyList<string> Codes { get; } = codes;
}

/// <summary>
/// The fail-closed check of owner decision D-235 (the system twin of the company schema gate): with the ACTUAL connected role (<c>current_user</c>, not a configured name) it asks
/// <see cref="SystemRuntimePrivileges.FindAsync"/> and refuses when there is a violation. The answer is cached for <see cref="CheckLifetime"/>. The opt-out
/// <c>Hasm:Database:AllowOverprivilegedSystemRuntime</c> (default false; disposable single-account test environments only, like <c>AllowOverprivilegedCompanyRuntime</c>) turns the refusal into a
/// loud warning, never into silence. Notes (over-grants on other system tables) are logged and never refuse.
/// </summary>
public sealed class SystemRuntimeGate(bool allowOverprivileged, TimeProvider clock, ILogger? logger = null)
{
    public TimeSpan CheckLifetime { get; init; } = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<int, DateTimeOffset> verifiedUntil = new();
    private int warned;

    /// <summary>Throws <see cref="SystemRuntimePrivilegeException"/> when the connected role or the secret store violates least privilege (unless the opt-out is set). Never caches a refusal.</summary>
    public async Task EnsureAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        if (verifiedUntil.TryGetValue(0, out var until) && until > clock.GetUtcNow()) return;
        string role;
        await using (var who = new NpgsqlCommand("SELECT current_user::text", conn)) role = (string)(await who.ExecuteScalarAsync(ct))!;
        var findings = await SystemRuntimePrivileges.FindAsync(conn, role, ct);
        if (findings.Notes.Count > 0 && Interlocked.Exchange(ref warned, 1) == 0)
            logger?.LogWarning("System database runtime account holds more than its catalog (reported, not refused): {Codes}. Run Hasm.Admin grant-system-runtime.", string.Join(", ", findings.Notes));
        if (!findings.IsLeastPrivilege)
        {
            if (!allowOverprivileged)
            {
                logger?.LogCritical("REFUSED: the system database runtime account is not least-privilege for the TOTP secret store ({Codes}). Run Hasm.Admin grant-system-runtime with a separate runtime account.", string.Join(", ", findings.Violations));
                throw new SystemRuntimePrivilegeException(findings.Violations);
            }
            logger?.LogCritical("UNSAFE: Hasm:Database:AllowOverprivilegedSystemRuntime is set; the system database runtime account is NOT least-privilege for the TOTP secret store ({Codes}). Disposable test environments only.", string.Join(", ", findings.Violations));
        }
        verifiedUntil[0] = clock.GetUtcNow() + CheckLifetime;
    }
}
