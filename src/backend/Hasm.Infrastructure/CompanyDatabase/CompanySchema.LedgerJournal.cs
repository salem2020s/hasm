namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0008 (Phase 3.2B, slice S5, journal entries and the posting engine). Physical names are opaque and fixed.
public static partial class CompanySchema
{
    public static class LedgerSettings
    {
        public const string Account = "c_946b25e2";
        public const string Rev = "c_5d6ede30";
        public const string Singleton = "c_1b115900";
        public const string Updated = "c_b500974f";
        public const string FGuard = "f_164bf90a";
        public const string KChk = "k_050add4c";
        public const string KFkAccount = "k_15a8f12e";
        public const string KPk = "k_e98861e6";
        public const string RGuard = "r_d545f73e";
        public const string RTrunc = "r_5beeca3c";
        public const string Table = "t_d8f3ea94";
    }

    public static class JournalEntry
    {
        public const string Adjusts = "c_f509b5de";
        public const string BaseCur = "c_429fabdf";
        public const string BaseTotal = "c_225ac5cd";
        public const string Branch = "c_59a672dc";
        public const string CreatedAt = "c_12ff75c5";
        public const string CreatedBy = "c_2a51947a";
        public const string Date = "c_4cb99522";
        public const string Desc = "c_a05818cd";
        public const string Id = "c_14f317c6";
        public const string Number = "c_5337d7b9";
        public const string Period = "c_dc0c9baf";
        public const string PostedAt = "c_84bcfb07";
        public const string PostedBy = "c_7e5c5517";
        public const string Reverses = "c_e11acf50";
        public const string Rev = "c_dfe6b7c5";
        public const string SoftPost = "c_e5cc9a38";
        public const string SourceKey = "c_9882fcfa";
        public const string Source = "c_41c437ea";
        public const string Status = "c_4e4c6448";
        public const string SubmittedAt = "c_7c479dc6";
        public const string SubmittedBy = "c_41579554";
        public const string Type = "c_960dbf25";
        public const string UpdatedAt = "c_f0e21870";
        public const string FCheck = "f_ba4c6a0d";
        public const string FDeferred = "f_47233bbd";
        public const string FGuard = "f_2dc99411";
        public const string KChk = "k_eb8d49c6";
        public const string KFkAdjusts = "k_ae9028b2";
        public const string KFkBaseCur = "k_0e8f8ebe";
        public const string KFkBranch = "k_4677dcab";
        public const string KFkPeriod = "k_d69e8b1c";
        public const string KFkReverses = "k_7c9d7b49";
        public const string KIDate = "k_ac858588";
        public const string KIStatus = "k_4e35cc09";
        public const string KPk = "k_d9694054";
        public const string KUNumber = "k_3218238f";
        public const string KUReverse = "k_dc49cb81";
        public const string KUSource = "k_f559dc87";
        public const string RDeferred = "r_fa0e9ea3";
        public const string RGuard = "r_bb0333e0";
        public const string RTrunc = "r_54fc2d18";
        public const string Table = "t_f3158869";
    }

    public static class JournalLine
    {
        public const string Account = "c_e6bfb072";
        public const string BaseCredit = "c_03200b86";
        public const string BaseDebit = "c_c560d388";
        public const string Branch = "c_147d3982";
        public const string Credit = "c_a1befb78";
        public const string Cur = "c_7a2c36af";
        public const string Debit = "c_d0e33fe9";
        public const string Desc = "c_31339018";
        public const string Entry = "c_39596059";
        public const string Id = "c_5ffa4fb6";
        public const string IsRounding = "c_781e3043";
        public const string No = "c_d6ebd9a0";
        public const string RateId = "c_2fc5b902";
        public const string RateReason = "c_7931ab9f";
        public const string Rate = "c_62c9d327";
        public const string ReversesLine = "c_b75ddd12";
        public const string FGuard = "f_f9323807";
        public const string FLineSum = "f_81b496df";
        public const string FTrunc = "f_ad8bda4d";
        public const string KChk = "k_433f8d1c";
        public const string KFkAccount = "k_c91cc298";
        public const string KFkBranch = "k_0bd82149";
        public const string KFkCur = "k_d98922ee";
        public const string KFkEntry = "k_55c6626a";
        public const string KFkRate = "k_be9adc08";
        public const string KFkRev = "k_f475ca55";
        public const string KIAccount = "k_76363ffa";
        public const string KPk = "k_397c59a3";
        public const string KUNo = "k_bac36c16";
        public const string RDeferred = "r_aee5d9ca";
        public const string RGuard = "r_9f0254e3";
        public const string RTrunc = "r_bab00893";
        public const string Table = "t_783c8d98";
    }

