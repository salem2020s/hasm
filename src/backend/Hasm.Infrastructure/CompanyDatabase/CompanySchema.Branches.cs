namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0004 (Phase 3.1B, branches). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class Branch
    {
        public const string Table = "t_8ba6c27d";
        public const string Id = "c_358158d2";
        public const string Code = "c_b6f1610c";
        public const string NameAr = "c_867f4322";
        public const string NameEn = "c_5fb942ca";
        public const string IsActive = "c_6605fbbc";
        public const string IsMain = "c_800064c9";
        public const string ActivationEpoch = "c_ca87abff";
        public const string Address1 = "c_dfcc1567";
        public const string Address2 = "c_f2e7f7b2";
        public const string City = "c_021a26d9";
        public const string Region = "c_93898dcd";
        public const string PostalCode = "c_656adcd4";
        public const string Phone = "c_85e55384";
        public const string Email = "c_b06bd18e";
        public const string Website = "c_18b78b39";
        public const string SeedMigration = "c_6c940bd3";
        public const string Revision = "c_7bf26af0";
        public const string CreatedAtUtc = "c_fd99167e";
        public const string UpdatedAtUtc = "c_2e3703ee";
        public const string PrimaryKey = "k_148f3917";
        public const string CheckCode = "k_b4deb97b";
        public const string CheckName = "k_eb34de98";
        public const string CheckMain = "k_b6b8f9bf";
        public const string CheckEpoch = "k_dd62a9c0";
        public const string CheckRevision = "k_dc2b3047";
        public const string CheckSeed = "k_90ce2e86";
        public const string UniqueCode = "k_24dea8d2";
        public const string UniqueMain = "k_d9b51fde";
        public const string GuardFunction = "f_f2c1b913";
        public const string GuardRow = "r_21c6ea13";
        public const string GuardTruncate = "r_55d4e2e7";
        public const string MainFunction = "f_2ae6347c";
        public const string MainTrigger = "r_30431c75";
    }

    public static class BranchRevision
    {
        public const string Table = "t_cd2deb17";
        public const string Id = "c_61c1cc7d";
        public const string BranchId = "c_ef189f30";
        public const string Revision = "c_bcd685d4";
        public const string ActorUserId = "c_6f8886b4";
        public const string ChangedAtUtc = "c_43fb2a4b";
        public const string Changes = "c_30b95919";
        public const string AuditStream = "c_5bb64bd0";
        public const string AuditSeq = "c_5cca8b25";
        public const string PrimaryKey = "k_1c3524fa";
        public const string UniqueBranchRevision = "k_5b1819b6";
        public const string UniqueAuditEvent = "k_095db982";
        public const string ForeignKeyBranch = "k_388a9277";
        public const string ForeignKeyAudit = "k_b8e1b7fd";
        public const string CheckValues = "k_f0445506";
        public const string GuardFunction = "f_a74edb08";
        public const string GuardRow = "r_e5e0dff0";
        public const string GuardTruncate = "r_2dfce87e";
    }

    private static IReadOnlyDictionary<string, string> BranchMap => branchMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> branchMap = new(() => new Dictionary<string, string>
    {
        ["Branch"] = Branch.Table,
        ["Branch.Id"] = Branch.Id,
        ["Branch.Code"] = Branch.Code,
        ["Branch.NameAr"] = Branch.NameAr,
        ["Branch.NameEn"] = Branch.NameEn,
        ["Branch.IsActive"] = Branch.IsActive,
        ["Branch.IsMain"] = Branch.IsMain,
        ["Branch.ActivationEpoch"] = Branch.ActivationEpoch,
        ["Branch.Address1"] = Branch.Address1,
        ["Branch.Address2"] = Branch.Address2,
        ["Branch.City"] = Branch.City,
        ["Branch.Region"] = Branch.Region,
        ["Branch.PostalCode"] = Branch.PostalCode,
        ["Branch.Phone"] = Branch.Phone,
        ["Branch.Email"] = Branch.Email,
        ["Branch.Website"] = Branch.Website,
        ["Branch.SeedMigration"] = Branch.SeedMigration,
        ["Branch.Revision"] = Branch.Revision,
        ["Branch.CreatedAtUtc"] = Branch.CreatedAtUtc,
        ["Branch.UpdatedAtUtc"] = Branch.UpdatedAtUtc,
        ["Branch.PrimaryKey"] = Branch.PrimaryKey,
        ["Branch.CheckCode"] = Branch.CheckCode,
        ["Branch.CheckName"] = Branch.CheckName,
        ["Branch.CheckMain"] = Branch.CheckMain,
        ["Branch.CheckEpoch"] = Branch.CheckEpoch,
        ["Branch.CheckRevision"] = Branch.CheckRevision,
        ["Branch.CheckSeed"] = Branch.CheckSeed,
        ["Branch.UniqueCode"] = Branch.UniqueCode,
        ["Branch.UniqueMain"] = Branch.UniqueMain,
        ["Branch.GuardFunction"] = Branch.GuardFunction,
        ["Branch.GuardRow"] = Branch.GuardRow,
        ["Branch.GuardTruncate"] = Branch.GuardTruncate,
        ["Branch.MainFunction"] = Branch.MainFunction,
        ["Branch.MainTrigger"] = Branch.MainTrigger,
        ["BranchRevision"] = BranchRevision.Table,
        ["BranchRevision.Id"] = BranchRevision.Id,
        ["BranchRevision.BranchId"] = BranchRevision.BranchId,
        ["BranchRevision.Revision"] = BranchRevision.Revision,
        ["BranchRevision.ActorUserId"] = BranchRevision.ActorUserId,
        ["BranchRevision.ChangedAtUtc"] = BranchRevision.ChangedAtUtc,
        ["BranchRevision.Changes"] = BranchRevision.Changes,
        ["BranchRevision.AuditStream"] = BranchRevision.AuditStream,
        ["BranchRevision.AuditSeq"] = BranchRevision.AuditSeq,
        ["BranchRevision.PrimaryKey"] = BranchRevision.PrimaryKey,
        ["BranchRevision.UniqueBranchRevision"] = BranchRevision.UniqueBranchRevision,
        ["BranchRevision.UniqueAuditEvent"] = BranchRevision.UniqueAuditEvent,
        ["BranchRevision.ForeignKeyBranch"] = BranchRevision.ForeignKeyBranch,
        ["BranchRevision.ForeignKeyAudit"] = BranchRevision.ForeignKeyAudit,
        ["BranchRevision.CheckValues"] = BranchRevision.CheckValues,
        ["BranchRevision.GuardFunction"] = BranchRevision.GuardFunction,
        ["BranchRevision.GuardRow"] = BranchRevision.GuardRow,
        ["BranchRevision.GuardTruncate"] = BranchRevision.GuardTruncate,

    });
}
