using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

/// <summary>Owner decision 2026-11-22 (Q3): the migrating commands refuse a PostgreSQL superuser account unless the explicit, logged development/test override is given.</summary>
public class MigrationAccountPolicyTests
{
    [Fact]
    public void A_non_superuser_account_may_migrate_and_nothing_is_logged()
    {
        var log = new List<string>();
        Assert.Null(MigrationAccountPolicy.Evaluate(isSuperuser: false, overrideGiven: false, log.Add));
        Assert.Null(MigrationAccountPolicy.Evaluate(isSuperuser: false, overrideGiven: true, log.Add));    // an unused override is not a use
        Assert.Empty(log);
    }

    [Fact]
    public void A_superuser_account_is_refused_by_default_with_a_clear_message_and_nothing_is_logged_as_allowed()
    {
        var log = new List<string>();
        var message = MigrationAccountPolicy.Evaluate(isSuperuser: true, overrideGiven: false, log.Add);
        Assert.NotNull(message);
        Assert.Contains("superuser", message);
        Assert.Contains("non-superuser", message);
        Assert.Contains("Q3", message);
        Assert.Contains("--allow-superuser-for-development", message);
        Assert.Contains("Nothing was changed", message);
        Assert.Empty(log);
    }

    [Fact]
    public void The_override_allows_a_superuser_and_every_use_is_logged()
    {
        var log = new List<string>();
        Assert.Null(MigrationAccountPolicy.Evaluate(isSuperuser: true, overrideGiven: true, log.Add));
        var line = Assert.Single(log);
        Assert.StartsWith("WARNING", line);
        Assert.Contains("superuser", line);
        Assert.Contains("--allow-superuser-for-development", line);
        Assert.Contains("development and tests only", line);

        Assert.Null(MigrationAccountPolicy.Evaluate(isSuperuser: true, overrideGiven: true, log.Add));
        Assert.Equal(2, log.Count);                                                                          // logged again, not once per process
    }

    [Fact]
    public void The_override_switch_is_a_fixed_command_line_word_and_not_a_configuration_setting()
    {
        Assert.Equal("--allow-superuser-for-development", MigrationAccountPolicy.OverrideSwitch);
        var options = new HasmDatabaseOptions();
        Assert.False(options.AllowSuperuserMigrationAccount);
        var configured = HasmDatabaseOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Hasm:Database:AllowSuperuserMigrationAccount"] = "true", ["Hasm:Database:AllowSuperuserForDevelopment"] = "true" }).Build());
        Assert.False(configured.AllowSuperuserMigrationAccount);                                              // a configuration value cannot set it
        configured.AllowSuperuserMigrationAccountForDevelopment();
        Assert.True(configured.AllowSuperuserMigrationAccount);
    }
}

/// <summary>Owner decision 2026-11-22 (Q2): the API never runs migrations, so a rejected administrative command cannot reach it. A source scan, the structural half of the proof (the runtime half is in the PostgreSQL suite).</summary>
public class ApiNeverMigratesTests
{
    [Fact]
    public void The_api_project_never_references_the_migration_runner_or_the_commands_that_use_it()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "backend", "Hasm.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var api = Path.Combine(dir!.FullName, "src", "backend", "Hasm.Api");
        var files = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
        Assert.True(files.Count > 20, "the scan must actually read the API sources");
        var forbidden = new[] { "MigrationRunner", "SystemDatabaseInitializer", "CompanyProvisioner", "CompanyMigrator", "MigrationAccountPolicy", "EnsureNoSwitchedOffAsync" };
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden) Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} mentions {word}");
        }
    }
}
