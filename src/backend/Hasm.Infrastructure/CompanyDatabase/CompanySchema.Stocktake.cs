namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0018 (Phase 3.8B-1, stocktake and variance, non-posting). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class Stocktake
    {
        public const string Table = "t_994f8458";
        public const string Id = "c_2ac20938";
        public const string Status = "c_941ef8c3";
        public const string BranchId = "c_b156e8dd";
        public const string WarehouseId = "c_a28d1691";
        public const string CountDate = "c_57c0972a";
        public const string Reference = "c_04707d57";
        public const string Notes = "c_8fcd8e81";
        public const string Number = "c_96d45bf1";
        public const string FiscalYearId = "c_e01c5942";
        public const string SnapshotAtUtc = "c_0554fa6b";
        public const string CreatedBy = "c_943afda5";
        public const string CreatedAtUtc = "c_2df1272f";
        public const string SubmittedBy = "c_9173aa18";
        public const string SubmittedAtUtc = "c_1626ea46";
        public const string DecidedBy = "c_e076b18d";
        public const string DecidedAtUtc = "c_82d1fb4d";
        public const string DecisionNote = "c_585cd4e6";
        public const string CancelledBy = "c_3f2a88ab";
        public const string CancelledAtUtc = "c_fea3e2d5";
        public const string CancelReason = "c_f2d1acab";
        public const string Revision = "c_94725861";
        public const string UpdatedAtUtc = "c_2990788a";
        public const string PrimaryKey = "k_1f36f5ef";
        public const string ForeignKeyBranch = "k_49e1294e";
        public const string ForeignKeyWarehouse = "k_7be8ca9b";
        public const string ForeignKeyYear = "k_cccad747";
        public const string CheckValues = "k_7504d581";
        public const string CheckNumber = "k_fa085040";
        public const string CheckSeparation = "k_cc957678";
        public const string CheckRevision = "k_524d3ac7";
        public const string UniqueNumber = "k_3325d7d5";
        public const string WarehouseIndex = "k_6201ed40";
        public const string StatusIndex = "k_acaa9997";
        public const string GuardFunction = "f_60401953";
        public const string CompleteFunction = "f_f2c2efbb";
        public const string GuardRow = "r_153d656d";
        public const string GuardTruncate = "r_05f93af4";
        public const string CompleteTrigger = "r_36837069";
    }

    public static class StocktakeLine
    {
        public const string Table = "t_d1e0ca90";
        public const string StocktakeId = "c_f16a23dd";
        public const string LineNo = "c_1cb4b07f";
        public const string ItemId = "c_0ee93266";
        public const string UnitId = "c_db9cde60";
        public const string CountedQuantity = "c_81e5a821";
        public const string Factor = "c_8c833228";
        public const string CountedBaseQuantity = "c_48580491";
        public const string BookQuantity = "c_dd0b869e";
        public const string Variance = "c_368e12dc";
        public const string Reason = "c_25495cbf";
        public const string PrimaryKey = "k_dd566b79";
        public const string ForeignKeyStocktake = "k_65bb9df3";
        public const string ForeignKeyItem = "k_6155e7ea";
        public const string ForeignKeyUnit = "k_a0ad6175";
        public const string UniqueItem = "k_ce971b1c";
        public const string CheckValues = "k_5ac711e2";
        public const string ItemIndex = "k_c5290328";
        public const string GuardFunction = "f_cb0cde13";
        public const string GuardRow = "r_b11caaa0";
        public const string GuardTruncate = "r_a6de3584";
    }

    public static class StocktakeEvent
    {
        public const string Table = "t_8176e273";
        public const string Id = "c_8c10458e";
        public const string StocktakeId = "c_4cd1da85";
        public const string Kind = "c_a4d2bd13";
        public const string FromStatus = "c_39cecf36";
        public const string ToStatus = "c_47e8cb8b";
        public const string ActorUserId = "c_c8706afa";
        public const string AtUtc = "c_c879da72";
        public const string Note = "c_6f8397d2";
        public const string AuditStream = "c_fdb4b98c";
        public const string AuditSeq = "c_bdaaec06";
        public const string PrimaryKey = "k_dabcb4b1";
        public const string ForeignKeyStocktake = "k_28297939";
        public const string ForeignKeyAudit = "k_8b4a36f2";
        public const string UniqueAuditEvent = "k_b214e2d8";
        public const string CheckValues = "k_101d5d5c";
        public const string StocktakeIndex = "k_12b5d1ab";
        public const string GuardFunction = "f_4b8fb32f";
        public const string GuardRow = "r_0188ca28";
        public const string GuardTruncate = "r_1b8e85b1";
    }

    private static IReadOnlyDictionary<string, string> StocktakeMap => stocktakeMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> stocktakeMap = new(() => new Dictionary<string, string>
    {
        ["Stocktake"] = Stocktake.Table,
        ["Stocktake.Id"] = Stocktake.Id,
        ["Stocktake.Status"] = Stocktake.Status,
        ["Stocktake.BranchId"] = Stocktake.BranchId,
        ["Stocktake.WarehouseId"] = Stocktake.WarehouseId,
        ["Stocktake.CountDate"] = Stocktake.CountDate,
        ["Stocktake.Reference"] = Stocktake.Reference,
        ["Stocktake.Notes"] = Stocktake.Notes,
        ["Stocktake.Number"] = Stocktake.Number,
        ["Stocktake.FiscalYearId"] = Stocktake.FiscalYearId,
        ["Stocktake.SnapshotAtUtc"] = Stocktake.SnapshotAtUtc,
        ["Stocktake.CreatedBy"] = Stocktake.CreatedBy,
        ["Stocktake.CreatedAtUtc"] = Stocktake.CreatedAtUtc,
        ["Stocktake.SubmittedBy"] = Stocktake.SubmittedBy,
        ["Stocktake.SubmittedAtUtc"] = Stocktake.SubmittedAtUtc,
        ["Stocktake.DecidedBy"] = Stocktake.DecidedBy,
        ["Stocktake.DecidedAtUtc"] = Stocktake.DecidedAtUtc,
        ["Stocktake.DecisionNote"] = Stocktake.DecisionNote,
        ["Stocktake.CancelledBy"] = Stocktake.CancelledBy,
        ["Stocktake.CancelledAtUtc"] = Stocktake.CancelledAtUtc,
        ["Stocktake.CancelReason"] = Stocktake.CancelReason,
        ["Stocktake.Revision"] = Stocktake.Revision,
        ["Stocktake.UpdatedAtUtc"] = Stocktake.UpdatedAtUtc,
        ["Stocktake.PrimaryKey"] = Stocktake.PrimaryKey,
        ["Stocktake.ForeignKeyBranch"] = Stocktake.ForeignKeyBranch,
        ["Stocktake.ForeignKeyWarehouse"] = Stocktake.ForeignKeyWarehouse,
        ["Stocktake.ForeignKeyYear"] = Stocktake.ForeignKeyYear,
        ["Stocktake.CheckValues"] = Stocktake.CheckValues,
        ["Stocktake.CheckNumber"] = Stocktake.CheckNumber,
        ["Stocktake.CheckSeparation"] = Stocktake.CheckSeparation,
        ["Stocktake.CheckRevision"] = Stocktake.CheckRevision,
        ["Stocktake.UniqueNumber"] = Stocktake.UniqueNumber,
        ["Stocktake.WarehouseIndex"] = Stocktake.WarehouseIndex,
        ["Stocktake.StatusIndex"] = Stocktake.StatusIndex,
        ["Stocktake.GuardFunction"] = Stocktake.GuardFunction,
        ["Stocktake.CompleteFunction"] = Stocktake.CompleteFunction,
        ["Stocktake.GuardRow"] = Stocktake.GuardRow,
        ["Stocktake.GuardTruncate"] = Stocktake.GuardTruncate,
        ["Stocktake.CompleteTrigger"] = Stocktake.CompleteTrigger,
        ["StocktakeLine"] = StocktakeLine.Table,
        ["StocktakeLine.StocktakeId"] = StocktakeLine.StocktakeId,
        ["StocktakeLine.LineNo"] = StocktakeLine.LineNo,
        ["StocktakeLine.ItemId"] = StocktakeLine.ItemId,
        ["StocktakeLine.UnitId"] = StocktakeLine.UnitId,
        ["StocktakeLine.CountedQuantity"] = StocktakeLine.CountedQuantity,
        ["StocktakeLine.Factor"] = StocktakeLine.Factor,
        ["StocktakeLine.CountedBaseQuantity"] = StocktakeLine.CountedBaseQuantity,
        ["StocktakeLine.BookQuantity"] = StocktakeLine.BookQuantity,
        ["StocktakeLine.Variance"] = StocktakeLine.Variance,
        ["StocktakeLine.Reason"] = StocktakeLine.Reason,
        ["StocktakeLine.PrimaryKey"] = StocktakeLine.PrimaryKey,
        ["StocktakeLine.ForeignKeyStocktake"] = StocktakeLine.ForeignKeyStocktake,
        ["StocktakeLine.ForeignKeyItem"] = StocktakeLine.ForeignKeyItem,
        ["StocktakeLine.ForeignKeyUnit"] = StocktakeLine.ForeignKeyUnit,
        ["StocktakeLine.UniqueItem"] = StocktakeLine.UniqueItem,
        ["StocktakeLine.CheckValues"] = StocktakeLine.CheckValues,
        ["StocktakeLine.ItemIndex"] = StocktakeLine.ItemIndex,
        ["StocktakeLine.GuardFunction"] = StocktakeLine.GuardFunction,
        ["StocktakeLine.GuardRow"] = StocktakeLine.GuardRow,
        ["StocktakeLine.GuardTruncate"] = StocktakeLine.GuardTruncate,
        ["StocktakeEvent"] = StocktakeEvent.Table,
        ["StocktakeEvent.Id"] = StocktakeEvent.Id,
        ["StocktakeEvent.StocktakeId"] = StocktakeEvent.StocktakeId,
        ["StocktakeEvent.Kind"] = StocktakeEvent.Kind,
        ["StocktakeEvent.FromStatus"] = StocktakeEvent.FromStatus,
        ["StocktakeEvent.ToStatus"] = StocktakeEvent.ToStatus,
        ["StocktakeEvent.ActorUserId"] = StocktakeEvent.ActorUserId,
        ["StocktakeEvent.AtUtc"] = StocktakeEvent.AtUtc,
        ["StocktakeEvent.Note"] = StocktakeEvent.Note,
        ["StocktakeEvent.AuditStream"] = StocktakeEvent.AuditStream,
        ["StocktakeEvent.AuditSeq"] = StocktakeEvent.AuditSeq,
        ["StocktakeEvent.PrimaryKey"] = StocktakeEvent.PrimaryKey,
        ["StocktakeEvent.ForeignKeyStocktake"] = StocktakeEvent.ForeignKeyStocktake,
        ["StocktakeEvent.ForeignKeyAudit"] = StocktakeEvent.ForeignKeyAudit,
        ["StocktakeEvent.UniqueAuditEvent"] = StocktakeEvent.UniqueAuditEvent,
        ["StocktakeEvent.CheckValues"] = StocktakeEvent.CheckValues,
        ["StocktakeEvent.StocktakeIndex"] = StocktakeEvent.StocktakeIndex,
        ["StocktakeEvent.GuardFunction"] = StocktakeEvent.GuardFunction,
        ["StocktakeEvent.GuardRow"] = StocktakeEvent.GuardRow,
        ["StocktakeEvent.GuardTruncate"] = StocktakeEvent.GuardTruncate,
    });
}
