using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>Journal entry states. Persisted and constrained by PostgreSQL; never renumber.</summary>
public enum JournalStatus : short { Draft = 1, PendingApproval = 2, Approved = 3, Posted = 4, Reversed = 5, Cancelled = 6 }

/// <summary>Normal entry, the reversal of a posted entry, or an adjusting entry that references the posted entry it adjusts.</summary>
public enum JournalType : short { Normal = 1, Reversal = 2, Adjusting = 3 }

/// <summary>Journal event kinds (the append-only event log). Persisted; never renumber.</summary>
public enum JournalEventKind : short { Created = 1, Updated = 2, Submitted = 3, ReturnedToDraft = 4, Cancelled = 5, Approved = 6, Rejected = 7, Posted = 8, ReversalDrafted = 9, AdjustingDrafted = 10, Reversed = 11, DecisionRecorded = 12 }

/// <summary>One allocation: an exact <see cref="Amount"/> in the line currency OR a <see cref="Percent"/> (all allocations of a line use the same mode; percentages must total exactly 100 and are converted by <see cref="AllocationMath"/>).</summary>
public sealed record JournalAllocationInput(Guid? CostCenterId, decimal? Amount, decimal? Percent = null);

/// <summary>
/// Deterministic distribution of an amount by percentages (largest-remainder method on exact decimals): every share is floored to the currency's minor units, the leftover units go one by one
/// to the largest fractional remainders, ties to the larger percentage and then to the lower cost-center id, so the SAME inputs always give the SAME amounts, whatever the order they were entered in,
/// and the shares always add up to the total exactly.
/// </summary>
public static class AllocationMath
{
    public static IReadOnlyDictionary<Guid, decimal> Distribute(decimal total, int minorUnits, IReadOnlyList<(Guid CostCenterId, decimal Percent)> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("no parts", nameof(parts));
        if (parts.Sum(p => p.Percent) != 100m || parts.Any(p => p.Percent <= 0)) throw new ArgumentException("percentages must be positive and total exactly 100", nameof(parts));
        var unit = (decimal)Math.Pow(10, -minorUnits);
        var units = decimal.Round(total / unit, 0);
        if (units * unit != total) throw new ArgumentException("the total has more decimals than the currency allows", nameof(total));
        var rows = parts.Select(p => { var exact = units * p.Percent / 100m; var floor = decimal.Floor(exact); return (p.CostCenterId, p.Percent, Floor: floor, Remainder: exact - floor); }).ToList();
        var leftover = (int)(units - rows.Sum(r => r.Floor));
        var bonus = rows.OrderByDescending(r => r.Remainder).ThenByDescending(r => r.Percent).ThenBy(r => r.CostCenterId).Take(leftover).Select(r => r.CostCenterId).ToHashSet();
        return rows.ToDictionary(r => r.CostCenterId, r => (r.Floor + (bonus.Contains(r.CostCenterId) ? 1m : 0m)) * unit);
    }
}

/// <summary>
/// One line as entered. Exactly one of <see cref="Debit"/> / <see cref="Credit"/> is positive. <see cref="Currency"/> defaults to the base currency. The exchange rate is NEVER
/// supplied by default (it is resolved from the effective rate); <see cref="RateOverride"/> plus a reason is the authorized manual override.
/// </summary>
public sealed record JournalLineInput(Guid? AccountId, string? Description, string? Currency, decimal? Debit, decimal? Credit, Guid? BranchId, decimal? RateOverride, string? RateOverrideReason, IReadOnlyList<JournalAllocationInput>? Allocations);

public sealed record JournalInput(DateOnly? EntryDate, string? Description, Guid? BranchId, IReadOnlyList<JournalLineInput>? Lines, Guid? AdjustsId);

public sealed record JournalAllocationView(Guid CostCenterId, decimal Amount, decimal BaseAmount);

public sealed record JournalLineView(
    Guid Id, int LineNo, Guid AccountId, string? Description, string Currency, decimal Debit, decimal Credit, decimal Rate, long? RateId, string? RateOverrideReason,
    decimal BaseDebit, decimal BaseCredit, Guid BranchId, bool IsRounding, Guid? ReversesLineId, IReadOnlyList<JournalAllocationView> Allocations);

public sealed record JournalEventView(long Id, JournalEventKind Kind, Guid Actor, DateTimeOffset AtUtc, long Revision, string? Detail);

/// <summary>A journal entry header (no lines): what lists and reports use.</summary>
public sealed record JournalEntrySummary(
    Guid Id, string? Number, JournalStatus Status, JournalType Type, DocumentSource Source, string? SourceKey, DateOnly EntryDate, string? Description, Guid BranchId, string BaseCurrency,
    decimal BaseTotal, long Revision, Guid CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid? SubmittedBy, DateTimeOffset? SubmittedAtUtc, Guid? PostedBy,
    DateTimeOffset? PostedAtUtc, Guid? PeriodId, bool PostedInSoftClosedPeriod, Guid? ReversesId, Guid? AdjustsId);

/// <summary>A full entry. <see cref="Balanced"/> is computed from the stored base amounts; <see cref="RequiredApprovals"/> from the strictest matching active policy.</summary>
public sealed record JournalEntryView(
    JournalEntrySummary Header, bool Balanced, decimal BaseDebitTotal, decimal BaseCreditTotal, int RequiredApprovals, IReadOnlyList<JournalLineView> Lines,
    IReadOnlyList<ApprovalDecisionView> Approvals, IReadOnlyList<JournalEventView> Events, IReadOnlyList<ApprovalStageProgress>? Plan = null);

public sealed record LedgerSettingsView(Guid? RoundingAccountId, long Revision, bool Exists);
public sealed record LedgerSettingsInput(Guid? RoundingAccountId);

public sealed record JournalFilter(JournalStatus? Status, DateOnly? From, DateOnly? To, Guid? BranchId, string? Text, int Limit = 100, int Offset = 0,
    IReadOnlyList<Guid>? AllowedBranches = null, IReadOnlyList<Guid>? AllowedCostCenters = null);

public sealed record ApprovalDecisionInput(bool? Approve, string? Comment);
public sealed record ReversalInput(DateOnly? EntryDate, string? Description);
public sealed record AutomaticJournalInput(string? SourceKey, JournalInput? Entry);

public static class JournalValidation
{
    public const int MaxLines = 500;
    public const int MaxAllocationsPerLine = 50;

    public static bool TryNormalizeSettings(LedgerSettingsInput? input, out List<FieldError> errors)
    {
        errors = [];
        if (input is null) errors.Add(new("body", "required"));
        return errors.Count == 0;
    }

    public static string? CleanText(string? v, int max, string field, List<FieldError> errors)
    {
        var t = v?.Trim().Normalize(System.Text.NormalizationForm.FormC);
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Length > max) { errors.Add(new(field, "too_long")); return null; }
        return t;
    }

    public static string? NormalizeSourceKey(string? raw)
    {
        var t = raw?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 100 || t.Any(char.IsControl)) return null;
        return t;
    }
}
