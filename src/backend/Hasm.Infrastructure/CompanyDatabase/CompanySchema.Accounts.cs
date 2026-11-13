namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0007 (Phase 3.2A, chart of accounts). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class Account
    {
        public const string Table = "t_11d107a9";
        public const string Id = "c_29a86598";
        public const string Code = "c_9d786d7c";
        public const string NameAr = "c_f03e9240";
        public const string NameEn = "c_188c6a2d";
        public const string ParentId = "c_1f4de422";
        public const string Type = "c_0caeac6a";
        public const string Nature = "c_db118818";
        public const string Statement = "c_4f5cb580";
        public const string IsGroup = "c_57d34c8a";
        public const string IsActive = "c_9761052f";
        public const string CurrencyPolicy = "c_dfab26fd";
        public const string CurrencyCode = "c_76c4ece0";
        public const string TemplateKey = "c_3d7a4cad";
        public const string Revision = "c_3513bcb9";
        public const string CreatedAtUtc = "c_a6a28d08";
        public const string UpdatedAtUtc = "c_f05f84fd";
        public const string PrimaryKey = "k_eb12da06";
        public const string ForeignKeyParent = "k_f3085d01";
        public const string ForeignKeyCurrency = "k_63c689e6";
        public const string CheckCode = "k_9e64a52d";
        public const string CheckName = "k_d075fab3";
        public const string CheckType = "k_60532cbe";
        public const string CheckNature = "k_d8639d1c";
        public const string CheckStatement = "k_9a12cdb1";
        public const string CheckCurrency = "k_84707f17";
        public const string CheckSelf = "k_3ceee586";
        public const string CheckRevision = "k_a2c24e8d";
        public const string UniqueCode = "k_7f3d3ed8";
        public const string IndexParent = "k_9ea9cb31";
        public const string GuardFunction = "f_9e328199";
        public const string GuardRow = "r_7a5a4200";
        public const string GuardTruncate = "r_09ffdb5e";
        public const string CheckFunction = "f_d830138f";
        public const string CheckTrigger = "r_36b31b96";
    }

    public static class AccountSettings
    {
        public const string Table = "t_4eaf5544";
        public const string Singleton = "c_683db677";
        public const string SegmentWidth = "c_38b81f61";
        public const string AutoCodes = "c_a8ea27c4";
        public const string Revision = "c_c8affba4";
        public const string UpdatedAtUtc = "c_d64f53d2";
        public const string PrimaryKey = "k_d1288af6";
        public const string CheckSingleton = "k_ed83d910";
        public const string CheckWidth = "k_212ceaf1";
        public const string CheckRevision = "k_9992af71";
        public const string GuardFunction = "f_2c5c2f2d";
        public const string GuardRow = "r_45534741";
        public const string GuardTruncate = "r_0b02c127";
    }

    public static class AccountRevision
    {
        public const string Table = "t_6bc0a848";
        public const string Id = "c_8730cef3";
        public const string Entity = "c_6f3728e4";
        public const string EntityId = "c_477f65d3";
        public const string Revision = "c_9617c4f4";
        public const string ActorUserId = "c_2c14f733";
        public const string ChangedAtUtc = "c_e3ff7f89";
        public const string Changes = "c_8ba4ef1c";
        public const string AuditStream = "c_53c3941c";
        public const string AuditSeq = "c_6c9c32b6";
        public const string PrimaryKey = "k_51737778";
        public const string UniqueRevision = "k_259d9b61";
        public const string UniqueAuditEvent = "k_5e0159a3";
        public const string ForeignKeyAudit = "k_6515f868";
        public const string CheckValues = "k_e47aaa6d";
        public const string GuardFunction = "f_1b681536";
        public const string GuardRow = "r_74e6f2fe";
        public const string GuardTruncate = "r_c1f53c3c";
    }

    private static IReadOnlyDictionary<string, string> AccountMap => accountMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> accountMap = new(() => new Dictionary<string, string>
    {
        ["Account"] = Account.Table,
        ["Account.Id"] = Account.Id,
        ["Account.Code"] = Account.Code,
        ["Account.NameAr"] = Account.NameAr,
        ["Account.NameEn"] = Account.NameEn,
        ["Account.ParentId"] = Account.ParentId,
        ["Account.Type"] = Account.Type,
        ["Account.Nature"] = Account.Nature,
        ["Account.Statement"] = Account.Statement,
        ["Account.IsGroup"] = Account.IsGroup,
        ["Account.IsActive"] = Account.IsActive,
        ["Account.CurrencyPolicy"] = Account.CurrencyPolicy,
        ["Account.CurrencyCode"] = Account.CurrencyCode,
        ["Account.TemplateKey"] = Account.TemplateKey,
        ["Account.Revision"] = Account.Revision,
        ["Account.CreatedAtUtc"] = Account.CreatedAtUtc,
        ["Account.UpdatedAtUtc"] = Account.UpdatedAtUtc,
        ["Account.PrimaryKey"] = Account.PrimaryKey,
        ["Account.ForeignKeyParent"] = Account.ForeignKeyParent,
        ["Account.ForeignKeyCurrency"] = Account.ForeignKeyCurrency,
        ["Account.CheckCode"] = Account.CheckCode,
        ["Account.CheckName"] = Account.CheckName,
        ["Account.CheckType"] = Account.CheckType,
        ["Account.CheckNature"] = Account.CheckNature,
        ["Account.CheckStatement"] = Account.CheckStatement,
        ["Account.CheckCurrency"] = Account.CheckCurrency,
        ["Account.CheckSelf"] = Account.CheckSelf,
        ["Account.CheckRevision"] = Account.CheckRevision,
        ["Account.UniqueCode"] = Account.UniqueCode,
        ["Account.IndexParent"] = Account.IndexParent,
        ["Account.GuardFunction"] = Account.GuardFunction,
        ["Account.GuardRow"] = Account.GuardRow,
        ["Account.GuardTruncate"] = Account.GuardTruncate,
        ["Account.CheckFunction"] = Account.CheckFunction,
        ["Account.CheckTrigger"] = Account.CheckTrigger,
        ["AccountSettings"] = AccountSettings.Table,
        ["AccountSettings.Singleton"] = AccountSettings.Singleton,
        ["AccountSettings.SegmentWidth"] = AccountSettings.SegmentWidth,
        ["AccountSettings.AutoCodes"] = AccountSettings.AutoCodes,
        ["AccountSettings.Revision"] = AccountSettings.Revision,
        ["AccountSettings.UpdatedAtUtc"] = AccountSettings.UpdatedAtUtc,
        ["AccountSettings.PrimaryKey"] = AccountSettings.PrimaryKey,
        ["AccountSettings.CheckSingleton"] = AccountSettings.CheckSingleton,
        ["AccountSettings.CheckWidth"] = AccountSettings.CheckWidth,
        ["AccountSettings.CheckRevision"] = AccountSettings.CheckRevision,
        ["AccountSettings.GuardFunction"] = AccountSettings.GuardFunction,
        ["AccountSettings.GuardRow"] = AccountSettings.GuardRow,
        ["AccountSettings.GuardTruncate"] = AccountSettings.GuardTruncate,
        ["AccountRevision"] = AccountRevision.Table,
        ["AccountRevision.Id"] = AccountRevision.Id,
        ["AccountRevision.Entity"] = AccountRevision.Entity,
        ["AccountRevision.EntityId"] = AccountRevision.EntityId,
        ["AccountRevision.Revision"] = AccountRevision.Revision,
        ["AccountRevision.ActorUserId"] = AccountRevision.ActorUserId,
        ["AccountRevision.ChangedAtUtc"] = AccountRevision.ChangedAtUtc,
        ["AccountRevision.Changes"] = AccountRevision.Changes,
        ["AccountRevision.AuditStream"] = AccountRevision.AuditStream,
        ["AccountRevision.AuditSeq"] = AccountRevision.AuditSeq,
        ["AccountRevision.PrimaryKey"] = AccountRevision.PrimaryKey,
        ["AccountRevision.UniqueRevision"] = AccountRevision.UniqueRevision,
        ["AccountRevision.UniqueAuditEvent"] = AccountRevision.UniqueAuditEvent,
        ["AccountRevision.ForeignKeyAudit"] = AccountRevision.ForeignKeyAudit,
        ["AccountRevision.CheckValues"] = AccountRevision.CheckValues,
        ["AccountRevision.GuardFunction"] = AccountRevision.GuardFunction,
        ["AccountRevision.GuardRow"] = AccountRevision.GuardRow,
        ["AccountRevision.GuardTruncate"] = AccountRevision.GuardTruncate,
    });
}
