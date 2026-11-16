namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0017 (Phase 3.7C, reversal of executed stock documents). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class StockPostingReversal
    {
        public const string Table = "t_233a2574";
        public const string Id = "c_52d9badb";
        public const string PostingId = "c_005e3448";
        public const string CostOperationId = "c_60b20e96";
        public const string JournalAction = "c_c101e068";
        public const string JournalEntryId = "c_14b5b750";
        public const string EffectiveDate = "c_487438cf";
        public const string ReversedAtUtc = "c_66848f31";
        public const string ReversedBy = "c_89094e1a";
        public const string Reason = "c_8c797b62";
        public const string PrimaryKey = "k_204ddd61";
        public const string UniquePosting = "k_0fc305cb";
        public const string UniqueOperation = "k_7d76d818";
        public const string ForeignKeyPosting = "k_7c20d7a1";
        public const string ForeignKeyOperation = "k_4e9b1ca4";
        public const string ForeignKeyJournal = "k_50c2e5cd";
        public const string CheckValues = "k_783eddc2";
        public const string GuardFunction = "f_1d38aef8";
        public const string GuardRow = "r_9fae11d0";
        public const string GuardTruncate = "r_bc1373c4";
    }

    private static IReadOnlyDictionary<string, string> StockReversalMap => stockReversalMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> stockReversalMap = new(() => new Dictionary<string, string>
    {
        ["StockPostingReversal"] = StockPostingReversal.Table,
        ["StockPostingReversal.Id"] = StockPostingReversal.Id,
        ["StockPostingReversal.PostingId"] = StockPostingReversal.PostingId,
        ["StockPostingReversal.CostOperationId"] = StockPostingReversal.CostOperationId,
        ["StockPostingReversal.JournalAction"] = StockPostingReversal.JournalAction,
        ["StockPostingReversal.JournalEntryId"] = StockPostingReversal.JournalEntryId,
        ["StockPostingReversal.EffectiveDate"] = StockPostingReversal.EffectiveDate,
        ["StockPostingReversal.ReversedAtUtc"] = StockPostingReversal.ReversedAtUtc,
        ["StockPostingReversal.ReversedBy"] = StockPostingReversal.ReversedBy,
        ["StockPostingReversal.Reason"] = StockPostingReversal.Reason,
        ["StockPostingReversal.PrimaryKey"] = StockPostingReversal.PrimaryKey,
        ["StockPostingReversal.UniquePosting"] = StockPostingReversal.UniquePosting,
        ["StockPostingReversal.UniqueOperation"] = StockPostingReversal.UniqueOperation,
        ["StockPostingReversal.ForeignKeyPosting"] = StockPostingReversal.ForeignKeyPosting,
        ["StockPostingReversal.ForeignKeyOperation"] = StockPostingReversal.ForeignKeyOperation,
        ["StockPostingReversal.ForeignKeyJournal"] = StockPostingReversal.ForeignKeyJournal,
        ["StockPostingReversal.CheckValues"] = StockPostingReversal.CheckValues,
        ["StockPostingReversal.GuardFunction"] = StockPostingReversal.GuardFunction,
        ["StockPostingReversal.GuardRow"] = StockPostingReversal.GuardRow,
        ["StockPostingReversal.GuardTruncate"] = StockPostingReversal.GuardTruncate,
    });
}
