namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0020 (Phase 3.8B-3, negative-stock exception). Physical names are opaque and fixed: changing one requires a new migration.
// The columns added to existing tables live with their tables: CompanySchema.Costing.cs (CostPosition) and CompanySchema.InventoryAccounting.cs (InventoryAccountingVersion).
public static partial class CompanySchema
{
    public static class NegativeStockGuard
    {
        public const string LastAverageFunction = "f_9b70c8c4";
        public const string LastAverageTrigger = "r_9040ab41";
        public const string ResolvedMethodFunction = "f_23735cac";
        public const string GroupInChainFunction = "f_6efd1234";
        public const string OpenQuantityFunction = "f_a21c7b04";
        public const string HasOpenFunction = "f_cf64ca57";
        public const string StockCheckFunction = "f_8821e766";
        public const string PositionStockFunction = "f_485217dd";
        public const string PositionStockTrigger = "r_7c0b96d2";
        public const string EmptyOperationFunction = "f_6df04d67";
        public const string EmptyOperationTrigger = "r_d621465c";
        public const string JournalGuardFunction = "f_e324c81a";
        public const string JournalGuardTrigger = "r_3bc14726";
        public const string ItemGuardFunction = "f_c9eb2c1d";
        public const string ItemGuardTrigger = "r_b3836cf7";
        public const string GroupGuardFunction = "f_d497cd12";
        public const string GroupGuardTrigger = "r_1e0d1868";
        public const string SettingsGuardFunction = "f_681eae1e";
        public const string SettingsGuardTrigger = "r_b2c26a63";
        public const string WarehouseGuardFunction = "f_8503be21";
        public const string WarehouseGuardTrigger = "r_dc7fee2b";
    }

    public static class NegativeStockSetting
    {
        public const string Table = "t_9b016b79";
        public const string Id = "c_475ca92d";
        public const string VersionNo = "c_a2849f5d";
        public const string Enabled = "c_bb298bfd";
        public const string RequireSecondApprover = "c_cae55ed1";
        public const string Note = "c_7d8ae708";
        public const string CreatedBy = "c_3f08c397";
        public const string CreatedAtUtc = "c_318fd3bb";
        public const string PrimaryKey = "k_44066dde";
        public const string UniqueVersion = "k_7046f466";
        public const string CheckValues = "k_d19853a8";
        public const string GuardFunction = "f_e3b7d97a";
        public const string GuardRow = "r_8526da8e";
        public const string GuardTruncate = "r_23f4b5f4";
    }

    public static class NegativeStockItemFlag
    {
        public const string Table = "t_3fb62185";
        public const string Id = "c_dd9030ed";
        public const string ItemId = "c_59aa8519";
        public const string Eligible = "c_7d66a9cb";
        public const string Note = "c_08a5b83a";
        public const string ChangedBy = "c_f807da02";
        public const string ChangedAtUtc = "c_f2a97a77";
        public const string PrimaryKey = "k_9ed92816";
        public const string ForeignKeyItem = "k_e154f3ad";
        public const string ItemIndex = "k_e7f8e42d";
        public const string GuardFunction = "f_6c6fdd4a";
        public const string GuardRow = "r_96bfe539";
        public const string GuardTruncate = "r_be34e15c";
        public const string RuleFunction = "f_89cdb0aa";
        public const string RuleTrigger = "r_e5805e00";
    }

    public static class NegativeExceptionApproval
    {
        public const string Table = "t_40ad576c";
        public const string Id = "c_faadf366";
        public const string DocumentId = "c_7da52180";
        public const string DocumentRevision = "c_459b8474";
        public const string ApproverId = "c_e75cc8cf";
        public const string ApprovedAtUtc = "c_76d044fe";
        public const string Reason = "c_f02786a1";
        public const string SnapshotHash = "c_c8ec123f";
        public const string Snapshot = "c_0516bfb2";
        public const string PrimaryKey = "k_25eb4425";
        public const string ForeignKeyDocument = "k_33e249b1";
        public const string UniqueApprover = "k_ea767cd5";
        public const string CheckValues = "k_979715cd";
        public const string GuardFunction = "f_3b9f8478";
        public const string GuardRow = "r_88f32ccf";
        public const string GuardTruncate = "r_aa4f2217";
        public const string RuleFunction = "f_c918fc84";
        public const string RuleTrigger = "r_e59c775c";
    }

