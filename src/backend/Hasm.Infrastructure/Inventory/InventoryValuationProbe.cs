using Hasm.Infrastructure.CompanyConfiguration;
using Npgsql;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using IA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingVersion;
using IG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.InventoryAccountingGroup;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;
using RV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPostingReversal;
using AP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentPosting;
using AR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockAdjustmentReversal;
using NO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeObligation;
using NG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.NegativeStockGuard;

namespace Hasm.Infrastructure.Inventory;

/// <summary>
/// The REAL inventory close probe (domain <c>inventory_valuation_differences</c>, Phase 3.7C; it replaces the "module absent" placeholder). A period cannot be closed while, for the stock history up to the END of that period:
/// <list type="bullet">
/// <item>a stock posting or stock adjustment posting (Phase 3.8B-2) whose accounting side FAILED exists (its journal entry was cancelled or reversed outside the stock reversal), or</item>
/// <item>the value of the costing ledger (the sum of the signed values of every costing entry dated up to the period end) differs from the balance of the inventory accounts in the General Ledger (posted and reversed
/// entries dated up to the period end). That difference is exactly the stock movement whose entry is still a draft or pending approval, a reversal entry not posted yet, or a manual entry on an inventory account.</item>
/// </list>
/// A company that has no stock posting up to the period end is not affected: the inventory module is not in use. KNOWN LIMITATION (accepted, tracked for Phase 3.27): once a stock posting exists, any ledger balance on an
/// inventory account that the costing ledger does not know about (an opening balance kept only in the ledger, a manual entry) counts as a difference until the opening stock is brought into the costing ledger by the opening-stock
/// import or the entry is corrected. The result is exact decimal arithmetic in the database and the same connection and transaction as the close, so it cannot be stale.
/// </summary>
public sealed class InventoryValuationPeriodCloseProbe : IPeriodCloseProbe
{
    public string Domain => "inventory_valuation_differences";
    public bool Implemented => true;

    public async Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var end = period.EndDate.ToDateTime(TimeOnly.MinValue);
        // the module is in use only once something was posted
        await using (var any = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {P.Table} WHERE {P.EffectiveDate} <= @e) OR EXISTS (SELECT 1 FROM {AP.Table} WHERE {AP.EffectiveDate} <= @e)", connection, transaction))
        {
            any.Parameters.AddWithValue("e", end);
            if (!(bool)(await any.ExecuteScalarAsync(ct))!) return false;
        }
        // a posting whose journal entry was cancelled or reversed outside the stock reversal has no valid accounting side (and is not reversed)
        await using (var failed = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {P.Table} p JOIN {JE.Table} j ON j.{JE.Id} = p.{P.JournalEntryId} WHERE p.{P.EffectiveDate} <= @e AND j.{JE.Status} IN (5, 6) " +
            $"AND NOT EXISTS (SELECT 1 FROM {RV.Table} rv WHERE rv.{RV.PostingId} = p.{P.Id})) " +
            // Phase 3.8B-2: the same for stock adjustments posted from a stocktake
            $"OR EXISTS (SELECT 1 FROM {AP.Table} p JOIN {JE.Table} j ON j.{JE.Id} = p.{AP.JournalEntryId} WHERE p.{AP.EffectiveDate} <= @e AND j.{JE.Status} IN (5, 6) " +
            $"AND NOT EXISTS (SELECT 1 FROM {AR.Table} rv WHERE rv.{AR.PostingId} = p.{AP.Id}))", connection, transaction))
        {
            failed.Parameters.AddWithValue("e", end);
            if ((bool)(await failed.ExecuteScalarAsync(ct))!) return true;
        }
        // Phase 3.8B-3 (N3, D7, R20): an obligation open now with a document date up to the period end blocks FINAL close (a period being final-closed is Soft Closed; at soft close it is only a warning of the close
        // report); for both closes the obligation account must reconcile at the period end and no settlement journal may have failed
        var negative = await NegativeStockReconciliation.ComputeAsync(connection, transaction, period.EndDate, ct);
        if (negative.Any)
        {
            if (negative.Expected != negative.Ledger || negative.Differences != negative.LedgerVariance || negative.FailedJournals > 0) return true;
            if (period.State == FiscalPeriodState.SoftClosed)
            {
                await using var open = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {NO.Table} o WHERE o.{NO.EffectiveDate} <= @e AND {NG.OpenQuantityFunction}(o.{NO.Id}) > 0)", connection, transaction);
                open.Parameters.AddWithValue("e", end);
                if ((bool)(await open.ExecuteScalarAsync(ct))!) return true;
            }
        }
        // costing ledger value versus the inventory accounts of the General Ledger, both up to the period end
        await using var diff = new NpgsqlCommand(
            $"SELECT (SELECT COALESCE(SUM(c.{CE.Direction} * c.{CE.Amount}), 0) FROM {CE.Table} c WHERE c.{CE.EffectiveDate} <= @e) <> " +
            $"(SELECT COALESCE(SUM(l.{JL.BaseDebit} - l.{JL.BaseCredit}), 0) FROM {JL.Table} l JOIN {JE.Table} j ON j.{JE.Id} = l.{JL.Entry} " +
            $"WHERE j.{JE.Status} IN (4, 5) AND j.{JE.Date} <= @e AND l.{JL.Account} IN (" +
            $"SELECT {IA.DefaultAccountId} FROM {IA.Table} WHERE {IA.DefaultAccountId} IS NOT NULL UNION SELECT {IG.AccountId} FROM {IG.Table}))", connection, transaction);
        diff.Parameters.AddWithValue("e", end);
        return (bool)(await diff.ExecuteScalarAsync(ct))!;
    }
}
