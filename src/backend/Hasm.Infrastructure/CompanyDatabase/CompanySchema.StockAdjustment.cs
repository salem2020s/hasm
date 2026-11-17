namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0019 (Phase 3.8B-2, stock adjustments generated from an approved stocktake, their posting and reversal). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class StockAdjustment
    {
        public const string Table = "t_ef411fe9";
        public const string Id = "c_a56d5ec0";
        public const string StocktakeId = "c_2f4d598c";
        public const string BranchId = "c_415b4325";
        public const string WarehouseId = "c_79686333";
        public const string AdjustmentDate = "c_b71d44ca";
        public const string Number = "c_f1979515";
        public const string FiscalYearId = "c_eb67c083";
        public const string CreatedBy = "c_9e3ca302";
        public const string CreatedAtUtc = "c_f6b01cc1";
        public const string PrimaryKey = "k_5aeddbc2";
        public const string UniqueStocktake = "k_eea5b3fb";
        public const string UniqueNumber = "k_b78c7db1";
        public const string ForeignKeyStocktake = "k_78644625";
        public const string ForeignKeyBranch = "k_08494743";
        public const string ForeignKeyWarehouse = "k_1c1e384e";
        public const string ForeignKeyYear = "k_7e9b2f72";
        public const string GuardFunction = "f_246d3559";
        public const string GuardRow = "r_52eebea3";
        public const string GuardTruncate = "r_9fe06ad3";
        public const string LinkFunction = "f_b31406b7";
        public const string LinkTrigger = "r_799efe30";
        public const string LinesRequiredFunction = "f_00c34d24";
        public const string LinesRequiredTrigger = "r_8c4f72a8";
        public const string CancelGuardFunction = "f_5c74ab1f";
        public const string CancelGuardTrigger = "r_677dd89c";
    }

    public static class StockAdjustmentLine
    {
        public const string Table = "t_98f8a941";
        public const string AdjustmentId = "c_132a508f";
        public const string LineNo = "c_17befa84";
        public const string ItemId = "c_b47d52ef";
        public const string UnitId = "c_2219608a";
        public const string Direction = "c_4679f0ab";
        public const string Quantity = "c_d4b14b3f";
        public const string CountedQuantity = "c_18db4018";
        public const string BookQuantity = "c_73fc0c09";
        public const string Reason = "c_b785c5b9";
        public const string PrimaryKey = "k_d395bcde";
        public const string UniqueItem = "k_466532f3";
        public const string ForeignKeyAdjustment = "k_506d9cd6";
        public const string ForeignKeyItem = "k_1599407e";
        public const string ForeignKeyUnit = "k_1920d25b";
        public const string CheckValues = "k_6c3e195a";
        public const string GuardFunction = "f_28ae24c1";
        public const string GuardRow = "r_48cdd797";
        public const string GuardTruncate = "r_4ca060a3";
    }

    public static class StockAdjustmentPosting
    {
        public const string Table = "t_fb4a8e51";
        public const string Id = "c_345ead50";
        public const string AdjustmentId = "c_c4df29e1";
        public const string EffectiveDate = "c_1d635eec";
        public const string PostedAtUtc = "c_f9dda402";
        public const string PostedBy = "c_d8a23a29";
        public const string IssueOperationId = "c_cba9bae0";
        public const string ReceiptOperationId = "c_112c1630";
        public const string AccountingVersionId = "c_20b44cf8";
        public const string JournalEntryId = "c_654d3c97";
        public const string TotalValue = "c_0533fd18";
        public const string PrimaryKey = "k_d4fc9e8b";
        public const string UniqueAdjustment = "k_51f39043";
        public const string UniqueIssueOperation = "k_fc0e18c2";
        public const string UniqueReceiptOperation = "k_20d5b573";
        public const string ForeignKeyAdjustment = "k_10b4e5e5";
        public const string ForeignKeyIssueOperation = "k_0eaa3589";
        public const string ForeignKeyReceiptOperation = "k_43dc28bd";
        public const string ForeignKeyVersion = "k_d9c1c449";
        public const string ForeignKeyJournal = "k_f87e6441";
        public const string CheckValues = "k_74ccd88f";
        public const string JournalIndex = "k_c1dfafaa";
        public const string GuardFunction = "f_35814604";
        public const string GuardRow = "r_71f9c6b0";
        public const string GuardTruncate = "r_076558a2";
    }

    public static class StockAdjustmentPostingLine
    {
        public const string Table = "t_9d086874";
        public const string PostingId = "c_7286ee2d";
        public const string LineNo = "c_f8909b7a";
        public const string Amount = "c_e80bb402";
        public const string PrimaryKey = "k_ec5f1f18";
        public const string ForeignKeyPosting = "k_de63672f";
        public const string CheckValues = "k_643c4bc3";
        public const string GuardFunction = "f_7eb60451";
        public const string GuardRow = "r_b1a3d6ae";
        public const string GuardTruncate = "r_0ce5e3d8";
    }

    public static class StockAdjustmentReversal
    {
        public const string Table = "t_a3118c51";
        public const string Id = "c_1c5f7ed2";
        public const string PostingId = "c_822aba00";
        public const string IssueOperationId = "c_1f9a474c";
        public const string ReceiptOperationId = "c_8538a39d";
        public const string JournalAction = "c_463eb3d1";
        public const string JournalEntryId = "c_c77ab4aa";
        public const string EffectiveDate = "c_b3b8ce31";
        public const string ReversedAtUtc = "c_4f5c797f";
        public const string ReversedBy = "c_9b9982a1";
        public const string Reason = "c_3a86d3ef";
        public const string PrimaryKey = "k_9533342b";
        public const string UniquePosting = "k_7178c141";
        public const string UniqueIssueOperation = "k_febfff89";
        public const string UniqueReceiptOperation = "k_4d03aeb7";
        public const string ForeignKeyPosting = "k_986ed903";
        public const string ForeignKeyIssueOperation = "k_27b97d74";
        public const string ForeignKeyReceiptOperation = "k_9ad61c31";
        public const string ForeignKeyJournal = "k_25ffd26d";
        public const string CheckValues = "k_796abcf4";
        public const string GuardFunction = "f_cdab459e";
        public const string GuardRow = "r_2fcc8129";
        public const string GuardTruncate = "r_0430fa66";
    }

    private static IReadOnlyDictionary<string, string> StockAdjustmentMap => stockAdjustmentMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> stockAdjustmentMap = new(() => new Dictionary<string, string>
    {
        ["StockAdjustment"] = StockAdjustment.Table,
        ["StockAdjustment.Id"] = StockAdjustment.Id,
        ["StockAdjustment.StocktakeId"] = StockAdjustment.StocktakeId,
        ["StockAdjustment.BranchId"] = StockAdjustment.BranchId,
        ["StockAdjustment.WarehouseId"] = StockAdjustment.WarehouseId,
        ["StockAdjustment.AdjustmentDate"] = StockAdjustment.AdjustmentDate,
        ["StockAdjustment.Number"] = StockAdjustment.Number,
        ["StockAdjustment.FiscalYearId"] = StockAdjustment.FiscalYearId,
        ["StockAdjustment.CreatedBy"] = StockAdjustment.CreatedBy,
        ["StockAdjustment.CreatedAtUtc"] = StockAdjustment.CreatedAtUtc,
        ["StockAdjustment.PrimaryKey"] = StockAdjustment.PrimaryKey,
        ["StockAdjustment.UniqueStocktake"] = StockAdjustment.UniqueStocktake,
        ["StockAdjustment.UniqueNumber"] = StockAdjustment.UniqueNumber,
        ["StockAdjustment.ForeignKeyStocktake"] = StockAdjustment.ForeignKeyStocktake,
        ["StockAdjustment.ForeignKeyBranch"] = StockAdjustment.ForeignKeyBranch,
        ["StockAdjustment.ForeignKeyWarehouse"] = StockAdjustment.ForeignKeyWarehouse,
        ["StockAdjustment.ForeignKeyYear"] = StockAdjustment.ForeignKeyYear,
        ["StockAdjustment.GuardFunction"] = StockAdjustment.GuardFunction,
        ["StockAdjustment.GuardRow"] = StockAdjustment.GuardRow,
        ["StockAdjustment.GuardTruncate"] = StockAdjustment.GuardTruncate,
        ["StockAdjustment.LinkFunction"] = StockAdjustment.LinkFunction,
        ["StockAdjustment.LinkTrigger"] = StockAdjustment.LinkTrigger,
        ["StockAdjustment.LinesRequiredFunction"] = StockAdjustment.LinesRequiredFunction,
        ["StockAdjustment.LinesRequiredTrigger"] = StockAdjustment.LinesRequiredTrigger,
        ["StockAdjustment.CancelGuardFunction"] = StockAdjustment.CancelGuardFunction,
        ["StockAdjustment.CancelGuardTrigger"] = StockAdjustment.CancelGuardTrigger,
        ["StockAdjustmentLine"] = StockAdjustmentLine.Table,
        ["StockAdjustmentLine.AdjustmentId"] = StockAdjustmentLine.AdjustmentId,
        ["StockAdjustmentLine.LineNo"] = StockAdjustmentLine.LineNo,
        ["StockAdjustmentLine.ItemId"] = StockAdjustmentLine.ItemId,
        ["StockAdjustmentLine.UnitId"] = StockAdjustmentLine.UnitId,
        ["StockAdjustmentLine.Direction"] = StockAdjustmentLine.Direction,
        ["StockAdjustmentLine.Quantity"] = StockAdjustmentLine.Quantity,
        ["StockAdjustmentLine.CountedQuantity"] = StockAdjustmentLine.CountedQuantity,
        ["StockAdjustmentLine.BookQuantity"] = StockAdjustmentLine.BookQuantity,
        ["StockAdjustmentLine.Reason"] = StockAdjustmentLine.Reason,
        ["StockAdjustmentLine.PrimaryKey"] = StockAdjustmentLine.PrimaryKey,
        ["StockAdjustmentLine.UniqueItem"] = StockAdjustmentLine.UniqueItem,
        ["StockAdjustmentLine.ForeignKeyAdjustment"] = StockAdjustmentLine.ForeignKeyAdjustment,
        ["StockAdjustmentLine.ForeignKeyItem"] = StockAdjustmentLine.ForeignKeyItem,
        ["StockAdjustmentLine.ForeignKeyUnit"] = StockAdjustmentLine.ForeignKeyUnit,
        ["StockAdjustmentLine.CheckValues"] = StockAdjustmentLine.CheckValues,
        ["StockAdjustmentLine.GuardFunction"] = StockAdjustmentLine.GuardFunction,
        ["StockAdjustmentLine.GuardRow"] = StockAdjustmentLine.GuardRow,
        ["StockAdjustmentLine.GuardTruncate"] = StockAdjustmentLine.GuardTruncate,
        ["StockAdjustmentPosting"] = StockAdjustmentPosting.Table,
        ["StockAdjustmentPosting.Id"] = StockAdjustmentPosting.Id,
        ["StockAdjustmentPosting.AdjustmentId"] = StockAdjustmentPosting.AdjustmentId,
        ["StockAdjustmentPosting.EffectiveDate"] = StockAdjustmentPosting.EffectiveDate,
        ["StockAdjustmentPosting.PostedAtUtc"] = StockAdjustmentPosting.PostedAtUtc,
        ["StockAdjustmentPosting.PostedBy"] = StockAdjustmentPosting.PostedBy,
        ["StockAdjustmentPosting.IssueOperationId"] = StockAdjustmentPosting.IssueOperationId,
        ["StockAdjustmentPosting.ReceiptOperationId"] = StockAdjustmentPosting.ReceiptOperationId,
        ["StockAdjustmentPosting.AccountingVersionId"] = StockAdjustmentPosting.AccountingVersionId,
        ["StockAdjustmentPosting.JournalEntryId"] = StockAdjustmentPosting.JournalEntryId,
        ["StockAdjustmentPosting.TotalValue"] = StockAdjustmentPosting.TotalValue,
        ["StockAdjustmentPosting.PrimaryKey"] = StockAdjustmentPosting.PrimaryKey,
        ["StockAdjustmentPosting.UniqueAdjustment"] = StockAdjustmentPosting.UniqueAdjustment,
        ["StockAdjustmentPosting.UniqueIssueOperation"] = StockAdjustmentPosting.UniqueIssueOperation,
        ["StockAdjustmentPosting.UniqueReceiptOperation"] = StockAdjustmentPosting.UniqueReceiptOperation,
        ["StockAdjustmentPosting.ForeignKeyAdjustment"] = StockAdjustmentPosting.ForeignKeyAdjustment,
        ["StockAdjustmentPosting.ForeignKeyIssueOperation"] = StockAdjustmentPosting.ForeignKeyIssueOperation,
        ["StockAdjustmentPosting.ForeignKeyReceiptOperation"] = StockAdjustmentPosting.ForeignKeyReceiptOperation,
        ["StockAdjustmentPosting.ForeignKeyVersion"] = StockAdjustmentPosting.ForeignKeyVersion,
        ["StockAdjustmentPosting.ForeignKeyJournal"] = StockAdjustmentPosting.ForeignKeyJournal,
        ["StockAdjustmentPosting.CheckValues"] = StockAdjustmentPosting.CheckValues,
        ["StockAdjustmentPosting.JournalIndex"] = StockAdjustmentPosting.JournalIndex,
        ["StockAdjustmentPosting.GuardFunction"] = StockAdjustmentPosting.GuardFunction,
        ["StockAdjustmentPosting.GuardRow"] = StockAdjustmentPosting.GuardRow,
        ["StockAdjustmentPosting.GuardTruncate"] = StockAdjustmentPosting.GuardTruncate,
        ["StockAdjustmentPostingLine"] = StockAdjustmentPostingLine.Table,
        ["StockAdjustmentPostingLine.PostingId"] = StockAdjustmentPostingLine.PostingId,
        ["StockAdjustmentPostingLine.LineNo"] = StockAdjustmentPostingLine.LineNo,
        ["StockAdjustmentPostingLine.Amount"] = StockAdjustmentPostingLine.Amount,
        ["StockAdjustmentPostingLine.PrimaryKey"] = StockAdjustmentPostingLine.PrimaryKey,
        ["StockAdjustmentPostingLine.ForeignKeyPosting"] = StockAdjustmentPostingLine.ForeignKeyPosting,
        ["StockAdjustmentPostingLine.CheckValues"] = StockAdjustmentPostingLine.CheckValues,
        ["StockAdjustmentPostingLine.GuardFunction"] = StockAdjustmentPostingLine.GuardFunction,
        ["StockAdjustmentPostingLine.GuardRow"] = StockAdjustmentPostingLine.GuardRow,
        ["StockAdjustmentPostingLine.GuardTruncate"] = StockAdjustmentPostingLine.GuardTruncate,
        ["StockAdjustmentReversal"] = StockAdjustmentReversal.Table,
        ["StockAdjustmentReversal.Id"] = StockAdjustmentReversal.Id,
        ["StockAdjustmentReversal.PostingId"] = StockAdjustmentReversal.PostingId,
        ["StockAdjustmentReversal.IssueOperationId"] = StockAdjustmentReversal.IssueOperationId,
        ["StockAdjustmentReversal.ReceiptOperationId"] = StockAdjustmentReversal.ReceiptOperationId,
        ["StockAdjustmentReversal.JournalAction"] = StockAdjustmentReversal.JournalAction,
        ["StockAdjustmentReversal.JournalEntryId"] = StockAdjustmentReversal.JournalEntryId,
        ["StockAdjustmentReversal.EffectiveDate"] = StockAdjustmentReversal.EffectiveDate,
        ["StockAdjustmentReversal.ReversedAtUtc"] = StockAdjustmentReversal.ReversedAtUtc,
        ["StockAdjustmentReversal.ReversedBy"] = StockAdjustmentReversal.ReversedBy,
        ["StockAdjustmentReversal.Reason"] = StockAdjustmentReversal.Reason,
        ["StockAdjustmentReversal.PrimaryKey"] = StockAdjustmentReversal.PrimaryKey,
        ["StockAdjustmentReversal.UniquePosting"] = StockAdjustmentReversal.UniquePosting,
        ["StockAdjustmentReversal.UniqueIssueOperation"] = StockAdjustmentReversal.UniqueIssueOperation,
        ["StockAdjustmentReversal.UniqueReceiptOperation"] = StockAdjustmentReversal.UniqueReceiptOperation,
        ["StockAdjustmentReversal.ForeignKeyPosting"] = StockAdjustmentReversal.ForeignKeyPosting,
        ["StockAdjustmentReversal.ForeignKeyIssueOperation"] = StockAdjustmentReversal.ForeignKeyIssueOperation,
        ["StockAdjustmentReversal.ForeignKeyReceiptOperation"] = StockAdjustmentReversal.ForeignKeyReceiptOperation,
        ["StockAdjustmentReversal.ForeignKeyJournal"] = StockAdjustmentReversal.ForeignKeyJournal,
        ["StockAdjustmentReversal.CheckValues"] = StockAdjustmentReversal.CheckValues,
        ["StockAdjustmentReversal.GuardFunction"] = StockAdjustmentReversal.GuardFunction,
        ["StockAdjustmentReversal.GuardRow"] = StockAdjustmentReversal.GuardRow,
        ["StockAdjustmentReversal.GuardTruncate"] = StockAdjustmentReversal.GuardTruncate,
    });
}
