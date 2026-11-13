using System.Text.RegularExpressions;
using Npgsql;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>Account types. Persisted and constrained by PostgreSQL; never renumber. Each type has a fixed statement: 1-3 Balance Sheet, 4-5 Profit and Loss.</summary>
public enum AccountType : short { Asset = 1, Liability = 2, Equity = 3, Revenue = 4, Expense = 5 }

/// <summary>The normal balance side. Stored per account (not derived) so that contra accounts are possible.</summary>
public enum AccountNature : short { Debit = 1, Credit = 2 }

public enum AccountStatement : short { BalanceSheet = 1, ProfitAndLoss = 2 }

/// <summary>Which currencies a future posting to the account may use: the base currency only, any enabled currency, or one specific currency.</summary>
public enum AccountCurrencyPolicy : short { Base = 1, Any = 2, Specific = 3 }

/// <summary>Raw client input for a new account. The code may be absent (assigned automatically); the type may be absent when a parent is given (inherited).</summary>
public sealed record AccountInput(
    string? Code, Guid? ParentId, string? NameAr, string? NameEn, string? Type, string? Nature, bool? IsGroup, string? CurrencyPolicy, string? CurrencyCode);

/// <summary>The editable attributes of an existing account. The type and the identity never change; code and parent have their own controlled operations.</summary>
public sealed record AccountDetailsInput(string? NameAr, string? NameEn, string? Nature, bool? IsGroup, string? CurrencyPolicy, string? CurrencyCode);

public sealed record AccountCodeInput(string? Code);
public sealed record AccountMoveInput(Guid? ParentId);

/// <summary>
/// An account as stored. A <b>postable</b> account is an ACTIVE account that is not a group (a leaf by construction: PostgreSQL gives a non-group no
/// children and every parent is a group). <see cref="Id"/> is the stable identifier everything refers to; <see cref="Code"/> may be changed (audited).
/// </summary>
public sealed record AccountView(
    Guid Id, string Code, string? NameAr, string? NameEn, Guid? ParentId, AccountType Type, AccountNature Nature, AccountStatement Statement, bool IsGroup, bool IsActive,
    AccountCurrencyPolicy CurrencyPolicy, string? CurrencyCode, string? TemplateKey, long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool HasChildren)
{
    public bool IsPostable => IsActive && !IsGroup;
}

/// <summary>Automatic code assignment settings. <c>Exists</c> false = defaults shown, nothing stored yet (a read never creates the row).</summary>
public sealed record ChartSettingsView(int SegmentWidth, bool AutoCodes, long Revision, bool Exists)
{
    public const int DefaultSegmentWidth = 2;
    public static ChartSettingsView Defaults { get; } = new(DefaultSegmentWidth, true, 0, false);
}

public sealed record ChartSettingsInput(int? SegmentWidth, bool? AutoCodes);

/// <summary>
/// A module that can hold postings to accounts (the General Ledger, Phase 3.2B). It reports whether an account has posting history, which freezes its
/// type-like attributes (nature, group flag, currency policy). The module does not exist yet, so only a placeholder is registered: it never claims
/// history and never claims to be implemented, and the API reports <c>UsageChecksImplemented = false</c>.
/// </summary>
public interface IAccountUsageProbe
{
    string Domain { get; }
    bool Implemented { get; }
    Task<bool> HasPostingsAsync(Guid companyId, Guid accountId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);
}

public sealed class UnimplementedAccountUsageProbe(string domain) : IAccountUsageProbe
{
    public string Domain => domain;
    public bool Implemented => false;
    public Task<bool> HasPostingsAsync(Guid companyId, Guid accountId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Evaluates every REQUIRED domain; a registered real probe replaces the placeholder for its domain.</summary>
public sealed class AccountUsageGuard
{
    public static readonly string[] RequiredDomains = ["general_ledger_postings"];

    private readonly Dictionary<string, IAccountUsageProbe> probes;

    public AccountUsageGuard(IEnumerable<IAccountUsageProbe>? registered = null)
    {
        probes = RequiredDomains.ToDictionary(d => d, d => (IAccountUsageProbe)new UnimplementedAccountUsageProbe(d));
        foreach (var p in registered ?? []) probes[p.Domain] = p;
    }

    public IReadOnlyList<string> UnimplementedDomains => [.. probes.Values.Where(p => !p.Implemented).Select(p => p.Domain).Order(StringComparer.Ordinal)];

    public async Task<bool> HasPostingsAsync(Guid companyId, Guid accountId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        foreach (var probe in probes.Values)
            if (await probe.HasPostingsAsync(companyId, accountId, connection, transaction, ct)) return true;
        return false;
    }
}

/// <summary>One account of a code-defined chart template. Parents are referenced by code; the codes are the template's own and become the initial codes.</summary>
public sealed record TemplateAccount(string Code, string? ParentCode, string NameAr, string NameEn, AccountType Type, AccountNature Nature, bool IsGroup);

public sealed record ChartTemplate(string Key, string NameAr, string NameEn, IReadOnlyList<TemplateAccount> Accounts);

public static partial class ConfigurationValidation
{
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._-]{0,29}$")] private static partial Regex AccountCodeShape();

    /// <summary>
    /// Normalises an account code consistently: trims, maps Arabic-Indic and Extended Arabic-Indic digits to Latin digits, upper-cases (invariant) and
    /// checks the shape (letters, digits, dot, hyphen, underscore; 1-30 characters; starts with a letter or digit). Null when invalid.
    /// </summary>
    public static string? NormalizeAccountCode(string? raw)
    {
        var v = Clean(raw);
        if (v is null) return null;
        var sb = new System.Text.StringBuilder(v.Length);
        foreach (var ch in v)
            sb.Append(ch is >= '٠' and <= '٩' ? (char)('0' + (ch - '٠')) : ch is >= '۰' and <= '۹' ? (char)('0' + (ch - '۰')) : char.ToUpperInvariant(ch));
        var code = sb.ToString();
        return AccountCodeShape().IsMatch(code) ? code : null;
    }

    public static bool TryParseAccountType(string? raw, out AccountType type)
    {
        type = default;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "asset": type = AccountType.Asset; return true;
            case "liability": type = AccountType.Liability; return true;
            case "equity": type = AccountType.Equity; return true;
            case "revenue": type = AccountType.Revenue; return true;
            case "expense": type = AccountType.Expense; return true;
            default: return false;
        }
    }

