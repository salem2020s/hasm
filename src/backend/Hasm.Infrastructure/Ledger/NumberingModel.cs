using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>Document types that receive official numbers. A registry (not free text) so a rule can only exist for a document the system knows.</summary>
public static class DocumentTypes
{
    public const string JournalEntry = "journal_entry";
    public const string StockReceipt = "stock_receipt";
    public const string StockIssue = "stock_issue";
    public const string StockTransfer = "stock_transfer";

    public static IReadOnlyList<string> All { get; } = [JournalEntry, StockReceipt, StockIssue, StockTransfer];

    /// <summary>Only journal entries run through the ordered approval-policy engine; stock documents use the independent-approver rule of their own service.</summary>
    public static bool UsesApprovalPolicies(string? docType) => docType == JournalEntry;

    public static string DefaultPrefix(string docType) => docType switch { JournalEntry => "JV", StockReceipt => "SR", StockIssue => "SI", StockTransfer => "ST", _ => "" };

    public static bool IsKnown(string? docType) => docType is not null && All.Contains(docType, StringComparer.Ordinal);
}

/// <summary>A numbering rule. <c>Exists</c> false = documented defaults shown, nothing stored yet (a read never creates the row).</summary>
public sealed record NumberingRuleView(string DocType, string Prefix, string Separator, int Width, bool PerBranch, bool PerFiscalYear, long Revision, bool Exists, bool NumbersIssued)
{
    public static NumberingRuleView Default(string docType) => new(docType, DocumentTypes.DefaultPrefix(docType), "-", 6, false, true, 0, false, false);
}

public sealed record NumberingRuleInput(string? Prefix, string? Separator, int? Width, bool? PerBranch, bool? PerFiscalYear);

/// <summary>Scope of one issuance. Labels are what appears inside the number; ids select the counter.</summary>
public sealed record NumberScope(Guid? BranchId, string? BranchCode, Guid? FiscalYearId, string? FiscalYearLabel);

public sealed record IssuedNumber(string Number, long Value, Guid BranchId, Guid FiscalYearId);

public static partial class NumberingValidation
{
    [GeneratedRegex("^[A-Za-z0-9._-]{0,20}$")] private static partial Regex PrefixShape();
    [GeneratedRegex("^[A-Za-z0-9._/-]{0,3}$")] private static partial Regex SeparatorShape();

    public static bool TryNormalize(NumberingRuleInput? input, out string prefix, out string separator, out int width, out bool perBranch, out bool perYear, out List<FieldError> errors)
    {
        errors = [];
        prefix = separator = ""; width = 6; perBranch = perYear = false;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        prefix = input.Prefix?.Trim() ?? "";
        separator = input.Separator?.Trim() ?? "";
        if (!PrefixShape().IsMatch(prefix)) errors.Add(new("prefix", "invalid_format"));
        if (!SeparatorShape().IsMatch(separator)) errors.Add(new("separator", "invalid_format"));
        if (input.Width is null) errors.Add(new("width", "required"));
        else if (input.Width is < 1 or > 12) errors.Add(new("width", "out_of_range"));
        else width = input.Width.Value;
        if (input.PerBranch is null) errors.Add(new("perBranch", "required")); else perBranch = input.PerBranch.Value;
        if (input.PerFiscalYear is null) errors.Add(new("perFiscalYear", "required")); else perYear = input.PerFiscalYear.Value;
        return errors.Count == 0;
    }
}
