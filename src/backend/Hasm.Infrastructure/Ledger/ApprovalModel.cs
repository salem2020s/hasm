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
/// <remarks>
/// Phase 3.2C S2 adds optional RISK CONDITIONS (journal type, manual exchange-rate override, soft-closed period; null = any) and ordered STAGES. A policy without stages is the CLASSIC policy
/// (one stage of <see cref="RequiredApprovals"/> distinct approvers); for a staged policy <see cref="RequiredApprovals"/> is the TOTAL over its stages.
/// </remarks>
public sealed record ApprovalPolicyView(
    Guid Id, string DocType, PolicySource Source, Guid? BranchId, decimal MinAmount, int RequiredApprovals, string? NameAr, string? NameEn, bool IsActive,
    long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    string? JournalType = null, bool? RateOverride = null, bool? SoftClosed = null, IReadOnlyList<ApprovalStageView>? Stages = null, string? ClosingKind = null)
{
    public bool IsStaged => Stages is { Count: > 0 };
}

/// <summary>One stage of a policy: <see cref="Required"/> distinct approvers, optionally holders of <see cref="RoleId"/>. Stages are completed strictly in order.</summary>
public sealed record ApprovalStageView(int StageNo, int Required, Guid? RoleId);
public sealed record ApprovalStageInput(int? Required, Guid? RoleId);
/// <summary>The progress of one stage of a document's effective plan for its current revision.</summary>
public sealed record ApprovalStageProgress(int Stage, int Required, int Approved, Guid? RoleId);

public sealed record ApprovalPolicyInput(string? DocType, string? Source, Guid? BranchId, decimal? MinAmount, int? RequiredApprovals, string? NameAr, string? NameEn,
    string? JournalType = null, bool? RateOverride = null, bool? SoftClosed = null, IReadOnlyList<ApprovalStageInput>? Stages = null, string? ClosingKind = null);
public sealed record ApprovalPolicyDetailsInput(string? Source, Guid? BranchId, decimal? MinAmount, int? RequiredApprovals, string? NameAr, string? NameEn,
    string? JournalType = null, bool? RateOverride = null, bool? SoftClosed = null, IReadOnlyList<ApprovalStageInput>? Stages = null, string? ClosingKind = null);

public sealed record ApprovalDecisionView(long Id, string DocType, Guid DocumentId, long DocumentRevision, Guid Approver, bool Approved, DateTimeOffset DecidedAtUtc, string? Comment, int Stage = 1);

/// <summary>A normalized policy as it is stored.</summary>
public sealed record NormalizedPolicy(string DocType, PolicySource Source, decimal MinAmount, int Required, string? NameAr, string? NameEn, short? JournalType, bool? RateOverride, bool? SoftClosed, IReadOnlyList<ApprovalStageView> Stages, short? ClosingKind = null)
{
    /// <summary>Approvals in total over every stage (classic: N).</summary>
    public int Total => Stages.Count > 0 ? Stages.Sum(s => s.Required) : Required;
}

