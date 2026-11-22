using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Cash;

// Phase 3.14 slice S1 (docs/PHASE-3.14.md; owner decisions D-155, D-211): the cash and bank ACCOUNT MASTER. Nothing here touches the ledger: no journal entry, no numbering, no balance.

/// <summary>Business audit events of cash and bank accounts (company stream, the block 2100-2199 allocated by the lead for Phase 3.14). Persisted codes: never renumber. Details carry ids, field names, a hash and MASKED bank values; never a full account number.</summary>
public static class CashAuditEvents
{
    public const short AccountCreated = 2100;
    public const short AccountUpdated = 2101;
    public const short AccountDeactivated = 2102;
    public const short AccountReactivated = 2103;
    /// <summary>Any change of the bank name, the bank branch name or the account number (with the masked old and new number; the administrators' notification route of D-102 is this event).</summary>
    public const short BankDetailsChanged = 2104;
    /// <summary>The FULL account number was shown to a holder of the sensitive permission (every reveal is one event).</summary>
    public const short AccountNumberRevealed = 2105;
    /// <summary>The encrypted account number of one account was re-wrapped under the current master key (maintenance; the id and the key ids only).</summary>
    public const short NumberRewrapped = 2106;
    /// <summary>A re-wrap of one account's number failed and nothing was changed (the id and a reason code only).</summary>
    public const short NumberRewrapFailed = 2107;
}

/// <summary>The outcome of the number re-wrap: how many were moved to the current key, how many already were under it (or hold no number), and how many failed (key missing, authentication failure) and were left untouched.</summary>
public sealed record CashRewrapOutcome(int Rewrapped, int Skipped, int Failed);

/// <summary>A cashbox (physical cash, never negative) or a bank account. Stored as a smallint.</summary>
public enum CashAccountKind : short { Cashbox = 1, Bank = 2 }

/// <summary>Raw client input. <see cref="AccountNumber"/> is the FULL number: null or the masked form means "unchanged" on an update; a new number needs the full value.</summary>
public sealed record CashAccountInput(string? Kind, string? Code, string? NameAr, string? NameEn, Guid? BranchId, Guid? AccountId, string? BankName, string? BankBranchName, string? AccountNumber);

/// <summary>The normalized values of a cash or bank account (the full number, when one is being set, is carried separately and never stored in clear).</summary>
public sealed record CashAccountData(CashAccountKind Kind, string Code, string? NameAr, string? NameEn, Guid BranchId, Guid AccountId, string? BankName, string? BankBranchName);

/// <summary>What a reader sees: the number only in its masked form (<see cref="MaskedNumber"/>).</summary>
public sealed record CashAccountView(Guid Id, CashAccountData Data, string Currency, string? MaskedNumber, bool IsActive, long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, GlAccountRef Account);

/// <summary>The general-ledger account a cash or bank account is bound to, for display (so a cash administrator does not need the chart permission to see which account it is).</summary>
public sealed record GlAccountRef(Guid Id, string Code, string? NameAr, string? NameEn);

/// <summary>A general-ledger account that could be bound now: an active, non-group asset account that admits the base currency and serves no cash or bank account yet.</summary>
public sealed record GlCandidate(Guid Id, string Code, string? NameAr, string? NameEn);

/// <summary>Whether anything refers to the account (S2 and S3 implement this; before them nothing can).</summary>
public sealed record CashAccountUsage(bool Referenced, bool HasOpenDocuments);

