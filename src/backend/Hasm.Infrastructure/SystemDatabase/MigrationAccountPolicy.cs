using Npgsql;

namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>
/// Owner decision 2026-11-22 (Q3): production migrations run as a dedicated NON-superuser administrative account. A superuser can switch
/// every trigger off (<c>session_replication_role</c>), bypass every privilege and replace any catalog object, so the guard's static rules
/// and the post-migration checks are only meaningful for an account that cannot do that. The administrative commands that migrate
/// (init-system-db, provision-company, migrate-companies) therefore refuse a superuser account unless the explicit development/test
/// override is given; every use of the override is written to the diagnostic stream.
/// </summary>
public static class MigrationAccountPolicy
{
    /// <summary>The command-line switch of Hasm.Admin that sets the override (development and tests only).</summary>
    public const string OverrideSwitch = "--allow-superuser-for-development";

    public static string RefusalMessage =>
        "The migration account is a PostgreSQL superuser. Production migrations must run as a dedicated non-superuser administrative account (owner decision 2026-11-22, Q3; " +
        "docs/MIGRATION-GUARD.md, \"Migration account\"): CREATEDB, owner of the system and company databases, no SUPERUSER, no CREATEROLE, no REPLICATION, no BYPASSRLS, separate from " +
        $"the runtime credentials. Nothing was changed. For development or tests only, run the command with {OverrideSwitch}; the override is logged.";

    /// <summary>The decision, separated from the reading so it can be tested without a superuser: null when the account may migrate, else the refusal.</summary>
    internal static string? Evaluate(bool isSuperuser, bool overrideGiven, Action<string> log)
    {
        if (!isSuperuser) return null;
        if (!overrideGiven) return RefusalMessage;
        log($"WARNING: migrations run as a PostgreSQL superuser because {OverrideSwitch} was given (development and tests only; never use a superuser in production).");
        return null;
    }

    /// <summary>
    /// Reads <c>is_superuser</c> of the open session and refuses a superuser unless <paramref name="allowSuperuser"/>. <paramref name="additionalReading"/> is a TEST SEAM: a second
    /// reading that can only ADD a refusal (the real reading is always taken), because the test clusters deliberately have no superuser account to connect with.
    /// </summary>
    public static async Task EnsureAsync(NpgsqlConnection conn, bool allowSuperuser, Action<string>? log, Func<bool>? additionalReading, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_catalog.current_setting('is_superuser')", conn);
        var real = string.Equals((string?)await cmd.ExecuteScalarAsync(ct), "on", StringComparison.Ordinal);
        var problem = Evaluate(real || additionalReading?.Invoke() == true, allowSuperuser, log ?? Console.Error.WriteLine);
        if (problem is not null) throw new MigrationException(problem);
    }

    /// <summary>The same check on a fresh connection, for the commands that create a database before the runner starts.</summary>
    public static async Task EnsureAsync(string connectionString, bool allowSuperuser, Action<string>? log = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureAsync(conn, allowSuperuser, log, null, ct);
    }
}
