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
