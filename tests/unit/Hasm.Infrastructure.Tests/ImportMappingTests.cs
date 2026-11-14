using System.Text.Json.Nodes;
using Hasm.Infrastructure.Imports;

namespace Hasm.Infrastructure.Tests;

/// <summary>Mapping validation, suggestion, canonical JSON and the type-driven, culture-independent normalization of cells.</summary>
public class ImportMappingTests
{
    private static readonly ImportField[] Fields =
    [
        new("code", "الرمز", "Code", ImportFieldType.Code, Required: true, MaxLength: 10, IsKey: true, UpperCase: true, Aliases: ["account no", "رقم الحساب"]),
        new("name", "الاسم", "Name", ImportFieldType.Text, Required: true, MaxLength: 20),
        new("qty", "الكمية", "Quantity", ImportFieldType.Integer),
        new("price", "السعر", "Price", ImportFieldType.Decimal),
        new("start", "التاريخ", "Start date", ImportFieldType.Date),
        new("active", "فعال", "Active", ImportFieldType.Boolean),
        new("kind", "النوع", "Kind", ImportFieldType.Enum, EnumValues: new Dictionary<string, string[]> { ["goods"] = ["بضاعة", "Goods item"], ["service"] = ["خدمة"] }),
    ];

    private static ImportField F(string key) => Fields.Single(f => f.Key == key);
    private static (string? V, string? Code) N(string key, string? raw, MappingOptions? o = null) { var (v, i) = ImportNormalizer.Normalize(F(key), raw, o ?? new MappingOptions()); return (v, i?.Code); }

    [Fact]
    public void Text_and_codes_are_trimmed_nfc_normalized_arabic_digits_converted_uppercased_and_length_checked()
    {
        Assert.Equal(("خدمة", null), N("name", "  خدمة  "));
        Assert.Equal(("é", null), N("name", "é"));                                                 // combining accent composed (NFC)
        Assert.Equal((null, "required"), N("name", "   "));
        Assert.Equal((null, "required"), N("code", null));
        Assert.Equal(("0101", null), N("code", " ٠١٠١ "));                                              // Arabic-Indic digits, leading zero kept
        Assert.Equal(("0101", null), N("code", "۰۱۰۱"));                                                 // Persian digits
        Assert.Equal(("AB-1", null), N("code", "ab-1"));
        Assert.Equal((null, "too_long"), N("code", "12345678901"));
        Assert.Equal((null, "too_long"), N("name", new string('x', 21)));
        Assert.Equal((null, null), N("qty", ""));                                                         // optional empty is simply empty
    }

    [Fact]
    public void Numbers_follow_the_mapping_options_never_the_machine_locale_and_dates_need_an_exact_format()
    {
        Assert.Equal(("1250", null), N("qty", "1,250"));
        Assert.Equal(("1250", null), N("qty", "١٬٢٥٠"));
        Assert.Equal(("-7", null), N("qty", "-7"));
        Assert.Equal(("1250", null), N("qty", "1 250"));
        Assert.Equal((null, "invalid_integer"), N("qty", "12.5"));
        Assert.Equal((null, "invalid_integer"), N("qty", "1e3"));
        Assert.Equal(("1250.5", null), N("price", "1,250.50"));
        Assert.Equal(("1250.5", null), N("price", "١٬٢٥٠٫٥٠"));
        Assert.Equal(("1250.5", null), N("price", "1.250,50", new MappingOptions(",", ".")));        // European style: decided by the option
        Assert.Equal(("1250.5", null), N("price", "1'250,50", new MappingOptions(",", "'")));
        Assert.Equal(("100", null), N("price", "100.000"));                                               // canonical: trailing zeros dropped
        Assert.Equal(("0.001", null), N("price", "0.001"));
        Assert.Equal((null, "invalid_decimal"), N("price", "1.2.3"));
        Assert.Equal((null, "invalid_decimal"), N("price", "12abc"));
        Assert.Equal((null, "invalid_decimal"), N("price", "1,250.50", new MappingOptions(",", ".")));    // a comma decimal with a dot thousands: this value has two decimal points
        Assert.Equal(("2026-03-09", null), N("start", "2026-03-09"));
        Assert.Equal(("2026-03-09", null), N("start", "2026-03-09T00:00:00"));                           // a workbook date cell
        Assert.Equal((null, "invalid_date"), N("start", "2026-03-09T14:30:00"));
        Assert.Equal(("2026-03-09", null), N("start", "09/03/2026", new MappingOptions(DateFormat: "dd/MM/yyyy")));
        Assert.Equal(("2026-09-03", null), N("start", "09/03/2026", new MappingOptions(DateFormat: "MM/dd/yyyy")));    // the same text means another day under another option
        Assert.Equal(("2026-03-09", null), N("start", "٠٩/٠٣/٢٠٢٦", new MappingOptions(DateFormat: "dd/MM/yyyy")));
        Assert.Equal((null, "invalid_date"), N("start", "31/02/2026", new MappingOptions(DateFormat: "dd/MM/yyyy")));
        Assert.Equal((null, "invalid_date"), N("start", "9/3/26", new MappingOptions(DateFormat: "dd/MM/yyyy")));
    }

