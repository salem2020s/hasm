namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0008 (Phase 3.2B, slice S4, approval policies and decisions). Physical names are opaque and fixed.
public static partial class CompanySchema
{
    public static class ApprovalPolicy
    {
        public const string Table = "t_8f2462c5";
        public const string Id = "c_a2b53e06";
        public const string DocType = "c_9c04c465";
        public const string Source = "c_b130782f";
        public const string BranchId = "c_5d70e226";
        public const string MinAmount = "c_a0b367ec";
        public const string RequiredApprovals = "c_a874380a";
        public const string NameAr = "c_2de53c75";
        public const string NameEn = "c_e5bbb507";
        public const string IsActive = "c_61d707fd";
        public const string Revision = "c_c9bff476";
        public const string CreatedAtUtc = "c_bc3b07c1";
        public const string UpdatedAtUtc = "c_4fb325f1";
        public const string PrimaryKey = "k_202ebd22";
        public const string ForeignKeyBranch = "k_9d1f135b";
        public const string CheckValues = "k_ecd43dd2";
        public const string CheckNames = "k_511df159";
        public const string CheckRevision = "k_3c36b22b";
        public const string ActiveIndex = "k_6d354b7e";
        public const string GuardFunction = "f_25cf6e1b";
        public const string GuardRow = "r_1fa261cf";
        public const string GuardTruncate = "r_0fceb20e";
        public const string RequiredFunction = "f_660e4c73";
        public const string SatisfiedFunction = "f_7cdbb48f";
    }

    public static class ApprovalDecision
    {
        public const string Table = "t_3fa074e5";
        public const string Id = "c_6aeb80ff";
        public const string DocType = "c_9adee5c9";
        public const string DocumentId = "c_175a20c9";
        public const string DocumentRevision = "c_f399e237";
        public const string Approver = "c_942d312c";
        public const string Decision = "c_3a789977";
        public const string DecidedAtUtc = "c_9d1b7065";
        public const string Comment = "c_a3ecb6f1";
        public const string AuditStream = "c_17279abc";
        public const string AuditSeq = "c_0d6e0bbc";
        public const string PrimaryKey = "k_3b3caa4b";
        public const string UniqueDecision = "k_d6960f83";
        public const string UniqueAuditEvent = "k_e6168660";
        public const string ForeignKeyAudit = "k_0bfb0744";
        public const string CheckValues = "k_d0d8b4b2";
        public const string GuardFunction = "f_e2da8d13";
        public const string GuardRow = "r_2356bf28";
        public const string GuardTruncate = "r_518d224c";
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> approvalMap = new(() => new Dictionary<string, string>
    {
        ["ApprovalPolicy"] = ApprovalPolicy.Table,
        ["ApprovalPolicy.Id"] = ApprovalPolicy.Id,
        ["ApprovalPolicy.DocType"] = ApprovalPolicy.DocType,
        ["ApprovalPolicy.Source"] = ApprovalPolicy.Source,
        ["ApprovalPolicy.BranchId"] = ApprovalPolicy.BranchId,
        ["ApprovalPolicy.MinAmount"] = ApprovalPolicy.MinAmount,
        ["ApprovalPolicy.RequiredApprovals"] = ApprovalPolicy.RequiredApprovals,
        ["ApprovalPolicy.NameAr"] = ApprovalPolicy.NameAr,
        ["ApprovalPolicy.NameEn"] = ApprovalPolicy.NameEn,
        ["ApprovalPolicy.IsActive"] = ApprovalPolicy.IsActive,
        ["ApprovalPolicy.Revision"] = ApprovalPolicy.Revision,
        ["ApprovalPolicy.CreatedAtUtc"] = ApprovalPolicy.CreatedAtUtc,
        ["ApprovalPolicy.UpdatedAtUtc"] = ApprovalPolicy.UpdatedAtUtc,
        ["ApprovalPolicy.PrimaryKey"] = ApprovalPolicy.PrimaryKey,
        ["ApprovalPolicy.ForeignKeyBranch"] = ApprovalPolicy.ForeignKeyBranch,
        ["ApprovalPolicy.CheckValues"] = ApprovalPolicy.CheckValues,
        ["ApprovalPolicy.CheckNames"] = ApprovalPolicy.CheckNames,
        ["ApprovalPolicy.CheckRevision"] = ApprovalPolicy.CheckRevision,
        ["ApprovalPolicy.ActiveIndex"] = ApprovalPolicy.ActiveIndex,
        ["ApprovalPolicy.GuardFunction"] = ApprovalPolicy.GuardFunction,
        ["ApprovalPolicy.GuardRow"] = ApprovalPolicy.GuardRow,
        ["ApprovalPolicy.GuardTruncate"] = ApprovalPolicy.GuardTruncate,
        ["ApprovalPolicy.RequiredFunction"] = ApprovalPolicy.RequiredFunction,
        ["ApprovalPolicy.SatisfiedFunction"] = ApprovalPolicy.SatisfiedFunction,
        ["ApprovalDecision"] = ApprovalDecision.Table,
        ["ApprovalDecision.Id"] = ApprovalDecision.Id,
        ["ApprovalDecision.DocType"] = ApprovalDecision.DocType,
        ["ApprovalDecision.DocumentId"] = ApprovalDecision.DocumentId,
        ["ApprovalDecision.DocumentRevision"] = ApprovalDecision.DocumentRevision,
        ["ApprovalDecision.Approver"] = ApprovalDecision.Approver,
        ["ApprovalDecision.Decision"] = ApprovalDecision.Decision,
        ["ApprovalDecision.DecidedAtUtc"] = ApprovalDecision.DecidedAtUtc,
        ["ApprovalDecision.Comment"] = ApprovalDecision.Comment,
        ["ApprovalDecision.AuditStream"] = ApprovalDecision.AuditStream,
        ["ApprovalDecision.AuditSeq"] = ApprovalDecision.AuditSeq,
        ["ApprovalDecision.PrimaryKey"] = ApprovalDecision.PrimaryKey,
        ["ApprovalDecision.UniqueDecision"] = ApprovalDecision.UniqueDecision,
        ["ApprovalDecision.UniqueAuditEvent"] = ApprovalDecision.UniqueAuditEvent,
        ["ApprovalDecision.ForeignKeyAudit"] = ApprovalDecision.ForeignKeyAudit,
        ["ApprovalDecision.CheckValues"] = ApprovalDecision.CheckValues,
        ["ApprovalDecision.GuardFunction"] = ApprovalDecision.GuardFunction,
        ["ApprovalDecision.GuardRow"] = ApprovalDecision.GuardRow,
        ["ApprovalDecision.GuardTruncate"] = ApprovalDecision.GuardTruncate,
    });
    private static IReadOnlyDictionary<string, string> ApprovalMap => approvalMap.Value;
}
