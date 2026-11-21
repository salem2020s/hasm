namespace Hasm.Infrastructure.Commercial;

/// <summary>Business-audit event types of commercial documents (persisted; never renumber; unique across every <c>*AuditEvents</c> class). Range 1900-1999 is reserved for Phase 3.13.</summary>
public static class CommercialAuditEvents
{
    public const short Created = 1900;
    public const short Updated = 1901;
    public const short Submitted = 1902;
    public const short Withdrawn = 1903;
    public const short Cancelled = 1904;
    public const short Approved = 1905;
    public const short Rejected = 1906;
    public const short ReapprovalRequested = 1907;
    public const short Reopened = 1908;
}

/// <summary>The commercial-status track (SRS 19.7): draft, submitted, approved (numbered and sealed), rejected, cancelled.</summary>
public enum CommercialStatus : short { Draft = 1, Submitted = 2, Approved = 3, Rejected = 4, Cancelled = 5 }

/// <summary>Direct (the invoice carries the physical event) or separate (delivery/receipt documents), chosen per document at draft creation (owner decision Q15 / D-052).</summary>
public enum CommercialMode : short { Direct = 1, Separate = 2 }

/// <summary>1 created, 2 updated, 3 submitted, 4 withdrawn, 5 cancelled, 6 approved, 7 rejected, 8 re-approval requested (a material edit sent an approved document back to draft), 9 reopened (a rejected document returned to draft).</summary>
public enum CommercialEventKind : short { Created = 1, Updated = 2, Submitted = 3, Withdrawn = 4, Cancelled = 5, Approved = 6, Rejected = 7, ReapprovalRequested = 8, Reopened = 9 }

/// <summary>The registered document types. Production registers NONE in slice S1a (owner decision Q1 / D-049: only an internal test-only type, registered in the TEST composition); real types arrive with 3.15/3.16.</summary>
public interface ICommercialDocumentTypeRegistry
{
    bool IsRegistered(string docType);
    IReadOnlyCollection<string> Types { get; }
}

public sealed class CommercialDocumentTypeRegistry(IEnumerable<string> types) : ICommercialDocumentTypeRegistry
{
    private readonly HashSet<string> set = new(types, StringComparer.Ordinal);
    public bool IsRegistered(string docType) => set.Contains(docType);
    public IReadOnlyCollection<string> Types => set;
}

/// <summary>The one internal, non-user-facing test type. It is registered ONLY by the test composition; the production composition contains no type and no switch.</summary>
public static class CommercialTestType
{
    public const string Name = "commercial_test";
}

/// <summary>A line of a draft (S1a: item, unit, quantity and an optional warehouse; no price, tax or cost). Line numbers are assigned by position.</summary>
public sealed record CommercialLineInput(Guid? ItemId, Guid? UnitId, decimal? Quantity, Guid? WarehouseId);

/// <summary>
/// Create and update body. On create DocType, BranchId, Mode and IdempotencyKey are required. On update the type, branch and key never change (null or equal); party, currency, date, mode, note and lines are replaced as a whole
/// (draft only). A line API request carries no price, tax, cost or snapshot field (S1a).
/// </summary>
public sealed record CommercialDocumentInput(string? DocType, Guid? BranchId, Guid? PartyId, string? Currency, DateOnly? DocumentDate, int? Mode, string? Note, string? IdempotencyKey, IReadOnlyList<CommercialLineInput>? Lines);

/// <summary>LineNet and PricingHash are produced by the SERVER at submit and sealed at approval (null on a draft).</summary>
public sealed record CommercialLineView(int LineNo, Guid ItemId, Guid UnitId, decimal Quantity, Guid? WarehouseId, decimal? LineNet = null, string? PricingHash = null);

public sealed record CommercialEventView(CommercialEventKind Kind, CommercialStatus? FromStatus, CommercialStatus ToStatus, long Revision, Guid ActorUserId, string? Reason, DateTimeOffset AtUtc);

public sealed record CommercialDocumentView(Guid Id, string DocType, Guid BranchId, Guid PartyId, string Currency, DateOnly DocumentDate, CommercialMode Mode, CommercialStatus Status, string? Note,
    Guid? SubmittedBy, long Revision, Guid CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, IReadOnlyList<CommercialLineView> Lines, IReadOnlyList<CommercialEventView> Events, bool Replayed = false,
    string? OfficialNumber = null, Guid? ApprovedBy = null, DateTimeOffset? ApprovedAtUtc = null, DateTimeOffset? SealedAtUtc = null, decimal? ExchangeRate = null, decimal? TotalNet = null, decimal? TotalTax = null, decimal? TotalGross = null,
    Guid? LastEditedBy = null, string? Fingerprint = null, Guid? FiscalYearId = null);

public sealed record CommercialDocumentFilter(CommercialStatus? Status = null, string? DocType = null, Guid? BranchId = null, IReadOnlyList<Guid>? AllowedBranches = null, int Limit = 50, int Offset = 0);

/// <summary>The tax operation key the server uses to resolve taxes for a commercial document until the concrete sales and purchase types (3.15/3.16) define their own (recorded decision).</summary>
public static class CommercialDefaults
{
    public const string TaxOperation = "sale";
}
