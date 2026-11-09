using System.Text.RegularExpressions;
using Hasm.Infrastructure.Naming;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

public class SystemSchemaTests
{
    [Fact]
    public void Every_physical_name_is_a_valid_identifier()
        => Assert.All(SystemSchema.Map, kv => Assert.True(PhysicalIdentifier.IsValid(kv.Value), kv.Key));

    [Fact]
    public void Physical_names_are_unique()
        => Assert.Equal(SystemSchema.Map.Count, SystemSchema.Map.Values.Distinct().Count());

    [Fact]
    public void Physical_names_do_not_contain_business_words()
    {
        var words = new[] { "company", "compan", "party", "ledger", "account", "invoice", "state", "provision", "name", "hasm" };
        foreach (var physical in SystemSchema.Map.Values)
            Assert.DoesNotContain(words, w => physical.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // Pinned snapshot: these values must never change once released (D-029: stable identifiers).
    [Theory]
    [InlineData("Company", "t_f446f4d0")]
    [InlineData("Company.Id", "c_a9a73285")]
    [InlineData("Company.PhysicalDatabaseName", "c_30fd1c11")]
    [InlineData("Company.ProvisioningState", "c_08f915ea")]
    [InlineData("Company.CreatedAtUtc", "c_15ceb585")]
    [InlineData("Company.StateChangedAtUtc", "c_d806142f")]
    [InlineData("Company.ProvisioningAttempts", "c_bf5cb42b")]
    [InlineData("Company.LastErrorCode", "c_e6369967")]
    [InlineData("Company.PrimaryKey", "k_78d272fe")]
    [InlineData("Company.UniqueDatabaseName", "k_b3d5d2a2")]
    [InlineData("Company.CheckState", "k_3aeb4060")]
    [InlineData("Company.CheckDatabaseName", "k_debceeec")]
    [InlineData("Company.GuardFunction", "f_c52b26ca")]
    [InlineData("Company.GuardTrigger", "r_d395935d")]
    public void Mapping_is_stable(string logical, string physical) => Assert.Equal(physical, SystemSchema.Resolve(logical));

    [Fact]
    public void Unknown_logical_name_fails_without_echoing_the_map()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => SystemSchema.Resolve("Nope"));
        Assert.DoesNotContain("t_f446f4d0", ex.Message);
    }

    [Fact]
    public void System_migrations_use_exactly_the_mapped_identifiers()
    {
        var sql = string.Join(' ',MigrationScript.LoadEmbedded().Select(s => s.Sql));
        var used = Regex.Matches(sql, @"\b[tcfkr]_[0-9a-f]{8}\b").Select(m => m.Value).ToHashSet();
        var mapped = SystemSchema.Map.Values.ToHashSet();
        Assert.Equal(mapped.OrderBy(x => x), used.OrderBy(x => x));
    }
}