public static partial class CashAccountRules
{
    public const int MaxNameLength = 200, MinNumberLength = 4, MaxNumberLength = 34;
    private const string MaskPrefix = "****";

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,29}$")] private static partial Regex CodeShape();
    [GeneratedRegex("^[A-Z0-9]{4,34}$")] private static partial Regex NumberShape();

    /// <summary>The masked form shown in lists, reports and exports: four asterisks and the tail the masking rule allows (see <see cref="Tail"/>).</summary>
    public static string Mask(string tail) => MaskPrefix + tail;

    /// <summary>A number never legitimately contains these characters, so anything that looks masked is never stored as a number.</summary>
    public static bool LooksMasked(string? value) => value is not null && (value.Contains('*') || value.Contains('•') || value.Contains('·'));

    /// <summary>Spaces and hyphens are separators only; letters are upper-cased. Null when the value is not a plausible account number (4 to 34 letters or digits).</summary>
    public static string? NormalizeNumber(string? raw)
    {
        if (raw is null) return null;
        var stripped = new string(raw.Where(c => c is not (' ' or '-')).ToArray());
        if (stripped.Any(c => c > 127)) return null;                    // ASCII BEFORE upper-casing: the invariant upper-case of some non-ASCII letters (the dotless i, the long s) is an ASCII letter and would otherwise slip in (review L4 of PR 206)
        var n = stripped.ToUpperInvariant();
        return NumberShape().IsMatch(n) ? n : null;
    }

    /// <summary>
    /// The masking rule (a TECHNICAL rule, not a business rule: it changes which accepted numbers are exposed, never which numbers are accepted): the mask reveals no more than a fixed safe share of the number. From 12 characters the last four,
    /// from 8 to 11 the last two, below 8 nothing. Only this tail is stored next to the encrypted number and it is the only part any reader, audit event, report or export may ever show.
    /// </summary>
    public static string Tail(string normalizedNumber) => normalizedNumber.Length >= 12 ? normalizedNumber[^4..] : normalizedNumber.Length >= 8 ? normalizedNumber[^2..] : "";

    private static string? Trim(string? s) { var t = s?.Trim(); return string.IsNullOrEmpty(t) ? null : t; }

    /// <summary>
    /// Validates and normalizes the input. <paramref name="existing"/> is null on a create. On an update the kind and the code are immutable (a different value is refused). <paramref name="newNumber"/> is the normalized full number
    /// when one is to be set, null when the number is unchanged (omitted or the masked form of <paramref name="currentMasked"/>).
    /// </summary>
    public static bool TryNormalize(CashAccountInput? input, CashAccountData? existing, string? currentMasked, out CashAccountData data, out string? newNumber, out List<FieldError> errors)
    {
        errors = [];
        newNumber = null;
        data = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }

        CashAccountKind kind = default;
        var kindText = Trim(input.Kind)?.ToLowerInvariant();
        if (kindText is null) { if (existing is null) errors.Add(new("kind", "required")); else kind = existing.Kind; }
        else if (kindText == "cashbox") kind = CashAccountKind.Cashbox;
        else if (kindText == "bank") kind = CashAccountKind.Bank;
        else errors.Add(new("kind", "invalid"));
        if (existing is not null && kindText is not null && errors.All(e => e.Field != "kind") && kind != existing.Kind) errors.Add(new("kind", "immutable"));

        var rawCode = Trim(input.Code);
        var code = rawCode is not null && rawCode.Any(c => c > 127) ? rawCode : rawCode?.ToUpperInvariant();   // a non-ASCII code is left as typed, so the shape check refuses it (same reason as the number)
        if (existing is null) { if (code is null) errors.Add(new("code", "required")); else if (!CodeShape().IsMatch(code)) errors.Add(new("code", "invalid")); }
        else if (code is not null && code != existing.Code) errors.Add(new("code", "immutable"));
        code ??= existing?.Code;

        var ar = Trim(input.NameAr); var en = Trim(input.NameEn);
        if (ar is null && en is null) errors.Add(new("nameAr", "required"));
        if (ar is { Length: > MaxNameLength }) errors.Add(new("nameAr", "too_long"));
        if (en is { Length: > MaxNameLength }) errors.Add(new("nameEn", "too_long"));
        if (input.BranchId is null || input.BranchId == Guid.Empty) errors.Add(new("branchId", "required"));
        if (input.AccountId is null || input.AccountId == Guid.Empty) errors.Add(new("accountId", "required"));

        var bankName = Trim(input.BankName); var bankBranch = Trim(input.BankBranchName); var number = Trim(input.AccountNumber);
        if (kind == CashAccountKind.Cashbox && (bankName is not null || bankBranch is not null || number is not null))
            errors.Add(new(bankName is not null ? "bankName" : bankBranch is not null ? "bankBranchName" : "accountNumber", "not_allowed_for_cashbox"));
        if (bankName is { Length: > MaxNameLength }) errors.Add(new("bankName", "too_long"));
        if (bankBranch is { Length: > MaxNameLength }) errors.Add(new("bankBranchName", "too_long"));
        if (kind == CashAccountKind.Bank && number is not null)
        {
            if (currentMasked is not null && number == currentMasked) { /* the masked form sent back: unchanged, never stored */ }
            else if (LooksMasked(number)) errors.Add(new("accountNumber", "masked_value"));
            else if (NormalizeNumber(number) is { } n) newNumber = n;
            else errors.Add(new("accountNumber", "invalid"));
        }
        if (errors.Count > 0) return false;
        data = new CashAccountData(kind, code!, ar, en, input.BranchId!.Value, input.AccountId!.Value, bankName, bankBranch);
        return true;
    }
}
