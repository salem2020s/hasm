namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0008 (Phase 3.2B, slice S2, exchange rates). Physical names are opaque and fixed.
public static partial class CompanySchema
{
    public static class ExchangeRate
    {
        public const string Table = "t_eba5b513";
        public const string Id = "c_e3463fc9";
        public const string CurrencyCode = "c_3fdf88c5";
        public const string Rate = "c_515a2107";
        public const string ValidFrom = "c_94455323";
        public const string EnteredAtUtc = "c_fc1d8c8c";
        public const string EnteredBy = "c_1cb257f5";
        public const string Note = "c_e2f9f81f";
        public const string AuditStream = "c_340bec19";
        public const string AuditSeq = "c_48359c89";
        public const string PrimaryKey = "k_f1618a01";
        public const string ForeignKeyCurrency = "k_53cc3679";
        public const string UniqueAuditEvent = "k_25aaa623";
        public const string ForeignKeyAudit = "k_a38d200e";
        public const string CheckValues = "k_71fa9285";
        public const string LookupIndex = "k_4770ec5e";
        public const string GuardFunction = "f_6be4dcce";
        public const string GuardRow = "r_22a4dc76";
        public const string GuardTruncate = "r_3e5844f1";
        public const string CheckFunction = "f_489b7b28";
        public const string CheckTrigger = "r_e25ae556";
        public const string ResolveFunction = "f_56eae1de";
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ratesMap = new(() => new Dictionary<string, string>
    {
        ["ExchangeRate"] = ExchangeRate.Table,
        ["ExchangeRate.Id"] = ExchangeRate.Id,
        ["ExchangeRate.CurrencyCode"] = ExchangeRate.CurrencyCode,
        ["ExchangeRate.Rate"] = ExchangeRate.Rate,
        ["ExchangeRate.ValidFrom"] = ExchangeRate.ValidFrom,
        ["ExchangeRate.EnteredAtUtc"] = ExchangeRate.EnteredAtUtc,
        ["ExchangeRate.EnteredBy"] = ExchangeRate.EnteredBy,
        ["ExchangeRate.Note"] = ExchangeRate.Note,
        ["ExchangeRate.AuditStream"] = ExchangeRate.AuditStream,
        ["ExchangeRate.AuditSeq"] = ExchangeRate.AuditSeq,
        ["ExchangeRate.PrimaryKey"] = ExchangeRate.PrimaryKey,
        ["ExchangeRate.ForeignKeyCurrency"] = ExchangeRate.ForeignKeyCurrency,
        ["ExchangeRate.UniqueAuditEvent"] = ExchangeRate.UniqueAuditEvent,
        ["ExchangeRate.ForeignKeyAudit"] = ExchangeRate.ForeignKeyAudit,
        ["ExchangeRate.CheckValues"] = ExchangeRate.CheckValues,
        ["ExchangeRate.LookupIndex"] = ExchangeRate.LookupIndex,
        ["ExchangeRate.GuardFunction"] = ExchangeRate.GuardFunction,
        ["ExchangeRate.GuardRow"] = ExchangeRate.GuardRow,
        ["ExchangeRate.GuardTruncate"] = ExchangeRate.GuardTruncate,
        ["ExchangeRate.CheckFunction"] = ExchangeRate.CheckFunction,
        ["ExchangeRate.CheckTrigger"] = ExchangeRate.CheckTrigger,
        ["ExchangeRate.ResolveFunction"] = ExchangeRate.ResolveFunction,
    });
    private static IReadOnlyDictionary<string, string> RatesMap => ratesMap.Value;
}
