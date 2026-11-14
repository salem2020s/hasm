using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>
/// Structural validation of a chart template against the rules the Chart of Accounts enforces, so that a template can be checked BEFORE it is ever applied (and before it is approved for production). Pure function: it
/// reads nothing and changes nothing. Returns human-readable problems; an empty list means the template is structurally sound. It does not judge accounting content (that is the owner's review).
/// </summary>
public static partial class ChartTemplateValidator
{
    /// <summary>Words that would hardwire one jurisdiction's tax regime into a template (taxes are configurable, never assumed).</summary>
    private static readonly string[] JurisdictionTerms = ["VAT", "GST", "ZAKAT", "WITHHOLDING", "ضريبة القيمة المضافة", "زكاة", "الزكاة", "ضريبة الاستقطاع"];

    [GeneratedRegex(@"\p{IsArabic}")] private static partial Regex ArabicLetter();

    public static IReadOnlyList<string> Validate(ChartTemplate template)
    {
        var problems = new List<string>();
        void P(string code, string message) => problems.Add($"{code}: {message}");
        if (string.IsNullOrWhiteSpace(template.Key) || template.Key != template.Key.Trim().ToLowerInvariant()) problems.Add("template key must be a trimmed lower-case identifier");
        if (string.IsNullOrWhiteSpace(template.NameAr) || string.IsNullOrWhiteSpace(template.NameEn)) problems.Add("template needs an Arabic and an English name");
        var byCode = new Dictionary<string, TemplateAccount>(StringComparer.Ordinal);
        foreach (var a in template.Accounts)
        {
            var normalized = ConfigurationValidation.NormalizeAccountCode(a.Code);
            if (normalized is null || normalized != a.Code) { P(a.Code, "code is not a valid normalized account code"); continue; }
            if (!byCode.TryAdd(a.Code, a)) P(a.Code, "duplicate code");
        }
        var children = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in byCode.Values)
        {
            if (string.IsNullOrWhiteSpace(a.NameAr) || !ArabicLetter().IsMatch(a.NameAr)) P(a.Code, "Arabic name missing or without Arabic letters");
            if (string.IsNullOrWhiteSpace(a.NameEn) || ArabicLetter().IsMatch(a.NameEn)) P(a.Code, "English name missing or containing Arabic letters");
            foreach (var term in JurisdictionTerms)
                if (a.NameEn.Contains(term, StringComparison.OrdinalIgnoreCase) || a.NameAr.Contains(term, StringComparison.Ordinal)) P(a.Code, $"jurisdiction-specific term '{term}' must not be hardcoded in a template");
            if (!Enum.IsDefined(a.Type)) P(a.Code, "unknown account type");
            if (a.Nature is not (AccountNature.Debit or AccountNature.Credit)) P(a.Code, "unknown nature");
            // a group summarises postable accounts and never carries a contra nature: only a postable account may be a contra account
            if (a.IsGroup && a.Nature != ConfigurationValidation.DefaultNature(a.Type)) P(a.Code, "a group account cannot be a contra account");
            if (a.ParentCode is null)
            {
                if (!a.IsGroup) P(a.Code, "a root account must be a group");
                continue;
            }
            if (!byCode.TryGetValue(a.ParentCode, out var parent)) { P(a.Code, $"parent {a.ParentCode} does not exist"); continue; }
            if (!parent.IsGroup) P(a.Code, $"parent {a.ParentCode} is not a group account");
            if (parent.Type != a.Type) P(a.Code, $"type {a.Type} differs from the parent's type {parent.Type}");
            children[a.ParentCode] = children.GetValueOrDefault(a.ParentCode) + 1;
        }
        foreach (var a in byCode.Values)
        {
            if (!a.IsGroup && children.ContainsKey(a.Code)) P(a.Code, "a postable account cannot have children");
            if (a.IsGroup && a.ParentCode is not null && !children.ContainsKey(a.Code)) P(a.Code, "a group account must have at least one child");
            // cycles: following the parents must reach a root without revisiting an account
            var seen = new HashSet<string>(StringComparer.Ordinal) { a.Code };
            for (var cur = a; cur.ParentCode is { } pc && byCode.TryGetValue(pc, out var next); cur = next)
                if (!seen.Add(next.Code)) { P(a.Code, "hierarchy cycle"); break; }
        }
        return problems;
    }
}
