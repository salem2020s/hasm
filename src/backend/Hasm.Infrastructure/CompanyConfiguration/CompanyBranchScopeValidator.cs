using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Branch;
using W = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Warehouse;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// The production scope validator: confirms branches and warehouses against the REAL rows of the company (its own database, through the company's
/// least-privilege role) and reports their activity and activation epoch. An id of another company, an unknown id, an unavailable company
/// database or any error yields "not verifiable" (the caller fails closed). Warehouse scope is verified against the warehouse itself (never
/// inherited from its branch).
/// Authorization (Hasm_System) and target state (company database) are different databases: nothing here assumes a cross-database transaction;
/// the activation epoch stored on each assignment makes a race harmless (an assignment recorded for an older epoch is simply inert).
/// </summary>
public sealed class CompanyBranchScopeValidator(Func<CompanyDatabaseResolver> resolverFactory) : IScopeTargetValidator, IBudgetedScopeTargetValidator
{
    public async Task<bool> ExistsAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct) =>
        (await GetStateAsync(companyId, kind, id, ct)) is not null;

    public Task<ScopeTargetState?> GetStateAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct)
        => GetStateCoreAsync(companyId, kind, id, ct, null);
    Task<ScopeTargetState?> IBudgetedScopeTargetValidator.GetStateAsync(Guid companyId, ScopeKind kind, Guid id, Action reserveCommand, CancellationToken ct)
        => GetStateCoreAsync(companyId, kind, id, ct, reserveCommand);
    private async Task<ScopeTargetState?> GetStateCoreAsync(Guid companyId, ScopeKind kind, Guid id, CancellationToken ct, Action? reserveCommand)
    {
        if (id == Guid.Empty) return null;
        string sql;
        switch (kind)
        {
            case ScopeKind.Branch: sql = $"SELECT {B.IsActive}, {B.ActivationEpoch} FROM {B.Table} WHERE {B.Id} = @id"; break;
            case ScopeKind.Warehouse: sql = $"SELECT {W.IsActive}, {W.ActivationEpoch} FROM {W.Table} WHERE {W.Id} = @id"; break;
            case ScopeKind.CostCenter: sql = $"SELECT {CompanySchema.CostCenter.IsActive}, {CompanySchema.CostCenterPolicy.ActivationEpoch} FROM {CompanySchema.CostCenter.Table} WHERE {CompanySchema.CostCenter.Id} = @id"; break;
            default: return null;
        }
        // The resolver is created on first use: hosts without a company runtime configuration still start, and every lookup then fails closed.
        var resolver = resolverFactory();
        await using var conn = reserveCommand is null ? await resolver.OpenAsync(companyId, ct)
            : await resolver.OpenAsync(companyId, reserveCommand, ct);
        reserveCommand?.Invoke();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ScopeTargetState(r.GetBoolean(0), r.GetInt64(1)) : null;
    }
}
