using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Infrastructure.Tests;

public class ListPagingTests
{
    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("", null, null, true)]
    [InlineData(null, 0, null, true)]
    [InlineData(null, null, 10, true)]
    public void The_paged_form_is_requested_by_any_of_the_three_parameters(string? search, int? offset, int? limit, bool expected)
        => Assert.Equal(expected, ListPaging.IsRequested(search, offset, limit));

    [Fact]
    public void The_like_clause_folds_both_sides_in_the_database_and_escapes_with_a_backslash()
    {
        Assert.Equal("(upper(c) LIKE upper(@q) ESCAPE '\\')", ListPaging.LikeClause("q", "c"));
        Assert.Equal("(upper(p.a) LIKE upper(@like) ESCAPE '\\' OR upper(p.b) LIKE upper(@like) ESCAPE '\\' OR upper(p.c) LIKE upper(@like) ESCAPE '\\')", ListPaging.LikeClause("like", "p.a", "p.b", "p.c"));
    }

    [Theory]
    [InlineData(null, 50, 50)]
    [InlineData(null, 500, 500)]
    [InlineData(0, 500, 1)]
    [InlineData(10, 500, 10)]
    [InlineData(100000, 50, 500)]
    public void The_limit_falls_back_as_told_and_is_clamped(int? limit, int fallback, int expected)
        => Assert.Equal(expected, ListPaging.ClampLimit(limit, fallback));

    [Fact]
    public void The_bounds_are_the_documented_ones()
        => Assert.Equal((50, 500, 100, 100), (ListPaging.DefaultLimit, ListPaging.MaxLimit, ListPaging.MaxSearchLength, ListPaging.MaxIds));
}
