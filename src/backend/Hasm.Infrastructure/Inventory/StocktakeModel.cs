using System.Globalization;
using System.Text;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Inventory;

/// <summary>1 draft, 2 submitted, 3 approved, 4 rejected, 5 cancelled (persisted; enforced by PostgreSQL).</summary>
public enum StocktakeStatus : short { Draft = 1, Submitted = 2, Approved = 3, Rejected = 4, Cancelled = 5 }

/// <summary>1 created, 2 updated, 3 submitted, 4 withdrawn, 5 approved, 6 rejected, 7 cancelled, 8 sheet generated.</summary>
public enum StocktakeEventKind : short { Created = 1, Updated = 2, Submitted = 3, Withdrawn = 4, Approved = 5, Rejected = 6, Cancelled = 7, SheetGenerated = 8 }

/// <summary>Business audit event types of stocktakes (1170-1179 of the business stream).</summary>
public static class StocktakeAuditEvents
{
    public const short Base = 1169;                     // event type = Base + StocktakeEventKind (1170 ... 1177)
    public static short Of(StocktakeEventKind kind) => (short)(Base + (short)kind);
}

/// <summary>A counted line: the counted quantity is optional while the sheet is a draft (not counted yet) and may be zero; a reason is required at submit when the line has a variance.</summary>
public sealed record StocktakeLineInput(Guid? ItemId, Guid? UnitId, decimal? CountedQuantity, string? Reason);

/// <summary>The warehouse is chosen on creation and never changes. The count date is also the as-of date of the book quantities.</summary>
public sealed record StocktakeInput(Guid? WarehouseId, string? CountDate, string? Reference, string? Notes, IReadOnlyList<StocktakeLineInput>? Lines);

/// <summary>Generate (or extend) the sheet: <see cref="ItemIds"/> null or empty = every item with a non-zero book quantity in the warehouse at the count date; otherwise exactly these items.</summary>
public sealed record StocktakeSheetInput(IReadOnlyList<Guid>? ItemIds);

/// <summary>
/// <see cref="BookQuantity"/> is the snapshot (base unit, as of the count date) taken when the line was last saved or at submit; <see cref="CurrentBookQuantity"/> is the same figure computed now. They differ (<see cref="Stale"/>)
/// only if a movement dated on or before the count date was posted or reversed after the snapshot. Information only: the adjustment of 3.8B-2 uses the current book quantity.
/// </summary>
public sealed record StocktakeLineView(int LineNo, Guid ItemId, string ItemCode, string? ItemNameAr, string? ItemNameEn, Guid UnitId, string UnitCode, decimal? CountedQuantity, decimal Factor, decimal? CountedBaseQuantity,
    decimal BookQuantity, decimal? Variance, string BaseUnitCode, string? Reason, decimal CurrentBookQuantity, bool Stale, IReadOnlyList<StockUnitRef>? Units = null);

public sealed record StocktakeView(
    Guid Id, int Status, Guid BranchId, Guid WarehouseId, string CountDate, string? Reference, string? Notes, string? Number, Guid? FiscalYearId, DateTimeOffset SnapshotAtUtc,
    Guid CreatedBy, DateTimeOffset CreatedAtUtc, Guid? SubmittedBy, DateTimeOffset? SubmittedAtUtc, Guid? DecidedBy, DateTimeOffset? DecidedAtUtc, string? DecisionNote,
    Guid? CancelledBy, DateTimeOffset? CancelledAtUtc, string? CancelReason, long Revision, DateTimeOffset UpdatedAtUtc, IReadOnlyList<StocktakeLineView> Lines, IReadOnlyList<StockEventView> Events,
    Guid? AdjustmentId = null, string? AdjustmentNumber = null)
{
    public bool Stale => Lines.Any(l => l.Stale);
}