/// <summary>A decision to record on a document revision. <see cref="CreatorId"/> and <see cref="SubmitterId"/> come from the DOCUMENT (never from the client).</summary>
public sealed record ApprovalDecisionRequest(string DocType, Guid DocumentId, long DocumentRevision, Guid ApproverId, Guid CreatorId, Guid? SubmitterId, bool Approve, string? Comment, int Stage = 1);

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

    /// <summary>journal type of a policy condition: "normal", "reversal" or "adjusting"; empty or "any" = no condition.</summary>
    public static bool TryParseJournalType(string? raw, out short? type)
    {
        type = null;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": case "any": return true;
            case "normal": type = 1; return true;
            case "reversal": type = 2; return true;
            case "adjusting": type = 3; return true;
            default: return false;
        }
    }

    /// <summary>Year-end condition: "closing" (year-end closing entries and their reversals) or "opening" (opening-balance entries and their reversals); empty or "any" = none.</summary>
    public static bool TryParseClosingKind(string? raw, out short? kind)
    {
        kind = null;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": case "any": return true;
            case "closing": kind = 1; return true;
            case "opening": kind = 2; return true;
            default: return false;
        }
    }

    public const int MaxApproversPerStage = 1000;

    /// <summary>The risk conditions and stages shared by create and update. Stages: any number (no business cap), each needing 1..1000 distinct approvers.</summary>
    private static void Extras(string? journalType, bool? rate, bool? soft, IReadOnlyList<ApprovalStageInput>? stages, out short? jt, out IReadOnlyList<ApprovalStageView> normalized, List<FieldError> errors)
    {
        jt = null; normalized = [];
        if (!TryParseJournalType(journalType, out jt)) errors.Add(new("journalType", "invalid_value"));
        if (stages is not { Count: > 0 }) return;
        var list = new List<ApprovalStageView>();
        for (var i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            if (s.Required is null) errors.Add(new($"stages[{i}].required", "required"));
            else if (s.Required is < 1 or > MaxApproversPerStage) errors.Add(new($"stages[{i}].required", "out_of_range"));
            else list.Add(new ApprovalStageView(i + 1, s.Required.Value, s.RoleId == Guid.Empty ? null : s.RoleId));
        }
        if (errors.All(e => !e.Field.StartsWith("stages["))) normalized = list;
    }

    /// <summary>Full normalization of a new policy (conditions and stages included). The classic <c>requiredApprovals</c> is ignored when stages are given.</summary>
    public static bool TryNormalizeFull(ApprovalPolicyInput? input, out NormalizedPolicy policy, out List<FieldError> errors)
    {
        policy = new("", PolicySource.Any, 0, 1, null, null, null, null, null, []);
        errors = [];
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var d = Clean(input.DocType)?.ToLowerInvariant();
        string docType = "";
        if (d is null) errors.Add(new("docType", "required"));
        else if (!DocTypeShape().IsMatch(d) || !DocumentTypes.UsesApprovalPolicies(d)) errors.Add(new("docType", "unknown_document_type"));
        else docType = d;
        Extras(input.JournalType, input.RateOverride, input.SoftClosed, input.Stages, out var jt, out var stages, errors);
        if (!TryParseClosingKind(input.ClosingKind, out var ck)) errors.Add(new("closingKind", "invalid_value"));
        Common(input.Source, input.MinAmount, input.Stages is { Count: > 0 } ? 1 : input.RequiredApprovals, input.NameAr, input.NameEn, out var src, out var min, out var req, out var ar, out var en, errors);
        if (errors.Count > 0) return false;
        policy = new(docType, src, min, req, ar, en, jt, input.RateOverride, input.SoftClosed, stages, ck);
        return true;
    }

    public static bool TryNormalizeDetailsFull(ApprovalPolicyDetailsInput? input, string docType, out NormalizedPolicy policy, out List<FieldError> errors)
    {
        policy = new(docType, PolicySource.Any, 0, 1, null, null, null, null, null, []);
        errors = [];
        if (input is null) { errors.Add(new("body", "required")); return false; }
        Extras(input.JournalType, input.RateOverride, input.SoftClosed, input.Stages, out var jt, out var stages, errors);
        if (!TryParseClosingKind(input.ClosingKind, out var ck)) errors.Add(new("closingKind", "invalid_value"));
        Common(input.Source, input.MinAmount, input.Stages is { Count: > 0 } ? 1 : input.RequiredApprovals, input.NameAr, input.NameEn, out var src, out var min, out var req, out var ar, out var en, errors);
        if (errors.Count > 0) return false;
        policy = new(docType, src, min, req, ar, en, jt, input.RateOverride, input.SoftClosed, stages, ck);
        return true;
    }

    public static bool TryNormalize(ApprovalPolicyInput? input, out string docType, out PolicySource source, out decimal minAmount, out int required, out string? nameAr, out string? nameEn, out List<FieldError> errors)
    {
        errors = [];
        docType = ""; source = PolicySource.Any; minAmount = 0; required = 1; nameAr = nameEn = null;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var d = Clean(input.DocType)?.ToLowerInvariant();
        if (d is null) errors.Add(new("docType", "required"));
        else if (!DocTypeShape().IsMatch(d) || !DocumentTypes.UsesApprovalPolicies(d)) errors.Add(new("docType", "unknown_document_type"));
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
