namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0006 (Phase 3.1D, fiscal calendar). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class FiscalYear
    {
        public const string Table = "t_4b5bb222";
        public const string Id = "c_cae1c56a";
        public const string Name = "c_845d536e";
        public const string StartDate = "c_5fd77e71";
        public const string EndDate = "c_a4d1e8a9";
        public const string PeriodType = "c_394036a1";
        public const string TimeZone = "c_7f40775b";
        public const string Status = "c_bd2464a9";
        public const string ClosedAtUtc = "c_7eb75e1d";
        public const string ClosedBy = "c_8add4e27";
        public const string Revision = "c_cd0b96d3";
        public const string CreatedAtUtc = "c_885c323c";
        public const string CreatedBy = "c_fcfa5d6f";
        public const string PrimaryKey = "k_d0f78690";
        public const string CheckName = "k_85ad4328";
        public const string CheckStart = "k_2d6cdf52";
        public const string CheckLength = "k_22e787ad";
        public const string CheckType = "k_3d1d9fba";
        public const string CheckStatus = "k_2ddfe435";
        public const string CheckClosed = "k_032ac2d3";
        public const string CheckRevision = "k_d97650d5";
        public const string ExcludeOverlap = "k_f3617578";
        public const string UniqueName = "k_d952d054";
        public const string GuardFunction = "f_436ebc08";
        public const string GuardRow = "r_9bbf6926";
        public const string GuardTruncate = "r_27d9fdf6";
    }

    public static class FiscalPeriod
    {
        public const string Table = "t_ae996edc";
        public const string Id = "c_d61cbb9c";
        public const string YearId = "c_957d240b";
        public const string Number = "c_c97ce291";
        public const string StartDate = "c_8299fb3d";
        public const string EndDate = "c_c0322bcf";
        public const string Status = "c_0a0b7f19";
        public const string StatusChangedAtUtc = "c_2f01f8f1";
        public const string StatusChangedBy = "c_9b0a4ba4";
        public const string FinalClosedBy = "c_35fb3486";
        public const string Revision = "c_1ac927f4";
        public const string CreatedAtUtc = "c_38f12ad5";
        public const string PrimaryKey = "k_338d8749";
        public const string ForeignKeyYear = "k_e1a8e61d";
        public const string UniqueNumber = "k_4b9544db";
        public const string CheckNumber = "k_e60bb6bd";
        public const string CheckDates = "k_708c49b9";
        public const string CheckStatus = "k_bf15df5a";
        public const string CheckFinal = "k_0dfcb7aa";
        public const string CheckRevision = "k_fc6b7a32";
        public const string ExcludeOverlap = "k_626845f8";
        public const string IndexYear = "k_35062bb2";
        public const string GuardFunction = "f_c2791eca";
        public const string GuardRow = "r_0a7cd8d4";
        public const string GuardTruncate = "r_02899443";
    }

    public static class FiscalCalendar
    {
        public const string ConsistencyFunction = "f_4ef79731";
        public const string YearTrigger = "r_9cb5c87a";
        public const string PeriodTrigger = "r_3cb5f09f";
        public const string PostableFunction = "f_a26691bc";
    }

    public static class FiscalHistory
    {
        public const string Table = "t_94cc83c6";
        public const string Id = "c_306cda79";
        public const string Entity = "c_3fd27d56";
        public const string EntityId = "c_7bf32b79";
        public const string Revision = "c_6eb10f43";
        public const string ActorUserId = "c_2d933826";
        public const string ChangedAtUtc = "c_4b5a9505";
        public const string Changes = "c_0877ff9e";
        public const string FromStatus = "c_4ad5cf82";
        public const string ToStatus = "c_7eed0210";
        public const string Reason = "c_05625748";
        public const string AuditStream = "c_b97f7f5a";
        public const string AuditSeq = "c_9bdd5615";
        public const string PrimaryKey = "k_68cf7849";
        public const string UniqueRevision = "k_3f90b693";
        public const string UniqueAuditEvent = "k_043ceda3";
        public const string ForeignKeyAudit = "k_af39351e";
        public const string CheckValues = "k_4a9b15cc";
        public const string CheckReason = "k_2fb5c89d";
        public const string GuardFunction = "f_253b0ace";
        public const string GuardRow = "r_2608e160";
        public const string GuardTruncate = "r_e67b01c2";
    }

    private static IReadOnlyDictionary<string, string> FiscalMap => fiscalMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> fiscalMap = new(() => new Dictionary<string, string>
    {
        ["FiscalYear"] = FiscalYear.Table,
        ["FiscalYear.Id"] = FiscalYear.Id,
        ["FiscalYear.Name"] = FiscalYear.Name,
        ["FiscalYear.StartDate"] = FiscalYear.StartDate,
        ["FiscalYear.EndDate"] = FiscalYear.EndDate,
        ["FiscalYear.PeriodType"] = FiscalYear.PeriodType,
        ["FiscalYear.TimeZone"] = FiscalYear.TimeZone,
        ["FiscalYear.Status"] = FiscalYear.Status,
        ["FiscalYear.ClosedAtUtc"] = FiscalYear.ClosedAtUtc,
        ["FiscalYear.ClosedBy"] = FiscalYear.ClosedBy,
        ["FiscalYear.Revision"] = FiscalYear.Revision,
        ["FiscalYear.CreatedAtUtc"] = FiscalYear.CreatedAtUtc,
        ["FiscalYear.CreatedBy"] = FiscalYear.CreatedBy,
        ["FiscalYear.PrimaryKey"] = FiscalYear.PrimaryKey,
        ["FiscalYear.CheckName"] = FiscalYear.CheckName,
        ["FiscalYear.CheckStart"] = FiscalYear.CheckStart,
        ["FiscalYear.CheckLength"] = FiscalYear.CheckLength,
        ["FiscalYear.CheckType"] = FiscalYear.CheckType,
        ["FiscalYear.CheckStatus"] = FiscalYear.CheckStatus,
        ["FiscalYear.CheckClosed"] = FiscalYear.CheckClosed,
        ["FiscalYear.CheckRevision"] = FiscalYear.CheckRevision,
        ["FiscalYear.ExcludeOverlap"] = FiscalYear.ExcludeOverlap,
        ["FiscalYear.UniqueName"] = FiscalYear.UniqueName,
        ["FiscalYear.GuardFunction"] = FiscalYear.GuardFunction,
        ["FiscalYear.GuardRow"] = FiscalYear.GuardRow,
        ["FiscalYear.GuardTruncate"] = FiscalYear.GuardTruncate,
        ["FiscalPeriod"] = FiscalPeriod.Table,
        ["FiscalPeriod.Id"] = FiscalPeriod.Id,
        ["FiscalPeriod.YearId"] = FiscalPeriod.YearId,
        ["FiscalPeriod.Number"] = FiscalPeriod.Number,
        ["FiscalPeriod.StartDate"] = FiscalPeriod.StartDate,
        ["FiscalPeriod.EndDate"] = FiscalPeriod.EndDate,
        ["FiscalPeriod.Status"] = FiscalPeriod.Status,
        ["FiscalPeriod.StatusChangedAtUtc"] = FiscalPeriod.StatusChangedAtUtc,
        ["FiscalPeriod.StatusChangedBy"] = FiscalPeriod.StatusChangedBy,
        ["FiscalPeriod.FinalClosedBy"] = FiscalPeriod.FinalClosedBy,
        ["FiscalPeriod.Revision"] = FiscalPeriod.Revision,
        ["FiscalPeriod.CreatedAtUtc"] = FiscalPeriod.CreatedAtUtc,
        ["FiscalPeriod.PrimaryKey"] = FiscalPeriod.PrimaryKey,
        ["FiscalPeriod.ForeignKeyYear"] = FiscalPeriod.ForeignKeyYear,
        ["FiscalPeriod.UniqueNumber"] = FiscalPeriod.UniqueNumber,
        ["FiscalPeriod.CheckNumber"] = FiscalPeriod.CheckNumber,
        ["FiscalPeriod.CheckDates"] = FiscalPeriod.CheckDates,
        ["FiscalPeriod.CheckStatus"] = FiscalPeriod.CheckStatus,
        ["FiscalPeriod.CheckFinal"] = FiscalPeriod.CheckFinal,
        ["FiscalPeriod.CheckRevision"] = FiscalPeriod.CheckRevision,
        ["FiscalPeriod.ExcludeOverlap"] = FiscalPeriod.ExcludeOverlap,
        ["FiscalPeriod.IndexYear"] = FiscalPeriod.IndexYear,
        ["FiscalPeriod.GuardFunction"] = FiscalPeriod.GuardFunction,
        ["FiscalPeriod.GuardRow"] = FiscalPeriod.GuardRow,
        ["FiscalPeriod.GuardTruncate"] = FiscalPeriod.GuardTruncate,
        ["FiscalCalendar.ConsistencyFunction"] = FiscalCalendar.ConsistencyFunction,
        ["FiscalCalendar.YearTrigger"] = FiscalCalendar.YearTrigger,
        ["FiscalCalendar.PeriodTrigger"] = FiscalCalendar.PeriodTrigger,
        ["FiscalCalendar.PostableFunction"] = FiscalCalendar.PostableFunction,
        ["FiscalHistory"] = FiscalHistory.Table,
        ["FiscalHistory.Id"] = FiscalHistory.Id,
        ["FiscalHistory.Entity"] = FiscalHistory.Entity,
        ["FiscalHistory.EntityId"] = FiscalHistory.EntityId,
        ["FiscalHistory.Revision"] = FiscalHistory.Revision,
        ["FiscalHistory.ActorUserId"] = FiscalHistory.ActorUserId,
        ["FiscalHistory.ChangedAtUtc"] = FiscalHistory.ChangedAtUtc,
        ["FiscalHistory.Changes"] = FiscalHistory.Changes,
        ["FiscalHistory.FromStatus"] = FiscalHistory.FromStatus,
        ["FiscalHistory.ToStatus"] = FiscalHistory.ToStatus,
        ["FiscalHistory.Reason"] = FiscalHistory.Reason,
        ["FiscalHistory.AuditStream"] = FiscalHistory.AuditStream,
        ["FiscalHistory.AuditSeq"] = FiscalHistory.AuditSeq,
        ["FiscalHistory.PrimaryKey"] = FiscalHistory.PrimaryKey,
        ["FiscalHistory.UniqueRevision"] = FiscalHistory.UniqueRevision,
        ["FiscalHistory.UniqueAuditEvent"] = FiscalHistory.UniqueAuditEvent,
        ["FiscalHistory.ForeignKeyAudit"] = FiscalHistory.ForeignKeyAudit,
        ["FiscalHistory.CheckValues"] = FiscalHistory.CheckValues,
        ["FiscalHistory.CheckReason"] = FiscalHistory.CheckReason,
        ["FiscalHistory.GuardFunction"] = FiscalHistory.GuardFunction,
        ["FiscalHistory.GuardRow"] = FiscalHistory.GuardRow,
        ["FiscalHistory.GuardTruncate"] = FiscalHistory.GuardTruncate,
    });
}