    public static class NegativeObligation
    {
        public const string Table = "t_b56b119d";
        public const string Id = "c_89d0d4ae";
        public const string Seq = "c_700da1f4";
        public const string ItemId = "c_05baef20";
        public const string WarehouseId = "c_971e8265";
        public const string PositionId = "c_e58fa36c";
        public const string PostingId = "c_64379fe3";
        public const string LineNo = "c_acdfca73";
        public const string DocumentId = "c_d366d343";
        public const string IssueOperationId = "c_1872eef0";
        public const string JournalEntryId = "c_b9765532";
        public const string AccountingVersionId = "c_f8f8295b";
        public const string ObligationAccountId = "c_ab2fc7e6";
        public const string VarianceAccountId = "c_e6b8b42b";
        public const string CounterAccountId = "c_d0597f45";
        public const string EffectiveDate = "c_1f0961d8";
        public const string OriginalQuantity = "c_95d329bc";
        public const string ProvisionalTotal = "c_af214a8a";
        public const string SourceCode = "c_dde517b8";
        public const string SourceEntryId = "c_667e4cc5";
        public const string SourceQuantity = "c_93fad1df";
        public const string SourceValue = "c_5db1a6de";
        public const string Justification = "c_e631ea3e";
        public const string ApprovalId = "c_10088048";
        public const string CreatedBy = "c_225cbcd3";
        public const string CreatedAtUtc = "c_70194123";
        public const string PrimaryKey = "k_f2195551";
        public const string UniqueSeq = "k_83e403f6";
        public const string UniquePostingLine = "k_3f84d074";
        public const string ForeignKeyItem = "k_9cf439a6";
        public const string ForeignKeyWarehouse = "k_cea1e750";
        public const string ForeignKeyPosition = "k_c43a0bb2";
        public const string ForeignKeyOperation = "k_6b66be21";
        public const string ForeignKeyJournal = "k_124184dc";
        public const string ForeignKeyVersion = "k_01dff459";
        public const string ForeignKeyObligationAccount = "k_445b35b0";
        public const string ForeignKeyVarianceAccount = "k_b83c3d92";
        public const string ForeignKeyCounterAccount = "k_7341fbdd";
        public const string ForeignKeySourceEntry = "k_e2e7b5d8";
        public const string ForeignKeyApproval = "k_0dffb30c";
        public const string CheckValues = "k_a133520d";
        public const string PositionIndex = "k_6579d292";
        public const string OperationIndex = "k_9e35380a";
        public const string GuardFunction = "f_f4b3e62e";
        public const string GuardRow = "r_bec254b7";
        public const string GuardTruncate = "r_121ad895";
        public const string StockFunction = "f_5bfbcb2c";
        public const string StockTrigger = "r_484122e8";
        public const string RuleFunction = "f_b0ee255f";
        public const string RuleTrigger = "r_07b8067c";
        public const string ForeignKeyPosting = "k_42837e93";
    }

