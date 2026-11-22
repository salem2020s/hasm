namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>
/// Logical-to-physical identifier map for the system database. Backend-internal only; never
/// exposed through APIs, clients or error reports. Physical names are fixed and committed:
/// they are never regenerated. Changing a value requires a new migration, not an edit here.
/// This is obfuscation for hygiene, not a security control.
/// </summary>
public static partial class SystemSchema
{
    public static class Company
    {
        public const string Table = "t_f446f4d0";
        public const string Id = "c_a9a73285";
        public const string PhysicalDatabaseName = "c_30fd1c11";
        public const string ProvisioningState = "c_08f915ea";
        public const string CreatedAtUtc = "c_15ceb585";
        public const string StateChangedAtUtc = "c_d806142f";
        /// <summary>Added by migration 0002.</summary>
        public const string ProvisioningAttempts = "c_bf5cb42b";
        /// <summary>Added by migration 0002. Short sanitized failure code, never an error message.</summary>
        public const string LastErrorCode = "c_e6369967";
        /// <summary>Added by migration 0006: nullable projections of the company-database names (the company database is authoritative).</summary>
        public const string DisplayNameAr = "c_5d1a9b34";
        public const string DisplayNameEn = "c_b7e20c68";
        /// <summary>Added by migration 0006: monotonic revision of the projected names (0 = never projected).</summary>
        public const string DisplayNameRevision = "c_41f8d6a2";
        public const string CheckDisplayName = "k_9c3e7f15";
        public const string RevisionGuardFunction = "f_7a2c9e41";
        public const string RevisionGuardTrigger = "r_3d8b5f20";

        public const string PrimaryKey = "k_78d272fe";
        public const string UniqueDatabaseName = "k_b3d5d2a2";
        public const string CheckState = "k_3aeb4060";
        public const string CheckDatabaseName = "k_debceeec";
        public const string GuardFunction = "f_c52b26ca";
        public const string GuardTrigger = "r_d395935d";
    }

    // ---- Identity (migration 0003) ----
    public static class User
    {
        public const string Table = "t_fc83d26e";
        public const string Id = "c_74b9e5ad";
        public const string Login = "c_65533e82";
        public const string DisplayName = "c_d4b85ec6";
        public const string PasswordHash = "c_6b6d1a87";
        public const string State = "c_2e208e57";
        public const string IsIdentityAdministrator = "c_241d633c";
        public const string CreatedAtUtc = "c_b1b46421";
        public const string StateChangedAtUtc = "c_82befcac";
        public const string PasswordChangedAtUtc = "c_2a818d05";
        public const string LastLoginAtUtc = "c_01184141";
        public const string PrimaryKey = "k_02c86823";
        public const string UniqueLogin = "k_65d29eb5";
        public const string CheckState = "k_4c15c740";
        public const string CheckLogin = "k_a3c1e5d0";
        public const string CheckDisplayName = "k_4d8c1d46";
        public const string CheckPasswordHash = "k_ff78c4f1";
        public const string GuardFunction = "f_6e1e11cd";
        public const string GuardTrigger = "r_be490ac9";
    }

    public static class Session
    {
        public const string Table = "t_5a3ce63e";
        public const string Id = "c_43661a7f";
        public const string UserId = "c_fcb7e5fc";
        public const string TokenHash = "c_29e9c869";
        public const string CreatedAtUtc = "c_ed958b18";
        public const string LastSeenAtUtc = "c_050c6f1d";
        public const string IdleExpiresAtUtc = "c_b417d62a";
        public const string AbsoluteExpiresAtUtc = "c_2ab88b03";
        public const string RevokedAtUtc = "c_7d127adf";
        public const string RevokedReason = "c_1fef066e";
        public const string ClientKind = "c_7239e57c";
        public const string PrimaryKey = "k_2dabc43f";
        public const string ForeignKeyUser = "k_72c79840";
        public const string UniqueTokenHash = "k_025e9ec5";
        public const string CheckTokenHash = "k_94e1ff35";
        public const string CheckClientKind = "k_31ff7b77";
        public const string CheckRevocation = "k_47a2a2e9";
        public const string IndexUser = "k_a3b3758c";
        /// <summary>Added by migration 0004: the company the session currently works in (company-selection context).</summary>
        public const string SelectedCompanyId = "c_f26c54a5";
        public const string ForeignKeySelectedCompany = "k_ba7a6e5a";
    }

