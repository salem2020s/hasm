using Hasm.Infrastructure.CompanyConfiguration;
using Npgsql;
using JE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalEntry;
using JL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.JournalLine;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// The REAL posting-history probe of the Chart of Accounts (replaces the placeholder): an account has posting history when a POSTED or REVERSED entry has a line on it.
/// Drafts, pending and cancelled entries are not history. It runs on the same connection and transaction as the chart change, so the answer cannot be stale.
/// </summary>
public sealed class JournalAccountUsageProbe : IAccountUsageProbe
{
    public string Domain => "general_ledger_postings";
    public bool Implemented => true;

    public async Task<bool> HasPostingsAsync(Guid companyId, Guid accountId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {JL.Table} l JOIN {JE.Table} e ON e.{JE.Id} = l.{JL.Entry} WHERE l.{JL.Account} = @a AND e.{JE.Status} IN (4, 5))", connection, transaction);
        cmd.Parameters.AddWithValue("a", accountId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>The General Ledger's financial-history probe (domain <c>general_ledger</c>): any posted or reversed entry freezes the base currency and the time zone.</summary>
public sealed class JournalFinancialHistoryProbe : IFinancialHistoryProbe
{
    public string Domain => "general_ledger";
    public bool Implemented => true;

    public async Task<bool> HasHistoryAsync(Guid companyId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {JE.Table} WHERE {JE.Status} IN (4, 5))", connection, transaction);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// The pending-postings period-close probe (domain <c>pending_accounting_postings</c>): a period cannot be closed while a Draft, Pending approval or Approved entry is dated inside it
/// (it could never be posted afterwards). It runs on the close's own connection and transaction.
/// </summary>
public sealed class PendingJournalPeriodCloseProbe : IPeriodCloseProbe
{
    public string Domain => "pending_accounting_postings";
    public bool Implemented => true;

    public async Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {JE.Table} WHERE {JE.Status} IN (1, 2, 3) AND {JE.Date} BETWEEN @s AND @e)", connection, transaction);
        cmd.Parameters.AddWithValue("s", period.StartDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("e", period.EndDate.ToDateTime(TimeOnly.MinValue));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// The pending-adjustments period-close probe (domain <c>pending_adjustments</c>): a period cannot be closed while a Draft, Pending approval or Approved REVERSAL or ADJUSTING entry
/// is dated inside it (a correction that was started and not completed). Runs on the close's own connection and transaction.
/// </summary>
public sealed class PendingAdjustmentsPeriodCloseProbe : IPeriodCloseProbe
{
    public string Domain => "pending_adjustments";
    public bool Implemented => true;

    public async Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {JE.Table} WHERE {JE.Status} IN (1, 2, 3) AND {JE.Type} IN (2, 3) AND {JE.Date} BETWEEN @s AND @e)", connection, transaction);
        cmd.Parameters.AddWithValue("s", period.StartDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("e", period.EndDate.ToDateTime(TimeOnly.MinValue));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// Year-end consistency (domain <c>year_end_closing</c>): a period of a year whose posted closing (or carried-forward opening balances) no longer matches the ledger cannot be closed
/// until the closing is reversed and generated again, so next-year opening balances can never be left inconsistent with the closed year.
/// </summary>
public sealed class YearEndClosingPeriodCloseProbe : IPeriodCloseProbe
{
    public string Domain => "year_end_closing";
    public bool Implemented => true;

    public async Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYearClosing.Table} cl WHERE cl.{Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYearClosing.YearId} = @y AND {Hasm.Infrastructure.CompanyDatabase.CompanySchema.ClosingLogic.StaleFunction}(cl.{Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYearClosing.Id}))", connection, transaction);
        cmd.Parameters.AddWithValue("y", period.YearId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// A close-check domain whose module does not exist in this release (for example inventory valuation differences): there can be nothing to check, so closing does not refuse, but the
/// API and the year-end closing list the domain as <c>not applicable</c>, never as passed. The module that introduces such data must replace this probe with a real one (the schema
/// contract test and the phase checklist require it).
/// </summary>
public sealed class ModuleAbsentPeriodCloseProbe(string domain) : IPeriodCloseProbe
{
    public string Domain => domain;
    public bool Implemented => false;
    public bool ModuleAbsent => true;
    public Task<bool> HasBlockersAsync(Guid companyId, FiscalPeriodView period, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(false);
}
