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
    [InlineData("User.Table", "t_fc83d26e")]
    [InlineData("User.Id", "c_74b9e5ad")]
    [InlineData("User.Login", "c_65533e82")]
    [InlineData("User.DisplayName", "c_d4b85ec6")]
    [InlineData("User.PasswordHash", "c_6b6d1a87")]
    [InlineData("User.State", "c_2e208e57")]
    [InlineData("User.IsIdentityAdministrator", "c_241d633c")]
    [InlineData("User.CreatedAtUtc", "c_b1b46421")]
    [InlineData("User.StateChangedAtUtc", "c_82befcac")]
    [InlineData("User.PasswordChangedAtUtc", "c_2a818d05")]
    [InlineData("User.LastLoginAtUtc", "c_01184141")]
    [InlineData("User.PrimaryKey", "k_02c86823")]
    [InlineData("User.UniqueLogin", "k_65d29eb5")]
    [InlineData("User.CheckState", "k_4c15c740")]
    [InlineData("User.CheckLogin", "k_a3c1e5d0")]
    [InlineData("User.CheckDisplayName", "k_4d8c1d46")]
    [InlineData("User.CheckPasswordHash", "k_ff78c4f1")]
    [InlineData("User.GuardFunction", "f_6e1e11cd")]
    [InlineData("User.GuardTrigger", "r_be490ac9")]
    [InlineData("Session.Table", "t_5a3ce63e")]
    [InlineData("Session.Id", "c_43661a7f")]
    [InlineData("Session.UserId", "c_fcb7e5fc")]
    [InlineData("Session.TokenHash", "c_29e9c869")]
    [InlineData("Session.CreatedAtUtc", "c_ed958b18")]
    [InlineData("Session.LastSeenAtUtc", "c_050c6f1d")]
    [InlineData("Session.IdleExpiresAtUtc", "c_b417d62a")]
    [InlineData("Session.AbsoluteExpiresAtUtc", "c_2ab88b03")]
    [InlineData("Session.RevokedAtUtc", "c_7d127adf")]
    [InlineData("Session.RevokedReason", "c_1fef066e")]
    [InlineData("Session.ClientKind", "c_7239e57c")]
    [InlineData("Session.PrimaryKey", "k_2dabc43f")]
    [InlineData("Session.ForeignKeyUser", "k_72c79840")]
    [InlineData("Session.UniqueTokenHash", "k_025e9ec5")]
    [InlineData("Session.CheckTokenHash", "k_94e1ff35")]
    [InlineData("Session.CheckClientKind", "k_31ff7b77")]
    [InlineData("Session.CheckRevocation", "k_47a2a2e9")]
    [InlineData("Session.IndexUser", "k_a3b3758c")]
    [InlineData("LoginThrottle.Table", "t_3d3d01d6")]
    [InlineData("LoginThrottle.Scope", "c_13311537")]
    [InlineData("LoginThrottle.KeyHash", "c_f17b49b8")]
    [InlineData("LoginThrottle.Failures", "c_9472ec58")]
    [InlineData("LoginThrottle.WindowStartedAtUtc", "c_e420b5b6")]
    [InlineData("LoginThrottle.BlockedUntilUtc", "c_2dedbd25")]
    [InlineData("LoginThrottle.PrimaryKey", "k_251c8a44")]
    [InlineData("LoginThrottle.CheckScope", "k_46e44b97")]
    [InlineData("LoginThrottle.CheckKeyHash", "k_1af200ce")]
    [InlineData("UserCompanyAccess.Table", "t_a071e305")]
    [InlineData("UserCompanyAccess.UserId", "c_72706bb3")]
    [InlineData("UserCompanyAccess.CompanyId", "c_8c6c1d90")]
    [InlineData("UserCompanyAccess.GrantedAtUtc", "c_d156ceff")]
    [InlineData("UserCompanyAccess.PrimaryKey", "k_ca5aeefe")]
    [InlineData("UserCompanyAccess.ForeignKeyUser", "k_35815ced")]
    [InlineData("UserCompanyAccess.ForeignKeyCompany", "k_f4bd087d")]
    [InlineData("Role.Table", "t_359332ac")]
    [InlineData("Role.Id", "c_59b19fa5")]
    [InlineData("Role.CompanyId", "c_0f3f541d")]
    [InlineData("Role.PresetKey", "c_65735af7")]
    [InlineData("Role.Name", "c_bb4f89f0")]
    [InlineData("Role.IsActive", "c_64bdf264")]
    [InlineData("Role.CreatedAtUtc", "c_9f7e535b")]
    [InlineData("Role.CreatedBy", "c_c093e1d1")]
    [InlineData("Role.UpdatedAtUtc", "c_1dd9dad5")]
    [InlineData("Role.PrimaryKey", "k_ce37d6de")]
    [InlineData("Role.ForeignKeyCompany", "k_1813aa7a")]
    [InlineData("Role.ForeignKeyCreatedBy", "k_3376e530")]
    [InlineData("Role.CheckPreset", "k_88edaaae")]
    [InlineData("Role.CheckName", "k_e35e76dd")]
    [InlineData("Role.UniquePresetKey", "k_e7e6c274")]
    [InlineData("Role.UniqueName", "k_8bc611fb")]
    [InlineData("Role.GuardFunction", "f_8131c8b7")]
    [InlineData("Role.GuardTrigger", "r_d3408f58")]
    [InlineData("RolePermission.Table", "t_3e3d4119")]
    [InlineData("RolePermission.RoleId", "c_0258a1a8")]
    [InlineData("RolePermission.Permission", "c_27be2f9e")]
    [InlineData("RolePermission.PrimaryKey", "k_31fe7478")]
    [InlineData("RolePermission.ForeignKeyRole", "k_2522e0fd")]
    [InlineData("RolePermission.CheckPermission", "k_42a38028")]
    [InlineData("RolePermission.GuardFunction", "f_73f996bc")]
    [InlineData("RolePermission.GuardTrigger", "r_2601e368")]
    [InlineData("RoleAssignment.Table", "t_48ebc0f2")]
    [InlineData("RoleAssignment.Id", "c_11c1f4c7")]
    [InlineData("RoleAssignment.UserId", "c_96082bd4")]
    [InlineData("RoleAssignment.CompanyId", "c_bc3a9c98")]
    [InlineData("RoleAssignment.RoleId", "c_e3896a58")]
    [InlineData("RoleAssignment.ScopeKind", "c_c7cc8c47")]
    [InlineData("RoleAssignment.ScopeId", "c_162127ba")]
    [InlineData("RoleAssignment.GrantedAtUtc", "c_216ac47e")]
    [InlineData("RoleAssignment.GrantedBy", "c_42fb57a8")]
    [InlineData("RoleAssignment.RevokedAtUtc", "c_e4e52db8")]
    [InlineData("RoleAssignment.RevokedBy", "c_9f9a9d22")]
    [InlineData("RoleAssignment.PrimaryKey", "k_283ee178")]
    [InlineData("RoleAssignment.ForeignKeyMembership", "k_0147ffd4")]
    [InlineData("RoleAssignment.ForeignKeyRole", "k_1d3c1ee7")]
    [InlineData("RoleAssignment.ForeignKeyGrantedBy", "k_3324fb20")]
    [InlineData("RoleAssignment.ForeignKeyRevokedBy", "k_b500a403")]
    [InlineData("RoleAssignment.CheckScope", "k_c3c0623f")]
    [InlineData("RoleAssignment.CheckRevocation", "k_77a2fff4")]
    [InlineData("RoleAssignment.UniqueActive", "k_faf323d8")]
    [InlineData("RoleAssignment.IndexMember", "k_5f919f68")]
    [InlineData("RoleAssignment.GuardFunction", "f_69804ba6")]
    [InlineData("RoleAssignment.GuardTrigger", "r_3bdbbc04")]
    [InlineData("AccessAudit.Table", "t_dcb6ad92")]
    [InlineData("AccessAudit.Id", "c_a8808fec")]
    [InlineData("AccessAudit.OccurredAtUtc", "c_b8a31332")]
    [InlineData("AccessAudit.ActorUserId", "c_627d2433")]
    [InlineData("AccessAudit.CompanyId", "c_19755f4e")]
    [InlineData("AccessAudit.TargetUserId", "c_dfddf84e")]
    [InlineData("AccessAudit.RoleId", "c_a664f509")]
    [InlineData("AccessAudit.EventCode", "c_b052e578")]
    [InlineData("AccessAudit.Detail", "c_9a300568")]
    [InlineData("AccessAudit.PrimaryKey", "k_bc822b29")]
    [InlineData("AccessAudit.ForeignKeyActor", "k_2272e470")]
    [InlineData("AccessAudit.ForeignKeyCompany", "k_df82964a")]
    [InlineData("AccessAudit.ForeignKeyTarget", "k_e9e81c87")]
    [InlineData("AccessAudit.CheckEvent", "k_4e027754")]
    [InlineData("AccessAudit.CheckDetail", "k_8415bbea")]
    [InlineData("AccessAudit.IndexCompany", "k_f3321107")]
    [InlineData("AccessAudit.GuardFunction", "f_6521eb62")]
    [InlineData("AccessAudit.GuardTrigger", "r_1edfb742")]
    [InlineData("UserCompanyAccess.GrantedBy", "c_2e108925")]
    [InlineData("UserCompanyAccess.RevokedAtUtc", "c_79ae7bab")]
    [InlineData("UserCompanyAccess.RevokedBy", "c_cf899c17")]
    [InlineData("UserCompanyAccess.ForeignKeyGrantedBy", "k_0bbbaa10")]
    [InlineData("UserCompanyAccess.ForeignKeyRevokedBy", "k_4b8ded2d")]
    [InlineData("Session.SelectedCompanyId", "c_f26c54a5")]
    [InlineData("Session.ForeignKeySelectedCompany", "k_ba7a6e5a")]
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