    public static class LoginThrottle
    {
        public const string Table = "t_3d3d01d6";
        public const string Scope = "c_13311537";
        public const string KeyHash = "c_f17b49b8";
        public const string Failures = "c_9472ec58";
        public const string WindowStartedAtUtc = "c_e420b5b6";
        public const string BlockedUntilUtc = "c_2dedbd25";
        public const string PrimaryKey = "k_251c8a44";
        public const string CheckScope = "k_46e44b97";
        public const string CheckKeyHash = "k_1af200ce";
    }

    public static class UserCompanyAccess
    {
        public const string Table = "t_a071e305";
        public const string UserId = "c_72706bb3";
        public const string CompanyId = "c_8c6c1d90";
        public const string GrantedAtUtc = "c_d156ceff";
        public const string PrimaryKey = "k_ca5aeefe";
        public const string ForeignKeyUser = "k_35815ced";
        public const string ForeignKeyCompany = "k_f4bd087d";
        /// <summary>Added by migration 0004.</summary>
        public const string GrantedBy = "c_2e108925";
        public const string RevokedAtUtc = "c_79ae7bab";
        public const string RevokedBy = "c_cf899c17";
        public const string ForeignKeyGrantedBy = "k_0bbbaa10";
        public const string ForeignKeyRevokedBy = "k_4b8ded2d";
    }

