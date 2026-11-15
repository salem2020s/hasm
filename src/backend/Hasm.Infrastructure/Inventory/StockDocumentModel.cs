using System.Text;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Inventory;

/// <summary>1 receipt, 2 issue, 3 transfer (persisted; never renumber).</summary>
public enum StockDocType : short { Receipt = 1, Issue = 2, Transfer = 3 }

/// <summary>1 draft, 2 submitted, 3 approved, 4 rejected, 5 cancelled (persisted; enforced by PostgreSQL).</summary>
public enum StockDocStatus : short { Draft = 1, Submitted = 2, Approved = 3, Rejected = 4, Cancelled = 5 }

/// <summary>1 created, 2 updated, 3 submitted, 4 withdrawn, 5 approved, 6 rejected, 7 cancelled.</summary>
public enum StockEventKind : short { Created = 1, Updated = 2, Submitted = 3, Withdrawn = 4, Approved = 5, Rejected = 6, Cancelled = 7 }

/// <summary>Business audit event types of stock documents (1140-1149 of the business stream).</summary>
public static class StockAuditEvents
{
    public const short Base = 1139;                     // event type = Base + StockEventKind
    public static short Of(StockEventKind kind) => (short)(Base + (short)kind);
}

public sealed record StockLineInput(Guid? ItemId, Guid? UnitId, decimal? Quantity, string? Notes);

/// <summary><see cref="DocType"/> is required on creation and can never change; the warehouses must fit the type (receipt: target; issue: source; transfer: both).</summary>
public sealed record StockDocumentInput(int? DocType, Guid? SourceWarehouseId, Guid? TargetWarehouseId, string? DocumentDate, string? Reference, string? Notes, IReadOnlyList<StockLineInput>? Lines);

public sealed record StockLineView(int LineNo, Guid ItemId, string ItemCode, string? ItemNameAr, string? ItemNameEn, Guid UnitId, string UnitCode, decimal Quantity, decimal Factor, decimal BaseQuantity,
    string BaseUnitCode, string? Notes);

public sealed record StockEventView(long Id, int Kind, int? FromStatus, int ToStatus, Guid ActorUserId, DateTimeOffset AtUtc, string? Note);

public sealed record StockDocumentView(
    Guid Id, int DocType, int Status, Guid BranchId, Guid? SourceWarehouseId, Guid? TargetWarehouseId, string DocumentDate, string? Reference, string? Notes, string? Number, Guid? FiscalYearId,
    Guid CreatedBy, DateTimeOffset CreatedAtUtc, Guid? SubmittedBy, DateTimeOffset? SubmittedAtUtc, Guid? DecidedBy, DateTimeOffset? DecidedAtUtc, string? DecisionNote,
    Guid? CancelledBy, DateTimeOffset? CancelledAtUtc, string? CancelReason, long Revision, DateTimeOffset UpdatedAtUtc, IReadOnlyList<StockLineView> Lines, IReadOnlyList<StockEventView> Events);

