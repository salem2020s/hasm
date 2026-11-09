namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>
/// Logical-to-physical identifier map for the system database. Backend-internal only; never
/// exposed through APIs, clients or error reports. Physical names are fixed and committed:
/// they are never regenerated. Changing a value requires a new migration, not an edit here.
/// This is obfuscation for hygiene, not a security control.
/// </summary>
public static class SystemSchema
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
    }

    /// <summary>Non-business infrastructure table that records applied migrations.</summary>
    public const string MigrationJournalTable = "migration_journal";

    /// <summary>Every logical name with its physical identifier (for validation, backup and restore tooling).</summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>
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
    };

    public static string Resolve(string logicalName) =>
        Map.TryGetValue(logicalName, out var physical)
            ? physical
            : throw new KeyNotFoundException("Unknown logical schema name.");
}
