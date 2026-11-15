using Hasm.Infrastructure.Inventory;
using Hasm.Infrastructure.Ledger;

namespace Hasm.Infrastructure.Tests;

public class StockDocumentValidationTests
{
    private static readonly Guid Main = Guid.NewGuid(), Other = Guid.NewGuid(), Item = Guid.NewGuid(), Unit = Guid.NewGuid();
    private static StockLineInput Line(decimal? q = 1m) => new(Item, Unit, q, null);
    private static StockDocumentInput Doc(int? type, Guid? source, Guid? target, string? date = "2026-03-01", params StockLineInput[] lines) => new(type, source, target, date, " ref ", null, lines.Length == 0 ? [Line()] : lines);

    [Fact]
    public void The_warehouses_must_fit_the_document_type()
    {
        Assert.True(StockDocumentValidation.TryNormalize(Doc(1, null, Main), null, out var r, out _, out _));
        Assert.Equal((StockDocType.Receipt, (Guid?)null, (Guid?)Main, "ref"), (r.Type, r.Source, r.Target, r.Reference));
        Assert.True(StockDocumentValidation.TryNormalize(Doc(2, Main, null), null, out _, out _, out _));
        Assert.True(StockDocumentValidation.TryNormalize(Doc(3, Main, Other), null, out _, out _, out _));
        foreach (var (input, field, code) in new (StockDocumentInput, string, string)[]
        {
            (Doc(1, null, null), "targetWarehouseId", "required"), (Doc(1, Main, Main), "sourceWarehouseId", "not_allowed"), (Doc(2, null, null), "sourceWarehouseId", "required"),
            (Doc(2, Main, Other), "targetWarehouseId", "not_allowed"), (Doc(3, Main, null), "targetWarehouseId", "required"), (Doc(3, Main, Main), "targetWarehouseId", "same_warehouse"),
            (Doc(null, null, Main), "docType", "required"), (Doc(9, null, Main), "docType", "invalid"),
        })
        {
            Assert.False(StockDocumentValidation.TryNormalize(input, null, out _, out _, out var errors));
            Assert.Contains(errors, e => e.Field == field && e.Code == code);
        }
    }

    [Fact]
    public void The_type_of_an_existing_document_cannot_change()
    {
        Assert.True(StockDocumentValidation.TryNormalize(Doc(null, null, Main), StockDocType.Receipt, out _, out _, out _));
        Assert.False(StockDocumentValidation.TryNormalize(Doc(2, Main, null), StockDocType.Receipt, out _, out _, out var errors));
        Assert.Contains(errors, e => e is { Field: "docType", Code: "immutable" });
    }

    [Fact]
    public void The_date_and_lines_are_required_and_quantities_are_exact_positive_decimals()
    {
        Assert.False(StockDocumentValidation.TryNormalize(Doc(1, null, Main, null), null, out _, out _, out var e1));
        Assert.Contains(e1, e => e is { Field: "documentDate", Code: "required" });
        Assert.False(StockDocumentValidation.TryNormalize(Doc(1, null, Main, "01/03/2026"), null, out _, out _, out var e2));
        Assert.Contains(e2, e => e.Code == "invalid_date");
        Assert.False(StockDocumentValidation.TryNormalize(new StockDocumentInput(1, null, Main, "2026-03-01", null, null, []), null, out _, out _, out var e3));
        Assert.Contains(e3, e => e is { Field: "lines", Code: "required" });
        foreach (var bad in new decimal?[] { 0m, -1m, 0.00000000001m, 1234567890123456789m, null })
            Assert.False(StockDocumentValidation.TryNormalize(Doc(1, null, Main, "2026-03-01", Line(bad)), null, out _, out _, out _));
        Assert.True(StockDocumentValidation.TryNormalize(Doc(1, null, Main, "2026-03-01", Line(0.0000000001m), Line(123456789012345678m)), null, out _, out var lines, out _));
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void A_document_is_limited_to_five_hundred_lines()
    {
        var many = Enumerable.Range(0, 501).Select(_ => Line()).ToArray();
        Assert.False(StockDocumentValidation.TryNormalize(Doc(1, null, Main, "2026-03-01", many), null, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Code == "too_many");
        Assert.True(StockDocumentValidation.TryNormalize(Doc(1, null, Main, "2026-03-01", many.Take(500).ToArray()), null, out _, out _, out _));
    }

    [Theory]
    [InlineData("12", "12.5", 3, 0, "150", null)]                      // 12 boxes x 12.5 = 150 pieces
    [InlineData("0.5", "12", 3, 0, "6", null)]                         // half a box of 12 = 6 pieces
    [InlineData("0.25", "10", 3, 0, null, "base_quantity_precision_exceeded")]   // 2.5 pieces are not representable in a unit with 0 decimals: refused, never rounded
    [InlineData("1.2345", "1", 3, 3, null, "quantity_precision_exceeded")]     // more decimals than the entered unit allows
    [InlineData("1.5", "0.001", 3, 3, null, "base_quantity_precision_exceeded")]
    [InlineData("3", "0.001", 3, 3, "0.003", null)]
    public void Unit_conversion_is_exact_and_refuses_what_it_cannot_represent(string quantity, string factor, int enteredDecimals, int baseDecimals, string? expected, string? error)
    {
        var (b, e) = StockDocumentValidation.Convert(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture), enteredDecimals, baseDecimals);
        Assert.Equal(error, e);
        if (expected is not null) Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), b);
    }

    [Fact]
    public void Conversion_never_uses_floating_point_so_the_classic_binary_fraction_cases_stay_exact()
    {
        var (b, e) = StockDocumentValidation.Convert(0.1m, 3m, 1, 1);
        Assert.Null(e);
        Assert.Equal(0.3m, b);                                           // 0.1 x 3 is exactly 0.3 (not 0.30000000000000004)
        var (sum, _) = StockDocumentValidation.Convert(1234567.891m, 1000m, 3, 3);
        Assert.Equal(1234567891m, sum);
    }

    [Fact]
    public void The_stock_document_numbering_types_are_registered_with_their_prefixes_and_stay_out_of_the_approval_policy_engine()
    {
        Assert.Equal(["journal_entry", "stock_receipt", "stock_issue", "stock_transfer"], DocumentTypes.All);
        Assert.Equal(["JV", "SR", "SI", "ST"], DocumentTypes.All.Select(DocumentTypes.DefaultPrefix));
        Assert.True(DocumentTypes.UsesApprovalPolicies(DocumentTypes.JournalEntry));
        Assert.All(new[] { DocumentTypes.StockReceipt, DocumentTypes.StockIssue, DocumentTypes.StockTransfer }, d => Assert.False(DocumentTypes.UsesApprovalPolicies(d)));
        Assert.Equal("stock_transfer", StockDocumentService.DocTypeOf(StockDocType.Transfer));
        Assert.Equal(1140, StockAuditEvents.Of(StockEventKind.Created));
        Assert.Equal(1146, StockAuditEvents.Of(StockEventKind.Cancelled));
    }
}