    // ---- Company access and RBAC (migration 0004) ----
    public static class Role
    {
        public const string Table = "t_359332ac";
        public const string Id = "c_59b19fa5";
        public const string CompanyId = "c_0f3f541d";
        public const string PresetKey = "c_65735af7";
        public const string Name = "c_bb4f89f0";
        public const string IsActive = "c_64bdf264";
        public const string CreatedAtUtc = "c_9f7e535b";
        public const string CreatedBy = "c_c093e1d1";
        public const string UpdatedAtUtc = "c_1dd9dad5";
        public const string PrimaryKey = "k_ce37d6de";
        public const string ForeignKeyCompany = "k_1813aa7a";
        public const string ForeignKeyCreatedBy = "k_3376e530";
        public const string CheckPreset = "k_88edaaae";
        public const string CheckName = "k_e35e76dd";
        public const string UniquePresetKey = "k_e7e6c274";
        public const string UniqueName = "k_8bc611fb";
        public const string GuardFunction = "f_8131c8b7";
        public const string GuardTrigger = "r_d3408f58";
    }
    public static class RolePermission
    {
        public const string Table = "t_3e3d4119";
        public const string RoleId = "c_0258a1a8";
        public const string Permission = "c_27be2f9e";
        public const string PrimaryKey = "k_31fe7478";
        public const string ForeignKeyRole = "k_2522e0fd";
        public const string CheckPermission = "k_42a38028";
        public const string GuardFunction = "f_73f996bc";
        public const string GuardTrigger = "r_2601e368";
    }
    public static class RoleAssignment
    {
        public const string Table = "t_48ebc0f2";
        public const string Id = "c_11c1f4c7";
        public const string UserId = "c_96082bd4";
        public const string CompanyId = "c_bc3a9c98";
        public const string RoleId = "c_e3896a58";
        public const string ScopeKind = "c_c7cc8c47";
        public const string ScopeId = "c_162127ba";
        public const string GrantedAtUtc = "c_216ac47e";
        public const string GrantedBy = "c_42fb57a8";
        public const string RevokedAtUtc = "c_e4e52db8";
        public const string RevokedBy = "c_9f9a9d22";
        public const string PrimaryKey = "k_283ee178";
        public const string ForeignKeyMembership = "k_0147ffd4";
        public const string ForeignKeyRole = "k_1d3c1ee7";
        public const string ForeignKeyGrantedBy = "k_3324fb20";
        public const string ForeignKeyRevokedBy = "k_b500a403";
        public const string CheckScope = "k_c3c0623f";
        public const string CheckRevocation = "k_77a2fff4";
        public const string UniqueActive = "k_faf323d8";
        public const string IndexMember = "k_5f919f68";
        public const string ScopeEpoch = "c_f18dd4c7";
        public const string CheckScopeEpoch = "k_57b77fbb";
        public const string WarehouseScopeEpoch = "c_6bdbb40f";
        public const string CheckWarehouseScopeEpoch = "k_163f9a59";
        public const string WarehouseEpochGuardFunction = "f_248f918f";
        public const string WarehouseEpochGuardTrigger = "r_771e0081";
        public const string EpochGuardFunction = "f_1f99d034";
        public const string EpochGuardTrigger = "r_1f9a28a4";
        public const string GuardFunction = "f_69804ba6";
        public const string GuardTrigger = "r_3bdbbc04";
    }
    public static class CostCenterAssignment
    {
        public const string Table = "t_83b6fff2";
        public const string Id = "c_c94f910f";
        public const string UserId = "c_be1b97e6";
        public const string CompanyId = "c_8f432b32";
        public const string RoleId = "c_4d6410a1";
        public const string CostCenterId = "c_8fbf9b9d";
        public const string Epoch = "c_92465d23";
        public const string GrantedAtUtc = "c_c8ea3e0d";
        public const string GrantedBy = "c_3a2484e7";
        public const string RevokedAtUtc = "c_7bddc39a";
        public const string RevokedBy = "c_01c768f2";
        public const string PrimaryKey = "k_8bb0ef86";
        public const string ForeignKeyMembership = "k_dcf9432f";
        public const string ForeignKeyRole = "k_eb86a5f4";
        public const string ForeignKeyGrantedBy = "k_bb9af908";
        public const string ForeignKeyRevokedBy = "k_b3e94581";
        public const string CheckRevocation = "k_2a9e912a";
        public const string CheckEpoch = "k_9213224c";
        public const string UniqueActive = "k_87e878b9";
        public const string IndexMember = "k_58461e5f";
        public const string GuardFunction = "f_b0860418";
        public const string GuardTrigger = "r_e867423b";
    }
    public static class AccessAudit
    {
        public const string Table = "t_dcb6ad92";
        public const string Id = "c_a8808fec";
        public const string OccurredAtUtc = "c_b8a31332";
        public const string ActorUserId = "c_627d2433";
        public const string CompanyId = "c_19755f4e";
        public const string TargetUserId = "c_dfddf84e";
        public const string RoleId = "c_a664f509";
        public const string EventCode = "c_b052e578";
        public const string Detail = "c_9a300568";
        public const string PrimaryKey = "k_bc822b29";
        public const string ForeignKeyActor = "k_2272e470";
        public const string ForeignKeyCompany = "k_df82964a";
        public const string ForeignKeyTarget = "k_e9e81c87";
        public const string CheckEvent = "k_4e027754";
        public const string CheckDetail = "k_8415bbea";
        public const string IndexCompany = "k_f3321107";
        public const string GuardFunction = "f_6521eb62";
        public const string GuardTrigger = "r_1edfb742";
    }
    /// <summary>Hash-chained, key-authenticated audit events (migration 0005); one chain per stream (system or one company).</summary>
    public static class AuditEvent
    {
        public const string Table = "t_7e93ef23";
        public const string StreamKey = "c_8a49147d";
        public const string Seq = "c_0aeec256";
        public const string EventId = "c_05b414b5";
        public const string CompanyId = "c_4260f38b";
        public const string OccurredAtUtc = "c_00969b7e";
        public const string EventType = "c_318d4b7d";
        public const string ActorUserId = "c_e99d8b01";
        public const string TargetUserId = "c_ae727dec";
        public const string SubjectId = "c_135114d2";
        public const string LegacyRef = "c_6d3e3fa8";
        public const string Detail = "c_b48ab4d7";
        public const string FormatVersion = "c_3ed2f8e1";
        public const string KeyId = "c_2e582193";
        public const string PrevHash = "c_1fecbd88";
        public const string Hash = "c_59ee706d";
        public const string Mac = "c_c64705b9";
        public const string PrimaryKey = "k_20f7f980";
        public const string UniqueEventId = "k_4b99ec45";
        public const string ForeignKeyCompany = "k_688a599e";
        public const string ForeignKeyActor = "k_134e62e4";
        public const string ForeignKeyTarget = "k_fb29a999";
        public const string CheckStream = "k_7ff931be";
        public const string CheckLengths = "k_67c74be8";
        public const string CheckDetail = "k_38f3d334";
        public const string CheckFields = "k_8473aed1";
        public const string IndexLegacyRef = "k_642bc5ec";
        public const string GuardFunction = "f_359f6a00";
        public const string GuardTrigger = "r_11080dd7";
    }

