using Hasm.Infrastructure.Parties;

namespace Hasm.Infrastructure.Tests;

public class PartyRoleSearchTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  abc ", "abc")]
    [InlineData("a b", "a b")]
    public void The_search_text_is_trimmed_and_an_empty_text_means_no_filter(string? input, string? expected)
    {
        Assert.True(PartyRoleSearch.TryCleanSearch(input, out var cleaned));
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public void The_search_text_is_bounded_and_refuses_control_characters()
    {
        Assert.True(PartyRoleSearch.TryCleanSearch(new string('x', PartyRoleSearch.MaxSearchLength), out _));
        Assert.False(PartyRoleSearch.TryCleanSearch(new string('x', PartyRoleSearch.MaxSearchLength + 1), out _));
        Assert.False(PartyRoleSearch.TryCleanSearch("a\nb", out _));
        Assert.False(PartyRoleSearch.TryCleanSearch("a\0b", out _));
    }

    [Theory]
    [InlineData("abc", "%abc%")]
    [InlineData("50%", "%50\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("a\\b", "%a\\\\b%")]
    [InlineData("x'; DROP", "%x'; DROP%")]
    public void The_like_pattern_is_a_literal_substring_and_the_case_is_left_to_PostgreSQL(string text, string expected)
        => Assert.Equal(expected, PartyRoleSearch.LikePattern(text));

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(int.MaxValue, 500)]
    public void The_limit_defaults_to_50_and_is_clamped_to_1_to_500(int? limit, int expected)
        => Assert.Equal(expected, PartyRoleSearch.ClampLimit(limit));

    [Theory]
    [InlineData(null, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(42, 42)]
    public void The_offset_is_never_negative(int? offset, int expected)
        => Assert.Equal(expected, PartyRoleSearch.ClampOffset(offset));
}
