using Hasm.Infrastructure.Inventory;

namespace Hasm.Infrastructure.Tests;

public class InventoryValidationTests
{
    private static readonly Guid Group = Guid.NewGuid(), Base = Guid.NewGuid(), Box = Guid.NewGuid(), Pack = Guid.NewGuid();

    private static ItemInput Item(Action<List<ItemUnitInput>>? units = null, int? type = 1, Guid? purchase = null, Guid? sales = null, bool lot = false, bool serial = false, bool expiry = false, string? code = null)
    {
        var alt = new List<ItemUnitInput>();
        units?.Invoke(alt);
        return new ItemInput(code, "صنف", "Item", type, Group, Base, purchase, sales, lot, serial, expiry, null, alt);
    }

    // ---- units
    [Fact]
    public void A_unit_needs_a_shaped_code_a_name_and_decimals_between_0_and_6()
    {
        Assert.True(InventoryValidation.TryNormalizeUnit(new UnitInput(" kg ", null, "Kilogram", 3), null, out var d, out _));
        Assert.Equal(("KG", (string?)null, (string?)"Kilogram", 3), d);
        foreach (var bad in new[] { new UnitInput("", "x", null, 0), new UnitInput("a b", "x", null, 0), new UnitInput("KG", null, null, 0), new UnitInput("KG", "x", null, 7), new UnitInput("KG", "x", null, -1), new UnitInput("KG", "x", null, null) })
            Assert.False(InventoryValidation.TryNormalizeUnit(bad, null, out _, out var errors) || errors.Count == 0);
    }

