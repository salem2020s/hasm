using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Parties;

/// <summary>1 organization, 2 individual (persisted in both databases; enforced by PostgreSQL).</summary>
public enum PartyKind : short { Organization = 1, Individual = 2 }

/// <summary>Status of a shared identity: 1 active, 2 inactive.</summary>
public enum GlobalPartyStatus : short { Active = 1, Inactive = 2 }

/// <summary>Link state of a local party: 1 pending central decision, 2 linked, 3 proposal rejected.</summary>
public enum PartyLinkState : short { Pending = 1, Linked = 2, Rejected = 3 }

/// <summary>Kind of a cross-database request (and of the central proposal it delivers): 1 new shared identity, 2 change of the shared data of a linked party.</summary>
public enum PartyRequestKind : short { NewIdentity = 1, Change = 2 }

/// <summary>Delivery status of a request of the company's request log (outbox): 1 pending delivery, 2 delivered, 3 completed.</summary>
public enum PartyRequestStatus : short { Pending = 1, Delivered = 2, Completed = 3 }

/// <summary>Outcome of a completed request: 1 approved, 2 rejected, 3 refused by the registry (never became a proposal).</summary>
public enum PartyRequestOutcome : short { Approved = 1, Rejected = 2, Refused = 3 }

/// <summary>Status of a central proposal: 1 pending, 2 approved, 3 rejected.</summary>
public enum PartyProposalStatus : short { Pending = 1, Approved = 2, Rejected = 3 }

/// <summary>How an approved proposal was resolved: 1 a new shared identity was created, 2 linked to an existing identity, 3 a change was applied.</summary>
public enum PartyProposalResolution : short { Created = 1, Linked = 2, Changed = 3 }

/// <summary>
/// Audit event types of the party registry. Company business stream (company database): 1300-1309. System stream (system database): 1310-1314.
/// Persisted codes: never renumber.
/// </summary>
public static class PartyAuditEvents
{
    public const short LocalPartyCreated = 1300;
    public const short LocalPartyUpdated = 1301;
    public const short RequestQueued = 1302;
    public const short RequestDelivered = 1303;
    public const short DeliveryFailed = 1304;
    public const short RequestRefused = 1305;
    public const short IdentityLinked = 1306;
    public const short ProposalRejected = 1307;
    public const short ChangeCompleted = 1308;
    public const short ReconciliationRepaired = 1309;

    public const short ProposalReceived = 1310;
    public const short ApprovedNewIdentity = 1311;
    public const short ApprovedByLinking = 1312;
    public const short ApprovedChange = 1313;
    public const short ProposalRejectedCentrally = 1314;
}

/// <summary>Proposed identifying data of a shared identity. <see cref="Status"/> is used only by a change proposal (a new identity is always active); <see cref="Note"/> justifies the proposal.</summary>
public sealed record SharedIdentityInput(int? Kind, string? LegalNameAr, string? LegalNameEn, string? TaxNumber, string? RegistrationNumber, int? Status = null, string? Note = null);

/// <summary>A company-local party record. <see cref="Identity"/> is required on creation (every new local party proposes its shared identity) and refused on update.</summary>
public sealed record LocalPartyInput(string? Code, string? DisplayName, string? Notes, bool? IsActive, SharedIdentityInput? Identity = null);

/// <summary>The shared identity as a linked company may see it: identifying data only. There is deliberately no field for balances or any company's data.</summary>
public sealed record SharedIdentityView(Guid GlobalPartyId, int Kind, string LegalNameAr, string? LegalNameEn, string? TaxNumber, string? RegistrationNumber, int Status, long Revision);

public sealed record PartyRequestView(Guid Id, int Kind, int Status, int? Outcome, int PartyKind, string LegalNameAr, string? LegalNameEn, string? TaxNumber, string? RegistrationNumber, int ProposedStatus,
    string? Note, int Attempts, DateTimeOffset? LastAttemptAtUtc, string? LastErrorCode, Guid CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset? DeliveredAtUtc, DateTimeOffset? CompletedAtUtc);

public sealed record LocalPartyView(Guid Id, string Code, string DisplayName, string? Notes, bool IsActive, int LinkState, Guid? GlobalPartyId, long Revision, Guid CreatedBy, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, SharedIdentityView? Identity, IReadOnlyList<PartyRequestView> Requests)
{
    public PartyRequestView? OpenRequest => Requests.FirstOrDefault(r => r.Status != (int)PartyRequestStatus.Completed);
}

public sealed record LocalPartySummary(Guid Id, string Code, string DisplayName, bool IsActive, int LinkState, string? LegalNameAr, string? LegalNameEn, bool HasOpenRequest, long Revision, DateTimeOffset UpdatedAtUtc);
public sealed record PartyFilter(string? Search, int? LinkState, int Offset, int Limit);
public sealed record PartyPage(IReadOnlyList<LocalPartySummary> Items, long Total);
public sealed record PartyCapabilities(bool View, bool Create, bool Update, bool Submit, bool Configure);

/// <summary>Result of synchronizing the request log: requests delivered, outcomes applied, attempts that failed, requests still waiting (pending delivery or an undecided proposal).</summary>
public sealed record PartySyncResult(int Delivered, int Applied, int Failed, int Waiting);