    public static class NegativeSettlement
    {
        public const string Table = "t_d370b1d6";
        public const string Id = "c_f91bd136";
        public const string ObligationId = "c_07e3dad0";
        public const string ReceiptPostingId = "c_58257cfe";
        public const string ReceiptLineNo = "c_42a1e7ee";
        public const string ReceiptOperationId = "c_7208ba7b";
        public const string LineQuantity = "c_23eac371";
        public const string LineAmount = "c_6f6ab47d";
        public const string MatchedQuantity = "c_e5a64555";
        public const string ProvisionalAmount = "c_e4745db7";
        public const string ActualAmount = "c_4972eb11";
        public const string Difference = "c_ec59e35f";
        public const string EffectiveDate = "c_5e337f95";
        public const string JournalEntryId = "c_2fe8c930";
        public const string ReversesSettlementId = "c_094f81ac";
        public const string ReversalId = "c_53ef40f0";
        public const string CreatedBy = "c_a5208414";
        public const string CreatedAtUtc = "c_4aa8f1a9";
        public const string PrimaryKey = "k_afe4960e";
        public const string ForeignKeyObligation = "k_fa315d14";
        public const string ForeignKeyOperation = "k_58734e01";
        public const string ForeignKeyJournal = "k_05b49006";
        public const string ForeignKeyReverses = "k_037e1cf5";
        public const string CheckValues = "k_9cf92817";
        public const string UniqueMatch = "k_48fca9a0";
        public const string UniqueReverses = "k_a76cd6a6";
        public const string ObligationIndex = "k_a3a30314";
        public const string PostingIndex = "k_c6673ebe";
        public const string OperationIndex = "k_3cd9be74";
        public const string GuardFunction = "f_0e81c3ee";
        public const string GuardRow = "r_88e59b65";
        public const string GuardTruncate = "r_7ec87d7f";
        public const string StockFunction = "f_4b1603d4";
        public const string StockTrigger = "r_77f82118";
        public const string RuleFunction = "f_14fbf8bd";
        public const string RuleTrigger = "r_5344ceda";
        public const string ForeignKeyPosting = "k_26bc3485";
        public const string Seq = "c_70203e95";
        public const string UniqueSeq = "k_82720473";
    }

    public static class NegativeObligationReversal
    {
        public const string Table = "t_075e0837";
        public const string Id = "c_f722aa60";
        public const string ObligationId = "c_4caf1cf3";
        public const string StockReversalId = "c_4dfed26f";
        public const string ReversalOperationId = "c_c79ef8bc";
        public const string EffectiveDate = "c_9f7cb668";
        public const string CreatedBy = "c_00c3c788";
        public const string CreatedAtUtc = "c_87f8853f";
        public const string PrimaryKey = "k_12b907e6";
        public const string UniqueObligation = "k_f1809d3e";
        public const string ForeignKeyObligation = "k_43a2aab3";
        public const string ForeignKeyOperation = "k_100a99ed";
        public const string GuardFunction = "f_23d36ecb";
        public const string GuardRow = "r_610ffd2a";
        public const string GuardTruncate = "r_b6426fe8";
        public const string RuleFunction = "f_1f6fd2d7";
        public const string RuleTrigger = "r_da893055";
    }

