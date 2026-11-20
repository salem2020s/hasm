using System.Text.Json.Nodes;
using Hasm.Infrastructure.Imports;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.9C slice 1: the pure part of reusable mapping templates - header-based content, validation against the adapter fields and application to staged headers.</summary>
public class ImportTemplateContentTests
{
    private static readonly IReadOnlyList<ImportField> Fields =
    [
        new("code", "الرمز", "Code", ImportFieldType.Code, Required: true),
        new("name", "الاسم", "Name", ImportFieldType.Text, Required: true),
        new("qty", "الكمية", "Quantity", ImportFieldType.Integer),
    ];
    private static ImportMapping Mapping(params (string Key, int? Column, string? Default)[] f) =>
        new(f.ToDictionary(x => x.Key, x => new FieldMap(x.Column, x.Default)), new MappingOptions(",", ".", "dd/MM/yyyy"));

    [Fact]
    public void Content_stores_headers_defaults_and_options_never_positions()
    {
        var (content, errors) = ImportTemplateContent.FromMapping(Mapping(("code", 1, null), ("name", 0, null), ("qty", null, "1")), ["Name", " Code "]);
        Assert.Empty(errors);
        Assert.Equal("Code", (string)content!["fields"]!["code"]!["header"]!);                                    // trimmed header text
        Assert.Equal("1", (string)content["fields"]!["qty"]!["default"]!);
        Assert.Null(content["fields"]!["qty"]!["header"]);
        Assert.Equal(",", (string)content["options"]!["decimalSeparator"]!);
        Assert.Empty(ImportTemplateContent.Validate(content, Fields));
    }

    [Fact]
    public void An_unusable_or_ambiguous_header_or_an_empty_mapping_cannot_become_a_template()
    {
        Assert.Contains(ImportTemplateContent.FromMapping(Mapping(("code", 0, null)), ["   "]).Errors, e => e.Code == "header_unusable");
        Assert.Contains(ImportTemplateContent.FromMapping(Mapping(("code", 0, null)), ["Code", "code"]).Errors, e => e.Code == "header_ambiguous");         // equal after normalization
        Assert.Contains(ImportTemplateContent.FromMapping(Mapping(("code", 0, null)), ["Co_de", "co de"]).Errors, e => e.Code == "header_ambiguous");
        Assert.Contains(ImportTemplateContent.FromMapping(Mapping(("code", 0, null)), [new string('x', 201)]).Errors, e => e.Code == "header_unusable");
        Assert.Contains(ImportTemplateContent.FromMapping(Mapping(), ["a"]).Errors, e => e.Code == "required");
    }

    [Fact]
    public void Stored_content_is_validated_against_the_adapter_before_it_is_ever_applied()
    {
        JsonObject Make(string json) => (JsonObject)JsonNode.Parse(json)!;
        Assert.Empty(ImportTemplateContent.Validate(Make("{\"fields\":{\"code\":{\"header\":\"Code\"}},\"options\":{\"decimalSeparator\":\",\"}}"), Fields));
        Assert.Contains(ImportTemplateContent.Validate(Make("{\"fields\":{\"nope\":{\"header\":\"x\"}}}"), Fields), e => e.Code == "unknown_field");
        Assert.Contains(ImportTemplateContent.Validate(Make("{\"fields\":{\"code\":{\"header\":\"\"}}}"), Fields), e => e.Code == "invalid");
        Assert.Contains(ImportTemplateContent.Validate(Make("{\"fields\":{\"code\":{\"header\":5}}}"), Fields), e => e.Code == "invalid");
        Assert.Contains(ImportTemplateContent.Validate(Make("{\"fields\":{\"code\":\"x\"}}"), Fields), e => e.Code == "invalid");
        Assert.Contains(ImportTemplateContent.Validate(Make("{\"fields\":{}}"), Fields), e => e.Code == "invalid");
        Assert.Contains(ImportTemplateContent.Validate(Make("{\"fields\":{\"code\":{\"header\":\"c\"}},\"options\":{\"dateFormat\":\"xx\"}}"), Fields), e => e.Field.Contains("dateFormat"));
        Assert.Contains(ImportTemplateContent.Validate(null, Fields), e => e.Code == "invalid");
    }

    [Fact]
    public void Apply_matches_headers_after_normalization_and_reports_every_field_it_could_not_place()
    {
        var (content, _) = ImportTemplateContent.FromMapping(Mapping(("code", 0, null), ("name", 1, null), ("qty", null, "9")), ["Item Code", "Item_Name"]);
        // reordered, different case, underscore/space variants, Arabic-Indic digits are normalized like the suggestion does
        var (m, unmatched) = ImportTemplateContent.Apply(content!, ["qty", "item-name", "ITEM  code"]);
        Assert.Empty(unmatched);
        Assert.Equal((2, 1), (m["fields"]!["code"]!["column"]!.GetValue<int>(), m["fields"]!["name"]!["column"]!.GetValue<int>()));
        Assert.Equal("9", (string)m["fields"]!["qty"]!["default"]!);
        // a missing header: the field stays unmapped (default kept) and is reported
        var (m2, un2) = ImportTemplateContent.Apply(content!, ["Item Code", "label"]);
        Assert.Equal(["name"], un2);
        Assert.Null(m2["fields"]!["name"]);
        Assert.Equal(0, m2["fields"]!["code"]!["column"]!.GetValue<int>());
        // an ambiguous header in the new file is not guessed
        var (m3, un3) = ImportTemplateContent.Apply(content!, ["Item Code", "item code", "Item Name"]);
        Assert.Equal(["code"], un3);
        Assert.Null(m3["fields"]!["code"]);
        // two template fields never share one column
        var shared = (JsonObject)JsonNode.Parse("{\"fields\":{\"code\":{\"header\":\"x\"},\"name\":{\"header\":\"X\"}}}")!;
        var (m4, un4) = ImportTemplateContent.Apply(shared, ["x"]);
        Assert.Single(un4);
        Assert.Equal(1, ((JsonObject)m4["fields"]!).Count);
        // the produced mapping is accepted by the ordinary parser only when it is itself valid
        var (parsed, errors) = ImportMapping.Parse(m, Fields, 3);
        Assert.NotNull(parsed); Assert.Empty(errors);
        Assert.NotEmpty(ImportMapping.Parse(m2, Fields, 2).Errors);                                              // required "name" unmapped: the ordinary validation refuses it
    }

    [Fact]
    public void A_default_longer_than_the_stored_limit_cannot_be_saved_so_it_can_never_make_a_template_unusable()
    {
        Assert.Contains(ImportTemplateContent.FromMapping(Mapping(("code", 0, new string('x', 401))), ["Code"]).Errors, e => e.Code == "default_too_long");
        var (ok, errors) = ImportTemplateContent.FromMapping(Mapping(("code", 0, new string('x', 400))), ["Code"]);
        Assert.Empty(errors);
        Assert.Empty(ImportTemplateContent.Validate(ok, Fields));
    }
}
