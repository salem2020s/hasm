using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;

namespace Hasm.Infrastructure.Companies;

/// <summary>
/// Data access for the central company registry. Internal infrastructure: it is deliberately not
/// exposed through any HTTP endpoint until authentication and authorization exist.
/// Physical identifiers are compile-time constants from <see cref="SystemSchema"/>, never user input.
/// </summary>
public sealed class CompanyRegistry(string connectionString, Func<string>? nameGenerator = null)
{
    internal string ConnectionString => connectionString;

    private const string UniqueViolation = "23505";
    private const int MaxNameAttempts = 5;

    private const string Columns = $"{S.Id}, {S.PhysicalDatabaseName}, {S.ProvisioningState}, {S.CreatedAtUtc}, {S.StateChangedAtUtc}, {S.ProvisioningAttempts}, {S.LastErrorCode}";

    /// <summary>Registers a company in the Pending state with a newly generated opaque database name. Does not create the database.</summary>
    public async Task<CompanyRecord> RegisterPendingAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        for (var attempt = 1; ; attempt++)
        {
            var record = CompanyRecord.CreatePending(DateTimeOffset.UtcNow, nameGenerator);
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {S.Table} ({Columns}) VALUES (@id, @name, @state, @created, @changed, 0, NULL)", conn);
            cmd.Parameters.AddWithValue("id", record.Id);
            cmd.Parameters.AddWithValue("name", record.PhysicalDatabaseName);
            cmd.Parameters.AddWithValue("state", (short)record.State);
            cmd.Parameters.AddWithValue("created", record.CreatedAtUtc.UtcDateTime);
            cmd.Parameters.AddWithValue("changed", record.StateChangedAtUtc.UtcDateTime);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
                return record;
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation && attempt < MaxNameAttempts)
            {
                // Astronomically unlikely generated-name collision: try a fresh name.
            }
        }
    }

    public Task<CompanyRecord?> GetAsync(Guid id, CancellationToken ct = default) => GetCoreAsync(id, ct, null);
    internal Task<CompanyRecord?> GetAsync(Guid id, Action reserveCommand, CancellationToken ct) => GetCoreAsync(id, ct, reserveCommand);
    private async Task<CompanyRecord?> GetCoreAsync(Guid id, CancellationToken ct, Action? reserveCommand)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        reserveCommand?.Invoke();
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {S.Table} WHERE {S.Id} = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<CompanyRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM {S.Table} ORDER BY {S.CreatedAtUtc}, {S.Id}", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<CompanyRecord>();
        while (await reader.ReadAsync(ct)) result.Add(Map(reader));
        return result;
    }

    /// <summary>
    /// Moves a company to a new provisioning state if the transition is allowed. The update is
    /// conditional on the state read, so concurrent writers cannot silently overwrite each other.
    /// Returns false when the company is missing or its state changed concurrently.
    /// </summary>
    public async Task<bool> TransitionAsync(Guid id, ProvisioningState next, CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct);
        if (current is null) return false;
        var updated = current.TransitionTo(next, DateTimeOffset.UtcNow);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {S.Table} SET {S.ProvisioningState} = @next, {S.StateChangedAtUtc} = @changed " +
            $"WHERE {S.Id} = @id AND {S.ProvisioningState} = @expected", conn);
        cmd.Parameters.AddWithValue("next", (short)next);
        cmd.Parameters.AddWithValue("changed", updated.StateChangedAtUtc.UtcDateTime);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected", (short)current.State);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }


    // The three methods below are used only by CompanyProvisioner, which holds the per-company
    // advisory lock while calling them. They are conditional updates so a lost race changes nothing.

    /// <summary>Pending/Failed/stale-Provisioning -> Provisioning, counting the attempt. Caller must hold the company lock.</summary>
    internal Task<bool> BeginProvisioningAsync(Guid id, CancellationToken ct) => ExecuteConditional(
        $"UPDATE {S.Table} SET {S.ProvisioningState} = {(short)ProvisioningState.Provisioning}, " +
        $"{S.ProvisioningAttempts} = {S.ProvisioningAttempts} + 1, {S.LastErrorCode} = NULL, {S.StateChangedAtUtc} = @now " +
        $"WHERE {S.Id} = @id AND {S.ProvisioningState} IN ({(short)ProvisioningState.Pending}, {(short)ProvisioningState.Provisioning}, {(short)ProvisioningState.Failed})",
        id, null, ct);

    internal Task<bool> CompleteProvisioningAsync(Guid id, CancellationToken ct) => ExecuteConditional(
        $"UPDATE {S.Table} SET {S.ProvisioningState} = {(short)ProvisioningState.Ready}, {S.LastErrorCode} = NULL, {S.StateChangedAtUtc} = @now " +
        $"WHERE {S.Id} = @id AND {S.ProvisioningState} = {(short)ProvisioningState.Provisioning}",
        id, null, ct);

    internal Task<bool> FailProvisioningAsync(Guid id, string errorCode, CancellationToken ct) => ExecuteConditional(
        $"UPDATE {S.Table} SET {S.ProvisioningState} = {(short)ProvisioningState.Failed}, {S.LastErrorCode} = @code, {S.StateChangedAtUtc} = @now " +
        $"WHERE {S.Id} = @id AND {S.ProvisioningState} = {(short)ProvisioningState.Provisioning}",
        id, errorCode, ct);

    private async Task<bool> ExecuteConditional(string sql, Guid id, string? code, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        if (code is not null) cmd.Parameters.AddWithValue("code", code);
        // Application clock (not the server clock) so a state change can never precede creation.
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static CompanyRecord Map(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        (ProvisioningState)r.GetInt16(2),
        new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc)),
        r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetString(6));
}