    public static class JournalAllocation
    {
        public const string Amount = "c_f811132c";
        public const string Base = "c_aacf931e";
        public const string CC = "c_71376e84";
        public const string Id = "c_55521d90";
        public const string Line = "c_0146172b";
        public const string FDeferred = "f_b79be4c8";
        public const string FGuard = "f_b6ad8e29";
        public const string KChk = "k_0fbdfaad";
        public const string KFkCC = "k_9226c56b";
        public const string KFkLine = "k_0de6a859";
        public const string KPk = "k_7e2bee77";
        public const string KUnique = "k_80d4e1cf";
        public const string RDeferred = "r_787dfce2";
        public const string RGuard = "r_0c17708a";
        public const string RTrunc = "r_f53a91f3";
        public const string Table = "t_58417560";
    }

    public static class JournalEvent
    {
        public const string Actor = "c_9dd85d91";
        public const string At = "c_8b0a36c1";
        public const string Detail = "c_a8566788";
        public const string Entry = "c_27d80de0";
        public const string Event = "c_e9c92b2a";
        public const string Id = "c_f8942dfa";
        public const string Rev = "c_39a332da";
        public const string Seq = "c_90ad2c77";
        public const string Stream = "c_4fae4433";
        public const string FGuard = "f_1669dc9b";
        public const string KChk = "k_2e340a81";
        public const string KFkAudit = "k_f04d6c2f";
        public const string KFkEntry = "k_64dc210e";
        public const string KPk = "k_d449a216";
        public const string KUAudit = "k_30d47901";
        public const string RGuard = "r_c48bbad2";
        public const string RTrunc = "r_ff06334f";
        public const string Table = "t_b6491d4d";
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> journalMap = new(() => new Dictionary<string, string>
    {
        ["LedgerSettings.Account"] = LedgerSettings.Account,
        ["LedgerSettings.Rev"] = LedgerSettings.Rev,
        ["LedgerSettings.Singleton"] = LedgerSettings.Singleton,
        ["LedgerSettings.Updated"] = LedgerSettings.Updated,
        ["LedgerSettings.FGuard"] = LedgerSettings.FGuard,
        ["LedgerSettings.KChk"] = LedgerSettings.KChk,
        ["LedgerSettings.KFkAccount"] = LedgerSettings.KFkAccount,
        ["LedgerSettings.KPk"] = LedgerSettings.KPk,
        ["LedgerSettings.RGuard"] = LedgerSettings.RGuard,
        ["LedgerSettings.RTrunc"] = LedgerSettings.RTrunc,
        ["LedgerSettings"] = LedgerSettings.Table,
        ["JournalEntry.Adjusts"] = JournalEntry.Adjusts,
        ["JournalEntry.BaseCur"] = JournalEntry.BaseCur,
        ["JournalEntry.BaseTotal"] = JournalEntry.BaseTotal,
        ["JournalEntry.Branch"] = JournalEntry.Branch,
        ["JournalEntry.CreatedAt"] = JournalEntry.CreatedAt,
        ["JournalEntry.CreatedBy"] = JournalEntry.CreatedBy,
        ["JournalEntry.Date"] = JournalEntry.Date,
        ["JournalEntry.Desc"] = JournalEntry.Desc,
        ["JournalEntry.Id"] = JournalEntry.Id,
        ["JournalEntry.Number"] = JournalEntry.Number,
        ["JournalEntry.Period"] = JournalEntry.Period,
        ["JournalEntry.PostedAt"] = JournalEntry.PostedAt,
        ["JournalEntry.PostedBy"] = JournalEntry.PostedBy,
        ["JournalEntry.Reverses"] = JournalEntry.Reverses,
        ["JournalEntry.Rev"] = JournalEntry.Rev,
        ["JournalEntry.SoftPost"] = JournalEntry.SoftPost,
        ["JournalEntry.SourceKey"] = JournalEntry.SourceKey,
        ["JournalEntry.Source"] = JournalEntry.Source,
        ["JournalEntry.Status"] = JournalEntry.Status,
        ["JournalEntry.SubmittedAt"] = JournalEntry.SubmittedAt,
        ["JournalEntry.SubmittedBy"] = JournalEntry.SubmittedBy,
        ["JournalEntry.Type"] = JournalEntry.Type,
        ["JournalEntry.UpdatedAt"] = JournalEntry.UpdatedAt,
        ["JournalEntry.FCheck"] = JournalEntry.FCheck,
        ["JournalEntry.FDeferred"] = JournalEntry.FDeferred,
        ["JournalEntry.FGuard"] = JournalEntry.FGuard,
        ["JournalEntry.KChk"] = JournalEntry.KChk,
        ["JournalEntry.KFkAdjusts"] = JournalEntry.KFkAdjusts,
        ["JournalEntry.KFkBaseCur"] = JournalEntry.KFkBaseCur,
        ["JournalEntry.KFkBranch"] = JournalEntry.KFkBranch,
        ["JournalEntry.KFkPeriod"] = JournalEntry.KFkPeriod,
        ["JournalEntry.KFkReverses"] = JournalEntry.KFkReverses,
        ["JournalEntry.KIDate"] = JournalEntry.KIDate,
        ["JournalEntry.KIStatus"] = JournalEntry.KIStatus,
        ["JournalEntry.KPk"] = JournalEntry.KPk,
        ["JournalEntry.KUNumber"] = JournalEntry.KUNumber,
        ["JournalEntry.KUReverse"] = JournalEntry.KUReverse,
        ["JournalEntry.KUSource"] = JournalEntry.KUSource,
        ["JournalEntry.RDeferred"] = JournalEntry.RDeferred,
        ["JournalEntry.RGuard"] = JournalEntry.RGuard,
        ["JournalEntry.RTrunc"] = JournalEntry.RTrunc,
        ["JournalEntry"] = JournalEntry.Table,
        ["JournalLine.Account"] = JournalLine.Account,
        ["JournalLine.BaseCredit"] = JournalLine.BaseCredit,
        ["JournalLine.BaseDebit"] = JournalLine.BaseDebit,
        ["JournalLine.Branch"] = JournalLine.Branch,
        ["JournalLine.Credit"] = JournalLine.Credit,
        ["JournalLine.Cur"] = JournalLine.Cur,
        ["JournalLine.Debit"] = JournalLine.Debit,
        ["JournalLine.Desc"] = JournalLine.Desc,
        ["JournalLine.Entry"] = JournalLine.Entry,
        ["JournalLine.Id"] = JournalLine.Id,
        ["JournalLine.IsRounding"] = JournalLine.IsRounding,
        ["JournalLine.No"] = JournalLine.No,
        ["JournalLine.RateId"] = JournalLine.RateId,
        ["JournalLine.RateReason"] = JournalLine.RateReason,
        ["JournalLine.Rate"] = JournalLine.Rate,
        ["JournalLine.ReversesLine"] = JournalLine.ReversesLine,
        ["JournalLine.FGuard"] = JournalLine.FGuard,
        ["JournalLine.FLineSum"] = JournalLine.FLineSum,
        ["JournalLine.FTrunc"] = JournalLine.FTrunc,
        ["JournalLine.KChk"] = JournalLine.KChk,
        ["JournalLine.KFkAccount"] = JournalLine.KFkAccount,
        ["JournalLine.KFkBranch"] = JournalLine.KFkBranch,
        ["JournalLine.KFkCur"] = JournalLine.KFkCur,
        ["JournalLine.KFkEntry"] = JournalLine.KFkEntry,
        ["JournalLine.KFkRate"] = JournalLine.KFkRate,
        ["JournalLine.KFkRev"] = JournalLine.KFkRev,
        ["JournalLine.KIAccount"] = JournalLine.KIAccount,
        ["JournalLine.KPk"] = JournalLine.KPk,
        ["JournalLine.KUNo"] = JournalLine.KUNo,
        ["JournalLine.RDeferred"] = JournalLine.RDeferred,
        ["JournalLine.RGuard"] = JournalLine.RGuard,
        ["JournalLine.RTrunc"] = JournalLine.RTrunc,
        ["JournalLine"] = JournalLine.Table,
        ["JournalAllocation.Amount"] = JournalAllocation.Amount,
        ["JournalAllocation.Base"] = JournalAllocation.Base,
        ["JournalAllocation.CC"] = JournalAllocation.CC,
        ["JournalAllocation.Id"] = JournalAllocation.Id,
        ["JournalAllocation.Line"] = JournalAllocation.Line,
        ["JournalAllocation.FDeferred"] = JournalAllocation.FDeferred,
        ["JournalAllocation.FGuard"] = JournalAllocation.FGuard,
        ["JournalAllocation.KChk"] = JournalAllocation.KChk,
        ["JournalAllocation.KFkCC"] = JournalAllocation.KFkCC,
        ["JournalAllocation.KFkLine"] = JournalAllocation.KFkLine,
        ["JournalAllocation.KPk"] = JournalAllocation.KPk,
        ["JournalAllocation.KUnique"] = JournalAllocation.KUnique,
        ["JournalAllocation.RDeferred"] = JournalAllocation.RDeferred,
        ["JournalAllocation.RGuard"] = JournalAllocation.RGuard,
        ["JournalAllocation.RTrunc"] = JournalAllocation.RTrunc,
        ["JournalAllocation"] = JournalAllocation.Table,
        ["JournalEvent.Actor"] = JournalEvent.Actor,
        ["JournalEvent.At"] = JournalEvent.At,
        ["JournalEvent.Detail"] = JournalEvent.Detail,
        ["JournalEvent.Entry"] = JournalEvent.Entry,
        ["JournalEvent.Event"] = JournalEvent.Event,
        ["JournalEvent.Id"] = JournalEvent.Id,
        ["JournalEvent.Rev"] = JournalEvent.Rev,
        ["JournalEvent.Seq"] = JournalEvent.Seq,
        ["JournalEvent.Stream"] = JournalEvent.Stream,
        ["JournalEvent.FGuard"] = JournalEvent.FGuard,
        ["JournalEvent.KChk"] = JournalEvent.KChk,
        ["JournalEvent.KFkAudit"] = JournalEvent.KFkAudit,
        ["JournalEvent.KFkEntry"] = JournalEvent.KFkEntry,
        ["JournalEvent.KPk"] = JournalEvent.KPk,
        ["JournalEvent.KUAudit"] = JournalEvent.KUAudit,
        ["JournalEvent.RGuard"] = JournalEvent.RGuard,
        ["JournalEvent.RTrunc"] = JournalEvent.RTrunc,
        ["JournalEvent"] = JournalEvent.Table,
    });
    private static IReadOnlyDictionary<string, string> JournalMap => journalMap.Value;
}