    private static IReadOnlyDictionary<string, string> NegativeStockMap => negativeStockMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> negativeStockMap = new(() => new Dictionary<string, string>
    {
        ["NegativeStockGuard.LastAverageFunction"] = NegativeStockGuard.LastAverageFunction,
        ["NegativeStockGuard.LastAverageTrigger"] = NegativeStockGuard.LastAverageTrigger,
        ["NegativeStockGuard.ResolvedMethodFunction"] = NegativeStockGuard.ResolvedMethodFunction,
        ["NegativeStockGuard.GroupInChainFunction"] = NegativeStockGuard.GroupInChainFunction,
        ["NegativeStockGuard.OpenQuantityFunction"] = NegativeStockGuard.OpenQuantityFunction,
        ["NegativeStockGuard.HasOpenFunction"] = NegativeStockGuard.HasOpenFunction,
        ["NegativeStockGuard.StockCheckFunction"] = NegativeStockGuard.StockCheckFunction,
        ["NegativeStockGuard.PositionStockFunction"] = NegativeStockGuard.PositionStockFunction,
        ["NegativeStockGuard.PositionStockTrigger"] = NegativeStockGuard.PositionStockTrigger,
        ["NegativeStockGuard.EmptyOperationFunction"] = NegativeStockGuard.EmptyOperationFunction,
        ["NegativeStockGuard.EmptyOperationTrigger"] = NegativeStockGuard.EmptyOperationTrigger,
        ["NegativeStockGuard.JournalGuardFunction"] = NegativeStockGuard.JournalGuardFunction,
        ["NegativeStockGuard.JournalGuardTrigger"] = NegativeStockGuard.JournalGuardTrigger,
        ["NegativeStockGuard.ItemGuardFunction"] = NegativeStockGuard.ItemGuardFunction,
        ["NegativeStockGuard.ItemGuardTrigger"] = NegativeStockGuard.ItemGuardTrigger,
        ["NegativeStockGuard.GroupGuardFunction"] = NegativeStockGuard.GroupGuardFunction,
        ["NegativeStockGuard.GroupGuardTrigger"] = NegativeStockGuard.GroupGuardTrigger,
        ["NegativeStockGuard.SettingsGuardFunction"] = NegativeStockGuard.SettingsGuardFunction,
        ["NegativeStockGuard.SettingsGuardTrigger"] = NegativeStockGuard.SettingsGuardTrigger,
        ["NegativeStockGuard.WarehouseGuardFunction"] = NegativeStockGuard.WarehouseGuardFunction,
        ["NegativeStockGuard.WarehouseGuardTrigger"] = NegativeStockGuard.WarehouseGuardTrigger,
        ["NegativeStockSetting"] = NegativeStockSetting.Table,
        ["NegativeStockSetting.Id"] = NegativeStockSetting.Id,
        ["NegativeStockSetting.VersionNo"] = NegativeStockSetting.VersionNo,
        ["NegativeStockSetting.Enabled"] = NegativeStockSetting.Enabled,
        ["NegativeStockSetting.RequireSecondApprover"] = NegativeStockSetting.RequireSecondApprover,
        ["NegativeStockSetting.Note"] = NegativeStockSetting.Note,
        ["NegativeStockSetting.CreatedBy"] = NegativeStockSetting.CreatedBy,
        ["NegativeStockSetting.CreatedAtUtc"] = NegativeStockSetting.CreatedAtUtc,
        ["NegativeStockSetting.PrimaryKey"] = NegativeStockSetting.PrimaryKey,
        ["NegativeStockSetting.UniqueVersion"] = NegativeStockSetting.UniqueVersion,
        ["NegativeStockSetting.CheckValues"] = NegativeStockSetting.CheckValues,
        ["NegativeStockSetting.GuardFunction"] = NegativeStockSetting.GuardFunction,
        ["NegativeStockSetting.GuardRow"] = NegativeStockSetting.GuardRow,
        ["NegativeStockSetting.GuardTruncate"] = NegativeStockSetting.GuardTruncate,
        ["NegativeStockItemFlag"] = NegativeStockItemFlag.Table,
        ["NegativeStockItemFlag.Id"] = NegativeStockItemFlag.Id,
        ["NegativeStockItemFlag.ItemId"] = NegativeStockItemFlag.ItemId,
        ["NegativeStockItemFlag.Eligible"] = NegativeStockItemFlag.Eligible,
        ["NegativeStockItemFlag.Note"] = NegativeStockItemFlag.Note,
        ["NegativeStockItemFlag.ChangedBy"] = NegativeStockItemFlag.ChangedBy,
        ["NegativeStockItemFlag.ChangedAtUtc"] = NegativeStockItemFlag.ChangedAtUtc,
        ["NegativeStockItemFlag.PrimaryKey"] = NegativeStockItemFlag.PrimaryKey,
        ["NegativeStockItemFlag.ForeignKeyItem"] = NegativeStockItemFlag.ForeignKeyItem,
        ["NegativeStockItemFlag.ItemIndex"] = NegativeStockItemFlag.ItemIndex,
        ["NegativeStockItemFlag.GuardFunction"] = NegativeStockItemFlag.GuardFunction,
        ["NegativeStockItemFlag.GuardRow"] = NegativeStockItemFlag.GuardRow,
        ["NegativeStockItemFlag.GuardTruncate"] = NegativeStockItemFlag.GuardTruncate,
        ["NegativeStockItemFlag.RuleFunction"] = NegativeStockItemFlag.RuleFunction,
        ["NegativeStockItemFlag.RuleTrigger"] = NegativeStockItemFlag.RuleTrigger,
        ["NegativeExceptionApproval"] = NegativeExceptionApproval.Table,
        ["NegativeExceptionApproval.Id"] = NegativeExceptionApproval.Id,
        ["NegativeExceptionApproval.DocumentId"] = NegativeExceptionApproval.DocumentId,
        ["NegativeExceptionApproval.DocumentRevision"] = NegativeExceptionApproval.DocumentRevision,
        ["NegativeExceptionApproval.ApproverId"] = NegativeExceptionApproval.ApproverId,
        ["NegativeExceptionApproval.ApprovedAtUtc"] = NegativeExceptionApproval.ApprovedAtUtc,
        ["NegativeExceptionApproval.Reason"] = NegativeExceptionApproval.Reason,
        ["NegativeExceptionApproval.SnapshotHash"] = NegativeExceptionApproval.SnapshotHash,
        ["NegativeExceptionApproval.Snapshot"] = NegativeExceptionApproval.Snapshot,
        ["NegativeExceptionApproval.PrimaryKey"] = NegativeExceptionApproval.PrimaryKey,
        ["NegativeExceptionApproval.ForeignKeyDocument"] = NegativeExceptionApproval.ForeignKeyDocument,
        ["NegativeExceptionApproval.UniqueApprover"] = NegativeExceptionApproval.UniqueApprover,
        ["NegativeExceptionApproval.CheckValues"] = NegativeExceptionApproval.CheckValues,
        ["NegativeExceptionApproval.GuardFunction"] = NegativeExceptionApproval.GuardFunction,
        ["NegativeExceptionApproval.GuardRow"] = NegativeExceptionApproval.GuardRow,
        ["NegativeExceptionApproval.GuardTruncate"] = NegativeExceptionApproval.GuardTruncate,
        ["NegativeExceptionApproval.RuleFunction"] = NegativeExceptionApproval.RuleFunction,
        ["NegativeExceptionApproval.RuleTrigger"] = NegativeExceptionApproval.RuleTrigger,
        ["NegativeObligation"] = NegativeObligation.Table,
        ["NegativeObligation.Id"] = NegativeObligation.Id,
        ["NegativeObligation.Seq"] = NegativeObligation.Seq,
        ["NegativeObligation.ItemId"] = NegativeObligation.ItemId,
        ["NegativeObligation.WarehouseId"] = NegativeObligation.WarehouseId,
        ["NegativeObligation.PositionId"] = NegativeObligation.PositionId,
        ["NegativeObligation.PostingId"] = NegativeObligation.PostingId,
        ["NegativeObligation.LineNo"] = NegativeObligation.LineNo,
        ["NegativeObligation.DocumentId"] = NegativeObligation.DocumentId,
        ["NegativeObligation.IssueOperationId"] = NegativeObligation.IssueOperationId,
        ["NegativeObligation.JournalEntryId"] = NegativeObligation.JournalEntryId,
        ["NegativeObligation.AccountingVersionId"] = NegativeObligation.AccountingVersionId,
        ["NegativeObligation.ObligationAccountId"] = NegativeObligation.ObligationAccountId,
        ["NegativeObligation.VarianceAccountId"] = NegativeObligation.VarianceAccountId,
        ["NegativeObligation.CounterAccountId"] = NegativeObligation.CounterAccountId,
        ["NegativeObligation.EffectiveDate"] = NegativeObligation.EffectiveDate,
        ["NegativeObligation.OriginalQuantity"] = NegativeObligation.OriginalQuantity,
        ["NegativeObligation.ProvisionalTotal"] = NegativeObligation.ProvisionalTotal,
        ["NegativeObligation.SourceCode"] = NegativeObligation.SourceCode,
        ["NegativeObligation.SourceEntryId"] = NegativeObligation.SourceEntryId,
        ["NegativeObligation.SourceQuantity"] = NegativeObligation.SourceQuantity,
        ["NegativeObligation.SourceValue"] = NegativeObligation.SourceValue,
        ["NegativeObligation.Justification"] = NegativeObligation.Justification,
        ["NegativeObligation.ApprovalId"] = NegativeObligation.ApprovalId,
        ["NegativeObligation.CreatedBy"] = NegativeObligation.CreatedBy,
        ["NegativeObligation.CreatedAtUtc"] = NegativeObligation.CreatedAtUtc,
        ["NegativeObligation.PrimaryKey"] = NegativeObligation.PrimaryKey,
        ["NegativeObligation.UniqueSeq"] = NegativeObligation.UniqueSeq,
        ["NegativeObligation.UniquePostingLine"] = NegativeObligation.UniquePostingLine,
        ["NegativeObligation.ForeignKeyItem"] = NegativeObligation.ForeignKeyItem,
        ["NegativeObligation.ForeignKeyWarehouse"] = NegativeObligation.ForeignKeyWarehouse,
        ["NegativeObligation.ForeignKeyPosition"] = NegativeObligation.ForeignKeyPosition,
        ["NegativeObligation.ForeignKeyOperation"] = NegativeObligation.ForeignKeyOperation,
        ["NegativeObligation.ForeignKeyJournal"] = NegativeObligation.ForeignKeyJournal,
        ["NegativeObligation.ForeignKeyVersion"] = NegativeObligation.ForeignKeyVersion,
        ["NegativeObligation.ForeignKeyObligationAccount"] = NegativeObligation.ForeignKeyObligationAccount,
        ["NegativeObligation.ForeignKeyVarianceAccount"] = NegativeObligation.ForeignKeyVarianceAccount,
        ["NegativeObligation.ForeignKeyCounterAccount"] = NegativeObligation.ForeignKeyCounterAccount,
        ["NegativeObligation.ForeignKeySourceEntry"] = NegativeObligation.ForeignKeySourceEntry,
        ["NegativeObligation.ForeignKeyApproval"] = NegativeObligation.ForeignKeyApproval,
        ["NegativeObligation.CheckValues"] = NegativeObligation.CheckValues,
        ["NegativeObligation.PositionIndex"] = NegativeObligation.PositionIndex,
        ["NegativeObligation.OperationIndex"] = NegativeObligation.OperationIndex,
        ["NegativeObligation.GuardFunction"] = NegativeObligation.GuardFunction,
        ["NegativeObligation.GuardRow"] = NegativeObligation.GuardRow,
        ["NegativeObligation.GuardTruncate"] = NegativeObligation.GuardTruncate,
        ["NegativeObligation.StockFunction"] = NegativeObligation.StockFunction,
        ["NegativeObligation.StockTrigger"] = NegativeObligation.StockTrigger,
        ["NegativeObligation.RuleFunction"] = NegativeObligation.RuleFunction,
        ["NegativeObligation.RuleTrigger"] = NegativeObligation.RuleTrigger,
        ["NegativeObligation.ForeignKeyPosting"] = NegativeObligation.ForeignKeyPosting,
        ["NegativeSettlement"] = NegativeSettlement.Table,
        ["NegativeSettlement.Id"] = NegativeSettlement.Id,
        ["NegativeSettlement.ObligationId"] = NegativeSettlement.ObligationId,
        ["NegativeSettlement.ReceiptPostingId"] = NegativeSettlement.ReceiptPostingId,
        ["NegativeSettlement.ReceiptLineNo"] = NegativeSettlement.ReceiptLineNo,
        ["NegativeSettlement.ReceiptOperationId"] = NegativeSettlement.ReceiptOperationId,
        ["NegativeSettlement.LineQuantity"] = NegativeSettlement.LineQuantity,
        ["NegativeSettlement.LineAmount"] = NegativeSettlement.LineAmount,
        ["NegativeSettlement.MatchedQuantity"] = NegativeSettlement.MatchedQuantity,
        ["NegativeSettlement.ProvisionalAmount"] = NegativeSettlement.ProvisionalAmount,
        ["NegativeSettlement.ActualAmount"] = NegativeSettlement.ActualAmount,
        ["NegativeSettlement.Difference"] = NegativeSettlement.Difference,
        ["NegativeSettlement.EffectiveDate"] = NegativeSettlement.EffectiveDate,
        ["NegativeSettlement.JournalEntryId"] = NegativeSettlement.JournalEntryId,
        ["NegativeSettlement.ReversesSettlementId"] = NegativeSettlement.ReversesSettlementId,
        ["NegativeSettlement.ReversalId"] = NegativeSettlement.ReversalId,
        ["NegativeSettlement.CreatedBy"] = NegativeSettlement.CreatedBy,
        ["NegativeSettlement.CreatedAtUtc"] = NegativeSettlement.CreatedAtUtc,
        ["NegativeSettlement.PrimaryKey"] = NegativeSettlement.PrimaryKey,
        ["NegativeSettlement.ForeignKeyObligation"] = NegativeSettlement.ForeignKeyObligation,
        ["NegativeSettlement.ForeignKeyOperation"] = NegativeSettlement.ForeignKeyOperation,
        ["NegativeSettlement.ForeignKeyJournal"] = NegativeSettlement.ForeignKeyJournal,
        ["NegativeSettlement.ForeignKeyReverses"] = NegativeSettlement.ForeignKeyReverses,
        ["NegativeSettlement.CheckValues"] = NegativeSettlement.CheckValues,
        ["NegativeSettlement.UniqueMatch"] = NegativeSettlement.UniqueMatch,
        ["NegativeSettlement.UniqueReverses"] = NegativeSettlement.UniqueReverses,
        ["NegativeSettlement.ObligationIndex"] = NegativeSettlement.ObligationIndex,
        ["NegativeSettlement.PostingIndex"] = NegativeSettlement.PostingIndex,
        ["NegativeSettlement.OperationIndex"] = NegativeSettlement.OperationIndex,
        ["NegativeSettlement.GuardFunction"] = NegativeSettlement.GuardFunction,
        ["NegativeSettlement.GuardRow"] = NegativeSettlement.GuardRow,
        ["NegativeSettlement.GuardTruncate"] = NegativeSettlement.GuardTruncate,
        ["NegativeSettlement.StockFunction"] = NegativeSettlement.StockFunction,
        ["NegativeSettlement.StockTrigger"] = NegativeSettlement.StockTrigger,
        ["NegativeSettlement.RuleFunction"] = NegativeSettlement.RuleFunction,
        ["NegativeSettlement.RuleTrigger"] = NegativeSettlement.RuleTrigger,
        ["NegativeSettlement.ForeignKeyPosting"] = NegativeSettlement.ForeignKeyPosting,
        ["NegativeSettlement.Seq"] = NegativeSettlement.Seq,
        ["NegativeSettlement.UniqueSeq"] = NegativeSettlement.UniqueSeq,
        ["NegativeObligationReversal"] = NegativeObligationReversal.Table,
        ["NegativeObligationReversal.Id"] = NegativeObligationReversal.Id,
        ["NegativeObligationReversal.ObligationId"] = NegativeObligationReversal.ObligationId,
        ["NegativeObligationReversal.StockReversalId"] = NegativeObligationReversal.StockReversalId,
        ["NegativeObligationReversal.ReversalOperationId"] = NegativeObligationReversal.ReversalOperationId,
        ["NegativeObligationReversal.EffectiveDate"] = NegativeObligationReversal.EffectiveDate,
        ["NegativeObligationReversal.CreatedBy"] = NegativeObligationReversal.CreatedBy,
        ["NegativeObligationReversal.CreatedAtUtc"] = NegativeObligationReversal.CreatedAtUtc,
        ["NegativeObligationReversal.PrimaryKey"] = NegativeObligationReversal.PrimaryKey,
        ["NegativeObligationReversal.UniqueObligation"] = NegativeObligationReversal.UniqueObligation,
        ["NegativeObligationReversal.ForeignKeyObligation"] = NegativeObligationReversal.ForeignKeyObligation,
        ["NegativeObligationReversal.ForeignKeyOperation"] = NegativeObligationReversal.ForeignKeyOperation,
        ["NegativeObligationReversal.GuardFunction"] = NegativeObligationReversal.GuardFunction,
        ["NegativeObligationReversal.GuardRow"] = NegativeObligationReversal.GuardRow,
        ["NegativeObligationReversal.GuardTruncate"] = NegativeObligationReversal.GuardTruncate,
        ["NegativeObligationReversal.RuleFunction"] = NegativeObligationReversal.RuleFunction,
        ["NegativeObligationReversal.RuleTrigger"] = NegativeObligationReversal.RuleTrigger,
    });
}

