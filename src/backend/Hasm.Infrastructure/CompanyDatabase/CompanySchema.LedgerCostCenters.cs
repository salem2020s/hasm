namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0008 (Phase 3.2B, slice S3, cost centers). Physical names are opaque and fixed.
public static partial class CompanySchema
{
    public static class CostCenter
    {
        public const string Table = "t_939ae118";
        public const string Id = "c_0770cd8f";
        public const string Code = "c_157be526";
        public const string NameAr = "c_9ce86e1c";
        public const string NameEn = "c_fdaf9504";
        public const string ParentId = "c_28b981db";
        public const string BranchId = "c_8d396764";
        public const string IsGroup = "c_85d71877";
        public const string IsActive = "c_8a8af184";
        public const string Revision = "c_0a692ced";
        public const string CreatedAtUtc = "c_ab43e719";
        public const string UpdatedAtUtc = "c_00a35896";
        public const string PrimaryKey = "k_0abaccee";
        public const string ForeignKeyParent = "k_b0879c0d";
        public const string ForeignKeyBranch = "k_2e043fa2";
        public const string CheckCode = "k_192f90d0";
        public const string CheckNames = "k_4b3cb87b";
        public const string CheckSelfParent = "k_b107bf76";
        public const string CheckRevision = "k_624f5166";
        public const string UniqueCode = "k_6268e18f";
        public const string ParentIndex = "k_d04065b3";
        public const string GuardFunction = "f_0b7108b0";
        public const string GuardRow = "r_7f3a3310";
        public const string GuardTruncate = "r_820f2fa2";
        public const string TreeFunction = "f_4a0f86c6";
        public const string TreeTrigger = "r_f46df562";
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> costCenterMap = new(() => new Dictionary<string, string>
    {
        ["CostCenter"] = CostCenter.Table,
        ["CostCenter.Id"] = CostCenter.Id,
        ["CostCenter.Code"] = CostCenter.Code,
        ["CostCenter.NameAr"] = CostCenter.NameAr,
        ["CostCenter.NameEn"] = CostCenter.NameEn,
        ["CostCenter.ParentId"] = CostCenter.ParentId,
        ["CostCenter.BranchId"] = CostCenter.BranchId,
        ["CostCenter.IsGroup"] = CostCenter.IsGroup,
        ["CostCenter.IsActive"] = CostCenter.IsActive,
        ["CostCenter.Revision"] = CostCenter.Revision,
        ["CostCenter.CreatedAtUtc"] = CostCenter.CreatedAtUtc,
        ["CostCenter.UpdatedAtUtc"] = CostCenter.UpdatedAtUtc,
        ["CostCenter.PrimaryKey"] = CostCenter.PrimaryKey,
        ["CostCenter.ForeignKeyParent"] = CostCenter.ForeignKeyParent,
        ["CostCenter.ForeignKeyBranch"] = CostCenter.ForeignKeyBranch,
        ["CostCenter.CheckCode"] = CostCenter.CheckCode,
        ["CostCenter.CheckNames"] = CostCenter.CheckNames,
        ["CostCenter.CheckSelfParent"] = CostCenter.CheckSelfParent,
        ["CostCenter.CheckRevision"] = CostCenter.CheckRevision,
        ["CostCenter.UniqueCode"] = CostCenter.UniqueCode,
        ["CostCenter.ParentIndex"] = CostCenter.ParentIndex,
        ["CostCenter.GuardFunction"] = CostCenter.GuardFunction,
        ["CostCenter.GuardRow"] = CostCenter.GuardRow,
        ["CostCenter.GuardTruncate"] = CostCenter.GuardTruncate,
        ["CostCenter.TreeFunction"] = CostCenter.TreeFunction,
        ["CostCenter.TreeTrigger"] = CostCenter.TreeTrigger,
    });
    private static IReadOnlyDictionary<string, string> CostCenterMap => costCenterMap.Value;
}
