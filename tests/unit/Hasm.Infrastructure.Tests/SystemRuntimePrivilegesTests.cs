using System.Reflection;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

/// <summary>Owner decision D-235: the system runtime catalog as reviewed code. These pins fail when the catalog is widened or a new system table is added without a decision about the runtime account.</summary>
public class SystemRuntimePrivilegesTests
{
    private static SystemRuntimeGrant Of(string logical) => SystemRuntimePrivileges.Tables.Single(t => t.Logical == logical);

    [Fact]
    public void The_TOTP_secret_store_is_select_insert_and_update_of_the_four_progress_columns_only()
    {
        var totp = SystemRuntimePrivileges.Totp;
        Assert.Equal(SystemSchema.UserTotp.Table, totp.Table);
        Assert.Equal(["SELECT", "INSERT"], totp.Privileges);
        Assert.Equal(new[] { SystemSchema.UserTotp.State, SystemSchema.UserTotp.ConfirmedAtUtc, SystemSchema.UserTotp.LastStep, SystemSchema.UserTotp.FailedConfirmations }.Order(), totp.UpdateColumns!.Order());
        foreach (var material in new[] { SystemSchema.UserTotp.Cipher, SystemSchema.UserTotp.Nonce, SystemSchema.UserTotp.Tag, SystemSchema.UserTotp.KekId, SystemSchema.UserTotp.SecretId, SystemSchema.UserTotp.UserId, SystemSchema.UserTotp.CreatedAtUtc })
            Assert.DoesNotContain(material, totp.UpdateColumns!);
    }

    [Fact]
    public void Every_system_table_has_exactly_one_catalog_entry()
    {
        var declared = typeof(SystemSchema).GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
            .Select(t => t.GetField("Table", BindingFlags.Public | BindingFlags.Static)).Where(f => f is not null).Select(f => (string)f!.GetRawConstantValue()!)
            .Append(SystemSchema.MigrationJournalTable).Order(StringComparer.Ordinal).ToArray();
        Assert.True(declared.Length >= 19);
        Assert.Equal(declared, SystemRuntimePrivileges.Tables.Select(t => t.Table).Order(StringComparer.Ordinal));     // a new table needs a deliberate entry (an empty one = administrative tool only)
        Assert.Equal(SystemRuntimePrivileges.Tables.Count, SystemRuntimePrivileges.Tables.Select(t => t.Logical).Distinct().Count());
    }

    [Fact]
    public void The_catalog_is_pinned_table_by_table()
    {
        var actual = SystemRuntimePrivileges.Tables.Select(t => $"{t.Logical}={string.Join(",", t.Privileges)}{(t.UpdateColumns is null ? "" : $"+UPDATE({t.UpdateColumns.Length})")}").ToArray();
        Assert.Equal(
        [
            "Company=SELECT+UPDATE(3)", "User=SELECT,INSERT,UPDATE", "Session=SELECT,INSERT,UPDATE,DELETE", "LoginThrottle=SELECT,INSERT,UPDATE,DELETE", "UserCompanyAccess=SELECT,INSERT,UPDATE",
            "Role=SELECT,INSERT,UPDATE", "RolePermission=SELECT,INSERT,DELETE", "RoleAssignment=SELECT,INSERT,UPDATE", "CostCenterAssignment=SELECT,INSERT+UPDATE(2)", "AccessAudit=SELECT,INSERT",
            "AuditEvent=SELECT,INSERT", "GlobalParty=SELECT,INSERT,UPDATE", "PartyProposal=SELECT,INSERT,UPDATE", "PartyLink=SELECT,INSERT", "UserTotp=SELECT,INSERT+UPDATE(4)",
            "MfaChallenge=SELECT,INSERT,DELETE+UPDATE(2)", "MfaPolicy=SELECT,INSERT+UPDATE(4)", "MfaPolicyRole=SELECT,INSERT,DELETE", "BackupLegalHold=", "BackupRetentionPin=", "MigrationJournal=",
        ], actual);
        Assert.All(SystemRuntimePrivileges.Tables, t => Assert.DoesNotContain(t.Privileges, p => p is "TRUNCATE" or "REFERENCES" or "TRIGGER"));
        Assert.All(SystemRuntimePrivileges.Tables.Where(t => t.UpdateColumns is not null), t => Assert.DoesNotContain("UPDATE", t.Privileges));   // a column grant never sits beside the table grant
        Assert.DoesNotContain("UPDATE", Of("AuditEvent").Privileges);
        Assert.DoesNotContain("DELETE", Of("AuditEvent").Privileges);
    }

