using System.Collections.Immutable;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Costing;

internal sealed partial class CostFlowSnapshotService
{
    /// <summary>
    /// Internal read-only ordinary preview. Always loads its own authorized evidence; never accepts
    /// a caller snapshot. One deadline covers loading, reconciliation, comparison and final live
    /// authorization on success AND failure. No API/DI, source approval, posting or execution.
    /// </summary>
    internal Task<ConfigResult<CostFlowComparisonResult>> PreviewAsync(Guid user, Guid company, Guid item,
        ProposedFlowOperation proposal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (proposal is null || proposal.Id == Guid.Empty || proposal.Lines.IsDefaultOrEmpty)
            return Task.FromResult(ConfigResult<CostFlowComparisonResult>.Fail(ConfigStatus.Conflict, "invalid_replay_input"));
        if (proposal.Lines.Length > 500)
            return Task.FromResult(ConfigResult<CostFlowComparisonResult>.Fail(ConfigStatus.Conflict, "impact_history_too_large"));
        if (proposal.Kind is not (CostOperationKind.Receipt or CostOperationKind.Issue or CostOperationKind.Transfer))
            return Task.FromResult(ConfigResult<CostFlowComparisonResult>.Fail(ConfigStatus.Conflict, "impact_history_unsupported"));
        if (proposal.Kind == CostOperationKind.Transfer && proposal.Lines.Length != 1)
            return Task.FromResult(ConfigResult<CostFlowComparisonResult>.Fail(ConfigStatus.Conflict, "invalid_replay_input"));
        // Bound request-only work before touching company metadata. Roots come from every child,
        // including destination references; validity is never used to hide an endpoint.
        var roots = ImmutableArray.CreateBuilder<Guid>();
        foreach (var line in proposal.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line is null)
                return Task.FromResult(ConfigResult<CostFlowComparisonResult>.Fail(ConfigStatus.Conflict, "invalid_replay_input"));
            roots.Add(line.WarehouseId);
            if (line.DestinationWarehouseId is { } destination) roots.Add(destination);
        }
        return ReadAuthorizedAsync(user, company, item, roots.ToImmutable(),
            (snapshot, token) => CostFlowComparison.Calculate(snapshot, proposal, token), ct);
    }
}
