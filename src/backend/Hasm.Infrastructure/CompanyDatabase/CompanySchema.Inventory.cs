namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0012 (Phase 3.4, inventory master data). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class InventorySettings
    {
        public const string Table = "t_1cf46c0f";
        public const string Id = "c_0868f51b";
        public const string DefaultValuation = "c_f0c103a8";
        public const string NextItemNumber = "c_b8aab360";
        public const string Revision = "c_eecac29f";
        public const string UpdatedAtUtc = "c_764b950c";
        public const string PrimaryKey = "k_c0a58025";
        public const string CheckValues = "k_41ce5d2f";
        public const string GuardFunction = "f_33791e7e";
        public const string GuardRow = "r_0c1c1961";
        public const string GuardTruncate = "r_65746198";
    }

    public static class Unit
    {
        public const string Table = "t_aa91e313";
        public const string Id = "c_1a9eb66f";
        public const string Code = "c_e98ea32d";
        public const string NameAr = "c_2dbd90f5";
        public const string NameEn = "c_02e63ef2";
        public const string Decimals = "c_1ee2fd51";
        public const string IsActive = "c_e6766677";
        public const string Revision = "c_6b82548d";
        public const string CreatedAtUtc = "c_1bd324ca";
        public const string UpdatedAtUtc = "c_2fc80e7c";
        public const string PrimaryKey = "k_a85e079b";
        public const string CheckCode = "k_9788a119";
        public const string CheckName = "k_4bab0b6f";
        public const string CheckDecimals = "k_64d7d117";
        public const string CheckRevision = "k_cb953b32";
        public const string UniqueCode = "k_e0764ec7";
        public const string GuardFunction = "f_ad66057e";
        public const string GuardRow = "r_40966aed";
        public const string GuardTruncate = "r_93f8f8d3";
    }

    public static class ItemGroup
    {
        public const string Table = "t_2640a66e";
        public const string Id = "c_01551296";
        public const string Code = "c_3ac4d7b1";
        public const string NameAr = "c_969e6e5c";
        public const string NameEn = "c_b1774f64";
        public const string ParentId = "c_57852ce5";
        public const string Valuation = "c_cde997ad";
        public const string IsActive = "c_bc9669d9";
        public const string Revision = "c_c629b321";
        public const string CreatedAtUtc = "c_42e56d9e";
        public const string UpdatedAtUtc = "c_bef3cba1";
        public const string PrimaryKey = "k_92e65533";
        public const string ForeignKeyParent = "k_af185ecd";
        public const string CheckCode = "k_be07a826";
        public const string CheckName = "k_9e4fca1b";
        public const string CheckValuation = "k_c7f5b161";
        public const string CheckRevision = "k_dd9b7645";
        public const string CheckParent = "k_bd3f4920";
        public const string UniqueCode = "k_d7b9b2d7";
        public const string ParentIndex = "k_889e2742";
        public const string GuardFunction = "f_4864e4e9";
        public const string TreeFunction = "f_f2149d9d";
        public const string GuardRow = "r_50b0eeae";
        public const string GuardTruncate = "r_9ea931de";
        public const string TreeTrigger = "r_1f7882ef";
    }

    public static class Item
    {
        public const string Table = "t_2f2a7184";
        public const string Id = "c_be51f22e";
        public const string Code = "c_947f1dff";
        public const string NameAr = "c_8875cec0";
        public const string NameEn = "c_b29bc61e";
        public const string ItemType = "c_74e6031c";
        public const string GroupId = "c_61dee773";
        public const string BaseUnitId = "c_63bb9134";
        public const string PurchaseUnitId = "c_2f3a7e14";
        public const string SalesUnitId = "c_b3b84b9e";
        public const string IsLot = "c_5a28ea5e";
        public const string IsSerial = "c_f913e227";
        public const string HasExpiry = "c_6617f8f5";
        public const string Notes = "c_3e1ffc1f";
        public const string IsActive = "c_18451cd8";
        public const string Revision = "c_9ddd31a8";
        public const string CreatedAtUtc = "c_37070396";
        public const string UpdatedAtUtc = "c_bc0909f7";
        public const string PrimaryKey = "k_2c09623b";
        public const string ForeignKeyGroup = "k_7a471ec8";
        public const string ForeignKeyBaseUnit = "k_2b8dd36c";
        public const string ForeignKeyPurchaseUnit = "k_8b0377cc";
        public const string ForeignKeySalesUnit = "k_8bd06da4";
        public const string CheckCode = "k_4eb56038";
        public const string CheckName = "k_d4fdd415";
        public const string CheckType = "k_5b735e8c";
        public const string CheckService = "k_de22e5a1";
        public const string CheckRevision = "k_5b4b06f2";
        public const string UniqueCode = "k_7f281e96";
        public const string GroupIndex = "k_3e715f4f";
        public const string GuardFunction = "f_f5abe175";
        public const string RuleFunction = "f_b0e186fc";
        public const string GuardRow = "r_8dbf3f39";
        public const string GuardTruncate = "r_27c96766";
        public const string RuleTrigger = "r_12570388";
    }

    public static class ItemUnit
    {
        public const string Table = "t_8c617180";
        public const string ItemId = "c_e2f980ec";
        public const string UnitId = "c_42ad8d14";
        public const string Factor = "c_60e69705";
        public const string PrimaryKey = "k_62eb16a5";
        public const string ForeignKeyItem = "k_84b52957";
        public const string ForeignKeyUnit = "k_42c9f654";
        public const string CheckFactor = "k_acd1f8cb";
        public const string UnitIndex = "k_8323e61c";
        public const string RuleFunction = "f_26717e64";
        public const string RuleTrigger = "r_00fa1bae";
    }

    public static class ItemBarcode
    {
        public const string Table = "t_51879811";
        public const string Id = "c_2b1f6575";
        public const string ItemId = "c_5280289d";
        public const string UnitId = "c_dd549278";
        public const string Barcode = "c_a0859993";
        public const string Kind = "c_a5064116";
        public const string IsActive = "c_4454cfd3";
        public const string CreatedAtUtc = "c_c5f9ecc6";
        public const string UpdatedAtUtc = "c_426b5488";
        public const string PrimaryKey = "k_b2af90d2";
        public const string ForeignKeyItem = "k_b7c25893";
        public const string ForeignKeyUnit = "k_3ded30be";
        public const string CheckBarcode = "k_cc7d58e1";
        public const string CheckKind = "k_20406eb1";
        public const string UniqueBarcode = "k_164a91f5";
        public const string ItemIndex = "k_04e5b067";
        public const string GuardFunction = "f_def5eab6";
        public const string RuleFunction = "f_a9da9aff";
        public const string GuardRow = "r_db83e3fe";
        public const string GuardTruncate = "r_72e317a1";
        public const string RuleTrigger = "r_4972557f";
    }

    public static class ItemIntegrity
    {
        public const string CheckFunction = "f_d5bd9b80";
    }

    private static IReadOnlyDictionary<string, string> InventoryMap => inventoryMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> inventoryMap = new(() => new Dictionary<string, string>
    {
        ["InventorySettings"] = InventorySettings.Table,
        ["InventorySettings.Id"] = InventorySettings.Id,
        ["InventorySettings.DefaultValuation"] = InventorySettings.DefaultValuation,
        ["InventorySettings.NextItemNumber"] = InventorySettings.NextItemNumber,
        ["InventorySettings.Revision"] = InventorySettings.Revision,
        ["InventorySettings.UpdatedAtUtc"] = InventorySettings.UpdatedAtUtc,
        ["InventorySettings.PrimaryKey"] = InventorySettings.PrimaryKey,
        ["InventorySettings.CheckValues"] = InventorySettings.CheckValues,
        ["InventorySettings.GuardFunction"] = InventorySettings.GuardFunction,
        ["InventorySettings.GuardRow"] = InventorySettings.GuardRow,
        ["InventorySettings.GuardTruncate"] = InventorySettings.GuardTruncate,
        ["Unit"] = Unit.Table,
        ["Unit.Id"] = Unit.Id,
        ["Unit.Code"] = Unit.Code,
        ["Unit.NameAr"] = Unit.NameAr,
        ["Unit.NameEn"] = Unit.NameEn,
        ["Unit.Decimals"] = Unit.Decimals,
        ["Unit.IsActive"] = Unit.IsActive,
        ["Unit.Revision"] = Unit.Revision,
        ["Unit.CreatedAtUtc"] = Unit.CreatedAtUtc,
        ["Unit.UpdatedAtUtc"] = Unit.UpdatedAtUtc,
        ["Unit.PrimaryKey"] = Unit.PrimaryKey,
        ["Unit.CheckCode"] = Unit.CheckCode,
        ["Unit.CheckName"] = Unit.CheckName,
        ["Unit.CheckDecimals"] = Unit.CheckDecimals,
        ["Unit.CheckRevision"] = Unit.CheckRevision,
        ["Unit.UniqueCode"] = Unit.UniqueCode,
        ["Unit.GuardFunction"] = Unit.GuardFunction,
        ["Unit.GuardRow"] = Unit.GuardRow,
        ["Unit.GuardTruncate"] = Unit.GuardTruncate,
        ["ItemGroup"] = ItemGroup.Table,
        ["ItemGroup.Id"] = ItemGroup.Id,
        ["ItemGroup.Code"] = ItemGroup.Code,
        ["ItemGroup.NameAr"] = ItemGroup.NameAr,
        ["ItemGroup.NameEn"] = ItemGroup.NameEn,
        ["ItemGroup.ParentId"] = ItemGroup.ParentId,
        ["ItemGroup.Valuation"] = ItemGroup.Valuation,
        ["ItemGroup.IsActive"] = ItemGroup.IsActive,
        ["ItemGroup.Revision"] = ItemGroup.Revision,
        ["ItemGroup.CreatedAtUtc"] = ItemGroup.CreatedAtUtc,
        ["ItemGroup.UpdatedAtUtc"] = ItemGroup.UpdatedAtUtc,
        ["ItemGroup.PrimaryKey"] = ItemGroup.PrimaryKey,
        ["ItemGroup.ForeignKeyParent"] = ItemGroup.ForeignKeyParent,
        ["ItemGroup.CheckCode"] = ItemGroup.CheckCode,
        ["ItemGroup.CheckName"] = ItemGroup.CheckName,
        ["ItemGroup.CheckValuation"] = ItemGroup.CheckValuation,
        ["ItemGroup.CheckRevision"] = ItemGroup.CheckRevision,
        ["ItemGroup.CheckParent"] = ItemGroup.CheckParent,
        ["ItemGroup.UniqueCode"] = ItemGroup.UniqueCode,
        ["ItemGroup.ParentIndex"] = ItemGroup.ParentIndex,
        ["ItemGroup.GuardFunction"] = ItemGroup.GuardFunction,
        ["ItemGroup.TreeFunction"] = ItemGroup.TreeFunction,
        ["ItemGroup.GuardRow"] = ItemGroup.GuardRow,
        ["ItemGroup.GuardTruncate"] = ItemGroup.GuardTruncate,
        ["ItemGroup.TreeTrigger"] = ItemGroup.TreeTrigger,
        ["Item"] = Item.Table,
        ["Item.Id"] = Item.Id,
        ["Item.Code"] = Item.Code,
        ["Item.NameAr"] = Item.NameAr,
        ["Item.NameEn"] = Item.NameEn,
        ["Item.ItemType"] = Item.ItemType,
        ["Item.GroupId"] = Item.GroupId,
        ["Item.BaseUnitId"] = Item.BaseUnitId,
        ["Item.PurchaseUnitId"] = Item.PurchaseUnitId,
        ["Item.SalesUnitId"] = Item.SalesUnitId,
        ["Item.IsLot"] = Item.IsLot,
        ["Item.IsSerial"] = Item.IsSerial,
        ["Item.HasExpiry"] = Item.HasExpiry,
        ["Item.Notes"] = Item.Notes,
        ["Item.IsActive"] = Item.IsActive,
        ["Item.Revision"] = Item.Revision,
        ["Item.CreatedAtUtc"] = Item.CreatedAtUtc,
        ["Item.UpdatedAtUtc"] = Item.UpdatedAtUtc,
        ["Item.PrimaryKey"] = Item.PrimaryKey,
        ["Item.ForeignKeyGroup"] = Item.ForeignKeyGroup,
        ["Item.ForeignKeyBaseUnit"] = Item.ForeignKeyBaseUnit,
        ["Item.ForeignKeyPurchaseUnit"] = Item.ForeignKeyPurchaseUnit,
        ["Item.ForeignKeySalesUnit"] = Item.ForeignKeySalesUnit,
        ["Item.CheckCode"] = Item.CheckCode,
        ["Item.CheckName"] = Item.CheckName,
        ["Item.CheckType"] = Item.CheckType,
        ["Item.CheckService"] = Item.CheckService,
        ["Item.CheckRevision"] = Item.CheckRevision,
        ["Item.UniqueCode"] = Item.UniqueCode,
        ["Item.GroupIndex"] = Item.GroupIndex,
        ["Item.GuardFunction"] = Item.GuardFunction,
        ["Item.RuleFunction"] = Item.RuleFunction,
        ["Item.GuardRow"] = Item.GuardRow,
        ["Item.GuardTruncate"] = Item.GuardTruncate,
        ["Item.RuleTrigger"] = Item.RuleTrigger,
        ["ItemUnit"] = ItemUnit.Table,
        ["ItemUnit.ItemId"] = ItemUnit.ItemId,
        ["ItemUnit.UnitId"] = ItemUnit.UnitId,
        ["ItemUnit.Factor"] = ItemUnit.Factor,
        ["ItemUnit.PrimaryKey"] = ItemUnit.PrimaryKey,
        ["ItemUnit.ForeignKeyItem"] = ItemUnit.ForeignKeyItem,
        ["ItemUnit.ForeignKeyUnit"] = ItemUnit.ForeignKeyUnit,
        ["ItemUnit.CheckFactor"] = ItemUnit.CheckFactor,
        ["ItemUnit.UnitIndex"] = ItemUnit.UnitIndex,
        ["ItemUnit.RuleFunction"] = ItemUnit.RuleFunction,
        ["ItemUnit.RuleTrigger"] = ItemUnit.RuleTrigger,
        ["ItemBarcode"] = ItemBarcode.Table,
        ["ItemBarcode.Id"] = ItemBarcode.Id,
        ["ItemBarcode.ItemId"] = ItemBarcode.ItemId,
        ["ItemBarcode.UnitId"] = ItemBarcode.UnitId,
        ["ItemBarcode.Barcode"] = ItemBarcode.Barcode,
        ["ItemBarcode.Kind"] = ItemBarcode.Kind,
        ["ItemBarcode.IsActive"] = ItemBarcode.IsActive,
        ["ItemBarcode.CreatedAtUtc"] = ItemBarcode.CreatedAtUtc,
        ["ItemBarcode.UpdatedAtUtc"] = ItemBarcode.UpdatedAtUtc,
        ["ItemBarcode.PrimaryKey"] = ItemBarcode.PrimaryKey,
        ["ItemBarcode.ForeignKeyItem"] = ItemBarcode.ForeignKeyItem,
        ["ItemBarcode.ForeignKeyUnit"] = ItemBarcode.ForeignKeyUnit,
        ["ItemBarcode.CheckBarcode"] = ItemBarcode.CheckBarcode,
        ["ItemBarcode.CheckKind"] = ItemBarcode.CheckKind,
        ["ItemBarcode.UniqueBarcode"] = ItemBarcode.UniqueBarcode,
        ["ItemBarcode.ItemIndex"] = ItemBarcode.ItemIndex,
        ["ItemBarcode.GuardFunction"] = ItemBarcode.GuardFunction,
        ["ItemBarcode.RuleFunction"] = ItemBarcode.RuleFunction,
        ["ItemBarcode.GuardRow"] = ItemBarcode.GuardRow,
        ["ItemBarcode.GuardTruncate"] = ItemBarcode.GuardTruncate,
        ["ItemBarcode.RuleTrigger"] = ItemBarcode.RuleTrigger,
        ["ItemIntegrity.CheckFunction"] = ItemIntegrity.CheckFunction,
    });
}
