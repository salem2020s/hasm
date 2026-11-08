using Hasm.Infrastructure.Naming;

namespace Hasm.Infrastructure.Tests;

public class PhysicalIdentifierTests
{
    [Theory]
    [InlineData("d0123456789abcdef0123456789abcdef")]
    [InlineData("t_f446f4d0")]
    [InlineData("a")]
    public void Valid_identifiers_are_accepted(string value) => Assert.True(PhysicalIdentifier.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1abc")]            // starts with digit
    [InlineData("_abc")]            // starts with underscore
    [InlineData("Abc")]             // upper case
    [InlineData("a-b")]             // hyphen
    [InlineData("a b")]             // space
    [InlineData("a\"b")]            // quote
    [InlineData("a;drop")]          // injection-shaped
    [InlineData("pg_catalog")]      // reserved prefix
    [InlineData("شركة")] // non-ASCII (Arabic)
    public void Invalid_identifiers_are_rejected(string? value)
    {
        Assert.False(PhysicalIdentifier.IsValid(value, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<ArgumentException>(() => PhysicalIdentifier.Validate(value, "x"));
    }

    [Fact]
    public void Length_limit_is_63()
    {
        Assert.True(PhysicalIdentifier.IsValid(new string('a', 63)));
        Assert.False(PhysicalIdentifier.IsValid(new string('a', 64)));
    }

    [Fact]
    public void Generated_database_names_are_valid_unique_and_opaque()
    {
        var names = Enumerable.Range(0, 1000).Select(_ => PhysicalIdentifier.GenerateDatabaseName()).ToList();
        Assert.All(names, n => Assert.True(PhysicalIdentifier.IsValid(n)));
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.DoesNotContain("hasm", n));
    }
}