    [Fact]
    public void The_code_and_decimals_of_an_existing_unit_cannot_change()
    {
        var unit = new UnitView(Guid.NewGuid(), "KG", "كغ", null, 3, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(InventoryValidation.TryNormalizeUnit(new UnitInput("kg", "كيلوغرام", null, 3), unit, out var ok, out _));
        Assert.Equal("KG", ok.Code);
        Assert.False(InventoryValidation.TryNormalizeUnit(new UnitInput("KGS", "x", null, 3), unit, out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "code", Code: "immutable" });
        Assert.False(InventoryValidation.TryNormalizeUnit(new UnitInput("KG", "x", null, 2), unit, out _, out var e2));
        Assert.Contains(e2, e => e is { Field: "decimals", Code: "immutable" });
    }

    // ---- groups
    [Fact]
    public void A_group_valuation_is_fifo_moving_average_or_inherited_and_nothing_else()
    {
        Assert.True(InventoryValidation.TryNormalizeGroup(new ItemGroupInput("g1", "x", null, null, null), null, out var inherit, out _));
        Assert.Null(inherit.Valuation);
        Assert.True(InventoryValidation.TryNormalizeGroup(new ItemGroupInput("G1", "x", null, null, 2), null, out var avg, out _));
        Assert.Equal(ValuationMethod.MovingAverage, avg.Valuation);
        Assert.False(InventoryValidation.TryNormalizeGroup(new ItemGroupInput("G1", "x", null, null, 3), null, out _, out var errors));
        Assert.Contains(errors, e => e.Field == "valuation");
    }

    [Fact]
    public void The_valuation_method_resolves_from_the_nearest_group_then_the_company_default_and_is_unknown_when_nobody_decided()
    {
        Assert.Equal(ValuationMethod.Fifo, InventoryValidation.ResolveValuation([null, ValuationMethod.Fifo, ValuationMethod.MovingAverage], ValuationMethod.MovingAverage));
        Assert.Equal(ValuationMethod.MovingAverage, InventoryValidation.ResolveValuation([null, null], ValuationMethod.MovingAverage));
        Assert.Null(InventoryValidation.ResolveValuation([null, null], null));
    }

    // ---- items
    [Fact]
    public void An_item_without_a_code_gets_one_generated_and_the_generated_code_has_a_fixed_shape()
    {
        Assert.True(InventoryValidation.TryNormalizeItem(Item(), null, out var d, out _));
        Assert.Null(d.Code);
        Assert.Equal("ITM-000042", InventoryValidation.FormatGeneratedCode(42));
        Assert.Equal("ITM-1234567", InventoryValidation.FormatGeneratedCode(1234567));
    }

    [Fact]
    public void A_manual_item_code_is_upper_cased_and_shape_checked()
    {
        Assert.True(InventoryValidation.TryNormalizeItem(Item(code: " abc-1 "), null, out var d, out _));
        Assert.Equal("ABC-1", d.Code);
        Assert.False(InventoryValidation.TryNormalizeItem(Item(code: "a b"), null, out _, out var errors));
        Assert.Contains(errors, e => e is { Field: "code", Code: "invalid_format" });
    }

    [Fact]
    public void A_service_cannot_be_tracked_and_the_type_must_be_goods_or_service()
    {
        Assert.True(InventoryValidation.TryNormalizeItem(Item(type: 2), null, out _, out _));
        Assert.False(InventoryValidation.TryNormalizeItem(Item(type: 2, lot: true), null, out _, out var e1));
        Assert.Contains(e1, e => e.Code == "service_cannot_be_tracked");
        Assert.False(InventoryValidation.TryNormalizeItem(Item(type: 3), null, out _, out _));
        Assert.False(InventoryValidation.TryNormalizeItem(Item(type: null), null, out _, out _));
        Assert.True(InventoryValidation.TryNormalizeItem(Item(type: 1, lot: true, serial: true, expiry: true), null, out _, out _));   // tracking flags are only attributes in this phase
    }

    [Fact]
    public void Alternate_units_need_a_distinct_unit_other_than_the_base_unit_and_an_exact_positive_factor()
    {
        Assert.True(InventoryValidation.TryNormalizeItem(Item(u => { u.Add(new(Box, 12m)); u.Add(new(Pack, 0.5m)); }), null, out var d, out _));
        Assert.Equal([(Box, 12m), (Pack, 0.5m)], d.Alternates);
        Assert.False(InventoryValidation.TryNormalizeItem(Item(u => u.Add(new(Base, 2m))), null, out _, out var e1));
        Assert.Contains(e1, e => e.Code == "equals_base_unit");
        Assert.False(InventoryValidation.TryNormalizeItem(Item(u => { u.Add(new(Box, 2m)); u.Add(new(Box, 3m)); }), null, out _, out var e2));
        Assert.Contains(e2, e => e.Code == "duplicate");
        foreach (var bad in new decimal?[] { 0m, -1m, 0.00000000001m, 1234567890123456789m, null })
            Assert.False(InventoryValidation.TryNormalizeItem(Item(u => u.Add(new(Box, bad))), null, out _, out _));
    }

    [Fact]
    public void Conversion_factors_are_exact_decimals_with_at_most_ten_decimals_and_eighteen_integer_digits()
    {
        Assert.True(InventoryValidation.IsValidFactor(0.0000000001m));
        Assert.True(InventoryValidation.IsValidFactor(123456789012345678m));
        Assert.True(InventoryValidation.IsValidFactor(1m / 4m));
        Assert.False(InventoryValidation.IsValidFactor(1m / 3m));             // not exactly representable in 10 decimals
        Assert.False(InventoryValidation.IsValidFactor(0m));
    }

    [Fact]
    public void The_purchase_and_sales_units_must_be_the_base_unit_or_an_alternate_unit()
    {
        Assert.True(InventoryValidation.TryNormalizeItem(Item(u => u.Add(new(Box, 12m)), purchase: Box, sales: Base), null, out _, out _));
        Assert.False(InventoryValidation.TryNormalizeItem(Item(u => u.Add(new(Box, 12m)), purchase: Pack), null, out _, out var errors));
        Assert.Contains(errors, e => e is { Field: "purchaseUnitId", Code: "unit_not_defined_for_item" });
    }

    [Fact]
    public void The_code_type_and_base_unit_of_an_existing_item_cannot_change()
    {
        var existing = new ItemView(Guid.NewGuid(), "ITM-000001", "صنف", "Item", 1, Group, Base, null, null, false, false, false, null, true, 1, 1, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(InventoryValidation.TryNormalizeItem(Item(code: "itm-000001"), existing, out var d, out _));
        Assert.Equal("ITM-000001", d.Code);
        Assert.False(InventoryValidation.TryNormalizeItem(Item(code: "OTHER"), existing, out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "code", Code: "immutable" });
        Assert.False(InventoryValidation.TryNormalizeItem(Item(type: 2), existing, out _, out var e2));
        Assert.Contains(e2, e => e is { Field: "itemType", Code: "immutable" });
        var other = Item() with { BaseUnitId = Box };
        Assert.False(InventoryValidation.TryNormalizeItem(other, existing, out _, out var e3));
        Assert.Contains(e3, e => e is { Field: "baseUnitId", Code: "immutable" });
    }

    [Fact]
    public void An_item_needs_a_name_a_group_and_a_base_unit()
    {
        Assert.False(InventoryValidation.TryNormalizeItem(Item() with { NameAr = null, NameEn = " " }, null, out _, out var e1));
        Assert.Contains(e1, e => e.Code == "name_required");
        Assert.False(InventoryValidation.TryNormalizeItem(Item() with { GroupId = null }, null, out _, out var e2));
        Assert.Contains(e2, e => e is { Field: "groupId", Code: "required" });
        Assert.False(InventoryValidation.TryNormalizeItem(Item() with { BaseUnitId = null }, null, out _, out var e3));
        Assert.Contains(e3, e => e is { Field: "baseUnitId", Code: "required" });
    }

    // ---- barcodes
    [Theory]
    [InlineData("4006381333931", true)]    // EAN-13 (valid GS1 example)
    [InlineData("4006381333932", false)]
    [InlineData("96385074", true)]         // EAN-8
    [InlineData("96385075", false)]
    [InlineData("036000291452", true)]     // UPC-A
    [InlineData("036000291453", false)]
    public void The_gs1_check_digit_is_verified(string digits, bool valid) => Assert.Equal(valid, InventoryValidation.CheckDigitValid(digits));

    [Fact]
    public void A_barcode_is_normalised_and_its_kind_detected_from_length_and_check_digit()
    {
        Assert.True(InventoryValidation.TryNormalizeBarcode(" 4006381333931 ", null, out var b, out var kind, out _));
        Assert.Equal(("4006381333931", BarcodeKind.Ean13), (b, kind));
        Assert.True(InventoryValidation.TryNormalizeBarcode("96385074", null, out _, out var k8, out _));
        Assert.Equal(BarcodeKind.Ean8, k8);
        Assert.True(InventoryValidation.TryNormalizeBarcode("036000291452", null, out _, out var kU, out _));
        Assert.Equal(BarcodeKind.UpcA, kU);
        Assert.True(InventoryValidation.TryNormalizeBarcode("abc-123/x", null, out var internalCode, out var kI, out _));
        Assert.Equal(("ABC-123/X", BarcodeKind.Internal), (internalCode, kI));
        Assert.True(InventoryValidation.TryNormalizeBarcode("4006381333932", null, out _, out var kBadDigit, out _));   // 13 digits with a wrong check digit is only an internal code when the kind is not declared
        Assert.Equal(BarcodeKind.Internal, kBadDigit);
    }

    [Fact]
    public void A_declared_gs1_kind_must_match_length_digits_and_check_digit()
    {
        Assert.False(InventoryValidation.TryNormalizeBarcode("4006381333932", (int)BarcodeKind.Ean13, out _, out _, out var e1));
        Assert.Contains(e1, e => e.Code == "invalid_check_digit");
        Assert.False(InventoryValidation.TryNormalizeBarcode("12345", (int)BarcodeKind.Ean8, out _, out _, out var e2));
        Assert.Contains(e2, e => e.Code == "invalid_length_or_digits");
        Assert.False(InventoryValidation.TryNormalizeBarcode("ABCDEFGH", (int)BarcodeKind.Ean8, out _, out _, out _));
        Assert.False(InventoryValidation.TryNormalizeBarcode("X", 9, out _, out _, out var e3));
        Assert.Contains(e3, e => e.Field == "kind");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("semi;colon")]
    public void A_barcode_with_a_bad_shape_is_refused(string? raw) => Assert.False(InventoryValidation.TryNormalizeBarcode(raw, null, out _, out _, out _));

    [Fact]
    public void A_barcode_longer_than_fifty_characters_is_refused() => Assert.False(InventoryValidation.TryNormalizeBarcode(new string('A', 51), null, out _, out _, out _));
}
