namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0020 (Phase 3.9A, company-local party records and the request log of the party registry). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class LocalParty
    {
        public const string Table = "t_8edce6b0";
        public const string Id = "c_3f494703";
        public const string Code = "c_dd3886dd";
        public const string DisplayName = "c_9aa7c8f8";
        public const string Notes = "c_feceaad5";
        public const string IsActive = "c_85cb3b72";
        public const string LinkState = "c_6b6f8108";
        public const string GlobalPartyId = "c_2d9bc856";
        public const string Revision = "c_733f0b71";
        public const string CreatedBy = "c_80d142cf";
        public const string CreatedAtUtc = "c_4316c3c1";
        public const string UpdatedAtUtc = "c_d28d5811";
        public const string PrimaryKey = "k_52285519";
        public const string UniqueCode = "k_d9cc7405";
        public const string UniqueGlobal = "k_76724b46";
        public const string CheckValues = "k_2abae7c6";
        public const string GuardFunction = "f_4af6e893";
        public const string GuardRow = "r_3ff1c4c8";
        public const string GuardTruncate = "r_4631adb3";
    }

    public static class PartyRequest
    {
        public const string Table = "t_ba81b291";
        public const string Id = "c_32333a7b";
        public const string LocalPartyId = "c_66c55e86";
        public const string Kind = "c_0c8f01f0";
        public const string TargetGlobalPartyId = "c_a49afa8d";
        public const string PartyKind = "c_6ce5dcfb";
        public const string LegalNameAr = "c_51c1ef2f";
        public const string LegalNameEn = "c_efa6fc18";
        public const string TaxNumber = "c_5f57af39";
        public const string RegistrationNumber = "c_b52f0b6e";
        public const string ProposedStatus = "c_0e95693d";
        public const string Note = "c_991403a6";
        public const string Status = "c_ec870880";
        public const string Outcome = "c_7a8c0700";
        public const string OutcomeGlobalPartyId = "c_215a5e58";
        public const string Attempts = "c_a7b2180c";
        public const string LastAttemptAtUtc = "c_6f84193e";
        public const string LastErrorCode = "c_9aeea05e";
        public const string CreatedBy = "c_284d429a";
        public const string CreatedAtUtc = "c_36f6903f";
        public const string DeliveredAtUtc = "c_8c242e76";
        public const string CompletedAtUtc = "c_933eb4ea";
        public const string Revision = "c_b6864ce1";
        public const string PrimaryKey = "k_1fc60f73";
        public const string ForeignKeyLocal = "k_3782fac2";
        public const string UniqueOpen = "k_468c1d6e";
        public const string LocalIndex = "k_2c342abc";
        public const string StatusIndex = "k_2ca207bc";
        public const string CheckValues = "k_dfff7486";
        public const string GuardFunction = "f_50f3a8a1";
        public const string GuardRow = "r_1a458c19";
        public const string GuardTruncate = "r_9b2d5a99";
    }

    private static IReadOnlyDictionary<string, string> PartyMap => partyMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> partyMap = new(() => new Dictionary<string, string>
    {
        ["LocalParty"] = LocalParty.Table,
        ["LocalParty.Id"] = LocalParty.Id,
        ["LocalParty.Code"] = LocalParty.Code,
        ["LocalParty.DisplayName"] = LocalParty.DisplayName,
        ["LocalParty.Notes"] = LocalParty.Notes,
        ["LocalParty.IsActive"] = LocalParty.IsActive,
        ["LocalParty.LinkState"] = LocalParty.LinkState,
        ["LocalParty.GlobalPartyId"] = LocalParty.GlobalPartyId,
        ["LocalParty.Revision"] = LocalParty.Revision,
        ["LocalParty.CreatedBy"] = LocalParty.CreatedBy,
        ["LocalParty.CreatedAtUtc"] = LocalParty.CreatedAtUtc,
        ["LocalParty.UpdatedAtUtc"] = LocalParty.UpdatedAtUtc,
        ["LocalParty.PrimaryKey"] = LocalParty.PrimaryKey,
        ["LocalParty.UniqueCode"] = LocalParty.UniqueCode,
        ["LocalParty.UniqueGlobal"] = LocalParty.UniqueGlobal,
        ["LocalParty.CheckValues"] = LocalParty.CheckValues,
        ["LocalParty.GuardFunction"] = LocalParty.GuardFunction,
        ["LocalParty.GuardRow"] = LocalParty.GuardRow,
        ["LocalParty.GuardTruncate"] = LocalParty.GuardTruncate,
        ["PartyRequest"] = PartyRequest.Table,
        ["PartyRequest.Id"] = PartyRequest.Id,
        ["PartyRequest.LocalPartyId"] = PartyRequest.LocalPartyId,
        ["PartyRequest.Kind"] = PartyRequest.Kind,
        ["PartyRequest.TargetGlobalPartyId"] = PartyRequest.TargetGlobalPartyId,
        ["PartyRequest.PartyKind"] = PartyRequest.PartyKind,
        ["PartyRequest.LegalNameAr"] = PartyRequest.LegalNameAr,
        ["PartyRequest.LegalNameEn"] = PartyRequest.LegalNameEn,
        ["PartyRequest.TaxNumber"] = PartyRequest.TaxNumber,
        ["PartyRequest.RegistrationNumber"] = PartyRequest.RegistrationNumber,
        ["PartyRequest.ProposedStatus"] = PartyRequest.ProposedStatus,
        ["PartyRequest.Note"] = PartyRequest.Note,
        ["PartyRequest.Status"] = PartyRequest.Status,
        ["PartyRequest.Outcome"] = PartyRequest.Outcome,
        ["PartyRequest.OutcomeGlobalPartyId"] = PartyRequest.OutcomeGlobalPartyId,
        ["PartyRequest.Attempts"] = PartyRequest.Attempts,
        ["PartyRequest.LastAttemptAtUtc"] = PartyRequest.LastAttemptAtUtc,
        ["PartyRequest.LastErrorCode"] = PartyRequest.LastErrorCode,
        ["PartyRequest.CreatedBy"] = PartyRequest.CreatedBy,
        ["PartyRequest.CreatedAtUtc"] = PartyRequest.CreatedAtUtc,
        ["PartyRequest.DeliveredAtUtc"] = PartyRequest.DeliveredAtUtc,
        ["PartyRequest.CompletedAtUtc"] = PartyRequest.CompletedAtUtc,
        ["PartyRequest.Revision"] = PartyRequest.Revision,
        ["PartyRequest.PrimaryKey"] = PartyRequest.PrimaryKey,
        ["PartyRequest.ForeignKeyLocal"] = PartyRequest.ForeignKeyLocal,
        ["PartyRequest.UniqueOpen"] = PartyRequest.UniqueOpen,
        ["PartyRequest.LocalIndex"] = PartyRequest.LocalIndex,
        ["PartyRequest.StatusIndex"] = PartyRequest.StatusIndex,
        ["PartyRequest.CheckValues"] = PartyRequest.CheckValues,
        ["PartyRequest.GuardFunction"] = PartyRequest.GuardFunction,
        ["PartyRequest.GuardRow"] = PartyRequest.GuardRow,
        ["PartyRequest.GuardTruncate"] = PartyRequest.GuardTruncate,
    });
}