public sealed record StockDocumentSummary(Guid Id, int DocType, int Status, string? Number, string DocumentDate, Guid BranchId, Guid? SourceWarehouseId, Guid? TargetWarehouseId, string? Reference, int LineCount, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record StockDocumentFilter(int? DocType, int? Status, Guid? WarehouseId, string? From, string? To, string? Search, IReadOnlyCollection<Guid>? VisibleWarehouses, int Offset, int Limit);
public sealed record StockDocumentPage(IReadOnlyList<StockDocumentSummary> Items, long Total);

/// <summary>Pure validation (no database): the shape of a document and its lines.</summary>
public static class StockDocumentValidation
{
    public const int MaxLines = 500;

    public sealed record Header(StockDocType Type, Guid? Source, Guid? Target, DateOnly Date, string? Reference, string? Notes);
    public sealed record Line(Guid ItemId, Guid UnitId, decimal Quantity, string? Notes);

    private static string? Clean(string? v)
    {
        if (v is null) return null;
        var t = v.Trim().Normalize(NormalizationForm.FormC);
        return t.Length == 0 ? null : t;
    }

    private static string? Text(string? raw, string field, int max, List<FieldError> errors)
    {
        var v = Clean(raw);
        if (v is null) return null;
        if (v.Length > max) errors.Add(new(field, "too_long"));
        else if (v.Any(char.IsControl)) errors.Add(new(field, "invalid_characters"));
        return v;
    }

    /// <summary>Quantities are exact decimals: positive, at most 10 decimals and 18 integer digits (numeric(28,10)).</summary>
    public static bool IsValidQuantity(decimal q) => q > 0 && InventoryValidation.IsValidFactor(q);

    public static bool TryNormalize(StockDocumentInput? input, StockDocType? existingType, out Header header, out List<Line> lines, out List<FieldError> errors)
    {
        errors = [];
        lines = [];
        header = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        StockDocType type = default;
        if (existingType is { } et)
        {
            if (input.DocType is { } t && t != (int)et) errors.Add(new("docType", "immutable"));
            type = et;
        }
        else if (input.DocType is 1 or 2 or 3) type = (StockDocType)input.DocType.Value;
        else errors.Add(new("docType", input.DocType is null ? "required" : "invalid"));
        var source = input.SourceWarehouseId == Guid.Empty ? null : input.SourceWarehouseId;
        var target = input.TargetWarehouseId == Guid.Empty ? null : input.TargetWarehouseId;
        if (errors.Count == 0)
        {
            if (type != StockDocType.Receipt && source is null) errors.Add(new("sourceWarehouseId", "required"));
            if (type != StockDocType.Issue && target is null) errors.Add(new("targetWarehouseId", "required"));
            if (type == StockDocType.Receipt && source is not null) errors.Add(new("sourceWarehouseId", "not_allowed"));
            if (type == StockDocType.Issue && target is not null) errors.Add(new("targetWarehouseId", "not_allowed"));
            if (type == StockDocType.Transfer && source is not null && source == target) errors.Add(new("targetWarehouseId", "same_warehouse"));
        }
        DateOnly date = default;
        if (Clean(input.DocumentDate) is not { } d) errors.Add(new("documentDate", "required"));
        else if (!DateOnly.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date)) errors.Add(new("documentDate", "invalid_date"));
        var reference = Text(input.Reference, "reference", 100, errors);
        var notes = Text(input.Notes, "notes", 1000, errors);
        var supplied = input.Lines ?? [];
        if (supplied.Count == 0) errors.Add(new("lines", "required"));
        else if (supplied.Count > MaxLines) errors.Add(new("lines", "too_many"));
        else
            for (var i = 0; i < supplied.Count; i++)
            {
                var l = supplied[i];
                var f = $"lines[{i}]";
                if (l.ItemId is null || l.ItemId == Guid.Empty) errors.Add(new(f + ".itemId", "required"));
                if (l.UnitId is null || l.UnitId == Guid.Empty) errors.Add(new(f + ".unitId", "required"));
                if (l.Quantity is null) errors.Add(new(f + ".quantity", "required"));
                else if (!IsValidQuantity(l.Quantity.Value)) errors.Add(new(f + ".quantity", "invalid_quantity"));
                var ln = Text(l.Notes, f + ".notes", 200, errors);
                if (l.ItemId is { } item && l.UnitId is { } unit && l.Quantity is { } q && IsValidQuantity(q)) lines.Add(new Line(item, unit, q, ln));
            }
        if (errors.Count > 0) return false;
        header = new Header(type, source, target, date, reference, notes);
        return true;
    }

    /// <summary>
    /// Deterministic conversion of an entered quantity into the item's base unit: base = quantity x factor, EXACTLY (decimal arithmetic; never floating point). The result must be representable in the base unit's
    /// decimals and the entered quantity in the entered unit's decimals; otherwise the line is refused instead of being rounded (nothing is ever silently rounded).
    /// </summary>
    public static (decimal Base, string? Error) Convert(decimal quantity, decimal factor, int enteredDecimals, int baseDecimals)
    {
        if (!FitsDecimals(quantity, enteredDecimals)) return (0, "quantity_precision_exceeded");
        var b = quantity * factor;
        if (!FitsDecimals(b, baseDecimals)) return (0, "base_quantity_precision_exceeded");
        if (b > 999_999_999_999_999_999m) return (0, "invalid_quantity");
        return (b, null);
    }

    private static bool FitsDecimals(decimal v, int decimals)
    {
        var scale = 1m;
        for (var i = 0; i < decimals; i++) scale *= 10m;
        var scaled = v * scale;
        return decimal.Truncate(scaled) == scaled;
    }
}
