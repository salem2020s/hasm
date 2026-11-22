using System.Collections.Immutable;

namespace Hasm.Infrastructure.Costing;

internal sealed record ProposedCostEvent(Guid Id, DateOnly EffectiveDate, CostOperationKind Kind, decimal Quantity, decimal? ReceiptAmount = null);
internal sealed record CostImpactChange(long EntryId, Guid OperationId, decimal OriginalAmount, decimal ProposedAmount,
    decimal OriginalQuantityAfter, decimal ProposedQuantityAfter, decimal OriginalValueAfter, decimal ProposedValueAfter);
internal sealed record CostImpactPreviewResult(string HistoryFingerprint, ProposedCostEvent ProposedEvent, ReplayStep ProposedStep,
    ImmutableArray<CostImpactChange> Changes, decimal OriginalQuantity, decimal ProposedQuantity, decimal OriginalValue, decimal ProposedValue);

/// <summary>
/// Internal ordinary receipt/issue comparison only; no API, persistence, approval or execution authority.
/// Uses the authorized snapshot adapter's complete ordinary history and existing exact costing arithmetic.
/// A same-date proposal follows all existing events on that date, preserving their original sequence.
/// Unsupported history, incomplete/inconsistent evidence, or a shortage anywhere in the revised trajectory refuses the entire comparison.
/// Values are simulation evidence, NOT journal adjustments; period/account/approval/transfer/obligation contracts are outside this helper.
/// </summary>
internal static class CostImpactPreview
{
    internal static CostImpactPreviewResult Calculate(CostHistorySnapshot snapshot, ProposedCostEvent proposed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.Entries.IsDefault || snapshot.Entries.Length > 100_000 || snapshot.LastSequence != snapshot.Entries.Length ||
            snapshot.CompanyId == Guid.Empty || snapshot.ItemId == Guid.Empty || snapshot.WarehouseId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.HistoryFingerprint))
            throw new CostingException("invalid_impact_snapshot");

        // This internal value object is constructible in tests. Check completeness again rather than trusting a caller's totals.
        var original = ImmutableArray.CreateBuilder<ReplayEvent>(snapshot.Entries.Length);
        DateOnly? previousDate = null;
        foreach (var e in snapshot.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.EntryId <= 0 || e.Sequence != original.Count + 1L || e.OperationId == Guid.Empty || (previousDate is { } previous && e.EffectiveDate < previous))
                throw new CostingException("impact_history_inconsistent");
            original.Add(e.ToReplayEvent());
            previousDate = e.EffectiveDate;
        }
        var baseline = CostReplay.Calculate(snapshot.Method, snapshot.MoneyDecimals, original.MoveToImmutable(), ct);
        for (var n = 0; n < snapshot.Entries.Length; n++)
        {
            var e = snapshot.Entries[n]; var step = baseline.Steps[n];
            if ((e.Amount, e.QuantityAfter, e.ValueAfter) != (step.Amount, step.QuantityAfter, step.ValueAfter))
                throw new CostingException("impact_history_inconsistent");
        }
        if ((snapshot.Quantity, snapshot.Value) != (baseline.QuantityAfter, baseline.ValueAfter))
            throw new CostingException("impact_history_inconsistent");

        var proposedInput = new ReplayEvent(proposed.Id, proposed.Kind, proposed.Quantity, proposed.ReceiptAmount);
        var insertion = 0;
        while (insertion < snapshot.Entries.Length && snapshot.Entries[insertion].EffectiveDate <= proposed.EffectiveDate) insertion++;
        var revisedInputs = ImmutableArray.CreateBuilder<ReplayEvent>(snapshot.Entries.Length + 1);
        for (var n = 0; n <= snapshot.Entries.Length; n++)
        {
            if (n == insertion) revisedInputs.Add(proposedInput);
            if (n < snapshot.Entries.Length) revisedInputs.Add(snapshot.Entries[n].ToReplayEvent());
        }
        var revised = CostReplay.Calculate(snapshot.Method, snapshot.MoneyDecimals, revisedInputs.MoveToImmutable(), ct);
        var changes = ImmutableArray.CreateBuilder<CostImpactChange>();
        for (var n = 0; n < snapshot.Entries.Length; n++)
        {
            ct.ThrowIfCancellationRequested();
            var e = snapshot.Entries[n]; var step = revised.Steps[n < insertion ? n : n + 1];
            // Include quantity/value trajectory changes even when an event's own cost is unchanged.
            if ((e.Amount, e.QuantityAfter, e.ValueAfter) != (step.Amount, step.QuantityAfter, step.ValueAfter))
                changes.Add(new CostImpactChange(e.EntryId, e.OperationId, e.Amount, step.Amount, e.QuantityAfter, step.QuantityAfter, e.ValueAfter, step.ValueAfter));
        }
        return new CostImpactPreviewResult(snapshot.HistoryFingerprint, proposed, revised.Steps[insertion], changes.ToImmutable(),
            snapshot.Quantity, revised.QuantityAfter, snapshot.Value, revised.ValueAfter);
    }
}