    /// <summary>Non-business infrastructure table that records applied migrations.</summary>
    public const string MigrationJournalTable = "migration_journal";

    /// <summary>
    /// The explicit SECRET-STORE flag of the logical schema dictionary (Phase 4.3 slice 4, D-070), with its PER-TABLE backup rule (slice 5, D-091 and D-120): the logical table names (keys of
    /// <see cref="Map"/>) whose rows hold secrets or ephemeral security data. Their DATA never reaches an UNENCRYPTED backup set. In an ENCRYPTED run a table is either still excluded
    /// (<see cref="SecretStoreBackupRule.AlwaysExcluded"/>: ephemeral, no recovery value) or included under HBE1 (<see cref="SecretStoreBackupRule.IncludedOnlyInEncryptedRuns"/>: rows that are already
    /// ciphertext under the protected key ring and must survive a full restore). A new secret-store table is added HERE, with its rule, and labelled in <c>BackupRunService.SystemDumpExclusions</c> in the
    /// same change (pinned by a unit test). D-120: the Phase 4.1 TOTP-secret table is <see cref="SecretStoreBackupRule.IncludedOnlyInEncryptedRuns"/>, set by whichever of 4.1 S1 and the slice 5
    /// implementation merges second.
    /// </summary>
    public static IReadOnlyDictionary<string, SecretStoreBackupRule> SecretStoreTables { get; } = new Dictionary<string, SecretStoreBackupRule>(StringComparer.Ordinal)
    {
        ["Session.Table"] = SecretStoreBackupRule.AlwaysExcluded,
        ["LoginThrottle.Table"] = SecretStoreBackupRule.AlwaysExcluded,
        // Phase 4.1 S1 (D-120): the TOTP secrets are already ciphertext under the protected key ring; an ENCRYPTED backup run includes them (so a full restore keeps the enrolled users enrolled, with the key export, D-087), an unencrypted run excludes them
        ["UserTotp.Table"] = SecretStoreBackupRule.IncludedOnlyInEncryptedRuns,
        // Phase 4.1 S2: login challenges are ephemeral, hashed and single use: no recovery value, so they are never backed up
        ["MfaChallenge.Table"] = SecretStoreBackupRule.AlwaysExcluded,
    };

    /// <summary>Every logical name with its physical identifier (for validation, backup and restore tooling).</summary>
    public static IReadOnlyDictionary<string, string> Map => map.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> map = new(() => CoreMap.Concat(PartyMap).Concat(MfaMap).Concat(MfaLoginMap).Concat(MfaPolicyMap).Concat(BackupRetentionMap).ToDictionary(kv => kv.Key, kv => kv.Value));

