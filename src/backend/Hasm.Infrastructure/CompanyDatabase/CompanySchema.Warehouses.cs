namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0005 (Phase 3.1C, warehouses). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class Warehouse
    {
        public const string Table = "t_d2006f04";
        public const string Id = "c_a9f73cc2";
        public const string Code = "c_e5c7450f";
        public const string BranchId = "c_46594f28";
        public const string NameAr = "c_50b75a87";
        public const string NameEn = "c_7222ad5f";
        public const string IsActive = "c_49cbc053";
        public const string ActivationEpoch = "c_e67b716c";
        public const string Address1 = "c_cb813fae";
        public const string Address2 = "c_4c0b4e36";
        public const string City = "c_88303a9c";
        public const string Region = "c_5233109b";
        public const string PostalCode = "c_7aa520de";
        public const string Phone = "c_9e684076";
        public const string Email = "c_10fcba74";
        public const string Website = "c_2f78c781";
        public const string Revision = "c_0fd28ae9";
        public const string CreatedAtUtc = "c_f1a1b71a";
        public const string UpdatedAtUtc = "c_6b856378";
        public const string PrimaryKey = "k_75e42766";
        public const string ForeignKeyBranch = "k_41c97f41";
        public const string CheckCode = "k_c973cec7";
        public const string CheckName = "k_0788216a";
        public const string CheckEpoch = "k_08f9cdea";
        public const string CheckRevision = "k_916f0f66";
        public const string UniqueCode = "k_bd8b1492";
        public const string BranchIndex = "k_24562720";
        public const string GuardFunction = "f_bf193561";
        public const string GuardRow = "r_6b48247b";
        public const string GuardTruncate = "r_c49466ee";
        public const string LinkFunction = "f_1080838c";
        public const string LinkWarehouseTrigger = "r_c2627527";
        public const string LinkBranchTrigger = "r_a3fc163f";
    }

    public static class WarehouseRevision
    {
        public const string Table = "t_49dad4e5";
        public const string Id = "c_292de6c9";
        public const string WarehouseId = "c_029c85f8";
        public const string Revision = "c_671480fa";
        public const string ActorUserId = "c_fd03261e";
        public const string ChangedAtUtc = "c_e0da49ec";
        public const string Changes = "c_a0d45e59";
        public const string AuditStream = "c_aeac7f2b";
        public const string AuditSeq = "c_21cb4a92";
        public const string PrimaryKey = "k_db659dbf";
        public const string UniqueWarehouseRevision = "k_53cd24cb";
        public const string UniqueAuditEvent = "k_dc2938dd";
        public const string ForeignKeyWarehouse = "k_3ee8ec0b";
        public const string ForeignKeyAudit = "k_3f3b3d0c";
        public const string CheckValues = "k_a1a2fb06";
        public const string GuardFunction = "f_da4f889c";
        public const string GuardRow = "r_01ed82c3";
        public const string GuardTruncate = "r_6a02c14c";
    }

    private static IReadOnlyDictionary<string, string> WarehouseMap => warehouseMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> warehouseMap = new(() => new Dictionary<string, string>
    {
        ["Warehouse"] = Warehouse.Table,
        ["Warehouse.Id"] = Warehouse.Id,
        ["Warehouse.Code"] = Warehouse.Code,
        ["Warehouse.BranchId"] = Warehouse.BranchId,
        ["Warehouse.NameAr"] = Warehouse.NameAr,
        ["Warehouse.NameEn"] = Warehouse.NameEn,
        ["Warehouse.IsActive"] = Warehouse.IsActive,
        ["Warehouse.ActivationEpoch"] = Warehouse.ActivationEpoch,
        ["Warehouse.Address1"] = Warehouse.Address1,
        ["Warehouse.Address2"] = Warehouse.Address2,
        ["Warehouse.City"] = Warehouse.City,
        ["Warehouse.Region"] = Warehouse.Region,
        ["Warehouse.PostalCode"] = Warehouse.PostalCode,
        ["Warehouse.Phone"] = Warehouse.Phone,
        ["Warehouse.Email"] = Warehouse.Email,
        ["Warehouse.Website"] = Warehouse.Website,
        ["Warehouse.Revision"] = Warehouse.Revision,
        ["Warehouse.CreatedAtUtc"] = Warehouse.CreatedAtUtc,
        ["Warehouse.UpdatedAtUtc"] = Warehouse.UpdatedAtUtc,
        ["Warehouse.PrimaryKey"] = Warehouse.PrimaryKey,
        ["Warehouse.ForeignKeyBranch"] = Warehouse.ForeignKeyBranch,
        ["Warehouse.CheckCode"] = Warehouse.CheckCode,
        ["Warehouse.CheckName"] = Warehouse.CheckName,
        ["Warehouse.CheckEpoch"] = Warehouse.CheckEpoch,
        ["Warehouse.CheckRevision"] = Warehouse.CheckRevision,
        ["Warehouse.UniqueCode"] = Warehouse.UniqueCode,
        ["Warehouse.BranchIndex"] = Warehouse.BranchIndex,
        ["Warehouse.GuardFunction"] = Warehouse.GuardFunction,
        ["Warehouse.GuardRow"] = Warehouse.GuardRow,
        ["Warehouse.GuardTruncate"] = Warehouse.GuardTruncate,
        ["Warehouse.LinkFunction"] = Warehouse.LinkFunction,
        ["Warehouse.LinkWarehouseTrigger"] = Warehouse.LinkWarehouseTrigger,
        ["Warehouse.LinkBranchTrigger"] = Warehouse.LinkBranchTrigger,
        ["WarehouseRevision"] = WarehouseRevision.Table,
        ["WarehouseRevision.Id"] = WarehouseRevision.Id,
        ["WarehouseRevision.WarehouseId"] = WarehouseRevision.WarehouseId,
        ["WarehouseRevision.Revision"] = WarehouseRevision.Revision,
        ["WarehouseRevision.ActorUserId"] = WarehouseRevision.ActorUserId,
        ["WarehouseRevision.ChangedAtUtc"] = WarehouseRevision.ChangedAtUtc,
        ["WarehouseRevision.Changes"] = WarehouseRevision.Changes,
        ["WarehouseRevision.AuditStream"] = WarehouseRevision.AuditStream,
        ["WarehouseRevision.AuditSeq"] = WarehouseRevision.AuditSeq,
        ["WarehouseRevision.PrimaryKey"] = WarehouseRevision.PrimaryKey,
        ["WarehouseRevision.UniqueWarehouseRevision"] = WarehouseRevision.UniqueWarehouseRevision,
        ["WarehouseRevision.UniqueAuditEvent"] = WarehouseRevision.UniqueAuditEvent,
        ["WarehouseRevision.ForeignKeyWarehouse"] = WarehouseRevision.ForeignKeyWarehouse,
        ["WarehouseRevision.ForeignKeyAudit"] = WarehouseRevision.ForeignKeyAudit,
        ["WarehouseRevision.CheckValues"] = WarehouseRevision.CheckValues,
        ["WarehouseRevision.GuardFunction"] = WarehouseRevision.GuardFunction,
        ["WarehouseRevision.GuardRow"] = WarehouseRevision.GuardRow,
        ["WarehouseRevision.GuardTruncate"] = WarehouseRevision.GuardTruncate,
    });
}
