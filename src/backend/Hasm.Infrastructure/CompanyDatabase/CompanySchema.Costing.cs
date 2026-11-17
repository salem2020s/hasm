namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0014 (Phase 3.6, inventory costing engine). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class CostOperation
    {
        public const string Table = "t_a648ca40";
        public const string Id = "c_f47f15da";
        public const string Key = "c_b45c4009";
        public const string Kind = "c_7f2ea712";
        public const string RequestHash = "c_1ef8e158";
        public const string SourceType = "c_7114c862";
        public const string SourceId = "c_b18070d3";
        public const string EffectiveDate = "c_a84fdcdc";
        public const string CreatedBy = "c_aba24d32";
        public const string CreatedAtUtc = "c_9bef7859";
        public const string ReversesOperationId = "c_03a68cea";
        public const string ReversedByOperationId = "c_710cc274";
        public const string PrimaryKey = "k_4034fa9b";
        public const string UniqueKey = "k_e74e9377";
        public const string CheckValues = "k_f356c89a";
        public const string ForeignKeyReverses = "k_eebf577b";
        public const string ForeignKeyReversedBy = "k_ed7cf326";
        public const string UniqueReverses = "k_99f6719a";
        public const string GuardFunction = "f_9c827423";
        public const string GuardRow = "r_dfcc7b00";
        public const string GuardTruncate = "r_bdcb866c";
    }

    public static class CostPosition
    {
        public const string Table = "t_a2903683";
        public const string Id = "c_6b4abdd9";
        public const string ItemId = "c_3380c61e";
        public const string WarehouseId = "c_f0a91938";
        public const string Method = "c_a71bfccf";
        public const string Quantity = "c_8e61b559";
        public const string Value = "c_2e1921d0";
        public const string LastSeq = "c_ad2af2e5";
        public const string LastEffectiveDate = "c_3c880b93";
        public const string CreatedAtUtc = "c_b6a6317a";
        public const string UpdatedAtUtc = "c_7672cc5e";
        // Phase 3.8B-3 (company migration 0020): the last (quantity, value) the position held while it had stock, written by its entries only (no backfill)
        public const string LastAverageQuantity = "c_97877d45";
        public const string LastAverageValue = "c_548e8abe";
        public const string CheckLastAverage = "k_351f45a9";
        public const string PrimaryKey = "k_369b1663";
        public const string ForeignKeyItem = "k_ceb4d486";
        public const string ForeignKeyWarehouse = "k_1be4b387";
        public const string UniquePair = "k_ee055292";
        public const string CheckValues = "k_81ff32eb";
        public const string GuardFunction = "f_589a0a19";
        public const string GuardRow = "r_5de79b17";
        public const string GuardTruncate = "r_b7546021";
    }

    public static class CostEntry
    {
        public const string Table = "t_bc727c1d";
        public const string Id = "c_ed61ae1a";
        public const string OperationId = "c_eb34b7d8";
        public const string PositionId = "c_98321e7f";
        public const string ItemId = "c_9ee52677";
        public const string WarehouseId = "c_0f65c03c";
        public const string Kind = "c_4105796c";
        public const string Direction = "c_e0cd1a14";
        public const string EffectiveDate = "c_12665ffb";
        public const string Quantity = "c_dba27822";
        public const string Amount = "c_ece66ed5";
        public const string QuantityAfter = "c_3437e13d";
        public const string ValueAfter = "c_30804877";
        public const string Seq = "c_2affbce9";
        public const string ReversesEntryId = "c_4f9e777a";
        public const string CreatedAtUtc = "c_585a4312";
        public const string PrimaryKey = "k_3d37b6c0";
        public const string ForeignKeyOperation = "k_732ebe05";
        public const string ForeignKeyPosition = "k_c5a919b3";
        public const string ForeignKeyItem = "k_4d09b17c";
        public const string ForeignKeyWarehouse = "k_1abaedac";
        public const string ForeignKeyReverses = "k_4976785f";
        public const string CheckValues = "k_0b3e9de8";
        public const string UniquePositionSeq = "k_214c34ab";
        public const string UniqueReverses = "k_c8fdc6c0";
        public const string OperationIndex = "k_ecc9213f";
        public const string GuardFunction = "f_8b9d471d";
        public const string GuardRow = "r_911c728a";
        public const string GuardTruncate = "r_317ff2e0";
    }

    public static class CostLayer
    {
        public const string Table = "t_3147676c";
        public const string Id = "c_d784aea8";
        public const string PositionId = "c_88f16993";
        public const string ItemId = "c_c4178514";
        public const string WarehouseId = "c_f0903b24";
        public const string EntryId = "c_07583c02";
        public const string Seq = "c_bf7d08a1";
        public const string OriginalQuantity = "c_c50486e0";
        public const string OriginalAmount = "c_f5a791ec";
        public const string RemainingQuantity = "c_69f58f45";
        public const string RemainingAmount = "c_d25b66c0";
        public const string SourceLayerId = "c_1c8be8cf";
        public const string CreatedAtUtc = "c_4f86bd91";
        public const string PrimaryKey = "k_b8c27d8c";
        public const string ForeignKeyPosition = "k_6ee66f43";
        public const string ForeignKeyEntry = "k_5b0f7de6";
        public const string ForeignKeySource = "k_72b1a97d";
        public const string CheckValues = "k_65138e7a";
        public const string UniquePositionSeq = "k_27306962";
        public const string OpenIndex = "k_ccd72f79";
        public const string GuardFunction = "f_b16f3723";
        public const string GuardRow = "r_05e5e9b8";
        public const string GuardTruncate = "r_2e25b0dd";
    }

    public static class CostConsumption
    {
        public const string Table = "t_6df3b92e";
        public const string Id = "c_22ac1076";
        public const string EntryId = "c_b17f9a0c";
        public const string LayerId = "c_9b2217bf";
        public const string Quantity = "c_13147040";
        public const string Amount = "c_b7bc1937";
        public const string Restores = "c_90e0409c";
        public const string PrimaryKey = "k_18cb3079";
        public const string ForeignKeyEntry = "k_4a2a4b1e";
        public const string ForeignKeyLayer = "k_47252d98";
        public const string CheckValues = "k_37922aa8";
        public const string LayerIndex = "k_f9b0a9ea";
        public const string EntryIndex = "k_a48dd68c";
        public const string GuardFunction = "f_4bf3449c";
        public const string GuardRow = "r_4b65e872";
        public const string GuardTruncate = "r_d9027ce4";
    }

    private static IReadOnlyDictionary<string, string> CostingMap => costingMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> costingMap = new(() => new Dictionary<string, string>
    {
        ["CostOperation"] = CostOperation.Table,
        ["CostOperation.Id"] = CostOperation.Id,
        ["CostOperation.Key"] = CostOperation.Key,
        ["CostOperation.Kind"] = CostOperation.Kind,
        ["CostOperation.RequestHash"] = CostOperation.RequestHash,
        ["CostOperation.SourceType"] = CostOperation.SourceType,
        ["CostOperation.SourceId"] = CostOperation.SourceId,
        ["CostOperation.EffectiveDate"] = CostOperation.EffectiveDate,
        ["CostOperation.CreatedBy"] = CostOperation.CreatedBy,
        ["CostOperation.CreatedAtUtc"] = CostOperation.CreatedAtUtc,
        ["CostOperation.ReversesOperationId"] = CostOperation.ReversesOperationId,
        ["CostOperation.ReversedByOperationId"] = CostOperation.ReversedByOperationId,
        ["CostOperation.PrimaryKey"] = CostOperation.PrimaryKey,
        ["CostOperation.UniqueKey"] = CostOperation.UniqueKey,
        ["CostOperation.CheckValues"] = CostOperation.CheckValues,
        ["CostOperation.ForeignKeyReverses"] = CostOperation.ForeignKeyReverses,
        ["CostOperation.ForeignKeyReversedBy"] = CostOperation.ForeignKeyReversedBy,
        ["CostOperation.UniqueReverses"] = CostOperation.UniqueReverses,
        ["CostOperation.GuardFunction"] = CostOperation.GuardFunction,
        ["CostOperation.GuardRow"] = CostOperation.GuardRow,
        ["CostOperation.GuardTruncate"] = CostOperation.GuardTruncate,
        ["CostPosition"] = CostPosition.Table,
        ["CostPosition.Id"] = CostPosition.Id,
        ["CostPosition.ItemId"] = CostPosition.ItemId,
        ["CostPosition.WarehouseId"] = CostPosition.WarehouseId,
        ["CostPosition.Method"] = CostPosition.Method,
        ["CostPosition.Quantity"] = CostPosition.Quantity,
        ["CostPosition.Value"] = CostPosition.Value,
        ["CostPosition.LastSeq"] = CostPosition.LastSeq,
        ["CostPosition.LastEffectiveDate"] = CostPosition.LastEffectiveDate,
        ["CostPosition.CreatedAtUtc"] = CostPosition.CreatedAtUtc,
        ["CostPosition.UpdatedAtUtc"] = CostPosition.UpdatedAtUtc,
        ["CostPosition.LastAverageQuantity"] = CostPosition.LastAverageQuantity,
        ["CostPosition.LastAverageValue"] = CostPosition.LastAverageValue,
        ["CostPosition.CheckLastAverage"] = CostPosition.CheckLastAverage,
        ["CostPosition.PrimaryKey"] = CostPosition.PrimaryKey,
        ["CostPosition.ForeignKeyItem"] = CostPosition.ForeignKeyItem,
        ["CostPosition.ForeignKeyWarehouse"] = CostPosition.ForeignKeyWarehouse,
        ["CostPosition.UniquePair"] = CostPosition.UniquePair,
        ["CostPosition.CheckValues"] = CostPosition.CheckValues,
        ["CostPosition.GuardFunction"] = CostPosition.GuardFunction,
        ["CostPosition.GuardRow"] = CostPosition.GuardRow,
        ["CostPosition.GuardTruncate"] = CostPosition.GuardTruncate,
        ["CostEntry"] = CostEntry.Table,
        ["CostEntry.Id"] = CostEntry.Id,
        ["CostEntry.OperationId"] = CostEntry.OperationId,
        ["CostEntry.PositionId"] = CostEntry.PositionId,
        ["CostEntry.ItemId"] = CostEntry.ItemId,
        ["CostEntry.WarehouseId"] = CostEntry.WarehouseId,
        ["CostEntry.Kind"] = CostEntry.Kind,
        ["CostEntry.Direction"] = CostEntry.Direction,
        ["CostEntry.EffectiveDate"] = CostEntry.EffectiveDate,
        ["CostEntry.Quantity"] = CostEntry.Quantity,
        ["CostEntry.Amount"] = CostEntry.Amount,
        ["CostEntry.QuantityAfter"] = CostEntry.QuantityAfter,
        ["CostEntry.ValueAfter"] = CostEntry.ValueAfter,
        ["CostEntry.Seq"] = CostEntry.Seq,
        ["CostEntry.ReversesEntryId"] = CostEntry.ReversesEntryId,
        ["CostEntry.CreatedAtUtc"] = CostEntry.CreatedAtUtc,
        ["CostEntry.PrimaryKey"] = CostEntry.PrimaryKey,
        ["CostEntry.ForeignKeyOperation"] = CostEntry.ForeignKeyOperation,
        ["CostEntry.ForeignKeyPosition"] = CostEntry.ForeignKeyPosition,
        ["CostEntry.ForeignKeyItem"] = CostEntry.ForeignKeyItem,
        ["CostEntry.ForeignKeyWarehouse"] = CostEntry.ForeignKeyWarehouse,
        ["CostEntry.ForeignKeyReverses"] = CostEntry.ForeignKeyReverses,
        ["CostEntry.CheckValues"] = CostEntry.CheckValues,
        ["CostEntry.UniquePositionSeq"] = CostEntry.UniquePositionSeq,
        ["CostEntry.UniqueReverses"] = CostEntry.UniqueReverses,
        ["CostEntry.OperationIndex"] = CostEntry.OperationIndex,
        ["CostEntry.GuardFunction"] = CostEntry.GuardFunction,
        ["CostEntry.GuardRow"] = CostEntry.GuardRow,
        ["CostEntry.GuardTruncate"] = CostEntry.GuardTruncate,
        ["CostLayer"] = CostLayer.Table,
        ["CostLayer.Id"] = CostLayer.Id,
        ["CostLayer.PositionId"] = CostLayer.PositionId,
        ["CostLayer.ItemId"] = CostLayer.ItemId,
        ["CostLayer.WarehouseId"] = CostLayer.WarehouseId,
        ["CostLayer.EntryId"] = CostLayer.EntryId,
        ["CostLayer.Seq"] = CostLayer.Seq,
        ["CostLayer.OriginalQuantity"] = CostLayer.OriginalQuantity,
        ["CostLayer.OriginalAmount"] = CostLayer.OriginalAmount,
        ["CostLayer.RemainingQuantity"] = CostLayer.RemainingQuantity,
        ["CostLayer.RemainingAmount"] = CostLayer.RemainingAmount,
        ["CostLayer.SourceLayerId"] = CostLayer.SourceLayerId,
        ["CostLayer.CreatedAtUtc"] = CostLayer.CreatedAtUtc,
        ["CostLayer.PrimaryKey"] = CostLayer.PrimaryKey,
        ["CostLayer.ForeignKeyPosition"] = CostLayer.ForeignKeyPosition,
        ["CostLayer.ForeignKeyEntry"] = CostLayer.ForeignKeyEntry,
        ["CostLayer.ForeignKeySource"] = CostLayer.ForeignKeySource,
        ["CostLayer.CheckValues"] = CostLayer.CheckValues,
        ["CostLayer.UniquePositionSeq"] = CostLayer.UniquePositionSeq,
        ["CostLayer.OpenIndex"] = CostLayer.OpenIndex,
        ["CostLayer.GuardFunction"] = CostLayer.GuardFunction,
        ["CostLayer.GuardRow"] = CostLayer.GuardRow,
        ["CostLayer.GuardTruncate"] = CostLayer.GuardTruncate,
        ["CostConsumption"] = CostConsumption.Table,
        ["CostConsumption.Id"] = CostConsumption.Id,
        ["CostConsumption.EntryId"] = CostConsumption.EntryId,
        ["CostConsumption.LayerId"] = CostConsumption.LayerId,
        ["CostConsumption.Quantity"] = CostConsumption.Quantity,
        ["CostConsumption.Amount"] = CostConsumption.Amount,
        ["CostConsumption.Restores"] = CostConsumption.Restores,
        ["CostConsumption.PrimaryKey"] = CostConsumption.PrimaryKey,
        ["CostConsumption.ForeignKeyEntry"] = CostConsumption.ForeignKeyEntry,
        ["CostConsumption.ForeignKeyLayer"] = CostConsumption.ForeignKeyLayer,
        ["CostConsumption.CheckValues"] = CostConsumption.CheckValues,
        ["CostConsumption.LayerIndex"] = CostConsumption.LayerIndex,
        ["CostConsumption.EntryIndex"] = CostConsumption.EntryIndex,
        ["CostConsumption.GuardFunction"] = CostConsumption.GuardFunction,
        ["CostConsumption.GuardRow"] = CostConsumption.GuardRow,
        ["CostConsumption.GuardTruncate"] = CostConsumption.GuardTruncate,
    });
}
