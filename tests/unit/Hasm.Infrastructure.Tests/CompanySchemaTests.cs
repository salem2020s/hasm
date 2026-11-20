using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Naming;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

public class CompanySchemaTests
{
    [Fact]
    public void Every_physical_name_is_a_valid_unique_identifier()
    {
        Assert.All(CompanySchema.Map, kv => Assert.True(PhysicalIdentifier.IsValid(kv.Value), kv.Key));
        Assert.Equal(CompanySchema.Map.Count, CompanySchema.Map.Values.Distinct().Count());
    }

    [Fact]
    public void Company_names_do_not_collide_with_system_names()
        => Assert.Empty(CompanySchema.Map.Values.Intersect(SystemSchema.Map.Values));

    [Fact]
    public void Physical_names_do_not_contain_business_words()
    {
        var words = new[] { "company", "compan", "identity", "ledger", "account", "invoice", "hasm" };
        foreach (var physical in CompanySchema.Map.Values)
            Assert.DoesNotContain(words, w => physical.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Identity", "t_209d0f15")]
    [InlineData("Identity.Singleton", "c_8709851b")]
    [InlineData("Identity.CompanyId", "c_2d7eb73b")]
    [InlineData("Identity.CreatedAtUtc", "c_a77d8875")]
    [InlineData("Identity.PrimaryKey", "k_bf65319b")]
    [InlineData("Identity.CheckSingleton", "k_8fd0da3b")]
    [InlineData("Identity.GuardFunction", "f_0b15f60c")]
    [InlineData("Identity.GuardTrigger", "r_ec68b932")]
    public void Mapping_is_stable(string logical, string physical) => Assert.Equal(physical, CompanySchema.Map[logical]);

    [Fact]
    public void Company_migrations_use_exactly_the_mapped_identifiers()
    {
        var sql = string.Join("\n", MigrationScript.LoadEmbeddedCompany().Select(s => s.Sql));
        var used = Regex.Matches(sql, @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        Assert.Equal(CompanySchema.Map.Values.OrderBy(x => x), used.OrderBy(x => x));
    }

    [Fact]
    public void System_and_company_migration_sets_are_separate()
    {
        var system = MigrationScript.LoadEmbedded();
        var company = MigrationScript.LoadEmbeddedCompany();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26], system.Select(s => s.Version));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25], company.Select(s => s.Version));
        Assert.DoesNotContain(system, s => s.Name.Contains("identity"));
        Assert.DoesNotContain(company, s => s.Name.Contains("registry"));
    }
}
