namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>
/// Logical-to-physical identifier map for the minimal schema of every company database.
/// Backend-internal only; never exposed through APIs or clients. Fixed and committed: changing a
/// value requires a new migration. Obfuscation for hygiene, not a security control (D-029).
/// </summary>
public static partial class CompanySchema
{
    /// <summary>Single-row table binding a company database to the company id of the central registry.</summary>
    public static class Identity
    {
        public const string Table = "t_209d0f15";
        public const string Singleton = "c_8709851b";
        public const string CompanyId = "c_2d7eb73b";
        public const string CreatedAtUtc = "c_a77d8875";
        public const string PrimaryKey = "k_bf65319b";
        public const string CheckSingleton = "k_8fd0da3b";
        public const string GuardFunction = "f_0b15f60c";
        public const string GuardTrigger = "r_ec68b932";
    }

    /// <summary>Company-business audit events (company migration 0002); one chain per company, stream 'business:&lt;company id&gt;'.</summary>
    public static class BusinessAudit
    {
        public const string Table = "t_2db8cf1f";
        public const string StreamKey = "c_839d1168";
        public const string Seq = "c_689dc6c4";
        public const string EventId = "c_ef116a9d";
        public const string CompanyId = "c_f02e2cd0";
        public const string OccurredAtUtc = "c_e01c5070";
        public const string EventType = "c_4c6c6496";
        public const string ActorUserId = "c_b11011ac";
        public const string TargetUserId = "c_deb363a5";
        public const string SubjectId = "c_629e971c";
        public const string Detail = "c_9881ea62";
        public const string FormatVersion = "c_b4c43b5e";
        public const string KeyId = "c_0e8a7d69";
        public const string PrevHash = "c_d8a88c1d";
        public const string Hash = "c_a9921270";
        public const string Mac = "c_b0fdf392";
        public const string PrimaryKey = "k_00c2489b";
        public const string UniqueEventId = "k_b10664e1";
        public const string CheckStream = "k_18ddb3ac";
        public const string CheckLengths = "k_a89e988d";
        public const string CheckDetail = "k_00e125c1";
        public const string CheckFields = "k_5837bd14";
        public const string GuardFunction = "f_d0f1d840";
        public const string GuardTrigger = "r_4427e195";
        public const string TruncateTrigger = "r_6b55b14e";
        public const string BindFunction = "f_104f29c0";
        public const string BindTrigger = "r_44f2cd47";
    }

    public static IReadOnlyDictionary<string, string> Map => map.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> map = new(() =>
        Phase1Map!.Concat(ConfigurationMap).Concat(BranchMap).Concat(WarehouseMap).Concat(FiscalMap).Concat(AccountMap).Concat(NumberingMap).Concat(RatesMap).Concat(CostCenterMap).Concat(ApprovalMap).Concat(JournalMap).Concat(PolicyMap).Concat(ImportMap).Concat(StatementMap).Concat(InventoryMap).Concat(StockDocumentMap).Concat(CostingMap).Concat(InventoryAccountingMap).Concat(StockPostingMap).Concat(StockReversalMap).Concat(StocktakeMap).Concat(StockAdjustmentMap).Concat(NegativeStockMap).Concat(PartyMap).Concat(TaxMap).Concat(PartyRoleMap).Concat(AttachmentMap).Concat(ImportTemplateMap).Concat(PricingMap).Concat(CustomerGroupMap).Concat(CommercialMap).Concat(CommercialApprovalMap).Concat(DocumentLinkMap).Concat(AttachmentRetentionMap).Concat(CommercialPolicyMap).Concat(ApprovalPolicyIdentityMap).Concat(StockPeriodGuardMap).Concat(CashBankMap).Concat(CompensationPlanMap).Concat(ReplayAuthorizationMap).Concat(ReplayGrantMap).Concat(ReplayEpochMap).ToDictionary(kv => kv.Key, kv => kv.Value));

    private static IReadOnlyDictionary<string, string> Phase1Map { get; } = new Dictionary<string, string>
    {
        ["BusinessAudit"] = BusinessAudit.Table,
        ["BusinessAudit.StreamKey"] = BusinessAudit.StreamKey,
        ["BusinessAudit.Seq"] = BusinessAudit.Seq,
        ["BusinessAudit.EventId"] = BusinessAudit.EventId,
        ["BusinessAudit.CompanyId"] = BusinessAudit.CompanyId,
        ["BusinessAudit.OccurredAtUtc"] = BusinessAudit.OccurredAtUtc,
        ["BusinessAudit.EventType"] = BusinessAudit.EventType,
        ["BusinessAudit.ActorUserId"] = BusinessAudit.ActorUserId,
        ["BusinessAudit.TargetUserId"] = BusinessAudit.TargetUserId,
        ["BusinessAudit.SubjectId"] = BusinessAudit.SubjectId,
        ["BusinessAudit.Detail"] = BusinessAudit.Detail,
        ["BusinessAudit.FormatVersion"] = BusinessAudit.FormatVersion,
        ["BusinessAudit.KeyId"] = BusinessAudit.KeyId,
        ["BusinessAudit.PrevHash"] = BusinessAudit.PrevHash,
        ["BusinessAudit.Hash"] = BusinessAudit.Hash,
        ["BusinessAudit.Mac"] = BusinessAudit.Mac,
        ["BusinessAudit.PrimaryKey"] = BusinessAudit.PrimaryKey,
        ["BusinessAudit.UniqueEventId"] = BusinessAudit.UniqueEventId,
        ["BusinessAudit.CheckStream"] = BusinessAudit.CheckStream,
        ["BusinessAudit.CheckLengths"] = BusinessAudit.CheckLengths,
        ["BusinessAudit.CheckDetail"] = BusinessAudit.CheckDetail,
        ["BusinessAudit.CheckFields"] = BusinessAudit.CheckFields,
        ["BusinessAudit.GuardFunction"] = BusinessAudit.GuardFunction,
        ["BusinessAudit.GuardTrigger"] = BusinessAudit.GuardTrigger,
        ["BusinessAudit.TruncateTrigger"] = BusinessAudit.TruncateTrigger,
        ["BusinessAudit.BindFunction"] = BusinessAudit.BindFunction,
        ["BusinessAudit.BindTrigger"] = BusinessAudit.BindTrigger,
        ["Identity"] = Identity.Table,
        ["Identity.Singleton"] = Identity.Singleton,
        ["Identity.CompanyId"] = Identity.CompanyId,
        ["Identity.CreatedAtUtc"] = Identity.CreatedAtUtc,
        ["Identity.PrimaryKey"] = Identity.PrimaryKey,
        ["Identity.CheckSingleton"] = Identity.CheckSingleton,
        ["Identity.GuardFunction"] = Identity.GuardFunction,
        ["Identity.GuardTrigger"] = Identity.GuardTrigger,
    };
}
