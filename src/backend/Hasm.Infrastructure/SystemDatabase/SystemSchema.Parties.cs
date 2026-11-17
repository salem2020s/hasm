namespace Hasm.Infrastructure.SystemDatabase;

// Generated once for system migration 0022 (Phase 3.9A, party registry: shared identity, central proposals, links). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class SystemSchema
{
    public static class GlobalParty
    {
        public const string Table = "t_eabdd8fc";
        public const string Id = "c_f8e48d99";
        public const string PartyKind = "c_0a313805";
        public const string LegalNameAr = "c_ebf7a8f6";
        public const string LegalNameEn = "c_3d6c80d1";
        public const string TaxNumber = "c_45afe91f";
        public const string RegistrationNumber = "c_49127565";
        public const string Status = "c_136713b0";
        public const string Revision = "c_7cc2d0df";
        public const string CreatedBy = "c_abea2e11";
        public const string CreatedAtUtc = "c_d6cc2370";
        public const string UpdatedAtUtc = "c_c8ed2c3d";
        public const string PrimaryKey = "k_750c92d7";
        public const string ForeignKeyCreatedBy = "k_c8b5deee";
        public const string CheckValues = "k_3d609b38";
        public const string UniqueTaxNumber = "k_8c4a4f3f";
        public const string UniqueRegistrationNumber = "k_3f114029";
        public const string NameIndex = "k_d02a9cf3";
        public const string GuardFunction = "f_987c4744";
        public const string GuardRow = "r_3dbb630e";
        public const string GuardTruncate = "r_304cbcbd";
    }

    public static class PartyProposal
    {
        public const string Table = "t_68b1b903";
        public const string Id = "c_1ed401f7";
        public const string CompanyId = "c_153f979f";
        public const string RequestKey = "c_87534b06";
        public const string LocalPartyId = "c_a9dfc171";
        public const string Kind = "c_d51f2b1e";
        public const string TargetGlobalPartyId = "c_1d897157";
        public const string PartyKind = "c_93954331";
        public const string LegalNameAr = "c_1d21b300";
        public const string LegalNameEn = "c_a95c5183";
        public const string TaxNumber = "c_ce1a1eb7";
        public const string RegistrationNumber = "c_5363aefe";
        public const string ProposedStatus = "c_ae2e3b2c";
        public const string Note = "c_f684f81e";
        public const string ProposedBy = "c_820709aa";
        public const string ProposedAtUtc = "c_68576c0b";
        public const string Status = "c_80581f31";
        public const string Resolution = "c_a19bd897";
        public const string OutcomeGlobalPartyId = "c_17d1e5a3";
        public const string DecidedBy = "c_c8294d0e";
        public const string DecidedAtUtc = "c_25b2714d";
        public const string DecisionNote = "c_925b20bb";
        public const string Revision = "c_445301d4";
        public const string PrimaryKey = "k_bb24c9f1";
        public const string ForeignKeyCompany = "k_5bb6036f";
        public const string ForeignKeyProposedBy = "k_693a7ce4";
        public const string ForeignKeyDecidedBy = "k_83ffe1f8";
        public const string ForeignKeyTarget = "k_d7289a19";
        public const string ForeignKeyOutcome = "k_06ae1580";
        public const string UniqueRequest = "k_3151851f";
        public const string UniqueOpenPerLocal = "k_ee312585";
        public const string StatusIndex = "k_8bcf8142";
        public const string CheckValues = "k_30e82a25";
        public const string CheckDecision = "k_8602fc77";
        public const string CheckSeparation = "k_585ecab3";
        public const string GuardFunction = "f_96b67c1d";
        public const string GuardRow = "r_5230ef3e";
        public const string GuardTruncate = "r_b02a8650";
    }

    public static class PartyLink
    {
        public const string Table = "t_0c91bd31";
        public const string CompanyId = "c_303d1fea";
        public const string LocalPartyId = "c_b150ec00";
        public const string GlobalPartyId = "c_a02c7beb";
        public const string ProposalId = "c_53499268";
        public const string LinkedAtUtc = "c_71001026";
        public const string PrimaryKey = "k_d74b4c0f";
        public const string UniqueCompanyGlobal = "k_dee379d4";
        public const string ForeignKeyCompany = "k_e276b776";
        public const string ForeignKeyGlobal = "k_8b26c8de";
        public const string ForeignKeyProposal = "k_c3989189";
        public const string GlobalIndex = "k_ed782a9b";
        public const string GuardFunction = "f_7cd7a401";
        public const string GuardRow = "r_94e983f3";
        public const string GuardTruncate = "r_468671c8";
    }

    private static IReadOnlyDictionary<string, string> PartyMap => partyMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> partyMap = new(() => new Dictionary<string, string>
    {
        ["GlobalParty"] = GlobalParty.Table,
        ["GlobalParty.Id"] = GlobalParty.Id,
        ["GlobalParty.PartyKind"] = GlobalParty.PartyKind,
        ["GlobalParty.LegalNameAr"] = GlobalParty.LegalNameAr,
        ["GlobalParty.LegalNameEn"] = GlobalParty.LegalNameEn,
        ["GlobalParty.TaxNumber"] = GlobalParty.TaxNumber,
        ["GlobalParty.RegistrationNumber"] = GlobalParty.RegistrationNumber,
        ["GlobalParty.Status"] = GlobalParty.Status,
        ["GlobalParty.Revision"] = GlobalParty.Revision,
        ["GlobalParty.CreatedBy"] = GlobalParty.CreatedBy,
        ["GlobalParty.CreatedAtUtc"] = GlobalParty.CreatedAtUtc,
        ["GlobalParty.UpdatedAtUtc"] = GlobalParty.UpdatedAtUtc,
        ["GlobalParty.PrimaryKey"] = GlobalParty.PrimaryKey,
        ["GlobalParty.ForeignKeyCreatedBy"] = GlobalParty.ForeignKeyCreatedBy,
        ["GlobalParty.CheckValues"] = GlobalParty.CheckValues,
        ["GlobalParty.UniqueTaxNumber"] = GlobalParty.UniqueTaxNumber,
        ["GlobalParty.UniqueRegistrationNumber"] = GlobalParty.UniqueRegistrationNumber,
        ["GlobalParty.NameIndex"] = GlobalParty.NameIndex,
        ["GlobalParty.GuardFunction"] = GlobalParty.GuardFunction,
        ["GlobalParty.GuardRow"] = GlobalParty.GuardRow,
        ["GlobalParty.GuardTruncate"] = GlobalParty.GuardTruncate,
        ["PartyProposal"] = PartyProposal.Table,
        ["PartyProposal.Id"] = PartyProposal.Id,
        ["PartyProposal.CompanyId"] = PartyProposal.CompanyId,
        ["PartyProposal.RequestKey"] = PartyProposal.RequestKey,
        ["PartyProposal.LocalPartyId"] = PartyProposal.LocalPartyId,
        ["PartyProposal.Kind"] = PartyProposal.Kind,
        ["PartyProposal.TargetGlobalPartyId"] = PartyProposal.TargetGlobalPartyId,
        ["PartyProposal.PartyKind"] = PartyProposal.PartyKind,
        ["PartyProposal.LegalNameAr"] = PartyProposal.LegalNameAr,
        ["PartyProposal.LegalNameEn"] = PartyProposal.LegalNameEn,
        ["PartyProposal.TaxNumber"] = PartyProposal.TaxNumber,
        ["PartyProposal.RegistrationNumber"] = PartyProposal.RegistrationNumber,
        ["PartyProposal.ProposedStatus"] = PartyProposal.ProposedStatus,
        ["PartyProposal.Note"] = PartyProposal.Note,
        ["PartyProposal.ProposedBy"] = PartyProposal.ProposedBy,
        ["PartyProposal.ProposedAtUtc"] = PartyProposal.ProposedAtUtc,
        ["PartyProposal.Status"] = PartyProposal.Status,
        ["PartyProposal.Resolution"] = PartyProposal.Resolution,
        ["PartyProposal.OutcomeGlobalPartyId"] = PartyProposal.OutcomeGlobalPartyId,
        ["PartyProposal.DecidedBy"] = PartyProposal.DecidedBy,
        ["PartyProposal.DecidedAtUtc"] = PartyProposal.DecidedAtUtc,
        ["PartyProposal.DecisionNote"] = PartyProposal.DecisionNote,
        ["PartyProposal.Revision"] = PartyProposal.Revision,
        ["PartyProposal.PrimaryKey"] = PartyProposal.PrimaryKey,
        ["PartyProposal.ForeignKeyCompany"] = PartyProposal.ForeignKeyCompany,
        ["PartyProposal.ForeignKeyProposedBy"] = PartyProposal.ForeignKeyProposedBy,
        ["PartyProposal.ForeignKeyDecidedBy"] = PartyProposal.ForeignKeyDecidedBy,
        ["PartyProposal.ForeignKeyTarget"] = PartyProposal.ForeignKeyTarget,
        ["PartyProposal.ForeignKeyOutcome"] = PartyProposal.ForeignKeyOutcome,
        ["PartyProposal.UniqueRequest"] = PartyProposal.UniqueRequest,
        ["PartyProposal.UniqueOpenPerLocal"] = PartyProposal.UniqueOpenPerLocal,
        ["PartyProposal.StatusIndex"] = PartyProposal.StatusIndex,
        ["PartyProposal.CheckValues"] = PartyProposal.CheckValues,
        ["PartyProposal.CheckDecision"] = PartyProposal.CheckDecision,
        ["PartyProposal.CheckSeparation"] = PartyProposal.CheckSeparation,
        ["PartyProposal.GuardFunction"] = PartyProposal.GuardFunction,
        ["PartyProposal.GuardRow"] = PartyProposal.GuardRow,
        ["PartyProposal.GuardTruncate"] = PartyProposal.GuardTruncate,
        ["PartyLink"] = PartyLink.Table,
        ["PartyLink.CompanyId"] = PartyLink.CompanyId,
        ["PartyLink.LocalPartyId"] = PartyLink.LocalPartyId,
        ["PartyLink.GlobalPartyId"] = PartyLink.GlobalPartyId,
        ["PartyLink.ProposalId"] = PartyLink.ProposalId,
        ["PartyLink.LinkedAtUtc"] = PartyLink.LinkedAtUtc,
        ["PartyLink.PrimaryKey"] = PartyLink.PrimaryKey,
        ["PartyLink.UniqueCompanyGlobal"] = PartyLink.UniqueCompanyGlobal,
        ["PartyLink.ForeignKeyCompany"] = PartyLink.ForeignKeyCompany,
        ["PartyLink.ForeignKeyGlobal"] = PartyLink.ForeignKeyGlobal,
        ["PartyLink.ForeignKeyProposal"] = PartyLink.ForeignKeyProposal,
        ["PartyLink.GlobalIndex"] = PartyLink.GlobalIndex,
        ["PartyLink.GuardFunction"] = PartyLink.GuardFunction,
        ["PartyLink.GuardRow"] = PartyLink.GuardRow,
        ["PartyLink.GuardTruncate"] = PartyLink.GuardTruncate,
    });
}
