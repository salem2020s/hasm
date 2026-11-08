using Hasm.Infrastructure.Configuration;
using Npgsql;

namespace Hasm.Infrastructure.SystemDatabase;

public sealed record InitializationResult(bool DatabaseCreated, IReadOnlyList<int> AppliedMigrations, int AlreadyApplied);

/// <summary>
/// Explicit administrative initialisation of the system database only: creates it if missing
/// (never drops or alters an existing one) and applies pending migrations. Idempotent.
/// Company databases are not touched; their provisioning is a separate, later task.
/// </summary>
public sealed class SystemDatabaseInitializer(HasmDatabaseOptions options)
{
    private const string DuplicateDatabase = "42P04";

    public async Task<InitializationResult> InitializeAsync(CancellationToken ct = default)
    {
        options.EnsureValid(requireAdmin: true);
        var scripts = MigrationScript.LoadEmbedded();

        var created = await EnsureDatabaseAsync(ct);
        var result = await new MigrationRunner(options.BuildSystemConnectionString(admin: true)).ApplyAsync(scripts, ct);
        return new InitializationResult(created, result.Applied, result.AlreadyApplied);
    }

    private async Task<bool> EnsureDatabaseAsync(CancellationToken ct)
    {
        var name = options.SystemDatabaseName!;
        await using var conn = new NpgsqlConnection(options.BuildMaintenanceConnectionString());
        await conn.OpenAsync(ct);

        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn))
        {
            exists.Parameters.AddWithValue("n", name);
            if (await exists.ExecuteScalarAsync(ct) is not null) return false;
        }

        // Name was validated as a plain lowercase identifier; quoting is defence in depth.
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(name);
        try
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE {quoted}", conn);
            await create.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (IsConcurrentCreation(ex))
        {
            // Created concurrently by another initialiser. Confirm it really exists so that no
            // unrelated failure is ever swallowed.
            await using var verify = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn);
            verify.Parameters.AddWithValue("n", name);
            if (await verify.ExecuteScalarAsync(ct) is not null) return false;
            throw;
        }
    }

    // Racing CREATE DATABASE calls fail with 42P04, or - when both pass the name check before either
    // commits its catalog row - with 23505 on the pg_database name index. Both mean "already created".
    internal static bool IsConcurrentCreation(PostgresException ex) =>
        ex.SqlState == DuplicateDatabase
        || (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "pg_database_datname_index");
}
