namespace Hasm.Infrastructure.Ledger;

/// <summary>The company-chosen equity account that receives the annual profit or loss, and whether year-end entries always need an independent approval.</summary>
public sealed record ClosingSettingsView(Guid? EquityAccountId, string? EquityCode, string? EquityNameAr, string? EquityNameEn, bool RequireApproval, long Revision, bool Configured);
public sealed record ClosingSettingsInput(Guid? EquityAccountId, bool? RequireApproval);

/// <summary>One validation of the closing: <c>Status</c> is passed, blocked, or not_applicable / unimplemented (a check whose module does not exist is NEVER reported as passed).</summary>
public sealed record ClosingCheck(string Code, string Status, string? Detail = null, string Phase = "both");

public sealed record YearEndLine(Guid AccountId, Guid BranchId, decimal Debit, decimal Credit);

/// <summary>
/// The dry run of a year-end closing and of the carryforward: the exact entries that would be generated, the validations and a reproducible evidence hash (SHA-256 of the canonical
/// line lists: the same ledger always gives the same hash). Nothing is written.
/// </summary>
public sealed record ClosingPreview(
    Guid YearId, string YearName, DateOnly YearStart, DateOnly YearEnd, string BaseCurrency, Guid? EquityAccountId, decimal NetResult,
    IReadOnlyList<YearEndLine> ClosingLines, IReadOnlyList<YearEndLine> OpeningLines, Guid? NextYearId, DateOnly? NextYearStart,
    IReadOnlyList<ClosingCheck> Checks, bool CanClose, bool CanCarryForward, int NextVersion, string EvidenceHash, Guid? ActiveClosingId);

/// <summary>A closing of a year (one version). <c>State</c>: prepared (entry not posted), closed (posted), carried_forward (opening balances posted), reversed, cancelled. <c>Stale</c>: the ledger changed after it was posted.</summary>
public sealed record FiscalYearClosingView(
    Guid Id, Guid YearId, int Version, decimal NetResult, Guid EquityAccountId, Guid ClosingEntryId, JournalStatus ClosingStatus, string? ClosingNumber,
    Guid? CarryforwardId, Guid? OpeningEntryId, JournalStatus? OpeningStatus, string? OpeningNumber, Guid? ToYearId, string State, bool Stale, string EvidenceHash, DateTimeOffset CreatedAtUtc, Guid CreatedBy);

/// <summary>Opening balances carried forward from a year that had no profit-and-loss activity (no closing needed).</summary>
public sealed record FiscalYearCarryforwardView(Guid Id, Guid FromYearId, Guid? ClosingId, Guid ToYearId, Guid EntryId, JournalStatus EntryStatus, string? EntryNumber, bool Stale, string EvidenceHash, DateTimeOffset CreatedAtUtc, Guid CreatedBy);

public sealed record YearEndStatus(Guid YearId, string YearName, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<FiscalYearClosingView> Closings, IReadOnlyList<FiscalYearCarryforwardView> Carryforwards);

public sealed record YearEndReversalInput(DateOnly? EntryDate);
