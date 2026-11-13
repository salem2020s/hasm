using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Ledger;

/// <summary>How a document came into being. Persisted (policies match on it); never renumber.</summary>
public enum DocumentSource : short { Manual = 1, Automatic = 2 }

/// <summary>Which sources a policy covers. 3 = either.</summary>
public enum PolicySource : short { Manual = 1, Automatic = 2, Any = 3 }

/// <summary>
/// An approval policy: documents of <see cref="DocType"/> and <see cref="Source"/>, of <see cref="BranchId"/> (null = every branch), whose BASE-currency amount is at least
/// <see cref="MinAmount"/>, need <see cref="RequiredApprovals"/> distinct approvers. Separation of duties is NOT configurable: an approver is never the creator or submitter.
/// </summary>
public sealed record ApprovalPolicyView(
    Guid Id, string DocType, PolicySource Source, Guid? BranchId, decimal MinAmount, int RequiredApprovals, string? NameAr, string? NameEn, bool IsActive,
    long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record ApprovalPolicyInput(string? DocType, string? Source, Guid? BranchId, decimal? MinAmount, int? RequiredApprovals, string? NameAr, string? NameEn);
public sealed record ApprovalPolicyDetailsInput(string? Source, Guid? BranchId, decimal? MinAmount, int? RequiredApprovals, string? NameAr, string? NameEn);

public sealed record ApprovalDecisionView(long Id, string DocType, Guid DocumentId, long DocumentRevision, Guid Approver, bool Approved, DateTimeOffset DecidedAtUtc, string? Comment);

/// <summary>A decision to record on a document revision. <see cref="CreatorId"/> and <see cref="SubmitterId"/> come from the DOCUMENT (never from the client).</summary>
public sealed record ApprovalDecisionRequest(string DocType, Guid DocumentId, long DocumentRevision, Guid ApproverId, Guid CreatorId, Guid? SubmitterId, bool Approve, string? Comment);

public enum ApprovalRecordStatus { Recorded, SeparationOfDuties, AlreadyDecided, CommentRequired }

public sealed record ApprovalRecordResult(ApprovalRecordStatus Status, ApprovalDecisionView? Decision)
{
    public bool IsRecorded => Status == ApprovalRecordStatus.Recorded;
}

public static partial class ApprovalValidation
{
    [GeneratedRegex("^[a-z][a-z_]{0,39}$")] private static partial Regex DocTypeShape();

    private static string? Clean(string? v)
    {
        var t = v?.Trim().Normalize(NormalizationForm.FormC);
        return string.IsNullOrEmpty(t) ? null : t;
    }

    public static bool TryParseSource(string? raw, out PolicySource source)
    {
        source = default;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "manual": source = PolicySource.Manual; return true;
            case "automatic": source = PolicySource.Automatic; return true;
            case "any": source = PolicySource.Any; return true;
            default: return false;
        }
    }

    private static void Common(string? source, decimal? min, int? required, string? ar, string? en, out PolicySource src, out decimal minAmount, out int req, out string? nameAr, out string? nameEn, List<FieldError> errors)
    {
        src = PolicySource.Any; minAmount = 0; req = 1;
        if (Clean(source) is null) errors.Add(new("source", "required"));
        else if (!TryParseSource(source, out src)) errors.Add(new("source", "invalid_value"));
        if (min is null) errors.Add(new("minAmount", "required"));
        else if (min < 0) errors.Add(new("minAmount", "must_not_be_negative"));
        else if (min > 9_999_999_999_999_999m) errors.Add(new("minAmount", "out_of_range"));
        else if (CurrencyMath.ScaleOf(min.Value) > 4 && min.Value != decimal.Round(min.Value, 4)) errors.Add(new("minAmount", "too_many_decimals"));
        else minAmount = decimal.Round(min.Value, 4);
        if (required is null) errors.Add(new("requiredApprovals", "required"));
        else if (required is < 1 or > 5) errors.Add(new("requiredApprovals", "out_of_range"));
        else req = required.Value;
        nameAr = Clean(ar); nameEn = Clean(en);
        if (nameAr is { Length: > 200 }) { errors.Add(new("nameAr", "too_long")); nameAr = null; }
        if (nameEn is { Length: > 200 }) { errors.Add(new("nameEn", "too_long")); nameEn = null; }
        if (nameAr is null && nameEn is null && errors.All(e => e.Field is not ("nameAr" or "nameEn"))) { errors.Add(new("nameAr", "name_required")); errors.Add(new("nameEn", "name_required")); }
    }

    public static bool TryNormalize(ApprovalPolicyInput? input, out string docType, out PolicySource source, out decimal minAmount, out int required, out string? nameAr, out string? nameEn, out List<FieldError> errors)
    {
        errors = [];
        docType = ""; source = PolicySource.Any; minAmount = 0; required = 1; nameAr = nameEn = null;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var d = Clean(input.DocType)?.ToLowerInvariant();
        if (d is null) errors.Add(new("docType", "required"));
        else if (!DocTypeShape().IsMatch(d) || !DocumentTypes.IsKnown(d)) errors.Add(new("docType", "unknown_document_type"));
        else docType = d;
        Common(input.Source, input.MinAmount, input.RequiredApprovals, input.NameAr, input.NameEn, out source, out minAmount, out required, out nameAr, out nameEn, errors);
        return errors.Count == 0;
    }

    public static bool TryNormalizeDetails(ApprovalPolicyDetailsInput? input, out PolicySource source, out decimal minAmount, out int required, out string? nameAr, out string? nameEn, out List<FieldError> errors)
    {
        errors = [];
        source = PolicySource.Any; minAmount = 0; required = 1; nameAr = nameEn = null;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        Common(input.Source, input.MinAmount, input.RequiredApprovals, input.NameAr, input.NameEn, out source, out minAmount, out required, out nameAr, out nameEn, errors);
        return errors.Count == 0;
    }
}
