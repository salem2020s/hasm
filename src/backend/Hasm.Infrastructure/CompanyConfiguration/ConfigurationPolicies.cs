using Hasm.Infrastructure.Companies;
using Microsoft.Extensions.Logging;
using Npgsql;
using S = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;

namespace Hasm.Infrastructure.CompanyConfiguration;

// ---------------------------------------------------------------------------------------------------------------- Trial entitlement

public enum Enforcement { Enforced, NotEnforced }

/// <summary>One approved Trial restriction and whether this build enforces it. "NotEnforced" is a release blocker, never an implicit allowance.</summary>
public sealed record TrialRestriction(string Key, int? Limit, Enforcement Enforcement, string Note);

/// <summary>
/// Restricted entitlement policy foundation (approved Trial edition, D-036). There is deliberately NO allow-all implementation: a
/// licensing phase replaces the edition source, never loosens this contract. This is not licensing, activation or purchasing.
/// </summary>
public interface IEntitlementPolicy
{
    string Edition { get; }
    /// <summary>Maximum currencies enabled at the same time (the base currency included).</summary>
    int MaxEnabledCurrencies { get; }
    /// <summary>Maximum branches EVER created (inactive ones count; branches are never deleted). The default is the Trial limit; a paid edition overrides it.</summary>
    int MaxBranches => 1;
    /// <summary>Maximum warehouses EVER created (inactive ones count; warehouses are never deleted). Trial default 1.</summary>
    int MaxWarehouses => 1;
    IReadOnlyList<TrialRestriction> Restrictions { get; }
}

public sealed class TrialEntitlementPolicy : IEntitlementPolicy
{
    public static TrialEntitlementPolicy Instance { get; } = new();
    public string Edition => "trial";
    public int MaxEnabledCurrencies => 1;
    public int MaxBranches => 1;
    public int MaxWarehouses => 1;

    public IReadOnlyList<TrialRestriction> Restrictions { get; } =
    [
        new("multi_currency", 1, Enforcement.Enforced, "Only the base currency may be enabled."),
        new("companies", 1, Enforcement.NotEnforced, "register-company / provision-company (Hasm.Admin) can create more companies."),
        new("users", 2, Enforcement.NotEnforced, "POST /api/v1/admin/users, Hasm.Admin bootstrap-admin and company memberships can exceed two users."),
        new("branches", 1, Enforcement.Enforced, "Every branch ever created counts, inactive ones included; the main branch created with the company is the one allowed branch."),
        new("warehouses", 1, Enforcement.Enforced, "Every warehouse ever created counts, inactive ones included."),
        new("posted_business_transactions", 100, Enforcement.NotEnforced, "No posting module exists yet."),
    ];
}

// ---------------------------------------------------------------------------------------------------------------- financial-history lock contract

/// <summary>
/// A future accounting/inventory module reports whether it holds financial history, valuation history or relevant opening balances for the
/// company (the facts that make the base currency and similar settings immutable). It must run on the SAME connection and transaction as the
/// configuration change so the answer cannot be stale.
/// </summary>
public interface IFinancialHistoryProbe
{
    string Domain { get; }
    /// <summary>False for the placeholders registered today: the domain's module does not exist, so nothing is checked.</summary>
    bool Implemented { get; }
    Task<bool> HasHistoryAsync(Guid companyId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);
}

/// <summary>Placeholder for a module that does not exist yet. It never claims history and never claims to be implemented.</summary>
public sealed class UnimplementedFinancialHistoryProbe(string domain) : IFinancialHistoryProbe
{
    public string Domain => domain;
    public bool Implemented => false;
    public Task<bool> HasHistoryAsync(Guid companyId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// Evaluates every REQUIRED domain. A registered real probe replaces the placeholder for its domain; a locked field is refused if ANY probe
/// reports history. Unimplemented domains are surfaced (<c>ChecksImplemented=false</c>) so callers and the UI never present "unlocked" as "safe".
/// A guard test fails when the company schema gains tables this registry does not know, so new financial modules cannot ship without probes.
/// </summary>
public sealed class FinancialHistoryGuard
{
    public static readonly string[] RequiredDomains = ["general_ledger", "inventory_valuation", "opening_balances"];

    private readonly Dictionary<string, IFinancialHistoryProbe> probes;

    public FinancialHistoryGuard(IEnumerable<IFinancialHistoryProbe>? registered = null)
    {
        probes = RequiredDomains.ToDictionary(d => d, d => (IFinancialHistoryProbe)new UnimplementedFinancialHistoryProbe(d));
        foreach (var p in registered ?? []) probes[p.Domain] = p;   // a later registration (a real module) replaces the placeholder
    }

    public IReadOnlyList<string> UnimplementedDomains => [.. probes.Values.Where(p => !p.Implemented).Select(p => p.Domain).Order(StringComparer.Ordinal)];

    public async Task<LockInformation> EvaluateAsync(Guid companyId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var locked = false;
        foreach (var probe in probes.Values)
            if (await probe.HasHistoryAsync(companyId, connection, transaction, ct)) locked = true;
        var unimplemented = UnimplementedDomains;
        return new LockInformation(locked, unimplemented.Count == 0, unimplemented);
    }
}

// ---------------------------------------------------------------------------------------------------------------- display-name projection

/// <summary>
/// Writes the nullable display-name projection into the Hasm_System registry. The company database stays authoritative. Updates are
/// monotonic (<c>WHERE display_name_revision &lt; @rev</c>), so a delayed or repeated projection can never overwrite newer information.
/// Failures are reported (never thrown into the caller's committed change) and are recovered by the next authorized read or write
/// or by the explicit sync endpoint.
/// </summary>
public sealed class CompanyDisplayNameProjection(CompanyRegistry registry, ILogger? logger = null)
{
    /// <summary>True when the registry holds this revision or a newer one afterwards; false when it could not be written.</summary>
    public async Task<bool> TryProjectAsync(Guid companyId, string? nameAr, string? nameEn, long revision, CancellationToken ct = default)
    {
        try
        {
            await registry.ProjectDisplayNameAsync(companyId, nameAr, nameEn, revision, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Code and company id only: no names, no connection details.
            logger?.LogWarning("company_projection_failed company={CompanyId} code={Code}", companyId, ex is PostgresException pg ? pg.SqlState : ex.GetType().Name);
            return false;
        }
    }
}

public static class CompanyRegistryProjectionExtensions
{
    private const string Sql =
        $"UPDATE {S.Table} SET {S.DisplayNameAr} = @ar, {S.DisplayNameEn} = @en, {S.DisplayNameRevision} = @rev WHERE {S.Id} = @id AND {S.DisplayNameRevision} < @rev";

    /// <summary>Monotonic conditional update; returns whether the registry changed (false = it already held this revision or a newer one).</summary>
    public static async Task<bool> ProjectDisplayNameAsync(this CompanyRegistry registry, Guid id, string? nameAr, string? nameEn, long revision, CancellationToken ct = default)
    {
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        await using var conn = new NpgsqlConnection(registry.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ar", (object?)nameAr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("en", (object?)nameEn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rev", revision);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
