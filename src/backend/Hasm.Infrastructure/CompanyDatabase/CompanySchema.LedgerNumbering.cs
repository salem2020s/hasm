namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0008 (Phase 3.2B, ledger foundation, slice S1). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class LedgerHistory
    {
        public const string Table = "t_6eec2227";
        public const string Id = "c_a7885cb2";
        public const string Entity = "c_03da32ec";
        public const string EntityKey = "c_f1c80996";
        public const string Revision = "c_10a3a331";
        public const string ActorUserId = "c_1194b7b0";
        public const string ChangedAtUtc = "c_31315446";
        public const string Changes = "c_1826908c";
        public const string AuditStream = "c_e2b8b005";
        public const string AuditSeq = "c_d3212691";
        public const string PrimaryKey = "k_3c3e2427";
        public const string UniqueRevision = "k_43abeb9e";
        public const string UniqueAuditEvent = "k_5dd892cc";
        public const string ForeignKeyAudit = "k_4a7c34e0";
        public const string CheckValues = "k_2d8d01ee";
        public const string GuardFunction = "f_0e4aded2";
        public const string GuardRow = "r_652b3b09";
        public const string GuardTruncate = "r_f403d7b8";
    }

    public static class NumberingRule
    {
        public const string Table = "t_5fda8ef3";
        public const string DocType = "c_095d6370";
        public const string Prefix = "c_0ac3602f";
        public const string Separator = "c_1630474f";
        public const string Width = "c_baec1230";
        public const string PerBranch = "c_80cf6dab";
        public const string PerFiscalYear = "c_724258b6";
        public const string Revision = "c_dd953656";
        public const string UpdatedAtUtc = "c_837d82b7";
        public const string PrimaryKey = "k_259acd92";
        public const string CheckDocType = "k_5cd883dc";
        public const string CheckPrefix = "k_b6a2eebc";
        public const string CheckSeparator = "k_7f92f98a";
        public const string CheckWidth = "k_e7fc456c";
        public const string CheckRevision = "k_c979c3d4";
        public const string GuardFunction = "f_d3834231";
        public const string GuardRow = "r_f46025ec";
        public const string GuardTruncate = "r_8535f654";
    }

    public static class NumberCounter
    {
        public const string Table = "t_2dd9917f";
        public const string DocType = "c_21d1110f";
        public const string BranchId = "c_708d4efd";
        public const string FiscalYearId = "c_2e69fe44";
        public const string LastValue = "c_9df284e3";
        public const string PrimaryKey = "k_a8d49511";
        public const string CheckLast = "k_f2d24336";
        public const string GuardFunction = "f_0fd105d1";
        public const string GuardRow = "r_43f2a9d6";
        public const string GuardTruncate = "r_11acc643";
        public const string CheckFunction = "f_bfc637d5";
        public const string CheckTrigger = "r_267e6ad3";
    }

    public static class NumberIssue
    {
        public const string Table = "t_738092a9";
        public const string Id = "c_a52512d6";
        public const string DocType = "c_547ade38";
        public const string Number = "c_39d67608";
        public const string BranchId = "c_53cb6b88";
        public const string FiscalYearId = "c_7aa75e94";
        public const string Value = "c_8573021c";
        public const string DocumentId = "c_9a091df7";
        public const string IssuedAtUtc = "c_dc70ea5c";
        public const string IssuedBy = "c_5dafa34c";
        public const string VoidedAtUtc = "c_7b2ec8bb";
        public const string VoidedBy = "c_f0eb68b6";
        public const string VoidReason = "c_b02aa3e9";
        public const string PrimaryKey = "k_6fecea87";
        public const string UniqueNumber = "k_026e90ca";
        public const string UniqueValue = "k_13a56f8e";
        public const string CheckValue = "k_0f947540";
        public const string CheckVoid = "k_e850a063";
        public const string GuardFunction = "f_ec66526a";
        public const string GuardRow = "r_45b8c86a";
        public const string GuardTruncate = "r_563a795d";
    }

    private static IReadOnlyDictionary<string, string> NumberingMap => numberingMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> numberingMap = new(() => new Dictionary<string, string>
    {
        ["LedgerHistory"] = LedgerHistory.Table,
        ["LedgerHistory.Id"] = LedgerHistory.Id,
        ["LedgerHistory.Entity"] = LedgerHistory.Entity,
        ["LedgerHistory.EntityKey"] = LedgerHistory.EntityKey,
        ["LedgerHistory.Revision"] = LedgerHistory.Revision,
        ["LedgerHistory.ActorUserId"] = LedgerHistory.ActorUserId,
        ["LedgerHistory.ChangedAtUtc"] = LedgerHistory.ChangedAtUtc,
        ["LedgerHistory.Changes"] = LedgerHistory.Changes,
        ["LedgerHistory.AuditStream"] = LedgerHistory.AuditStream,
        ["LedgerHistory.AuditSeq"] = LedgerHistory.AuditSeq,
        ["LedgerHistory.PrimaryKey"] = LedgerHistory.PrimaryKey,
        ["LedgerHistory.UniqueRevision"] = LedgerHistory.UniqueRevision,
        ["LedgerHistory.UniqueAuditEvent"] = LedgerHistory.UniqueAuditEvent,
        ["LedgerHistory.ForeignKeyAudit"] = LedgerHistory.ForeignKeyAudit,
        ["LedgerHistory.CheckValues"] = LedgerHistory.CheckValues,
        ["LedgerHistory.GuardFunction"] = LedgerHistory.GuardFunction,
        ["LedgerHistory.GuardRow"] = LedgerHistory.GuardRow,
        ["LedgerHistory.GuardTruncate"] = LedgerHistory.GuardTruncate,
        ["NumberingRule"] = NumberingRule.Table,
        ["NumberingRule.DocType"] = NumberingRule.DocType,
        ["NumberingRule.Prefix"] = NumberingRule.Prefix,
        ["NumberingRule.Separator"] = NumberingRule.Separator,
        ["NumberingRule.Width"] = NumberingRule.Width,
        ["NumberingRule.PerBranch"] = NumberingRule.PerBranch,
        ["NumberingRule.PerFiscalYear"] = NumberingRule.PerFiscalYear,
        ["NumberingRule.Revision"] = NumberingRule.Revision,
        ["NumberingRule.UpdatedAtUtc"] = NumberingRule.UpdatedAtUtc,
        ["NumberingRule.PrimaryKey"] = NumberingRule.PrimaryKey,
        ["NumberingRule.CheckDocType"] = NumberingRule.CheckDocType,
        ["NumberingRule.CheckPrefix"] = NumberingRule.CheckPrefix,
        ["NumberingRule.CheckSeparator"] = NumberingRule.CheckSeparator,
        ["NumberingRule.CheckWidth"] = NumberingRule.CheckWidth,
        ["NumberingRule.CheckRevision"] = NumberingRule.CheckRevision,
        ["NumberingRule.GuardFunction"] = NumberingRule.GuardFunction,
        ["NumberingRule.GuardRow"] = NumberingRule.GuardRow,
        ["NumberingRule.GuardTruncate"] = NumberingRule.GuardTruncate,
        ["NumberCounter"] = NumberCounter.Table,
        ["NumberCounter.DocType"] = NumberCounter.DocType,
        ["NumberCounter.BranchId"] = NumberCounter.BranchId,
        ["NumberCounter.FiscalYearId"] = NumberCounter.FiscalYearId,
        ["NumberCounter.LastValue"] = NumberCounter.LastValue,
        ["NumberCounter.PrimaryKey"] = NumberCounter.PrimaryKey,
        ["NumberCounter.CheckLast"] = NumberCounter.CheckLast,
        ["NumberCounter.GuardFunction"] = NumberCounter.GuardFunction,
        ["NumberCounter.GuardRow"] = NumberCounter.GuardRow,
        ["NumberCounter.GuardTruncate"] = NumberCounter.GuardTruncate,
        ["NumberCounter.CheckFunction"] = NumberCounter.CheckFunction,
        ["NumberCounter.CheckTrigger"] = NumberCounter.CheckTrigger,
        ["NumberIssue"] = NumberIssue.Table,
        ["NumberIssue.Id"] = NumberIssue.Id,
        ["NumberIssue.DocType"] = NumberIssue.DocType,
        ["NumberIssue.Number"] = NumberIssue.Number,
        ["NumberIssue.BranchId"] = NumberIssue.BranchId,
        ["NumberIssue.FiscalYearId"] = NumberIssue.FiscalYearId,
        ["NumberIssue.Value"] = NumberIssue.Value,
        ["NumberIssue.DocumentId"] = NumberIssue.DocumentId,
        ["NumberIssue.IssuedAtUtc"] = NumberIssue.IssuedAtUtc,
        ["NumberIssue.IssuedBy"] = NumberIssue.IssuedBy,
        ["NumberIssue.VoidedAtUtc"] = NumberIssue.VoidedAtUtc,
        ["NumberIssue.VoidedBy"] = NumberIssue.VoidedBy,
        ["NumberIssue.VoidReason"] = NumberIssue.VoidReason,
        ["NumberIssue.PrimaryKey"] = NumberIssue.PrimaryKey,
        ["NumberIssue.UniqueNumber"] = NumberIssue.UniqueNumber,
        ["NumberIssue.UniqueValue"] = NumberIssue.UniqueValue,
        ["NumberIssue.CheckValue"] = NumberIssue.CheckValue,
        ["NumberIssue.CheckVoid"] = NumberIssue.CheckVoid,
        ["NumberIssue.GuardFunction"] = NumberIssue.GuardFunction,
        ["NumberIssue.GuardRow"] = NumberIssue.GuardRow,
        ["NumberIssue.GuardTruncate"] = NumberIssue.GuardTruncate,
    });
}
