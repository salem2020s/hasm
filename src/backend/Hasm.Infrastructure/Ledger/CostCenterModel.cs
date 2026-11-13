using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// A cost center. A <b>GROUP</b> only organizes the tree and never receives allocations (so hierarchical totals, which roll leaves up the tree, never double count);
/// a <b>LEAF</b> (not a group) that is ACTIVE can receive allocations (<see cref="CanReceiveAllocations"/>). The optional <see cref="BranchId"/> is an informational link
/// to an organizational branch: cost centers and branches are separate concepts and nothing forces them to match.
/// </summary>
public sealed record CostCenterView(
    Guid Id, string Code, string? NameAr, string? NameEn, Guid? ParentId, Guid? BranchId, bool IsGroup, bool IsActive,
    long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool HasChildren)
{
    public bool CanReceiveAllocations => IsActive && !IsGroup;
}

public sealed record CostCenterInput(string? Code, Guid? ParentId, string? NameAr, string? NameEn, Guid? BranchId, bool? IsGroup);
public sealed record CostCenterDetailsInput(string? NameAr, string? NameEn, Guid? BranchId, bool? IsGroup);
public sealed record CostCenterCodeInput(string? Code);
public sealed record CostCenterMoveInput(Guid? ParentId);

public static partial class CostCenterValidation
{
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._-]{0,29}$")] private static partial Regex CodeShape();

    private static string? Clean(string? v)
    {
        var t = v?.Trim().Normalize(NormalizationForm.FormC);
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>Same code convention as the chart of accounts: Arabic-Indic digits mapped to Latin, upper-cased, letters/digits/dot/hyphen/underscore, 1-30 characters.</summary>
    public static string? NormalizeCode(string? raw)
    {
        var v = Clean(raw);
        if (v is null) return null;
        var sb = new StringBuilder(v.Length);
        foreach (var ch in v)
            sb.Append(ch is >= '٠' and <= '٩' ? (char)('0' + (ch - '٠')) : ch is >= '۰' and <= '۹' ? (char)('0' + (ch - '۰')) : char.ToUpperInvariant(ch));
        var code = sb.ToString();
        return CodeShape().IsMatch(code) ? code : null;
    }

    private static void Names(string? ar, string? en, out string? nameAr, out string? nameEn, List<FieldError> errors)
    {
        nameAr = Clean(ar); nameEn = Clean(en);
        if (nameAr is { Length: > 200 }) { errors.Add(new("nameAr", "too_long")); nameAr = null; }
        if (nameEn is { Length: > 200 }) { errors.Add(new("nameEn", "too_long")); nameEn = null; }
        if (nameAr is null && nameEn is null && errors.All(e => e.Field is not ("nameAr" or "nameEn"))) { errors.Add(new("nameAr", "name_required")); errors.Add(new("nameEn", "name_required")); }
    }

    public static bool TryNormalize(CostCenterInput? input, out string code, out string? nameAr, out string? nameEn, out bool isGroup, out List<FieldError> errors)
    {
        errors = [];
        code = ""; nameAr = nameEn = null; isGroup = false;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        if (Clean(input.Code) is null) errors.Add(new("code", "required"));
        else if (NormalizeCode(input.Code) is { } c) code = c;
        else errors.Add(new("code", "invalid_format"));
        Names(input.NameAr, input.NameEn, out nameAr, out nameEn, errors);
        isGroup = input.IsGroup ?? false;
        return errors.Count == 0;
    }

    public static bool TryNormalizeDetails(CostCenterDetailsInput? input, out string? nameAr, out string? nameEn, out List<FieldError> errors)
    {
        errors = [];
        nameAr = nameEn = null;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        Names(input.NameAr, input.NameEn, out nameAr, out nameEn, errors);
        return errors.Count == 0;
    }
}