    [Fact]
    public void The_statements_strip_first_grant_the_catalog_and_never_name_another_role()
    {
        var sql = SystemRuntimePrivileges.SchemaStatements("rt\"x");
        Assert.Equal("REVOKE ALL ON SCHEMA public FROM PUBLIC", sql[0]);                                          // as the company twin: PUBLIC holds nothing on the schema
        Assert.Contains("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC", sql);
        Assert.All(sql.Where(s => s.StartsWith("REVOKE") && s.Contains("\"rt\"\"x\"")), s => Assert.EndsWith(" CASCADE", s));       // a grant passed on WITH GRANT OPTION by the runtime account goes with its own
        var firstGrant = sql.Select((s, i) => (s, i)).First(x => x.s.StartsWith("GRANT")).i;
        Assert.All(sql.Take(firstGrant), s => Assert.StartsWith("REVOKE ", s));
        Assert.All(sql.Skip(firstGrant), s => Assert.StartsWith("GRANT ", s));
        Assert.All(sql, s => Assert.True(s.EndsWith(" PUBLIC") || s.EndsWith(" \"rt\"\"x\"") || s.EndsWith(" \"rt\"\"x\" CASCADE"), s));             // only PUBLIC (revoked) and the quoted runtime account; the owner is never named
        Assert.All(sql.Where(s => s.StartsWith("GRANT")), s => Assert.DoesNotContain("PUBLIC", s));
        Assert.DoesNotContain(sql, s => s.Contains("GRANT ALL") || s.Contains("WITH GRANT OPTION") || s.Contains(" DATABASE ") || s.Contains("CREATE"));
        Assert.Contains($"GRANT SELECT, INSERT ON TABLE \"{SystemSchema.UserTotp.Table}\" TO \"rt\"\"x\"", sql);
        Assert.Single(sql, s => s.Contains(SystemSchema.UserTotp.Table) && s.StartsWith("GRANT UPDATE ("));
        Assert.DoesNotContain(sql, s => s.Contains(SystemSchema.UserTotp.Cipher) || s.Contains(SystemSchema.UserTotp.KekId));
        Assert.DoesNotContain(sql, s => s.StartsWith("GRANT") && (s.Contains(SystemSchema.BackupLegalHold.Table) || s.Contains(SystemSchema.MigrationJournalTable)));
    }

    [Fact]
    public void The_two_tables_with_a_narrow_update_have_exactly_their_revocation_and_progress_columns()
    {
        Assert.Equal(new[] { SystemSchema.CostCenterAssignment.RevokedAtUtc, SystemSchema.CostCenterAssignment.RevokedBy }.Order(), Of("CostCenterAssignment").UpdateColumns!.Order());   // AccessService only ever ends an assignment
        Assert.Equal(new[] { SystemSchema.MfaChallenge.Attempts, SystemSchema.MfaChallenge.ConsumedAtUtc }.Order(), Of("MfaChallenge").UpdateColumns!.Order());                           // count an attempt, consume
        Assert.Contains("DELETE", Of("MfaChallenge").Privileges);                                                                                                                       // the housekeeping purge of expired challenges
        Assert.DoesNotContain("DELETE", Of("CostCenterAssignment").Privileges);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void The_opt_out_is_off_unless_explicitly_set(string? value, bool expected)
    {
        var d = new Dictionary<string, string?> { ["Hasm:Database:Host"] = "db.internal", ["Hasm:Database:AllowOverprivilegedSystemRuntime"] = value };
        Assert.Equal(expected, HasmDatabaseOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(d).Build()).AllowOverprivilegedSystemRuntime);
        Assert.False(HasmDatabaseOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(d).Build()).AllowOverprivilegedCompanyRuntime);   // the two opt-outs are independent
    }

    [Fact]
    public void A_malformed_opt_out_is_a_configuration_error_not_a_silent_default() =>
        Assert.Throws<DatabaseConfigurationException>(() => HasmDatabaseOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Hasm:Database:AllowOverprivilegedSystemRuntime"] = "yes" }).Build()));
}
