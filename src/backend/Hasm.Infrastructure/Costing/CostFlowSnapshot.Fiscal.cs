using System.Collections.Immutable;
using Hasm.Infrastructure.CompanyConfiguration;
using Npgsql;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using Y = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;

namespace Hasm.Infrastructure.Costing;

internal sealed record CostFiscalPeriodEvidence(Guid Id, Guid YearId, DateOnly Start, DateOnly End,
    FiscalPeriodState State, long Revision, bool YearClosed, long YearRevision);

internal sealed partial class CostFlowSnapshotService
{
    // Source and changed-history dates only. Unchanged closed baseline history is not repriced.
    // Compensation dates/account policy/approval and locked execution revalidation remain separate.
    private static async Task<ImmutableArray<CostFiscalPeriodEvidence>> ReadFiscalEvidence(NpgsqlConnection conn, NpgsqlTransaction tx,
        CostSourceHeader source, ImmutableArray<CostFlowSnapshot> snapshots, ImmutableArray<CostFlowComparisonResult> comparisons,
        Budget budget, CancellationToken ct)
    {
        var dates = new HashSet<DateOnly> { source.Date };
        if (snapshots.Length != comparisons.Length) throw Inconsistent();
        for (var index = 0; index < snapshots.Length; index++)
        {
            var original = new Dictionary<long, DateOnly>();
            foreach (var entry in snapshots[index].Entries)
            {
                ct.ThrowIfCancellationRequested(); if (!original.TryAdd(entry.Id, entry.Date)) throw Inconsistent();
            }
            foreach (var change in comparisons[index].Changes)
            {
                ct.ThrowIfCancellationRequested(); if (!original.TryGetValue(change.EntryId, out var date)) throw Inconsistent();
                if (!dates.Contains(date) && dates.Count == 200_001) throw TooLarge(); dates.Add(date);
            }
        }
        var orderedDates = dates.Order().ToArray();
        var periods = new List<CostFiscalPeriodEvidence>();
        await using (var q = budget.Command($"SELECT p.{P.Id},p.{P.YearId},p.{P.StartDate},p.{P.EndDate},p.{P.Status},p.{P.Revision},y.{Y.Status},y.{Y.Revision} FROM {P.Table} p LEFT JOIN {Y.Table} y ON y.{Y.Id}=p.{P.YearId} WHERE p.{P.StartDate}<=@last AND p.{P.EndDate}>=@first ORDER BY p.{P.StartDate},p.{P.Id} LIMIT @limit", conn, tx))
        {
            q.Parameters.AddWithValue("first", orderedDates[0]); q.Parameters.AddWithValue("last", orderedDates[^1]);
            q.Parameters.AddWithValue("limit", budget.DiscoveryRemaining + 1);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                budget.Discovery(); if (r.IsDBNull(6) || r.IsDBNull(7)) throw Inconsistent();
                var period = new CostFiscalPeriodEvidence(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2),
                    r.GetFieldValue<DateOnly>(3), (FiscalPeriodState)r.GetInt16(4), r.GetInt64(5), r.GetInt16(6) == 2, r.GetInt64(7));
                if (period.Id == Guid.Empty || period.YearId == Guid.Empty || period.Start > period.End || period.Revision < 1 ||
                    period.YearRevision < 1 || r.GetInt16(6) is not (1 or 2) ||
                    period.State is not (FiscalPeriodState.Open or FiscalPeriodState.SoftClosed or FiscalPeriodState.FinalClosed)) throw Inconsistent();
                if (periods.Count > 0 && periods[^1].End >= period.Start) throw Inconsistent();
                periods.Add(period);
            }
        }
        var used = ImmutableArray.CreateBuilder<CostFiscalPeriodEvidence>();
        var cursor = 0; Guid? previous = null;
        foreach (var date in orderedDates)
        {
            ct.ThrowIfCancellationRequested();
            while (cursor < periods.Count && periods[cursor].End < date) { ct.ThrowIfCancellationRequested(); cursor++; }
            var period = cursor < periods.Count && periods[cursor].Start <= date ? periods[cursor] : null;
            var gate = FiscalPeriodGate.Evaluate(period?.Id, period?.State, false);
            if (!gate.Allowed) throw new CostingException(gate.Reason!);
            if (period is null || period.YearClosed) throw Inconsistent();
            if (date == source.Date && period.YearId != source.FiscalYearId) throw new CostingException("source_fiscal_year_changed");
            if (previous != period.Id) { used.Add(period); previous = period.Id; }
        }
        ct.ThrowIfCancellationRequested();
        return used.ToImmutable();
    }
}
