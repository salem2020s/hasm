namespace Hasm.Infrastructure.Cash;

// Phase 3.14 slice S2a (docs/PHASE-3.14.md; owner decisions D-211, D-214): cash vouchers WITHOUT any financial posting. A voucher here is a draft, a submitted draft or a cancelled one. Nothing in this slice approves a
// voucher, gives it an official number, creates a journal entry or changes a balance: financial posting for cash and bank stays closed until the reviews D-214 requires are recorded.

/// <summary>Business audit events of cash vouchers (company stream, inside the block 2100-2199 of Phase 3.14; 2100-2107 are the cash account master). Persisted codes: never renumber.
/// 2113 is deliberately UNUSED (no withdrawal exists: the approved state machine has no submitted -> draft transition); it is left free, not reused for another event. 2115-2129 are left to the later voucher slice.</summary>
public static class CashVoucherAuditEvents
{
    public const short Created = 2110;
    public const short Updated = 2111;
    public const short Submitted = 2112;
    public const short Cancelled = 2114;
}

/// <summary>A receipt (money comes into the cash or bank account) or a payment (money leaves it). Stored as a smallint.</summary>
public enum CashVoucherKind : short { Receipt = 1, Payment = 2 }

/// <summary>The states this slice implements. 3 (approved) and 4 (rejected) are reserved by the plan for the slice that adds approval (S2b); PostgreSQL refuses them here and no code of this slice reaches them.</summary>
public enum CashVoucherStatus : short { Draft = 1, Submitted = 2, Cancelled = 5 }

/// <summary>1 created, 2 updated, 3 submitted, 5 cancelled. 4 is not used (there is no withdrawal) and PostgreSQL refuses it.</summary>
public enum CashVoucherEventKind : short { Created = 1, Updated = 2, Submitted = 3, Cancelled = 5 }

/// <summary>A counter line: the explicitly chosen counter account (D-211 Q3) and a positive amount in the voucher currency. Line numbers are assigned by position; the line's branch is always the voucher's (Q11), so it is not an input.</summary>
public sealed record CashVoucherLineInput(Guid? AccountId, decimal? Amount, string? Description);

/// <summary>
/// Create and update body. On create <see cref="Kind"/> ("receipt" or "payment"), <see cref="CashAccountId"/> and <see cref="IdempotencyKey"/> are required. On update the kind, the cash account and the key never change (null or
/// equal); the date, party, reference, note and the whole line set are replaced (draft only). The branch and the currency are the cash account's and are never an input.
/// </summary>
public sealed record CashVoucherInput(string? Kind, Guid? CashAccountId, DateOnly? VoucherDate, Guid? PartyId, string? Reference, string? Note, string? IdempotencyKey, IReadOnlyList<CashVoucherLineInput>? Lines);

public sealed record CashVoucherLineView(int LineNo, Guid AccountId, decimal Amount, string? Description);

public sealed record CashVoucherEventView(CashVoucherEventKind Kind, CashVoucherStatus? FromStatus, CashVoucherStatus ToStatus, long Revision, Guid ActorUserId, string? Reason, DateTimeOffset AtUtc);

/// <summary>A voucher as read. <see cref="Total"/> is the sum of the current lines (derived, never stored). There is no official number, no approver and no accounting state in this slice.</summary>
public sealed record CashVoucherView(Guid Id, CashVoucherKind Kind, Guid CashAccountId, Guid BranchId, string Currency, DateOnly VoucherDate, Guid? PartyId, string? Reference, string? Note, CashVoucherStatus Status,
    Guid? SubmittedBy, Guid UpdatedBy, long Revision, Guid CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, decimal Total, IReadOnlyList<CashVoucherLineView> Lines, IReadOnlyList<CashVoucherEventView> Events, bool Replayed = false);

public sealed record CashVoucherFilter(CashVoucherStatus? Status = null, CashVoucherKind? Kind = null, Guid? CashAccountId = null, Guid? BranchId = null, IReadOnlyList<Guid>? AllowedBranches = null, int Limit = 50, int Offset = 0);

public static class CashVoucherRules
{
    /// <summary>One line less than a journal entry admits (<c>JournalValidation.MaxLines</c>): the later slice turns a voucher into ONE entry with the cash line and these counter lines, so a voucher that could never be posted is not accepted.</summary>
    public const int MaxLines = 499;
    /// <summary>The largest amount a journal line admits; a single line and the voucher total (the cash line of the later entry) both stay within it.</summary>
    public const decimal MaxAmount = 999_999_999_999_999m;
    public const int MaxReferenceLength = 100, MaxTextLength = 500;

    public static CashVoucherKind? ParseKind(string? raw) => raw?.Trim().ToLowerInvariant() switch { "receipt" => CashVoucherKind.Receipt, "payment" => CashVoucherKind.Payment, _ => null };
    public static string KindName(CashVoucherKind kind) => kind == CashVoucherKind.Payment ? "payment" : "receipt";

    /// <summary>Trimmed text, null when empty; false when it is too long or carries a control character.</summary>
    public static bool TryText(string? raw, int max, out string? text)
    {
        text = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        return text is null || (text.Length <= max && !text.Any(char.IsControl));
    }

    /// <summary>The amount rule of a line: positive, within the journal's range and with no more decimals than the base currency has minor units. Null when the amount is acceptable.</summary>
    public static string? AmountError(decimal? amount, int minorUnits) =>
        amount is null ? "required" : amount <= 0 || amount > MaxAmount ? "out_of_range" : amount != decimal.Round(amount.Value, minorUnits) ? "too_many_decimals" : null;
}
