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
    public const short LinkCreated = 1909;
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
    /// <summary>Whether a document of <paramref name="fromType"/> may be linked to a document of <paramref name="toType"/> with this kind (S2, D-075). Fail-closed: only an explicitly registered (source type, target type, kind) pair is allowed.</summary>
    bool IsLinkAllowed(string fromType, string toType, CommercialLinkKind kind);
    /// <summary>The registered link pairs whose two types are both registered (static registry data; the screen offers link creation only for a target type that appears here).</summary>
    IReadOnlyList<LinkPairView> LinkPairs { get; }
}

/// <summary>One registered link pair as the API shows it: documents of <c>Source</c> may be linked into documents of <c>Target</c> with <c>Kind</c> (1 fulfils, 2 invoices, 3 returns, 4 amends).</summary>
public sealed record LinkPairView(string Source, string Target, short Kind);

/// <summary>The answer of the types route: the registered type names and the registered link pairs. Production registers neither (3.15/3.16).</summary>
public sealed record CommercialTypesView(IReadOnlyList<string> Types, IReadOnlyList<LinkPairView> LinkPairs);

/// <summary>The registered types and, separately, the permitted link pairs. Production registers neither until the concrete sales and purchase types (3.15/3.16); only the test composition registers pairs.</summary>
public sealed class CommercialDocumentTypeRegistry(IEnumerable<string> types, IEnumerable<(string From, string To, CommercialLinkKind Kind)>? linkPairs = null) : ICommercialDocumentTypeRegistry
{
    private readonly HashSet<string> set = new(types, StringComparer.Ordinal);
    private readonly HashSet<(string, string, CommercialLinkKind)> pairs = [.. linkPairs ?? []];
    public bool IsRegistered(string docType) => set.Contains(docType);
    public IReadOnlyCollection<string> Types => set;
    public bool IsLinkAllowed(string fromType, string toType, CommercialLinkKind kind) => set.Contains(fromType) && set.Contains(toType) && pairs.Contains((fromType, toType, kind));
    public IReadOnlyList<LinkPairView> LinkPairs => [.. pairs.Where(p => set.Contains(p.Item1) && set.Contains(p.Item2)).OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal).ThenBy(p => p.Item3).Select(p => new LinkPairView(p.Item1, p.Item2, (short)p.Item3))];
}

/// <summary>The kind of a link between two documents (S2): the source line is fulfilled, invoiced, returned or amended by the target line. Only the first three have a derived quantity.</summary>
public enum CommercialLinkKind : short { Fulfils = 1, Invoices = 2, Returns = 3, Amends = 4 }

/// <summary>Create body of a link. Lines are named by (document, line number); the SOURCE is the earlier line (an order, a delivery, an original), the TARGET the later one that fulfils, invoices, returns or amends it.</summary>
public sealed record DocumentLinkInput(Guid? FromDocumentId, int? FromLineNo, Guid? ToDocumentId, int? ToLineNo, int? Kind, decimal? Quantity, string? IdempotencyKey);

public sealed record DocumentLinkView(Guid Id, Guid FromDocumentId, int FromLineNo, Guid ToDocumentId, int ToLineNo, CommercialLinkKind Kind, decimal Quantity, Guid CreatedBy, DateTimeOffset CreatedAtUtc, bool Replayed = false);

/// <summary>Derived, never stored: ordered is the quantity of the line, the others are the sums of the links FROM this line by kind; remaining is what each kind may still take (tolerance 0, Q3 default).</summary>
public sealed record LineQuantityView(int LineNo, decimal Ordered, decimal Fulfilled, decimal Invoiced, decimal Returned)
{
    public decimal RemainingToFulfil => Ordered - Fulfilled;
    public decimal RemainingToInvoice => Ordered - Invoiced;
    public decimal RemainingToReturn => Ordered - Returned;
}

/// <summary>The one internal, non-user-facing test type. It is registered ONLY by the test composition; the production composition contains no type and no switch.</summary>
public static class CommercialTestType
{
    public const string Name = "commercial_test";
    /// <summary>A second test-only type with no registered link pair (the incompatible-type tests).</summary>
    public const string Other = "commercial_test_other";
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

/// <summary><c>PartyId</c>, <c>Currency</c> and <c>Search</c> (a literal part of the official number, case-insensitive) are server-side filters applied BEFORE paging, so a page and its total are exact (S6 Links, D-048).</summary>
public sealed record CommercialDocumentFilter(CommercialStatus? Status = null, string? DocType = null, Guid? BranchId = null, IReadOnlyList<Guid>? AllowedBranches = null, int Limit = 50, int Offset = 0, Guid? PartyId = null, string? Currency = null, string? Search = null);

/// <summary>One page of document headers and the number of documents matching the filters in all (before paging).</summary>
public sealed record CommercialDocumentPage(IReadOnlyList<CommercialDocumentView> Items, long Total);

/// <summary>The tax operation key the server uses to resolve taxes for a commercial document until the concrete sales and purchase types (3.15/3.16) define their own (recorded decision).</summary>
public static class CommercialDefaults
{
    public const string TaxOperation = "sale";
}