    [Fact]
    public void Booleans_and_enums_accept_declared_aliases_in_any_language_and_nothing_else()
    {
        foreach (var t in new[] { "true", "TRUE", "yes", "1", "نعم", "صح" }) Assert.Equal(("true", null), N("active", t));
        foreach (var f in new[] { "false", "No", "0", "لا", "خطأ" }) Assert.Equal(("false", null), N("active", f));
        Assert.Equal((null, "invalid_boolean"), N("active", "maybe"));
        Assert.Equal(("goods", null), N("kind", "GOODS"));
        Assert.Equal(("goods", null), N("kind", "بضاعة"));
        Assert.Equal(("goods", null), N("kind", " goods item "));
        Assert.Equal(("service", null), N("kind", "خدمة"));
        Assert.Equal((null, "invalid_value"), N("kind", "other"));
    }

    [Fact]
    public void A_row_is_normalized_through_the_mapping_with_defaults_for_empty_cells_and_collected_issues()
    {
        var (mapping, errors) = ImportMapping.Parse(JsonNode.Parse("""{"fields":{"code":{"column":0},"name":{"column":1},"qty":{"column":2,"default":"1"},"kind":{"default":"service"}}}"""), Fields, 3);
        Assert.Empty(errors);
        var (values, issues) = ImportNormalizer.NormalizeRow(Fields, mapping!, ["a-1", "Pen", null]);
        Assert.Equal(("A-1", "Pen", "1", "service", null, null), (values["code"], values["name"], values["qty"], values["kind"], values["price"], values["start"]));
        Assert.Empty(issues);
        var (_, bad) = ImportNormalizer.NormalizeRow(Fields, mapping!, [null, "", "x"]);
        Assert.Equal([("code", "required"), ("name", "required"), ("qty", "invalid_integer")], bad.Select(i => (i.Field, i.Code)));
    }

