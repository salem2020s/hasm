using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Inventory;

/// <summary>Business audit event types of inventory master data (Phase 3.4, 1120-1139 of the business stream).</summary>
public static class InventoryAuditEvents
{
    public const short UnitCreated = 1120;
    public const short UnitUpdated = 1121;
    public const short UnitActiveChanged = 1122;
    public const short ItemGroupCreated = 1123;
    public const short ItemGroupUpdated = 1124;
    public const short ItemGroupActiveChanged = 1125;
    public const short ItemCreated = 1126;
    public const short ItemUpdated = 1127;
    public const short ItemActiveChanged = 1128;
    public const short BarcodeAdded = 1129;
    public const short BarcodeActiveChanged = 1130;
    public const short SettingsChanged = 1131;
}

/// <summary>Approved valuation methods (D-017). The costing engine itself is a later phase; this phase only records the policy.</summary>
public enum ValuationMethod : short { Fifo = 1, MovingAverage = 2 }

public enum ItemType : short { Goods = 1, Service = 2 }

/// <summary>1 EAN-13, 2 EAN-8, 3 UPC-A (check digit verified), 4 internal free text.</summary>
public enum BarcodeKind : short { Ean13 = 1, Ean8 = 2, UpcA = 3, Internal = 4 }

// ---- units
public sealed record UnitInput(string? Code, string? NameAr, string? NameEn, int? Decimals);
/// <summary>One page of a searched or paged master-data list with the number of matching rows (before paging).</summary>
public sealed record UnitPage(IReadOnlyList<UnitView> Items, long Total);
public sealed record ItemGroupPage(IReadOnlyList<ItemGroupView> Items, long Total);
public sealed record UnitView(Guid Id, string Code, string? NameAr, string? NameEn, int Decimals, bool IsActive, long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

// ---- item groups
public sealed record ItemGroupInput(string? Code, string? NameAr, string? NameEn, Guid? ParentId, int? Valuation);
/// <summary><see cref="Valuation"/> is the group's own setting (null = inherit); <see cref="EffectiveValuation"/> is the resolved method (null while undecided everywhere).</summary>
public sealed record ItemGroupView(Guid Id, string Code, string? NameAr, string? NameEn, Guid? ParentId, int? Valuation, int? EffectiveValuation, bool IsActive, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

// ---- items
public sealed record ItemUnitInput(Guid? UnitId, decimal? Factor);
public sealed record ItemInput(string? Code, string? NameAr, string? NameEn, int? ItemType, Guid? GroupId, Guid? BaseUnitId, Guid? PurchaseUnitId, Guid? SalesUnitId,
    bool? IsLot, bool? IsSerial, bool? HasExpiry, string? Notes, IReadOnlyList<ItemUnitInput>? AlternateUnits);
public sealed record ItemUnitView(Guid UnitId, string UnitCode, decimal Factor);
public sealed record ItemView(Guid Id, string Code, string? NameAr, string? NameEn, int ItemType, Guid GroupId, Guid BaseUnitId, Guid? PurchaseUnitId, Guid? SalesUnitId,
    bool IsLot, bool IsSerial, bool HasExpiry, string? Notes, bool IsActive, long Revision, int? EffectiveValuation,
    IReadOnlyList<ItemUnitView> AlternateUnits, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

// ---- barcodes
public sealed record BarcodeInput(string? Barcode, Guid? UnitId, int? Kind);
public sealed record BarcodeView(Guid Id, Guid ItemId, Guid UnitId, string Barcode, int Kind, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
/// <summary>An exact barcode lookup: the barcode with its item and unit (and the unit factor to the item's base unit).</summary>
public sealed record BarcodeLookup(BarcodeView Barcode, string ItemCode, string? ItemNameAr, string? ItemNameEn, string UnitCode, decimal UnitFactor);

// ---- settings
public sealed record InventorySettingsInput(int? DefaultValuation);
public sealed record InventorySettingsView(int? DefaultValuation, long Revision);

/// <summary>Pure validation and normalisation (no database): shape rules, barcode check digits and the valuation-method resolution.</summary>
public static partial class InventoryValidation
{
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_-]{0,19}$")] private static partial Regex UnitOrGroupCode();
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_-]{0,29}$")] private static partial Regex ItemCode();
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._/-]{0,49}$")] private static partial Regex BarcodeShape();
    [GeneratedRegex(@"^[0-9]+$")] private static partial Regex Digits();

    public const int MaxAlternateUnits = 20;
    public const int MaxFactorIntegerDigits = 18;
    public const int MaxFactorDecimals = 10;

    internal static string? Clean(string? value)
    {
        if (value is null) return null;
        var t = value.Trim().Normalize(NormalizationForm.FormC);
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

    private static void Names(string? ar, string? en, int max, List<FieldError> errors, out string? nameAr, out string? nameEn)
    {
        nameAr = Text(ar, "nameAr", max, errors);
        nameEn = Text(en, "nameEn", max, errors);
        if (nameAr is null && nameEn is null && errors.All(e => e.Field != "nameAr" && e.Field != "nameEn"))
        { errors.Add(new("nameAr", "name_required")); errors.Add(new("nameEn", "name_required")); }
    }

    private static string? Code(string? raw, Regex shape, string? existing, List<FieldError> errors)
    {
        if (existing is not null)
        {
            if (Clean(raw) is { } supplied && !string.Equals(supplied, existing, StringComparison.OrdinalIgnoreCase)) errors.Add(new("code", "immutable"));
            return existing;
        }
        var code = Clean(raw)?.ToUpperInvariant();
        if (code is null) errors.Add(new("code", "required"));
        else if (!shape.IsMatch(code)) { errors.Add(new("code", "invalid_format")); return null; }
        return code;
    }

    public static bool TryNormalizeUnit(UnitInput? input, UnitView? existing, out (string Code, string? NameAr, string? NameEn, int Decimals) data, out List<FieldError> errors)
    {
        errors = [];
        data = default;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var code = Code(input.Code, UnitOrGroupCode(), existing?.Code, errors);
        Names(input.NameAr, input.NameEn, 100, errors, out var ar, out var en);
        var decimals = input.Decimals;
        if (existing is not null) { if (decimals is { } d && d != existing.Decimals) errors.Add(new("decimals", "immutable")); decimals = existing.Decimals; }
        else if (decimals is null) errors.Add(new("decimals", "required"));
        else if (decimals is < 0 or > 6) errors.Add(new("decimals", "out_of_range"));
        if (errors.Count > 0) return false;
        data = (code!, ar, en, decimals!.Value);
        return true;
    }

    public static bool TryNormalizeGroup(ItemGroupInput? input, string? existingCode, out (string Code, string? NameAr, string? NameEn, Guid? ParentId, ValuationMethod? Valuation) data, out List<FieldError> errors)
    {
        errors = [];
        data = default;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var code = Code(input.Code, UnitOrGroupCode(), existingCode, errors);
        Names(input.NameAr, input.NameEn, 200, errors, out var ar, out var en);
        ValuationMethod? valuation = null;
        if (input.Valuation is { } v) { if (v is 1 or 2) valuation = (ValuationMethod)v; else errors.Add(new("valuation", "invalid_method")); }
        if (input.ParentId == Guid.Empty) errors.Add(new("parentId", "invalid"));
        if (errors.Count > 0) return false;
        data = (code!, ar, en, input.ParentId, valuation);
        return true;
    }

    public sealed record ItemData(string? Code, string? NameAr, string? NameEn, ItemType Type, Guid GroupId, Guid BaseUnitId, Guid? PurchaseUnitId, Guid? SalesUnitId,
        bool IsLot, bool IsSerial, bool HasExpiry, string? Notes, IReadOnlyList<(Guid UnitId, decimal Factor)> Alternates);

    /// <summary>Shape validation of an item. The code may be null on creation (it is then generated); on update the stored code/type/base unit cannot change.</summary>
    public static bool TryNormalizeItem(ItemInput? input, ItemView? existing, out ItemData data, out List<FieldError> errors)
    {
        errors = [];
        data = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        string? code;
        if (existing is not null) code = Code(input.Code, ItemCode(), existing.Code, errors);
        else if (Clean(input.Code) is null) code = null;                                       // generated by the service
        else code = Code(input.Code, ItemCode(), null, errors);
        Names(input.NameAr, input.NameEn, 200, errors, out var ar, out var en);
        ItemType type = default;
        if (existing is not null) { if (input.ItemType is { } t && t != existing.ItemType) errors.Add(new("itemType", "immutable")); type = (ItemType)existing.ItemType; }
        else if (input.ItemType is 1 or 2) type = (ItemType)input.ItemType.Value;
        else errors.Add(new("itemType", input.ItemType is null ? "required" : "invalid"));
        if (input.GroupId is null || input.GroupId == Guid.Empty) errors.Add(new("groupId", "required"));
        var baseUnit = input.BaseUnitId;
        if (existing is not null) { if (baseUnit is { } b && b != existing.BaseUnitId) errors.Add(new("baseUnitId", "immutable")); baseUnit = existing.BaseUnitId; }
        else if (baseUnit is null || baseUnit == Guid.Empty) errors.Add(new("baseUnitId", "required"));
        var notes = Text(input.Notes, "notes", 1000, errors);
        bool lot = input.IsLot ?? false, serial = input.IsSerial ?? false, expiry = input.HasExpiry ?? false;
        if (type == ItemType.Service && (lot || serial || expiry)) errors.Add(new("itemType", "service_cannot_be_tracked"));
        var alternates = new List<(Guid, decimal)>();
        var supplied = input.AlternateUnits ?? [];
        if (supplied.Count > MaxAlternateUnits) errors.Add(new("alternateUnits", "too_many"));
        else
        {
            var seen = new HashSet<Guid>();
            for (var i = 0; i < supplied.Count; i++)
            {
                var u = supplied[i];
                var field = $"alternateUnits[{i}]";
                if (u.UnitId is null || u.UnitId == Guid.Empty) { errors.Add(new(field + ".unitId", "required")); continue; }
                if (baseUnit is { } bu && u.UnitId == bu) errors.Add(new(field + ".unitId", "equals_base_unit"));
                else if (!seen.Add(u.UnitId.Value)) errors.Add(new(field + ".unitId", "duplicate"));
                if (u.Factor is null) errors.Add(new(field + ".factor", "required"));
                else if (!IsValidFactor(u.Factor.Value)) errors.Add(new(field + ".factor", "invalid_factor"));
                else alternates.Add((u.UnitId.Value, u.Factor.Value));
            }
        }
        if (errors.Count > 0) return false;
        data = new ItemData(code, ar, en, type, input.GroupId!.Value, baseUnit!.Value, input.PurchaseUnitId, input.SalesUnitId, lot, serial, expiry, notes, alternates);
        foreach (var u in new[] { (nameof(input.PurchaseUnitId), input.PurchaseUnitId), (nameof(input.SalesUnitId), input.SalesUnitId) })
            if (u.Item2 is { } id && id != data.BaseUnitId && alternates.All(a => a.Item1 != id)) errors.Add(new(char.ToLowerInvariant(u.Item1[0]) + u.Item1[1..], "unit_not_defined_for_item"));
        return errors.Count == 0;
    }

    /// <summary>A conversion factor is exact: positive, at most 10 decimals and 18 integer digits (numeric(28,10)).</summary>
    public static bool IsValidFactor(decimal factor)
    {
        if (factor <= 0) return false;
        var scaled = factor * 10_000_000_000m;
        if (decimal.Truncate(scaled) != scaled) return false;
        return decimal.Truncate(factor).ToString(System.Globalization.CultureInfo.InvariantCulture).Length <= MaxFactorIntegerDigits;
    }

    // ---- barcodes

    /// <summary>
    /// Normalises a barcode (trim, upper case) and validates it: EAN-13 / EAN-8 / UPC-A must be all digits of the right length with a valid check digit; an internal barcode
    /// follows the stored shape (letters, digits and . _ / -, at most 50). When <paramref name="kind"/> is null it is detected: 13, 8 or 12 digits with a valid check digit
    /// are typed as such, anything else is an internal code.
    /// </summary>
    public static bool TryNormalizeBarcode(string? raw, int? kind, out string barcode, out BarcodeKind resolved, out List<FieldError> errors)
    {
        errors = [];
        barcode = "";
        resolved = BarcodeKind.Internal;
        var v = Clean(raw)?.ToUpperInvariant();
        if (v is null) { errors.Add(new("barcode", "required")); return false; }
        if (!BarcodeShape().IsMatch(v)) { errors.Add(new("barcode", "invalid_format")); return false; }
        if (kind is not null && kind is < 1 or > 4) { errors.Add(new("kind", "invalid")); return false; }
        if (kind is null)
        {
            resolved = Digits().IsMatch(v) ? v.Length switch { 13 when CheckDigitValid(v) => BarcodeKind.Ean13, 8 when CheckDigitValid(v) => BarcodeKind.Ean8, 12 when CheckDigitValid(v) => BarcodeKind.UpcA, _ => BarcodeKind.Internal } : BarcodeKind.Internal;
        }
        else
        {
            resolved = (BarcodeKind)kind.Value;
            var length = resolved switch { BarcodeKind.Ean13 => 13, BarcodeKind.Ean8 => 8, BarcodeKind.UpcA => 12, _ => 0 };
            if (length > 0)
            {
                if (!Digits().IsMatch(v) || v.Length != length) { errors.Add(new("barcode", "invalid_length_or_digits")); return false; }
                if (!CheckDigitValid(v)) { errors.Add(new("barcode", "invalid_check_digit")); return false; }
            }
        }
        barcode = v;
        return true;
    }

    /// <summary>GS1 check digit: from the right, the digit before the check digit has weight 3, then 1, 3, ... ; the total must be a multiple of 10.</summary>
    public static bool CheckDigitValid(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            sum += i == 0 ? d : d * (i % 2 == 1 ? 3 : 1);
        }
        return sum % 10 == 0;
    }

    // ---- valuation

    /// <summary>
    /// Resolves the valuation method of a group: the nearest group on the chain to the root with an explicit method, otherwise the company default; null when nobody decided.
    /// <paramref name="chain"/> is the group first, then its parents.
    /// </summary>
    public static ValuationMethod? ResolveValuation(IEnumerable<ValuationMethod?> chain, ValuationMethod? companyDefault)
    {
        foreach (var v in chain) if (v is not null) return v;
        return companyDefault;
    }

    public static string FormatGeneratedCode(long number) => "ITM-" + number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}