    private static IReadOnlyDictionary<string, string> CoreMap { get; } = new Dictionary<string, string>
    {
        ["Company"] = Company.Table,
        ["Company.Id"] = Company.Id,
        ["Company.PhysicalDatabaseName"] = Company.PhysicalDatabaseName,
        ["Company.ProvisioningState"] = Company.ProvisioningState,
        ["Company.CreatedAtUtc"] = Company.CreatedAtUtc,
        ["Company.StateChangedAtUtc"] = Company.StateChangedAtUtc,
        ["Company.ProvisioningAttempts"] = Company.ProvisioningAttempts,
        ["Company.LastErrorCode"] = Company.LastErrorCode,
        ["Company.PrimaryKey"] = Company.PrimaryKey,
        ["Company.UniqueDatabaseName"] = Company.UniqueDatabaseName,
        ["Company.CheckState"] = Company.CheckState,
        ["Company.CheckDatabaseName"] = Company.CheckDatabaseName,
        ["Company.DisplayNameAr"] = Company.DisplayNameAr,
        ["Company.DisplayNameEn"] = Company.DisplayNameEn,
        ["Company.DisplayNameRevision"] = Company.DisplayNameRevision,
        ["Company.CheckDisplayName"] = Company.CheckDisplayName,
        ["Company.RevisionGuardFunction"] = Company.RevisionGuardFunction,
        ["Company.RevisionGuardTrigger"] = Company.RevisionGuardTrigger,
        ["Company.GuardFunction"] = Company.GuardFunction,
        ["Company.GuardTrigger"] = Company.GuardTrigger,
        ["User.Table"] = User.Table,
        ["User.Id"] = User.Id,
        ["User.Login"] = User.Login,
        ["User.DisplayName"] = User.DisplayName,
        ["User.PasswordHash"] = User.PasswordHash,
        ["User.State"] = User.State,
        ["User.IsIdentityAdministrator"] = User.IsIdentityAdministrator,
        ["User.CreatedAtUtc"] = User.CreatedAtUtc,
        ["User.StateChangedAtUtc"] = User.StateChangedAtUtc,
        ["User.PasswordChangedAtUtc"] = User.PasswordChangedAtUtc,
        ["User.LastLoginAtUtc"] = User.LastLoginAtUtc,
        ["User.PrimaryKey"] = User.PrimaryKey,
        ["User.UniqueLogin"] = User.UniqueLogin,
        ["User.CheckState"] = User.CheckState,
        ["User.CheckLogin"] = User.CheckLogin,
        ["User.CheckDisplayName"] = User.CheckDisplayName,
        ["User.CheckPasswordHash"] = User.CheckPasswordHash,
        ["User.GuardFunction"] = User.GuardFunction,
        ["User.GuardTrigger"] = User.GuardTrigger,
        ["Session.Table"] = Session.Table,
        ["Session.Id"] = Session.Id,
        ["Session.UserId"] = Session.UserId,
        ["Session.TokenHash"] = Session.TokenHash,
        ["Session.CreatedAtUtc"] = Session.CreatedAtUtc,
        ["Session.LastSeenAtUtc"] = Session.LastSeenAtUtc,
        ["Session.IdleExpiresAtUtc"] = Session.IdleExpiresAtUtc,
        ["Session.AbsoluteExpiresAtUtc"] = Session.AbsoluteExpiresAtUtc,
        ["Session.RevokedAtUtc"] = Session.RevokedAtUtc,
        ["Session.RevokedReason"] = Session.RevokedReason,
        ["Session.ClientKind"] = Session.ClientKind,
        ["Session.PrimaryKey"] = Session.PrimaryKey,
        ["Session.ForeignKeyUser"] = Session.ForeignKeyUser,
        ["Session.UniqueTokenHash"] = Session.UniqueTokenHash,
        ["Session.CheckTokenHash"] = Session.CheckTokenHash,
        ["Session.CheckClientKind"] = Session.CheckClientKind,
        ["Session.CheckRevocation"] = Session.CheckRevocation,
        ["Session.IndexUser"] = Session.IndexUser,
        ["LoginThrottle.Table"] = LoginThrottle.Table,
        ["LoginThrottle.Scope"] = LoginThrottle.Scope,
        ["LoginThrottle.KeyHash"] = LoginThrottle.KeyHash,
        ["LoginThrottle.Failures"] = LoginThrottle.Failures,
        ["LoginThrottle.WindowStartedAtUtc"] = LoginThrottle.WindowStartedAtUtc,
        ["LoginThrottle.BlockedUntilUtc"] = LoginThrottle.BlockedUntilUtc,
        ["LoginThrottle.PrimaryKey"] = LoginThrottle.PrimaryKey,
        ["LoginThrottle.CheckScope"] = LoginThrottle.CheckScope,
        ["LoginThrottle.CheckKeyHash"] = LoginThrottle.CheckKeyHash,
        ["UserCompanyAccess.Table"] = UserCompanyAccess.Table,
        ["UserCompanyAccess.UserId"] = UserCompanyAccess.UserId,
        ["UserCompanyAccess.CompanyId"] = UserCompanyAccess.CompanyId,
        ["UserCompanyAccess.GrantedAtUtc"] = UserCompanyAccess.GrantedAtUtc,
        ["UserCompanyAccess.PrimaryKey"] = UserCompanyAccess.PrimaryKey,
        ["UserCompanyAccess.ForeignKeyUser"] = UserCompanyAccess.ForeignKeyUser,
        ["UserCompanyAccess.ForeignKeyCompany"] = UserCompanyAccess.ForeignKeyCompany,
        ["Role.Table"] = Role.Table,
        ["Role.Id"] = Role.Id,
        ["Role.CompanyId"] = Role.CompanyId,
        ["Role.PresetKey"] = Role.PresetKey,
        ["Role.Name"] = Role.Name,
        ["Role.IsActive"] = Role.IsActive,
        ["Role.CreatedAtUtc"] = Role.CreatedAtUtc,
        ["Role.CreatedBy"] = Role.CreatedBy,
        ["Role.UpdatedAtUtc"] = Role.UpdatedAtUtc,
        ["Role.PrimaryKey"] = Role.PrimaryKey,
        ["Role.ForeignKeyCompany"] = Role.ForeignKeyCompany,
        ["Role.ForeignKeyCreatedBy"] = Role.ForeignKeyCreatedBy,
        ["Role.CheckPreset"] = Role.CheckPreset,
        ["Role.CheckName"] = Role.CheckName,
        ["Role.UniquePresetKey"] = Role.UniquePresetKey,
        ["Role.UniqueName"] = Role.UniqueName,
        ["Role.GuardFunction"] = Role.GuardFunction,
        ["Role.GuardTrigger"] = Role.GuardTrigger,
        ["RolePermission.Table"] = RolePermission.Table,
        ["RolePermission.RoleId"] = RolePermission.RoleId,
        ["RolePermission.Permission"] = RolePermission.Permission,
        ["RolePermission.PrimaryKey"] = RolePermission.PrimaryKey,
        ["RolePermission.ForeignKeyRole"] = RolePermission.ForeignKeyRole,
        ["RolePermission.CheckPermission"] = RolePermission.CheckPermission,
        ["RolePermission.GuardFunction"] = RolePermission.GuardFunction,
        ["RolePermission.GuardTrigger"] = RolePermission.GuardTrigger,
        ["RoleAssignment.Table"] = RoleAssignment.Table,
        ["RoleAssignment.Id"] = RoleAssignment.Id,
        ["RoleAssignment.UserId"] = RoleAssignment.UserId,
        ["RoleAssignment.CompanyId"] = RoleAssignment.CompanyId,
        ["RoleAssignment.RoleId"] = RoleAssignment.RoleId,
        ["RoleAssignment.ScopeKind"] = RoleAssignment.ScopeKind,
        ["RoleAssignment.ScopeId"] = RoleAssignment.ScopeId,
        ["RoleAssignment.GrantedAtUtc"] = RoleAssignment.GrantedAtUtc,
        ["RoleAssignment.GrantedBy"] = RoleAssignment.GrantedBy,
        ["RoleAssignment.RevokedAtUtc"] = RoleAssignment.RevokedAtUtc,
        ["RoleAssignment.RevokedBy"] = RoleAssignment.RevokedBy,
        ["RoleAssignment.PrimaryKey"] = RoleAssignment.PrimaryKey,
        ["RoleAssignment.ForeignKeyMembership"] = RoleAssignment.ForeignKeyMembership,
        ["RoleAssignment.ForeignKeyRole"] = RoleAssignment.ForeignKeyRole,
        ["RoleAssignment.ForeignKeyGrantedBy"] = RoleAssignment.ForeignKeyGrantedBy,
        ["RoleAssignment.ForeignKeyRevokedBy"] = RoleAssignment.ForeignKeyRevokedBy,
        ["RoleAssignment.CheckScope"] = RoleAssignment.CheckScope,
        ["RoleAssignment.CheckRevocation"] = RoleAssignment.CheckRevocation,
        ["RoleAssignment.UniqueActive"] = RoleAssignment.UniqueActive,
        ["RoleAssignment.IndexMember"] = RoleAssignment.IndexMember,
        ["RoleAssignment.ScopeEpoch"] = RoleAssignment.ScopeEpoch,
        ["RoleAssignment.CheckScopeEpoch"] = RoleAssignment.CheckScopeEpoch,
        ["RoleAssignment.WarehouseScopeEpoch"] = RoleAssignment.WarehouseScopeEpoch,
        ["RoleAssignment.CheckWarehouseScopeEpoch"] = RoleAssignment.CheckWarehouseScopeEpoch,
        ["RoleAssignment.WarehouseEpochGuardFunction"] = RoleAssignment.WarehouseEpochGuardFunction,
        ["RoleAssignment.WarehouseEpochGuardTrigger"] = RoleAssignment.WarehouseEpochGuardTrigger,
        ["RoleAssignment.EpochGuardFunction"] = RoleAssignment.EpochGuardFunction,
        ["RoleAssignment.EpochGuardTrigger"] = RoleAssignment.EpochGuardTrigger,
        ["RoleAssignment.GuardFunction"] = RoleAssignment.GuardFunction,
        ["RoleAssignment.GuardTrigger"] = RoleAssignment.GuardTrigger,
        ["CostCenterAssignment.Table"] = CostCenterAssignment.Table,
        ["CostCenterAssignment.Id"] = CostCenterAssignment.Id,
        ["CostCenterAssignment.UserId"] = CostCenterAssignment.UserId,
        ["CostCenterAssignment.CompanyId"] = CostCenterAssignment.CompanyId,
        ["CostCenterAssignment.RoleId"] = CostCenterAssignment.RoleId,
        ["CostCenterAssignment.CostCenterId"] = CostCenterAssignment.CostCenterId,
        ["CostCenterAssignment.Epoch"] = CostCenterAssignment.Epoch,
        ["CostCenterAssignment.GrantedAtUtc"] = CostCenterAssignment.GrantedAtUtc,
        ["CostCenterAssignment.GrantedBy"] = CostCenterAssignment.GrantedBy,
        ["CostCenterAssignment.RevokedAtUtc"] = CostCenterAssignment.RevokedAtUtc,
        ["CostCenterAssignment.RevokedBy"] = CostCenterAssignment.RevokedBy,
        ["CostCenterAssignment.PrimaryKey"] = CostCenterAssignment.PrimaryKey,
        ["CostCenterAssignment.ForeignKeyMembership"] = CostCenterAssignment.ForeignKeyMembership,
        ["CostCenterAssignment.ForeignKeyRole"] = CostCenterAssignment.ForeignKeyRole,
        ["CostCenterAssignment.ForeignKeyGrantedBy"] = CostCenterAssignment.ForeignKeyGrantedBy,
        ["CostCenterAssignment.ForeignKeyRevokedBy"] = CostCenterAssignment.ForeignKeyRevokedBy,
        ["CostCenterAssignment.CheckRevocation"] = CostCenterAssignment.CheckRevocation,
        ["CostCenterAssignment.CheckEpoch"] = CostCenterAssignment.CheckEpoch,
        ["CostCenterAssignment.UniqueActive"] = CostCenterAssignment.UniqueActive,
        ["CostCenterAssignment.IndexMember"] = CostCenterAssignment.IndexMember,
        ["CostCenterAssignment.GuardFunction"] = CostCenterAssignment.GuardFunction,
        ["CostCenterAssignment.GuardTrigger"] = CostCenterAssignment.GuardTrigger,
        ["AccessAudit.Table"] = AccessAudit.Table,
        ["AccessAudit.Id"] = AccessAudit.Id,
        ["AccessAudit.OccurredAtUtc"] = AccessAudit.OccurredAtUtc,
        ["AccessAudit.ActorUserId"] = AccessAudit.ActorUserId,
        ["AccessAudit.CompanyId"] = AccessAudit.CompanyId,
        ["AccessAudit.TargetUserId"] = AccessAudit.TargetUserId,
        ["AccessAudit.RoleId"] = AccessAudit.RoleId,
        ["AccessAudit.EventCode"] = AccessAudit.EventCode,
        ["AccessAudit.Detail"] = AccessAudit.Detail,
        ["AccessAudit.PrimaryKey"] = AccessAudit.PrimaryKey,
        ["AccessAudit.ForeignKeyActor"] = AccessAudit.ForeignKeyActor,
        ["AccessAudit.ForeignKeyCompany"] = AccessAudit.ForeignKeyCompany,
        ["AccessAudit.ForeignKeyTarget"] = AccessAudit.ForeignKeyTarget,
        ["AccessAudit.CheckEvent"] = AccessAudit.CheckEvent,
        ["AccessAudit.CheckDetail"] = AccessAudit.CheckDetail,
        ["AccessAudit.IndexCompany"] = AccessAudit.IndexCompany,
        ["AccessAudit.GuardFunction"] = AccessAudit.GuardFunction,
        ["AccessAudit.GuardTrigger"] = AccessAudit.GuardTrigger,
        ["AuditEvent.Table"] = AuditEvent.Table,
        ["AuditEvent.StreamKey"] = AuditEvent.StreamKey,
        ["AuditEvent.Seq"] = AuditEvent.Seq,
        ["AuditEvent.EventId"] = AuditEvent.EventId,
        ["AuditEvent.CompanyId"] = AuditEvent.CompanyId,
        ["AuditEvent.OccurredAtUtc"] = AuditEvent.OccurredAtUtc,
        ["AuditEvent.EventType"] = AuditEvent.EventType,
        ["AuditEvent.ActorUserId"] = AuditEvent.ActorUserId,
        ["AuditEvent.TargetUserId"] = AuditEvent.TargetUserId,
        ["AuditEvent.SubjectId"] = AuditEvent.SubjectId,
        ["AuditEvent.LegacyRef"] = AuditEvent.LegacyRef,
        ["AuditEvent.Detail"] = AuditEvent.Detail,
        ["AuditEvent.FormatVersion"] = AuditEvent.FormatVersion,
        ["AuditEvent.KeyId"] = AuditEvent.KeyId,
        ["AuditEvent.PrevHash"] = AuditEvent.PrevHash,
        ["AuditEvent.Hash"] = AuditEvent.Hash,
        ["AuditEvent.Mac"] = AuditEvent.Mac,
        ["AuditEvent.PrimaryKey"] = AuditEvent.PrimaryKey,
        ["AuditEvent.UniqueEventId"] = AuditEvent.UniqueEventId,
        ["AuditEvent.ForeignKeyCompany"] = AuditEvent.ForeignKeyCompany,
        ["AuditEvent.ForeignKeyActor"] = AuditEvent.ForeignKeyActor,
        ["AuditEvent.ForeignKeyTarget"] = AuditEvent.ForeignKeyTarget,
        ["AuditEvent.CheckStream"] = AuditEvent.CheckStream,
        ["AuditEvent.CheckLengths"] = AuditEvent.CheckLengths,
        ["AuditEvent.CheckDetail"] = AuditEvent.CheckDetail,
        ["AuditEvent.CheckFields"] = AuditEvent.CheckFields,
        ["AuditEvent.IndexLegacyRef"] = AuditEvent.IndexLegacyRef,
        ["AuditEvent.GuardFunction"] = AuditEvent.GuardFunction,
        ["AuditEvent.GuardTrigger"] = AuditEvent.GuardTrigger,
        ["UserCompanyAccess.GrantedBy"] = UserCompanyAccess.GrantedBy,
        ["UserCompanyAccess.RevokedAtUtc"] = UserCompanyAccess.RevokedAtUtc,
        ["UserCompanyAccess.RevokedBy"] = UserCompanyAccess.RevokedBy,
        ["UserCompanyAccess.ForeignKeyGrantedBy"] = UserCompanyAccess.ForeignKeyGrantedBy,
        ["UserCompanyAccess.ForeignKeyRevokedBy"] = UserCompanyAccess.ForeignKeyRevokedBy,
        ["Session.SelectedCompanyId"] = Session.SelectedCompanyId,
        ["Session.ForeignKeySelectedCompany"] = Session.ForeignKeySelectedCompany,
    };

    public static string Resolve(string logicalName) =>
        Map.TryGetValue(logicalName, out var physical)
            ? physical
            : throw new KeyNotFoundException("Unknown logical schema name.");
}

/// <summary>The backup rule of a secret-store table (Phase 4.3 slice 5, D-091, D-120).</summary>
public enum SecretStoreBackupRule
{
    /// <summary>Data excluded from every system dump, encrypted or not (ephemeral security data with no recovery value: sessions, login throttling).</summary>
    AlwaysExcluded,
    /// <summary>Data excluded from unencrypted DEV/TEST dumps and included, under HBE1, in encrypted runs (rows already ciphertext under the protected key ring, for example TOTP secrets).</summary>
    IncludedOnlyInEncryptedRuns,
}