    [Fact]
    public void A_mapping_is_validated_against_the_fields_and_the_column_count_and_round_trips_canonically()
    {
        (ImportMapping? M, string[] E) P(string json, int cols = 3) { var (m, e) = ImportMapping.Parse(JsonNode.Parse(json), Fields, cols); return (m, e.Select(x => $"{x.Field}:{x.Code}").ToArray()); }
        Assert.Equal(["mapping:required"], ImportMapping.Parse(null, Fields, 3).Errors.Select(x => $"{x.Field}:{x.Code}"));
        Assert.Contains("mapping.fields.code:required_field_unmapped", P("""{"fields":{"name":{"column":1}}}""").E);
        Assert.Contains("mapping.fields.name:required_field_unmapped", P("""{"fields":{"code":{"column":0}}}""").E);
        Assert.Contains("mapping.fields.nope:unknown_field", P("""{"fields":{"code":{"column":0},"name":{"column":1},"nope":{"column":2}}}""").E);
        Assert.Contains("mapping.fields.code.column:invalid_column", P("""{"fields":{"code":{"column":7},"name":{"column":1}}}""").E);
        Assert.Contains("mapping.fields.code.column:invalid_column", P("""{"fields":{"code":{"column":-1},"name":{"column":1}}}""").E);
        Assert.Contains("mapping.fields.name.column:duplicate_column", P("""{"fields":{"code":{"column":0},"name":{"column":0}}}""").E);
        Assert.Contains("mapping.options.decimalSeparator:invalid", P("""{"fields":{"code":{"column":0},"name":{"column":1}},"options":{"decimalSeparator":";"}}""").E);
        Assert.Contains("mapping.options.dateFormat:invalid", P("""{"fields":{"code":{"column":0},"name":{"column":1}},"options":{"dateFormat":"yy-M-d"}}""").E);
        Assert.Contains("mapping.options.thousandsSeparator:same_as_decimal", P("""{"fields":{"code":{"column":0},"name":{"column":1}},"options":{"decimalSeparator":",","thousandsSeparator":","}}""").E);
        var ok = P("""{"fields":{"name":{"column":1},"code":{"column":0},"qty":{"default":"5"}},"options":{"decimalSeparator":","}}""");
        Assert.Empty(ok.E);
        var json = ok.M!.ToJson();
        Assert.Equal(ImportNormalizer.CanonicalJson(json), ImportNormalizer.CanonicalJson(ImportMapping.Parse(json, Fields, 3).Mapping!.ToJson()));    // stable round trip
        Assert.Equal("""{"fields":{"code":{"column":0,"default":null},"name":{"column":1,"default":null},"qty":{"column":null,"default":"5"}},"options":{"dateFormat":"yyyy-MM-dd","decimalSeparator":",","thousandsSeparator":"."}}""", ImportNormalizer.CanonicalJson(json));
    }

    [Fact]
    public void Suggestions_match_headers_to_field_keys_names_and_aliases_in_either_language_using_each_column_once()
    {
        var s = ImportMapping.Suggest(["رقم الحساب", "Name", "الكمية", "unrelated", "Start_Date", "KIND"], Fields);
        var f = s["fields"]!.AsObject();
        Assert.Equal(0, f["code"]!["column"]!.GetValue<int>());                                           // an alias
        Assert.Equal(1, f["name"]!["column"]!.GetValue<int>());
        Assert.Equal(2, f["qty"]!["column"]!.GetValue<int>());                                            // Arabic name
        Assert.Equal(4, f["start"]!["column"]!.GetValue<int>());                                          // "Start date" vs "Start_Date"
        Assert.Equal(5, f["kind"]!["column"]!.GetValue<int>());
        Assert.False(f.ContainsKey("price"));
        var dup = ImportMapping.Suggest(["name", "name"], Fields)["fields"]!.AsObject();
        Assert.Equal(0, dup["name"]!["column"]!.GetValue<int>());                                         // one column, one field
    }

    [Fact]
    public void Canonical_json_orders_properties_and_is_stable_for_nested_values()
    {
        var a = JsonNode.Parse("""{"b":[{"y":1,"x":2}],"a":"é"}""");
        var b = JsonNode.Parse("""{"a":"é","b":[{"x":2,"y":1}]}""");
        Assert.Equal(ImportNormalizer.CanonicalJson(a), ImportNormalizer.CanonicalJson(b));
        var backslash = ((char)92).ToString();
        Assert.Equal("{\"a\":\"" + backslash + "u00E9\",\"b\":[{\"x\":2,\"y\":1}]}", ImportNormalizer.CanonicalJson(a));    // the default encoder writes non-ASCII as an escape: stable and ASCII-safe
    }
}
