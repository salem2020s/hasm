namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0016 (Phase 3.7B, atomic stock posting records). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class StockPosting
    {
        public const string Table = "t_4d4b251f";
        public const string Id = "c_fc71418e";
        public const string DocumentId = "c_eb889fd0";
        public const string DocType = "c_78f9abc2";
        public const string EffectiveDate = "c_02d7d540";
        public const string PostedAtUtc = "c_b88be209";
        public const string PostedBy = "c_793ebee9";
        public const string CostOperationId = "c_4ce1587f";
        public const string AccountingVersionId = "c_fa7b9500";
        public const string JournalEntryId = "c_d667f7a4";
        public const string TotalValue = "c_6fb26986";
        public const string PrimaryKey = "k_2a9cf3b5";
        public const string UniqueDocument = "k_b529fec3";
        public const string UniqueOperation = "k_cc1974ee";
        public const string ForeignKeyDocument = "k_4f60a33d";
        public const string ForeignKeyOperation = "k_e40048b5";
        public const string ForeignKeyVersion = "k_fc4061bb";
        public const string ForeignKeyJournal = "k_f2a632a1";
        public const string JournalIndex = "k_999c64c1";
        public const string CheckValues = "k_a64a7947";
        public const string GuardFunction = "f_ffd78df3";
        public const string GuardRow = "r_53a924b0";
        public const string GuardTruncate = "r_3763de91";
    }

    public static class StockPostingLine
    {
        public const string Table = "t_7eb4c679";
        public const string PostingId = "c_ecf9865c";
        public const string LineNo = "c_8c33b614";
        public const string ItemId = "c_7a455438";
        public const string WarehouseId = "c_1f1c9cdf";
        public const string BaseQuantity = "c_b691de84";
        public const string Amount = "c_ea5e66bd";
        public const string PrimaryKey = "k_28b222ec";
        public const string ForeignKeyPosting = "k_ec4d973c";
        public const string ForeignKeyItem = "k_f4fb4d0c";
        public const string ForeignKeyWarehouse = "k_bda7550f";
        public const string CheckValues = "k_13876eb4";
        public const string GuardFunction = "f_a5d2d207";
        public const string GuardRow = "r_de410774";
        public const string GuardTruncate = "r_f0b3acfd";
    }

    public static class StockPostingEvent
    {
        public const string Table = "t_906dd3bf";
        public const string Id = "c_e164dc28";
        public const string PostingId = "c_eb6f593b";
        public const string Kind = "c_8ed3a437";
        public const string JournalEntryId = "c_ce401ba2";
        public const string AtUtc = "c_83d19b52";
        public const string ActorUserId = "c_c16e977b";
        public const string Note = "c_70218d90";
        public const string PrimaryKey = "k_4a246e07";
        public const string ForeignKeyPosting = "k_dd32cefa";
        public const string ForeignKeyJournal = "k_ab2dd267";
        public const string PostingIndex = "k_cf27714e";
        public const string CheckValues = "k_0e605b26";
        public const string GuardFunction = "f_c9a6ad54";
        public const string CancelGuardFunction = "f_9ca15a1d";
        public const string GuardRow = "r_a3ef992d";
        public const string GuardTruncate = "r_5c54812f";
        public const string CancelGuard = "r_45999b93";
    }

    private static IReadOnlyDictionary<string, string> StockPostingMap => stockPostingMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> stockPostingMap = new(() => new Dictionary<string, string>
    {
        ["StockPosting"] = StockPosting.Table,
        ["StockPosting.Id"] = StockPosting.Id,
        ["StockPosting.DocumentId"] = StockPosting.DocumentId,
        ["StockPosting.DocType"] = StockPosting.DocType,
        ["StockPosting.EffectiveDate"] = StockPosting.EffectiveDate,
        ["StockPosting.PostedAtUtc"] = StockPosting.PostedAtUtc,
        ["StockPosting.PostedBy"] = StockPosting.PostedBy,
        ["StockPosting.CostOperationId"] = StockPosting.CostOperationId,
        ["StockPosting.AccountingVersionId"] = StockPosting.AccountingVersionId,
        ["StockPosting.JournalEntryId"] = StockPosting.JournalEntryId,
        ["StockPosting.TotalValue"] = StockPosting.TotalValue,
        ["StockPosting.PrimaryKey"] = StockPosting.PrimaryKey,
        ["StockPosting.UniqueDocument"] = StockPosting.UniqueDocument,
        ["StockPosting.UniqueOperation"] = StockPosting.UniqueOperation,
        ["StockPosting.ForeignKeyDocument"] = StockPosting.ForeignKeyDocument,
        ["StockPosting.ForeignKeyOperation"] = StockPosting.ForeignKeyOperation,
        ["StockPosting.ForeignKeyVersion"] = StockPosting.ForeignKeyVersion,
        ["StockPosting.ForeignKeyJournal"] = StockPosting.ForeignKeyJournal,
        ["StockPosting.JournalIndex"] = StockPosting.JournalIndex,
        ["StockPosting.CheckValues"] = StockPosting.CheckValues,
        ["StockPosting.GuardFunction"] = StockPosting.GuardFunction,
        ["StockPosting.GuardRow"] = StockPosting.GuardRow,
        ["StockPosting.GuardTruncate"] = StockPosting.GuardTruncate,
        ["StockPostingLine"] = StockPostingLine.Table,
        ["StockPostingLine.PostingId"] = StockPostingLine.PostingId,
        ["StockPostingLine.LineNo"] = StockPostingLine.LineNo,
        ["StockPostingLine.ItemId"] = StockPostingLine.ItemId,
        ["StockPostingLine.WarehouseId"] = StockPostingLine.WarehouseId,
        ["StockPostingLine.BaseQuantity"] = StockPostingLine.BaseQuantity,
        ["StockPostingLine.Amount"] = StockPostingLine.Amount,
        ["StockPostingLine.PrimaryKey"] = StockPostingLine.PrimaryKey,
        ["StockPostingLine.ForeignKeyPosting"] = StockPostingLine.ForeignKeyPosting,
        ["StockPostingLine.ForeignKeyItem"] = StockPostingLine.ForeignKeyItem,
        ["StockPostingLine.ForeignKeyWarehouse"] = StockPostingLine.ForeignKeyWarehouse,
        ["StockPostingLine.CheckValues"] = StockPostingLine.CheckValues,
        ["StockPostingLine.GuardFunction"] = StockPostingLine.GuardFunction,
        ["StockPostingLine.GuardRow"] = StockPostingLine.GuardRow,
        ["StockPostingLine.GuardTruncate"] = StockPostingLine.GuardTruncate,
        ["StockPostingEvent"] = StockPostingEvent.Table,
        ["StockPostingEvent.Id"] = StockPostingEvent.Id,
        ["StockPostingEvent.PostingId"] = StockPostingEvent.PostingId,
        ["StockPostingEvent.Kind"] = StockPostingEvent.Kind,
        ["StockPostingEvent.JournalEntryId"] = StockPostingEvent.JournalEntryId,
        ["StockPostingEvent.AtUtc"] = StockPostingEvent.AtUtc,
        ["StockPostingEvent.ActorUserId"] = StockPostingEvent.ActorUserId,
        ["StockPostingEvent.Note"] = StockPostingEvent.Note,
        ["StockPostingEvent.PrimaryKey"] = StockPostingEvent.PrimaryKey,
        ["StockPostingEvent.ForeignKeyPosting"] = StockPostingEvent.ForeignKeyPosting,
        ["StockPostingEvent.ForeignKeyJournal"] = StockPostingEvent.ForeignKeyJournal,
        ["StockPostingEvent.PostingIndex"] = StockPostingEvent.PostingIndex,
        ["StockPostingEvent.CheckValues"] = StockPostingEvent.CheckValues,
        ["StockPostingEvent.GuardFunction"] = StockPostingEvent.GuardFunction,
        ["StockPostingEvent.CancelGuardFunction"] = StockPostingEvent.CancelGuardFunction,
        ["StockPostingEvent.GuardRow"] = StockPostingEvent.GuardRow,
        ["StockPostingEvent.GuardTruncate"] = StockPostingEvent.GuardTruncate,
        ["StockPostingEvent.CancelGuard"] = StockPostingEvent.CancelGuard,
    });
}