public sealed record StocktakeSummary(Guid Id, int Status, string? Number, string CountDate, Guid BranchId, Guid WarehouseId, string? Reference, int LineCount, int CountedLines, int VarianceLines, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record StocktakeFilter(int? Status, Guid? WarehouseId, string? From, string? To, string? Search, IReadOnlyCollection<Guid>? VisibleWarehouses, int Offset, int Limit);
public sealed record StocktakePage(IReadOnlyList<StocktakeSummary> Items, long Total);

/// <summary>For each action, the warehouses on which the caller holds the stocktake permission.
/// <see cref="Adjust"/> (Phase 3.8B-2): where the caller may generate the adjustment of an approved stocktake (stocktake view plus <c>company.stockdocs.create</c> on the warehouse); <see cref="Post"/> and <see cref="Reverse"/>: where it may post or reverse that adjustment (<c>company.stockdocs.post</c> / <c>.reverse</c>).</summary>
public sealed record StocktakeCapabilities(IReadOnlyList<Guid> View, IReadOnlyList<Guid> Create, IReadOnlyList<Guid> Update, IReadOnlyList<Guid> Submit, IReadOnlyList<Guid> Approve, IReadOnlyList<Guid> Cancel,
    IReadOnlyList<Guid>? Adjust = null, IReadOnlyList<Guid>? Post = null, IReadOnlyList<Guid>? Reverse = null);

/// <summary>Pure validation (no database): the shape of a stocktake and its lines.</summary>
public static class StocktakeValidation
{
    /// <summary>A sheet holds at most this many items (a larger warehouse is counted in several sheets of chosen items).</summary>
    public const int MaxLines = 2000;

    public sealed record Header(Guid WarehouseId, DateOnly CountDate, string? Reference, string? Notes);
    public sealed record Line(Guid ItemId, Guid UnitId, decimal? Counted, string? Reason);

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

    /// <summary>A counted quantity is an exact decimal: zero or positive, at most 10 decimals and 18 integer digits (numeric(28,10)).</summary>
    public static bool IsValidCount(decimal q) => q == 0 || InventoryValidation.IsValidFactor(q);

    /// <summary>Variance of a line in the base unit: counted base quantity - book quantity, exact decimal arithmetic (never floating point). Null while the line is not counted.</summary>
    public static decimal? Variance(decimal? countedBase, decimal book) => countedBase is { } c ? c - book : null;

    public static bool TryNormalize(StocktakeInput? input, Guid? existingWarehouse, out Header header, out List<Line> lines, out List<FieldError> errors)
    {
        errors = [];
        lines = [];
        header = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var wh = input.WarehouseId == Guid.Empty ? null : input.WarehouseId;
        if (existingWarehouse is { } ew)
        {
            if (wh is { } w && w != ew) errors.Add(new("warehouseId", "immutable"));
            wh = ew;
        }
        else if (wh is null) errors.Add(new("warehouseId", "required"));
        DateOnly date = default;
        if (Clean(input.CountDate) is not { } d) errors.Add(new("countDate", "required"));
        else if (!DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) errors.Add(new("countDate", "invalid_date"));
        var reference = Text(input.Reference, "reference", 100, errors);
        var notes = Text(input.Notes, "notes", 1000, errors);
        var supplied = input.Lines ?? [];
        if (supplied.Count > MaxLines) errors.Add(new("lines", "too_many"));
        else
        {
            var seen = new HashSet<Guid>();
            for (var i = 0; i < supplied.Count; i++)
            {
                var l = supplied[i];
                var f = $"lines[{i}]";
                if (l.ItemId is null || l.ItemId == Guid.Empty) errors.Add(new(f + ".itemId", "required"));
                else if (!seen.Add(l.ItemId.Value)) errors.Add(new(f + ".itemId", "duplicate_item"));
                if (l.UnitId is null || l.UnitId == Guid.Empty) errors.Add(new(f + ".unitId", "required"));
                if (l.CountedQuantity is { } q && !IsValidCount(q)) errors.Add(new(f + ".countedQuantity", "invalid_quantity"));
                var reason = Text(l.Reason, f + ".reason", 200, errors);
                if (l.ItemId is { } item && item != Guid.Empty && l.UnitId is { } unit && unit != Guid.Empty) lines.Add(new Line(item, unit, l.CountedQuantity, reason));
            }
        }
        if (errors.Count > 0) return false;
        header = new Header(wh!.Value, date, reference, notes);
        return true;
    }
}
