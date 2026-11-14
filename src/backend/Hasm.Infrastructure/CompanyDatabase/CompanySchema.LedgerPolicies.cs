namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0009 (Phase 3.2C, General Ledger completion). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class PolicyHistory
    {
        public const string Table = "t_a8b39266";
        public const string Id = "c_93186753";
        public const string Entity = "c_55e6d16b";
        public const string EntityKey = "c_60cde077";
        public const string Revision = "c_6416fcc4";
        public const string ActorUserId = "c_09a62494";
        public const string ChangedAtUtc = "c_db94aeca";
        public const string Changes = "c_36e7e7df";
        public const string AuditStream = "c_a4154dd1";
        public const string AuditSeq = "c_02354aed";
        public const string PrimaryKey = "k_e634ceb2";
        public const string UniqueRevision = "k_16fd3b9e";
        public const string UniqueAuditEvent = "k_0972beda";
        public const string ForeignKeyAudit = "k_c133f676";
        public const string CheckValues = "k_5d1e394e";
        public const string GuardFunction = "f_5eab45d2";
        public const string GuardRow = "r_e88a45e6";
        public const string GuardTruncate = "r_e7b2e217";
    }

    public static class AccountCostCenterRule
    {
        public const string Table = "t_6e4ce78a";
        public const string AccountId = "c_7d1b8b04";
        public const string IsRequired = "c_5528e5aa";
        public const string Revision = "c_7ee10b36";
        public const string CreatedAtUtc = "c_ccecab3c";
        public const string UpdatedAtUtc = "c_b0c95884";
        public const string PrimaryKey = "k_834015ed";
        public const string ForeignKeyAccount = "k_86da7d6a";
        public const string CheckRevision = "k_9ca3e5b6";
        public const string GuardFunction = "f_15c15908";
        public const string GuardRow = "r_c0f14fa3";
        public const string GuardTruncate = "r_3ffc9ccc";
        public const string LeafFunction = "f_6442050a";
        public const string LeafTrigger = "r_752a9fd8";
    }

    public static class CostCenterBranchRule
    {
        public const string Table = "t_7a16c2a5";
        public const string CostCenterId = "c_5b013ba0";
        public const string IsRestricted = "c_18cc6412";
        public const string Revision = "c_5d01f060";
        public const string CreatedAtUtc = "c_f574e60d";
        public const string UpdatedAtUtc = "c_7897de19";
        public const string PrimaryKey = "k_d85321d2";
        public const string ForeignKeyCostCenter = "k_1a073f8e";
        public const string CheckRevision = "k_737033a8";
        public const string GuardFunction = "f_f9af5ad2";
        public const string GuardRow = "r_209cf8a5";
        public const string GuardTruncate = "r_07d64a25";
    }

    public static class CostCenterBranchEligibility
    {
        public const string Table = "t_fc54b254";
        public const string CostCenterId = "c_66b1947c";
        public const string BranchId = "c_47cd1114";
        public const string IsEligible = "c_e40d44ac";
        public const string UpdatedAtUtc = "c_bb15f8ae";
        public const string PrimaryKey = "k_9740f03f";
        public const string ForeignKeyCostCenter = "k_d48cd228";
        public const string ForeignKeyBranch = "k_0f4c85d5";
        public const string GuardFunction = "f_711189a4";
        public const string GuardRow = "r_6a4c8ed5";
        public const string GuardTruncate = "r_074e2207";
    }

    public static class CostCenterPolicy
    {
        public const string ActivationEpoch = "c_10eea50a";
        public const string CheckEpoch = "k_42ba0bc5";
        public const string EpochFunction = "f_ff034cd6";
        public const string EpochTrigger = "r_6023e975";
    }

    public static class JournalPolicy
    {
        public const string PostingFunction = "f_03f44dbc";
        public const string PostingTrigger = "r_2e73af6e";
    }

    public static class ApprovalPolicyConditions
    {
        public const string JournalType = "c_1a2f7f5e";
        public const string RateOverride = "c_6088d474";
        public const string SoftClosed = "c_ca9500fa";
        public const string CheckJournalType = "k_2f349944";
    }

    public static class ApprovalPolicyStage
    {
        public const string Table = "t_e8cadae9";
        public const string PolicyId = "c_85629458";
        public const string PolicyRevision = "c_41aad198";
        public const string StageNo = "c_6e0d1427";
        public const string Required = "c_002a7676";
        public const string RoleId = "c_22b0a7aa";
        public const string PrimaryKey = "k_eb36fd64";
        public const string ForeignKeyPolicy = "k_85b10301";
        public const string CheckValues = "k_a805f03c";
        public const string GuardFunction = "f_bdb6446b";
        public const string GuardRow = "r_73cc750f";
        public const string GuardTruncate = "r_5071d47e";
        public const string ContiguousFunction = "f_34b9c229";
        public const string ContiguousTrigger = "r_ea4636a5";
    }

    public static class DocumentApprovalBinding
    {
        public const string Table = "t_9e0faeaf";
        public const string Id = "c_2ea61267";
        public const string DocType = "c_17ea6905";
        public const string DocumentId = "c_5e3e1a37";
        public const string DocumentRevision = "c_63b7acab";
        public const string StageNo = "c_d293a70b";
        public const string Required = "c_95045342";
        public const string RoleId = "c_2b451767";
        public const string PolicyId = "c_4b12f026";
        public const string PolicyRevision = "c_ec5ab3ff";
        public const string BoundAtUtc = "c_35b46080";
        public const string PrimaryKey = "k_6b06f5c7";
        public const string UniqueStage = "k_424b5a35";
        public const string CheckValues = "k_e9ef623d";
        public const string GuardFunction = "f_07fd9f8a";
        public const string GuardRow = "r_cd6baace";
        public const string GuardTruncate = "r_86e6ce91";
    }

    public static class ApprovalStaging
    {
        public const string DecisionStage = "c_da356dc3";
        public const string CheckDecisionStage = "k_b274f0c2";
        public const string PickFunction = "f_e565501a";
        public const string PlanFunction = "f_177b9f61";
        public const string SatisfiedFunction = "f_a4b5fe50";
        public const string OrderFunction = "f_7cb412a3";
        public const string OrderTrigger = "r_01accc03";
        public const string GateFunction = "f_7df31fbf";
        public const string GateTrigger = "r_b57e2508";
    }

    public static class ClosingSettings
    {
        public const string Table = "t_b80fbcc5";
        public const string Singleton = "c_a9d7d7ac";
        public const string EquityAccountId = "c_b4f2a328";
        public const string RequireApproval = "c_ccdfd1a6";
        public const string Revision = "c_e421441c";
        public const string UpdatedAtUtc = "c_21160187";
        public const string PrimaryKey = "k_09835e78";
        public const string ForeignKeyAccount = "k_8620669f";
        public const string CheckValues = "k_f5ab1009";
        public const string CheckApprovalMandatory = "k_a845fff4";
        public const string GuardFunction = "f_fde6d5fe";
        public const string GuardRow = "r_edf794c1";
        public const string GuardTruncate = "r_1e8a2ca7";
        public const string AccountFunction = "f_3bdf358f";
        public const string AccountTrigger = "r_7d268211";
    }

    public static class FiscalYearClosing
    {
        public const string Table = "t_e040bbbc";
        public const string Id = "c_36778b5b";
        public const string YearId = "c_9b5d60ca";
        public const string Version = "c_105a683c";
        public const string NetResult = "c_d87a8437";
        public const string EquityAccountId = "c_0867cfb5";
        public const string ClosingEntryId = "c_bb26888c";
        public const string Evidence = "c_14a6d58d";
        public const string CreatedBy = "c_9581993a";
        public const string CreatedAtUtc = "c_d17789b9";
        public const string PrimaryKey = "k_2b414f61";
        public const string ForeignKeyYear = "k_9589961f";
        public const string ForeignKeyEntry = "k_de014d4e";
        public const string ForeignKeyAccount = "k_52a70dc3";
        public const string UniqueVersion = "k_a4d388b7";
        public const string UniqueEntry = "k_526dbda7";
        public const string CheckValues = "k_b0db6b95";
        public const string GuardFunction = "f_5bed5c2c";
        public const string GuardRow = "r_5d14ddcb";
        public const string GuardTruncate = "r_64e3d0e1";
        public const string InsertFunction = "f_d80dfe38";
        public const string InsertTrigger = "r_695531d4";
    }

    public static class FiscalYearCarryforward
    {
        public const string Table = "t_c1779e24";
        public const string Id = "c_2526ddd0";
        public const string FromYearId = "c_5f38151d";
        public const string ForeignKeyFrom = "k_8568f3ff";
        public const string ClosingId = "c_af0a82fd";
        public const string ToYearId = "c_2435b2f2";
        public const string EntryId = "c_8fcce06f";
        public const string Evidence = "c_1386051a";
        public const string CreatedBy = "c_c3941478";
        public const string CreatedAtUtc = "c_2b3c0f3a";
        public const string PrimaryKey = "k_fac07acb";
        public const string ForeignKeyClosing = "k_82789067";
        public const string ForeignKeyYear = "k_5485e4c8";
        public const string ForeignKeyEntry = "k_9b528a38";
        public const string UniqueClosing = "k_2a95437b";
        public const string UniqueEntry = "k_e16fc837";
        public const string CheckValues = "k_0c26b442";
        public const string GuardFunction = "f_a3b6d4d4";
        public const string GuardRow = "r_d0a9d030";
        public const string GuardTruncate = "r_812681c5";
        public const string InsertFunction = "f_79e11403";
        public const string InsertTrigger = "r_6e7b68e4";
    }

    public static class ClosingLogic
    {
        public const string PolicyClosingKind = "c_43137a95";
        public const string CheckPolicyClosingKind = "k_7286962e";
        public const string KindFunction = "f_09413dbf";
        public const string PnlFunction = "f_455769bc";
        public const string MatchClosingFunction = "f_b6fc8d8c";
        public const string MatchOpeningFunction = "f_457ef32f";
        public const string StaleFunction = "f_26ca2fc5";
        public const string DimensionBalanceFunction = "f_1246db8b";
        public const string EntryDimensionFunction = "f_31b2e5fb";
        public const string PostFunction = "f_64d2a45a";
        public const string PostTrigger = "r_a0ce1bf4";
    }

    private static IReadOnlyDictionary<string, string> PolicyMap => policyMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> policyMap = new(() => new Dictionary<string, string>
    {
        ["PolicyHistory"] = PolicyHistory.Table,
        ["PolicyHistory.Id"] = PolicyHistory.Id,
        ["PolicyHistory.Entity"] = PolicyHistory.Entity,
        ["PolicyHistory.EntityKey"] = PolicyHistory.EntityKey,
        ["PolicyHistory.Revision"] = PolicyHistory.Revision,
        ["PolicyHistory.ActorUserId"] = PolicyHistory.ActorUserId,
        ["PolicyHistory.ChangedAtUtc"] = PolicyHistory.ChangedAtUtc,
        ["PolicyHistory.Changes"] = PolicyHistory.Changes,
        ["PolicyHistory.AuditStream"] = PolicyHistory.AuditStream,
        ["PolicyHistory.AuditSeq"] = PolicyHistory.AuditSeq,
        ["PolicyHistory.PrimaryKey"] = PolicyHistory.PrimaryKey,
        ["PolicyHistory.UniqueRevision"] = PolicyHistory.UniqueRevision,
        ["PolicyHistory.UniqueAuditEvent"] = PolicyHistory.UniqueAuditEvent,
        ["PolicyHistory.ForeignKeyAudit"] = PolicyHistory.ForeignKeyAudit,
        ["PolicyHistory.CheckValues"] = PolicyHistory.CheckValues,
        ["PolicyHistory.GuardFunction"] = PolicyHistory.GuardFunction,
        ["PolicyHistory.GuardRow"] = PolicyHistory.GuardRow,
        ["PolicyHistory.GuardTruncate"] = PolicyHistory.GuardTruncate,
        ["AccountCostCenterRule"] = AccountCostCenterRule.Table,
        ["AccountCostCenterRule.AccountId"] = AccountCostCenterRule.AccountId,
        ["AccountCostCenterRule.IsRequired"] = AccountCostCenterRule.IsRequired,
        ["AccountCostCenterRule.Revision"] = AccountCostCenterRule.Revision,
        ["AccountCostCenterRule.CreatedAtUtc"] = AccountCostCenterRule.CreatedAtUtc,
        ["AccountCostCenterRule.UpdatedAtUtc"] = AccountCostCenterRule.UpdatedAtUtc,
        ["AccountCostCenterRule.PrimaryKey"] = AccountCostCenterRule.PrimaryKey,
        ["AccountCostCenterRule.ForeignKeyAccount"] = AccountCostCenterRule.ForeignKeyAccount,
        ["AccountCostCenterRule.CheckRevision"] = AccountCostCenterRule.CheckRevision,
        ["AccountCostCenterRule.GuardFunction"] = AccountCostCenterRule.GuardFunction,
        ["AccountCostCenterRule.GuardRow"] = AccountCostCenterRule.GuardRow,
        ["AccountCostCenterRule.GuardTruncate"] = AccountCostCenterRule.GuardTruncate,
        ["AccountCostCenterRule.LeafFunction"] = AccountCostCenterRule.LeafFunction,
        ["AccountCostCenterRule.LeafTrigger"] = AccountCostCenterRule.LeafTrigger,
        ["CostCenterBranchRule"] = CostCenterBranchRule.Table,
        ["CostCenterBranchRule.CostCenterId"] = CostCenterBranchRule.CostCenterId,
        ["CostCenterBranchRule.IsRestricted"] = CostCenterBranchRule.IsRestricted,
        ["CostCenterBranchRule.Revision"] = CostCenterBranchRule.Revision,
        ["CostCenterBranchRule.CreatedAtUtc"] = CostCenterBranchRule.CreatedAtUtc,
        ["CostCenterBranchRule.UpdatedAtUtc"] = CostCenterBranchRule.UpdatedAtUtc,
        ["CostCenterBranchRule.PrimaryKey"] = CostCenterBranchRule.PrimaryKey,
        ["CostCenterBranchRule.ForeignKeyCostCenter"] = CostCenterBranchRule.ForeignKeyCostCenter,
        ["CostCenterBranchRule.CheckRevision"] = CostCenterBranchRule.CheckRevision,
        ["CostCenterBranchRule.GuardFunction"] = CostCenterBranchRule.GuardFunction,
        ["CostCenterBranchRule.GuardRow"] = CostCenterBranchRule.GuardRow,
        ["CostCenterBranchRule.GuardTruncate"] = CostCenterBranchRule.GuardTruncate,
        ["CostCenterBranchEligibility"] = CostCenterBranchEligibility.Table,
        ["CostCenterBranchEligibility.CostCenterId"] = CostCenterBranchEligibility.CostCenterId,
        ["CostCenterBranchEligibility.BranchId"] = CostCenterBranchEligibility.BranchId,
        ["CostCenterBranchEligibility.IsEligible"] = CostCenterBranchEligibility.IsEligible,
        ["CostCenterBranchEligibility.UpdatedAtUtc"] = CostCenterBranchEligibility.UpdatedAtUtc,
        ["CostCenterBranchEligibility.PrimaryKey"] = CostCenterBranchEligibility.PrimaryKey,
        ["CostCenterBranchEligibility.ForeignKeyCostCenter"] = CostCenterBranchEligibility.ForeignKeyCostCenter,
        ["CostCenterBranchEligibility.ForeignKeyBranch"] = CostCenterBranchEligibility.ForeignKeyBranch,
        ["CostCenterBranchEligibility.GuardFunction"] = CostCenterBranchEligibility.GuardFunction,
        ["CostCenterBranchEligibility.GuardRow"] = CostCenterBranchEligibility.GuardRow,
        ["CostCenterBranchEligibility.GuardTruncate"] = CostCenterBranchEligibility.GuardTruncate,
        ["CostCenterPolicy.ActivationEpoch"] = CostCenterPolicy.ActivationEpoch,
        ["CostCenterPolicy.CheckEpoch"] = CostCenterPolicy.CheckEpoch,
        ["CostCenterPolicy.EpochFunction"] = CostCenterPolicy.EpochFunction,
        ["CostCenterPolicy.EpochTrigger"] = CostCenterPolicy.EpochTrigger,
        ["JournalPolicy.PostingFunction"] = JournalPolicy.PostingFunction,
        ["JournalPolicy.PostingTrigger"] = JournalPolicy.PostingTrigger,
        ["ApprovalPolicyConditions.JournalType"] = ApprovalPolicyConditions.JournalType,
        ["ApprovalPolicyConditions.RateOverride"] = ApprovalPolicyConditions.RateOverride,
        ["ApprovalPolicyConditions.SoftClosed"] = ApprovalPolicyConditions.SoftClosed,
        ["ApprovalPolicyConditions.CheckJournalType"] = ApprovalPolicyConditions.CheckJournalType,
        ["ApprovalPolicyStage"] = ApprovalPolicyStage.Table,
        ["ApprovalPolicyStage.PolicyId"] = ApprovalPolicyStage.PolicyId,
        ["ApprovalPolicyStage.PolicyRevision"] = ApprovalPolicyStage.PolicyRevision,
        ["ApprovalPolicyStage.StageNo"] = ApprovalPolicyStage.StageNo,
        ["ApprovalPolicyStage.Required"] = ApprovalPolicyStage.Required,
        ["ApprovalPolicyStage.RoleId"] = ApprovalPolicyStage.RoleId,
        ["ApprovalPolicyStage.PrimaryKey"] = ApprovalPolicyStage.PrimaryKey,
        ["ApprovalPolicyStage.ForeignKeyPolicy"] = ApprovalPolicyStage.ForeignKeyPolicy,
        ["ApprovalPolicyStage.CheckValues"] = ApprovalPolicyStage.CheckValues,
        ["ApprovalPolicyStage.GuardFunction"] = ApprovalPolicyStage.GuardFunction,
        ["ApprovalPolicyStage.GuardRow"] = ApprovalPolicyStage.GuardRow,
        ["ApprovalPolicyStage.GuardTruncate"] = ApprovalPolicyStage.GuardTruncate,
        ["ApprovalPolicyStage.ContiguousFunction"] = ApprovalPolicyStage.ContiguousFunction,
        ["ApprovalPolicyStage.ContiguousTrigger"] = ApprovalPolicyStage.ContiguousTrigger,
        ["DocumentApprovalBinding"] = DocumentApprovalBinding.Table,
        ["DocumentApprovalBinding.Id"] = DocumentApprovalBinding.Id,
        ["DocumentApprovalBinding.DocType"] = DocumentApprovalBinding.DocType,
        ["DocumentApprovalBinding.DocumentId"] = DocumentApprovalBinding.DocumentId,
        ["DocumentApprovalBinding.DocumentRevision"] = DocumentApprovalBinding.DocumentRevision,
        ["DocumentApprovalBinding.StageNo"] = DocumentApprovalBinding.StageNo,
        ["DocumentApprovalBinding.Required"] = DocumentApprovalBinding.Required,
        ["DocumentApprovalBinding.RoleId"] = DocumentApprovalBinding.RoleId,
        ["DocumentApprovalBinding.PolicyId"] = DocumentApprovalBinding.PolicyId,
        ["DocumentApprovalBinding.PolicyRevision"] = DocumentApprovalBinding.PolicyRevision,
        ["DocumentApprovalBinding.BoundAtUtc"] = DocumentApprovalBinding.BoundAtUtc,
        ["DocumentApprovalBinding.PrimaryKey"] = DocumentApprovalBinding.PrimaryKey,
        ["DocumentApprovalBinding.UniqueStage"] = DocumentApprovalBinding.UniqueStage,
        ["DocumentApprovalBinding.CheckValues"] = DocumentApprovalBinding.CheckValues,
        ["DocumentApprovalBinding.GuardFunction"] = DocumentApprovalBinding.GuardFunction,
        ["DocumentApprovalBinding.GuardRow"] = DocumentApprovalBinding.GuardRow,
        ["DocumentApprovalBinding.GuardTruncate"] = DocumentApprovalBinding.GuardTruncate,
        ["ApprovalStaging.DecisionStage"] = ApprovalStaging.DecisionStage,
        ["ApprovalStaging.CheckDecisionStage"] = ApprovalStaging.CheckDecisionStage,
        ["ApprovalStaging.PickFunction"] = ApprovalStaging.PickFunction,
        ["ApprovalStaging.PlanFunction"] = ApprovalStaging.PlanFunction,
        ["ApprovalStaging.SatisfiedFunction"] = ApprovalStaging.SatisfiedFunction,
        ["ApprovalStaging.OrderFunction"] = ApprovalStaging.OrderFunction,
        ["ApprovalStaging.OrderTrigger"] = ApprovalStaging.OrderTrigger,
        ["ApprovalStaging.GateFunction"] = ApprovalStaging.GateFunction,
        ["ApprovalStaging.GateTrigger"] = ApprovalStaging.GateTrigger,
        ["ClosingSettings"] = ClosingSettings.Table,
        ["ClosingSettings.Singleton"] = ClosingSettings.Singleton,
        ["ClosingSettings.EquityAccountId"] = ClosingSettings.EquityAccountId,
        ["ClosingSettings.RequireApproval"] = ClosingSettings.RequireApproval,
        ["ClosingSettings.Revision"] = ClosingSettings.Revision,
        ["ClosingSettings.UpdatedAtUtc"] = ClosingSettings.UpdatedAtUtc,
        ["ClosingSettings.PrimaryKey"] = ClosingSettings.PrimaryKey,
        ["ClosingSettings.ForeignKeyAccount"] = ClosingSettings.ForeignKeyAccount,
        ["ClosingSettings.CheckValues"] = ClosingSettings.CheckValues,
        ["ClosingSettings.CheckApprovalMandatory"] = ClosingSettings.CheckApprovalMandatory,
        ["ClosingSettings.GuardFunction"] = ClosingSettings.GuardFunction,
        ["ClosingSettings.GuardRow"] = ClosingSettings.GuardRow,
        ["ClosingSettings.GuardTruncate"] = ClosingSettings.GuardTruncate,
        ["ClosingSettings.AccountFunction"] = ClosingSettings.AccountFunction,
        ["ClosingSettings.AccountTrigger"] = ClosingSettings.AccountTrigger,
        ["FiscalYearClosing"] = FiscalYearClosing.Table,
        ["FiscalYearClosing.Id"] = FiscalYearClosing.Id,
        ["FiscalYearClosing.YearId"] = FiscalYearClosing.YearId,
        ["FiscalYearClosing.Version"] = FiscalYearClosing.Version,
        ["FiscalYearClosing.NetResult"] = FiscalYearClosing.NetResult,
        ["FiscalYearClosing.EquityAccountId"] = FiscalYearClosing.EquityAccountId,
        ["FiscalYearClosing.ClosingEntryId"] = FiscalYearClosing.ClosingEntryId,
        ["FiscalYearClosing.Evidence"] = FiscalYearClosing.Evidence,
        ["FiscalYearClosing.CreatedBy"] = FiscalYearClosing.CreatedBy,
        ["FiscalYearClosing.CreatedAtUtc"] = FiscalYearClosing.CreatedAtUtc,
        ["FiscalYearClosing.PrimaryKey"] = FiscalYearClosing.PrimaryKey,
        ["FiscalYearClosing.ForeignKeyYear"] = FiscalYearClosing.ForeignKeyYear,
        ["FiscalYearClosing.ForeignKeyEntry"] = FiscalYearClosing.ForeignKeyEntry,
        ["FiscalYearClosing.ForeignKeyAccount"] = FiscalYearClosing.ForeignKeyAccount,
        ["FiscalYearClosing.UniqueVersion"] = FiscalYearClosing.UniqueVersion,
        ["FiscalYearClosing.UniqueEntry"] = FiscalYearClosing.UniqueEntry,
        ["FiscalYearClosing.CheckValues"] = FiscalYearClosing.CheckValues,
        ["FiscalYearClosing.GuardFunction"] = FiscalYearClosing.GuardFunction,
        ["FiscalYearClosing.GuardRow"] = FiscalYearClosing.GuardRow,
        ["FiscalYearClosing.GuardTruncate"] = FiscalYearClosing.GuardTruncate,
        ["FiscalYearClosing.InsertFunction"] = FiscalYearClosing.InsertFunction,
        ["FiscalYearClosing.InsertTrigger"] = FiscalYearClosing.InsertTrigger,
        ["FiscalYearCarryforward"] = FiscalYearCarryforward.Table,
        ["FiscalYearCarryforward.Id"] = FiscalYearCarryforward.Id,
        ["FiscalYearCarryforward.FromYearId"] = FiscalYearCarryforward.FromYearId,
        ["FiscalYearCarryforward.ForeignKeyFrom"] = FiscalYearCarryforward.ForeignKeyFrom,
        ["FiscalYearCarryforward.ClosingId"] = FiscalYearCarryforward.ClosingId,
        ["FiscalYearCarryforward.ToYearId"] = FiscalYearCarryforward.ToYearId,
        ["FiscalYearCarryforward.EntryId"] = FiscalYearCarryforward.EntryId,
        ["FiscalYearCarryforward.Evidence"] = FiscalYearCarryforward.Evidence,
        ["FiscalYearCarryforward.CreatedBy"] = FiscalYearCarryforward.CreatedBy,
        ["FiscalYearCarryforward.CreatedAtUtc"] = FiscalYearCarryforward.CreatedAtUtc,
        ["FiscalYearCarryforward.PrimaryKey"] = FiscalYearCarryforward.PrimaryKey,
        ["FiscalYearCarryforward.ForeignKeyClosing"] = FiscalYearCarryforward.ForeignKeyClosing,
        ["FiscalYearCarryforward.ForeignKeyYear"] = FiscalYearCarryforward.ForeignKeyYear,
        ["FiscalYearCarryforward.ForeignKeyEntry"] = FiscalYearCarryforward.ForeignKeyEntry,
        ["FiscalYearCarryforward.UniqueClosing"] = FiscalYearCarryforward.UniqueClosing,
        ["FiscalYearCarryforward.UniqueEntry"] = FiscalYearCarryforward.UniqueEntry,
        ["FiscalYearCarryforward.CheckValues"] = FiscalYearCarryforward.CheckValues,
        ["FiscalYearCarryforward.GuardFunction"] = FiscalYearCarryforward.GuardFunction,
        ["FiscalYearCarryforward.GuardRow"] = FiscalYearCarryforward.GuardRow,
        ["FiscalYearCarryforward.GuardTruncate"] = FiscalYearCarryforward.GuardTruncate,
        ["FiscalYearCarryforward.InsertFunction"] = FiscalYearCarryforward.InsertFunction,
        ["FiscalYearCarryforward.InsertTrigger"] = FiscalYearCarryforward.InsertTrigger,
        ["ClosingLogic.PolicyClosingKind"] = ClosingLogic.PolicyClosingKind,
        ["ClosingLogic.CheckPolicyClosingKind"] = ClosingLogic.CheckPolicyClosingKind,
        ["ClosingLogic.KindFunction"] = ClosingLogic.KindFunction,
        ["ClosingLogic.PnlFunction"] = ClosingLogic.PnlFunction,
        ["ClosingLogic.MatchClosingFunction"] = ClosingLogic.MatchClosingFunction,
        ["ClosingLogic.MatchOpeningFunction"] = ClosingLogic.MatchOpeningFunction,
        ["ClosingLogic.StaleFunction"] = ClosingLogic.StaleFunction,
        ["ClosingLogic.DimensionBalanceFunction"] = ClosingLogic.DimensionBalanceFunction,
        ["ClosingLogic.EntryDimensionFunction"] = ClosingLogic.EntryDimensionFunction,
        ["ClosingLogic.PostFunction"] = ClosingLogic.PostFunction,
        ["ClosingLogic.PostTrigger"] = ClosingLogic.PostTrigger,
    });
}