/// <summary>One mismatch between the company's request log and links and the registry of the system database (codes in docs/PHASE-3.9A.md D6).</summary>
public sealed record PartyReconciliationFinding(string Code, Guid? LocalPartyId, Guid? RequestId, Guid? GlobalPartyId, bool Repairable, bool Repaired);
public sealed record PartyReconciliationReport(IReadOnlyList<PartyReconciliationFinding> Findings, bool RepairRequested);

/// <summary>A central proposal as the central approver sees it, with the target identity (change) and the registry identities that look like it (same identifier or legal name).</summary>
public sealed record PartyProposalView(Guid Id, Guid CompanyId, string? CompanyNameAr, string? CompanyNameEn, Guid LocalPartyId, int Kind, Guid? TargetGlobalPartyId, int PartyKind, string LegalNameAr,
    string? LegalNameEn, string? TaxNumber, string? RegistrationNumber, int ProposedStatus, string? Note, Guid ProposedBy, string? ProposedByName, DateTimeOffset ProposedAtUtc, int Status, int? Resolution,
    Guid? OutcomeGlobalPartyId, Guid? DecidedBy, DateTimeOffset? DecidedAtUtc, string? DecisionNote, long Revision, SharedIdentityView? Target, IReadOnlyList<SharedIdentityView> Matches);

public sealed record PartyProposalSummary(Guid Id, Guid CompanyId, string? CompanyNameAr, string? CompanyNameEn, int Kind, string LegalNameAr, string? LegalNameEn, string? TaxNumber, string? RegistrationNumber,
    DateTimeOffset ProposedAtUtc, int Status, long Revision);
public sealed record PartyProposalPage(IReadOnlyList<PartyProposalSummary> Items, long Total);

/// <summary>Pure validation and normalization (no database).</summary>
public static partial class PartyValidation
{
    public sealed record Local(string Code, string DisplayName, string? Notes, bool IsActive);
    public sealed record Identity(PartyKind Kind, string LegalNameAr, string? LegalNameEn, string? TaxNumber, string? RegistrationNumber, GlobalPartyStatus Status, string? Note);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._-]{0,29}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[A-Z0-9./-]{1,50}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string? Clean(string? v)
    {
        if (v is null) return null;
        var t = v.Trim().Normalize(NormalizationForm.FormC);
        return t.Length == 0 ? null : t;
    }

    private static string? Text(string? raw, string field, int max, List<FieldError> errors, bool required = false)
    {
        var v = Clean(raw);
        if (v is null) { if (required) errors.Add(new(field, "required")); return null; }
        if (v.Length > max) errors.Add(new(field, "too_long"));
        else if (v.Any(char.IsControl)) errors.Add(new(field, "invalid_characters"));
        return v;
    }

    /// <summary>Upper case, inner white space removed; null when blank. Letters, digits, '.', '/', '-' only, at most 50.</summary>
    public static string? NormalizeIdentifier(string? raw, string field, List<FieldError> errors)
    {
        var v = Clean(raw);
        if (v is null) return null;
        v = Spaces().Replace(v, "").ToUpperInvariant();
        if (!IdentifierPattern().IsMatch(v)) { errors.Add(new(field, "invalid_identifier")); return null; }
        return v;
    }

    public static bool TryLocal(LocalPartyInput? input, bool creating, out Local local, out List<FieldError> errors)
    {
        errors = [];
        local = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var code = Clean(input.Code)?.ToUpperInvariant();
        if (code is null) errors.Add(new("code", "required"));
        else if (!CodePattern().IsMatch(code)) errors.Add(new("code", "invalid_code"));
        var name = Text(input.DisplayName, "displayName", 200, errors, required: true);
        var notes = Text(input.Notes, "notes", 1000, errors);
        if (!creating && input.Identity is not null) errors.Add(new("identity", "use_propose"));
        if (errors.Count > 0) return false;
        local = new Local(code!, name!, notes, input.IsActive ?? true);
        return true;
    }

    /// <summary><paramref name="change"/>: a change proposal may set the status (active or inactive); a new identity is always active.</summary>
    public static bool TryIdentity(SharedIdentityInput? input, bool change, string prefix, out Identity identity, out List<FieldError> errors)
    {
        errors = [];
        identity = default!;
        if (input is null) { errors.Add(new(prefix.TrimEnd('.'), "required")); return false; }
        PartyKind kind = default;
        if (input.Kind is not (1 or 2)) errors.Add(new(prefix + "kind", input.Kind is null ? "required" : "invalid_kind"));
        else kind = (PartyKind)input.Kind.Value;
        var ar = Text(input.LegalNameAr, prefix + "legalNameAr", 200, errors, required: true);
        var en = Text(input.LegalNameEn, prefix + "legalNameEn", 200, errors);
        var tax = NormalizeIdentifier(input.TaxNumber, prefix + "taxNumber", errors);
        var reg = NormalizeIdentifier(input.RegistrationNumber, prefix + "registrationNumber", errors);
        var status = GlobalPartyStatus.Active;
        if (input.Status is { } s)
        {
            if (!change && s != 1) errors.Add(new(prefix + "status", "invalid_status"));
            else if (s is not (1 or 2)) errors.Add(new(prefix + "status", "invalid_status"));
            else status = (GlobalPartyStatus)s;
        }
        var note = Text(input.Note, prefix + "note", 500, errors);
        if (errors.Count > 0) return false;
        identity = new Identity(kind, ar!, en, tax, reg, status, note);
        return true;
    }
}