    public static AccountNature DefaultNature(AccountType type) => type is AccountType.Asset or AccountType.Expense ? AccountNature.Debit : AccountNature.Credit;

    private static string? NameText(string? raw, string field, List<FieldError> errors)
    {
        var v = Clean(raw);
        if (v is { Length: > 200 }) { errors.Add(new(field, "too_long")); return null; }
        return v;
    }

    private static void ValidateNames(string? ar, string? en, out string? nameAr, out string? nameEn, List<FieldError> errors)
    {
        nameAr = NameText(ar, "nameAr", errors);
        nameEn = NameText(en, "nameEn", errors);
        if (nameAr is null && nameEn is null && errors.All(e => e.Field != "nameAr" && e.Field != "nameEn"))
        { errors.Add(new("nameAr", "name_required")); errors.Add(new("nameEn", "name_required")); }
    }

    private static void ValidateCurrency(string? policy, string? code, out AccountCurrencyPolicy pol, out string? currency, List<FieldError> errors)
    {
        pol = AccountCurrencyPolicy.Base;
        currency = null;
        switch (policy?.Trim().ToLowerInvariant())
        {
            case null or "" or "base": break;
            case "any": pol = AccountCurrencyPolicy.Any; break;
            case "specific": pol = AccountCurrencyPolicy.Specific; break;
            default: errors.Add(new("currencyPolicy", "invalid_value")); return;
        }
        var c = Clean(code)?.ToUpperInvariant();
        if (pol == AccountCurrencyPolicy.Specific) { if (c is null || !Regex.IsMatch(c, "^[A-Z]{3}$")) errors.Add(new("currencyCode", c is null ? "required" : "invalid_format")); else currency = c; }
        else if (c is not null) errors.Add(new("currencyCode", "not_allowed"));
    }

    /// <summary>Validates a new account (everything except what needs the stored chart: the parent, the inherited type, automatic codes, enabled currencies).</summary>
    public static bool TryNormalizeAccount(AccountInput? input, out string? code, out string? nameAr, out string? nameEn, out AccountType? type, out AccountNature? nature,
        out bool isGroup, out AccountCurrencyPolicy policy, out string? currencyCode, out List<FieldError> errors)
    {
        errors = [];
        code = nameAr = nameEn = currencyCode = null; type = null; nature = null; isGroup = false; policy = AccountCurrencyPolicy.Base;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        if (Clean(input.Code) is not null)
        {
            code = NormalizeAccountCode(input.Code);
            if (code is null) errors.Add(new("code", "invalid_format"));
        }
        ValidateNames(input.NameAr, input.NameEn, out nameAr, out nameEn, errors);
        if (Clean(input.Type) is not null)
        {
            if (TryParseAccountType(input.Type, out var t)) type = t; else errors.Add(new("type", "invalid_value"));
        }
        if (Clean(input.Nature) is not null)
        {
            switch (input.Nature!.Trim().ToLowerInvariant())
            {
                case "debit": nature = AccountNature.Debit; break;
                case "credit": nature = AccountNature.Credit; break;
                default: errors.Add(new("nature", "invalid_value")); break;
            }
        }
        isGroup = input.IsGroup ?? false;
        ValidateCurrency(input.CurrencyPolicy, input.CurrencyCode, out policy, out currencyCode, errors);
        return errors.Count == 0;
    }

    public static bool TryNormalizeAccountDetails(AccountDetailsInput? input, out string? nameAr, out string? nameEn, out AccountNature? nature, out bool? isGroup,
        out AccountCurrencyPolicy policy, out string? currencyCode, out List<FieldError> errors)
    {
        errors = [];
        nameAr = nameEn = currencyCode = null; nature = null; isGroup = null; policy = AccountCurrencyPolicy.Base;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        ValidateNames(input.NameAr, input.NameEn, out nameAr, out nameEn, errors);
        if (Clean(input.Nature) is not null)
        {
            switch (input.Nature!.Trim().ToLowerInvariant())
            {
                case "debit": nature = AccountNature.Debit; break;
                case "credit": nature = AccountNature.Credit; break;
                default: errors.Add(new("nature", "invalid_value")); break;
            }
        }
        isGroup = input.IsGroup;
        ValidateCurrency(input.CurrencyPolicy, input.CurrencyCode, out policy, out currencyCode, errors);
        return errors.Count == 0;
    }

    public static bool TryNormalizeChartSettings(ChartSettingsInput? input, out int width, out bool auto, out List<FieldError> errors)
    {
        errors = [];
        width = ChartSettingsView.DefaultSegmentWidth; auto = true;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        if (input.SegmentWidth is null) errors.Add(new("segmentWidth", "required"));
        else if (input.SegmentWidth is < 1 or > 4) errors.Add(new("segmentWidth", "out_of_range"));
        else width = input.SegmentWidth.Value;
        if (input.AutoCodes is null) errors.Add(new("autoCodes", "required")); else auto = input.AutoCodes.Value;
        return errors.Count == 0;
    }
}
