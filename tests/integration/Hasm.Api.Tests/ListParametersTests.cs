using System.Text.Json;
using Hasm.Api.Company;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Api.Tests;

/// <summary>The text-bound query parameters of the master-data list endpoints: a malformed value names its parameter, nothing else changes (no database needed).</summary>
public class ListParametersTests
{
    [Fact]
    public void Valid_or_absent_values_pass_and_an_empty_offset_or_limit_counts_as_absent()
    {
        Assert.Null(ListParameters.TryParse(null, null, null, out var none));
        Assert.False(none.Requested);
        Assert.Null(ListParameters.TryParse(null, "", " ", out var blank));
        Assert.False(blank.Requested);                                               // exactly what the typed binding did
        Assert.Null(ListParameters.TryParse("abc", "0", "500", out var all));
        Assert.Equal(("abc", 0, 500, true), (all.Search, all.Offset, all.Limit, all.Requested));
        Assert.Null(ListParameters.TryParse("", null, null, out var emptySearch));
        Assert.True(emptySearch.Requested);                                          // an explicit empty search selects the paged form, as before
    }

    [Theory]
    [InlineData(null, "abc", null, "offset")]
    [InlineData(null, "-1", null, "offset")]
    [InlineData(null, "1.5", null, "offset")]
    [InlineData(null, "99999999999", null, "offset")]
    [InlineData(null, null, "abc", "limit")]
    [InlineData(null, null, "0", "limit")]
    [InlineData(null, null, "-3", "limit")]
    [InlineData(null, null, "1e3", "limit")]
    [InlineData("a\nb", null, null, "search")]
    public void A_malformed_value_names_the_first_offending_parameter(string? search, string? offset, string? limit, string field)
        => Assert.Equal(field, ListParameters.TryParse(search, offset, limit, out _));

    [Fact]
    public void An_over_long_search_names_search_and_the_first_bad_parameter_wins()
    {
        Assert.Equal("search", ListParameters.TryParse(new string('x', 101), null, null, out _));
        Assert.Equal("search", ListParameters.TryParse(new string('x', 101), "bad", "bad", out _));
        Assert.Equal("offset", ListParameters.TryParse("ok", "bad", "bad", out _));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("1", (short)1)]
    [InlineData("2", (short)2)]
    public void The_kind_is_absent_customer_or_supplier(string? raw, short? expected)
    {
        Assert.Null(ListParameters.TryParseKind(raw, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("x")]
    [InlineData("1.0")]
    [InlineData("99999")]
    public void Any_other_kind_is_refused_by_name(string raw) => Assert.Equal("kind", ListParameters.TryParseKind(raw, out _));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("customer", (short)1)]
    [InlineData("Customer", (short)1)]
    [InlineData(" SUPPLIER ", (short)2)]
    public void The_without_role_filter_is_absent_customer_or_supplier_in_any_case(string? raw, short? expected)
    {
        Assert.Null(ListParameters.TryParseWithoutRole(raw, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("1")]
    [InlineData("customers")]
    [InlineData("cust omer")]
    [InlineData("x'; DROP TABLE t; --")]
    public void Any_other_without_role_value_is_refused_by_name(string raw)
    {
        Assert.Equal("withoutRole", ListParameters.TryParseWithoutRole(raw, out var kind));
        Assert.Null(kind);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData(" TRUE ", true)]
    public void The_active_filter_is_absent_true_or_false(string? raw, bool? expected)
    {
        Assert.Null(ListParameters.TryParseActive(raw, out var active));
        Assert.Equal(expected, active);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("0")]
    [InlineData("truee")]
    public void Any_other_active_value_is_refused_by_name(string raw) => Assert.Equal("active", ListParameters.TryParseActive(raw, out _));

    [Fact]
    public void The_ids_must_be_guids_at_most_one_hundred_and_an_empty_array_means_no_ids()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Null(ListParameters.TryParseIds(null, out var none)); Assert.Null(none);
        Assert.Null(ListParameters.TryParseIds([], out var empty)); Assert.Null(empty);      // the framework's binding of an absent repeated parameter
        Assert.Null(ListParameters.TryParseIds([a.ToString(), b.ToString("N")], out var two));
        Assert.Equal([a, b], two);
        Assert.Equal("ids", ListParameters.TryParseIds([a.ToString(), "not-a-guid"], out _));
        Assert.Equal("ids", ListParameters.TryParseIds([""], out _));
        Assert.Null(ListParameters.TryParseIds([.. Enumerable.Range(0, 100).Select(_ => Guid.NewGuid().ToString())], out var hundred)); Assert.Equal(100, hundred!.Length);
        Assert.Equal("ids", ListParameters.TryParseIds([.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString())], out _));
    }

    [Fact]
    public async Task The_refusal_is_a_400_in_the_standard_validation_shape_naming_the_field()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        context.Response.Body = body;
        await ListParameters.Invalid("limit").ExecuteAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        body.Position = 0;
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
        var field = Assert.Single(json.GetProperty("fields").EnumerateArray());                  // the same shape as every other validation error
        Assert.Equal("limit", field.GetProperty("field").GetString());
        Assert.Equal("invalid_value", field.GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("field", out _));
    }
}
