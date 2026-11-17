namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0015 (Phase 3.7A, inventory accounting configuration). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class InventoryAccountingVersion
    {
        public const string Table = "t_a78f5224";
        public const string Id = "c_3bffe448";
        public const string VersionNo = "c_004ae1f7";
        public const string CreatedAtUtc = "c_cc3839ff";
        public const string CreatedBy = "c_daea169c";
        public const string Note = "c_f7b0ed06";
        public const string DefaultAccountId = "c_924e88aa";
        public const string ReceiptCounterAccountId = "c_0edb6645";
        public const string IssueCounterAccountId = "c_1661ba21";
        public const string GroupCount = "c_3ab635fe";
        public const string ContentHash = "c_767c84c9";
        /// <summary>Company migration 0019 (Phase 3.8B-2): the counter account of stock adjustments (null in every version saved before; nothing with value can be adjusted until one is configured).</summary>
        public const string AdjustmentCounterAccountId = "c_76c92b02";
        public const string ForeignKeyAdjustment = "k_cceb2706";
        /// <summary>Company migration 0020 (Phase 3.8B-3): the provisional obligation account O and the settlement variance account V of the negative-stock exception (null in every version saved before).</summary>
        public const string ObligationAccountId = "c_17133030";
        public const string VarianceAccountId = "c_3dcd9eb2";
        public const string ForeignKeyObligationAccount = "k_3d8110d0";
        public const string ForeignKeyVarianceAccount = "k_807d7ceb";
        public const string PrimaryKey = "k_7bd31da5";
        public const string UniqueVersion = "k_6843e5c1";
        public const string ForeignKeyDefault = "k_753f71eb";
        public const string ForeignKeyReceipt = "k_8bf4d88f";
        public const string ForeignKeyIssue = "k_11728294";
        public const string CheckValues = "k_883e3b4b";
        public const string GuardFunction = "f_969e1275";
        public const string GuardRow = "r_e0da33d6";
        public const string GuardTruncate = "r_c90a9345";
    }

    public static class InventoryAccountingGroup
    {
        public const string Table = "t_af40caae";
        public const string VersionId = "c_8b2f8137";
        public const string GroupId = "c_57750caa";
        public const string AccountId = "c_f406a771";
        public const string PrimaryKey = "k_58cc7193";
        public const string ForeignKeyVersion = "k_6ea0b3d4";
        public const string ForeignKeyGroup = "k_4fc40bed";
        public const string ForeignKeyAccount = "k_a437cec0";
        public const string AccountIndex = "k_09ac4cbd";
        public const string GuardFunction = "f_4853d4c1";
        public const string GuardRow = "r_51c58d49";
        public const string GuardTruncate = "r_3fcf0a46";
    }

    private static IReadOnlyDictionary<string, string> InventoryAccountingMap => inventoryAccountingMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> inventoryAccountingMap = new(() => new Dictionary<string, string>
    {
        ["InventoryAccountingVersion"] = InventoryAccountingVersion.Table,
        ["InventoryAccountingVersion.Id"] = InventoryAccountingVersion.Id,
        ["InventoryAccountingVersion.VersionNo"] = InventoryAccountingVersion.VersionNo,
        ["InventoryAccountingVersion.CreatedAtUtc"] = InventoryAccountingVersion.CreatedAtUtc,
        ["InventoryAccountingVersion.CreatedBy"] = InventoryAccountingVersion.CreatedBy,
        ["InventoryAccountingVersion.Note"] = InventoryAccountingVersion.Note,
        ["InventoryAccountingVersion.DefaultAccountId"] = InventoryAccountingVersion.DefaultAccountId,
        ["InventoryAccountingVersion.ReceiptCounterAccountId"] = InventoryAccountingVersion.ReceiptCounterAccountId,
        ["InventoryAccountingVersion.IssueCounterAccountId"] = InventoryAccountingVersion.IssueCounterAccountId,
        ["InventoryAccountingVersion.GroupCount"] = InventoryAccountingVersion.GroupCount,
        ["InventoryAccountingVersion.ContentHash"] = InventoryAccountingVersion.ContentHash,
        ["InventoryAccountingVersion.PrimaryKey"] = InventoryAccountingVersion.PrimaryKey,
        ["InventoryAccountingVersion.UniqueVersion"] = InventoryAccountingVersion.UniqueVersion,
        ["InventoryAccountingVersion.ForeignKeyDefault"] = InventoryAccountingVersion.ForeignKeyDefault,
        ["InventoryAccountingVersion.ForeignKeyReceipt"] = InventoryAccountingVersion.ForeignKeyReceipt,
        ["InventoryAccountingVersion.ForeignKeyIssue"] = InventoryAccountingVersion.ForeignKeyIssue,
        ["InventoryAccountingVersion.CheckValues"] = InventoryAccountingVersion.CheckValues,
        ["InventoryAccountingVersion.GuardFunction"] = InventoryAccountingVersion.GuardFunction,
        ["InventoryAccountingVersion.GuardRow"] = InventoryAccountingVersion.GuardRow,
        ["InventoryAccountingVersion.GuardTruncate"] = InventoryAccountingVersion.GuardTruncate,
        ["InventoryAccountingVersion.AdjustmentCounterAccountId"] = InventoryAccountingVersion.AdjustmentCounterAccountId,
        ["InventoryAccountingVersion.ForeignKeyAdjustment"] = InventoryAccountingVersion.ForeignKeyAdjustment,
        ["InventoryAccountingVersion.ObligationAccountId"] = InventoryAccountingVersion.ObligationAccountId,
        ["InventoryAccountingVersion.VarianceAccountId"] = InventoryAccountingVersion.VarianceAccountId,
        ["InventoryAccountingVersion.ForeignKeyObligationAccount"] = InventoryAccountingVersion.ForeignKeyObligationAccount,
        ["InventoryAccountingVersion.ForeignKeyVarianceAccount"] = InventoryAccountingVersion.ForeignKeyVarianceAccount,
        ["InventoryAccountingGroup"] = InventoryAccountingGroup.Table,
        ["InventoryAccountingGroup.VersionId"] = InventoryAccountingGroup.VersionId,
        ["InventoryAccountingGroup.GroupId"] = InventoryAccountingGroup.GroupId,
        ["InventoryAccountingGroup.AccountId"] = InventoryAccountingGroup.AccountId,
        ["InventoryAccountingGroup.PrimaryKey"] = InventoryAccountingGroup.PrimaryKey,
        ["InventoryAccountingGroup.ForeignKeyVersion"] = InventoryAccountingGroup.ForeignKeyVersion,
        ["InventoryAccountingGroup.ForeignKeyGroup"] = InventoryAccountingGroup.ForeignKeyGroup,
        ["InventoryAccountingGroup.ForeignKeyAccount"] = InventoryAccountingGroup.ForeignKeyAccount,
        ["InventoryAccountingGroup.AccountIndex"] = InventoryAccountingGroup.AccountIndex,
        ["InventoryAccountingGroup.GuardFunction"] = InventoryAccountingGroup.GuardFunction,
        ["InventoryAccountingGroup.GuardRow"] = InventoryAccountingGroup.GuardRow,
        ["InventoryAccountingGroup.GuardTruncate"] = InventoryAccountingGroup.GuardTruncate,
    });
}
