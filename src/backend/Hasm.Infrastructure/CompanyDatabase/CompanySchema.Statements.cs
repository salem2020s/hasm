namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0011 (Phase 3.3 S3, financial statement classification). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class StatementMappingVersion
    {
        public const string Table = "t_1fcba8f0";
        public const string Id = "c_85c806d1";
        public const string VersionNo = "c_f09acb95";
        public const string CreatedAtUtc = "c_41ef1ee1";
        public const string CreatedBy = "c_7c815034";
        public const string Source = "c_e26999dd";
        public const string Note = "c_17be4333";
        public const string EntryCount = "c_d1bdae31";
        public const string ContentHash = "c_15ccac28";
        public const string PrimaryKey = "k_583c2e19";
        public const string UniqueVersion = "k_23a6458c";
        public const string CheckValues = "k_dcf31000";
        public const string GuardFunction = "f_7189b7b8";
        public const string GuardRow = "r_c4942dde";
        public const string GuardTruncate = "r_1f5ee9ca";
    }

    public static class StatementMappingEntry
    {
        public const string Table = "t_e69c0053";
        public const string VersionId = "c_146d13d4";
        public const string AccountId = "c_48e48219";
        public const string Section = "c_a1dc02c3";
        public const string PrimaryKey = "k_97bc0743";
        public const string ForeignKeyVersion = "k_ac1b72ef";
        public const string ForeignKeyAccount = "k_0fc7a88d";
        public const string CheckValues = "k_5120fa8a";
        public const string IndexAccount = "k_27f31c89";
        public const string GuardFunction = "f_bd61d548";
        public const string GuardRow = "r_94a99b30";
        public const string GuardTruncate = "r_76b4e5c4";
        public const string CompatibilityFunction = "f_97c1ad60";
        public const string CompatibilityTrigger = "r_fe076bfb";
    }

    private static IReadOnlyDictionary<string, string> StatementMap => statementMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> statementMap = new(() => new Dictionary<string, string>
    {
        ["StatementMappingVersion"] = StatementMappingVersion.Table,
        ["StatementMappingVersion.Id"] = StatementMappingVersion.Id,
        ["StatementMappingVersion.VersionNo"] = StatementMappingVersion.VersionNo,
        ["StatementMappingVersion.CreatedAtUtc"] = StatementMappingVersion.CreatedAtUtc,
        ["StatementMappingVersion.CreatedBy"] = StatementMappingVersion.CreatedBy,
        ["StatementMappingVersion.Source"] = StatementMappingVersion.Source,
        ["StatementMappingVersion.Note"] = StatementMappingVersion.Note,
        ["StatementMappingVersion.EntryCount"] = StatementMappingVersion.EntryCount,
        ["StatementMappingVersion.ContentHash"] = StatementMappingVersion.ContentHash,
        ["StatementMappingVersion.PrimaryKey"] = StatementMappingVersion.PrimaryKey,
        ["StatementMappingVersion.UniqueVersion"] = StatementMappingVersion.UniqueVersion,
        ["StatementMappingVersion.CheckValues"] = StatementMappingVersion.CheckValues,
        ["StatementMappingVersion.GuardFunction"] = StatementMappingVersion.GuardFunction,
        ["StatementMappingVersion.GuardRow"] = StatementMappingVersion.GuardRow,
        ["StatementMappingVersion.GuardTruncate"] = StatementMappingVersion.GuardTruncate,
        ["StatementMappingEntry"] = StatementMappingEntry.Table,
        ["StatementMappingEntry.VersionId"] = StatementMappingEntry.VersionId,
        ["StatementMappingEntry.AccountId"] = StatementMappingEntry.AccountId,
        ["StatementMappingEntry.Section"] = StatementMappingEntry.Section,
        ["StatementMappingEntry.PrimaryKey"] = StatementMappingEntry.PrimaryKey,
        ["StatementMappingEntry.ForeignKeyVersion"] = StatementMappingEntry.ForeignKeyVersion,
        ["StatementMappingEntry.ForeignKeyAccount"] = StatementMappingEntry.ForeignKeyAccount,
        ["StatementMappingEntry.CheckValues"] = StatementMappingEntry.CheckValues,
        ["StatementMappingEntry.IndexAccount"] = StatementMappingEntry.IndexAccount,
        ["StatementMappingEntry.GuardFunction"] = StatementMappingEntry.GuardFunction,
        ["StatementMappingEntry.GuardRow"] = StatementMappingEntry.GuardRow,
        ["StatementMappingEntry.GuardTruncate"] = StatementMappingEntry.GuardTruncate,
        ["StatementMappingEntry.CompatibilityFunction"] = StatementMappingEntry.CompatibilityFunction,
        ["StatementMappingEntry.CompatibilityTrigger"] = StatementMappingEntry.CompatibilityTrigger,
    });
}
