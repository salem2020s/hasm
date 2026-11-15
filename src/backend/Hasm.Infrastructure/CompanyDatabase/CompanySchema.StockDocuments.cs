namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0013 (Phase 3.5, stock movement documents (non-posting)). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class StockDocument
    {
        public const string Table = "t_e92d29e5";
        public const string Id = "c_9a59d13e";
        public const string DocType = "c_bc592f66";
        public const string Status = "c_3fc0f063";
        public const string BranchId = "c_36399733";
        public const string SourceWarehouseId = "c_a2143b1c";
        public const string TargetWarehouseId = "c_fc0a402b";
        public const string DocumentDate = "c_2580befc";
        public const string Reference = "c_88247301";
        public const string Notes = "c_bb37c08a";
        public const string Number = "c_308afbee";
        public const string FiscalYearId = "c_3ce67397";
        public const string CreatedBy = "c_6b6eb86b";
        public const string CreatedAtUtc = "c_fd289e25";
        public const string SubmittedBy = "c_afb8d9de";
        public const string SubmittedAtUtc = "c_fb5a1444";
        public const string DecidedBy = "c_c7d41bd1";
        public const string DecidedAtUtc = "c_0829bc7e";
        public const string DecisionNote = "c_a88bb246";
        public const string CancelledBy = "c_3816d744";
        public const string CancelledAtUtc = "c_afd3ee0f";
        public const string CancelReason = "c_bd8e717d";
        public const string Revision = "c_9ba641c8";
        public const string UpdatedAtUtc = "c_18954c0b";
        public const string PrimaryKey = "k_0fcd5508";
        public const string ForeignKeyBranch = "k_aaf84f1b";
        public const string ForeignKeySource = "k_ebf4a958";
        public const string ForeignKeyTarget = "k_ffff26f4";
        public const string ForeignKeyYear = "k_a362f531";
        public const string CheckValues = "k_4b4356af";
        public const string CheckWarehouses = "k_16edf2e8";
        public const string CheckNumber = "k_d7ed621d";
        public const string CheckSeparation = "k_4bfa3063";
        public const string CheckRevision = "k_07dfb9ae";
        public const string UniqueNumber = "k_8812f13c";
        public const string BranchIndex = "k_8561e099";
        public const string SourceIndex = "k_ec56b670";
        public const string TargetIndex = "k_aa884833";
        public const string StatusIndex = "k_8a3610b6";
        public const string GuardFunction = "f_62c4b763";
        public const string LinesRequiredFunction = "f_ec93103d";
        public const string GuardRow = "r_5a3d0a7d";
        public const string GuardTruncate = "r_9db8154d";
        public const string LinesRequiredTrigger = "r_54fc93c3";
    }

    public static class StockDocumentLine
    {
        public const string Table = "t_53dfc3d8";
        public const string DocumentId = "c_15325713";
        public const string LineNo = "c_f7be759d";
        public const string ItemId = "c_2ca8e448";
        public const string UnitId = "c_038c5606";
        public const string Quantity = "c_65dbf88c";
        public const string Factor = "c_cbc761e0";
        public const string BaseQuantity = "c_010de8d8";
        public const string Notes = "c_90c87357";
        public const string PrimaryKey = "k_bb95eac0";
        public const string ForeignKeyDocument = "k_1aed6392";
        public const string ForeignKeyItem = "k_9dfee29e";
        public const string ForeignKeyUnit = "k_26d9b156";
        public const string CheckValues = "k_2460e30c";
        public const string ItemIndex = "k_327b54c8";
        public const string GuardFunction = "f_9f62c5ce";
        public const string GuardRow = "r_d9b5ae38";
        public const string GuardTruncate = "r_6ed8a105";
    }

    public static class StockDocumentEvent
    {
        public const string Table = "t_b9231a6b";
        public const string Id = "c_e01af6ca";
        public const string DocumentId = "c_fd72fc35";
        public const string Kind = "c_2fe7652c";
        public const string FromStatus = "c_49806027";
        public const string ToStatus = "c_a54fce39";
        public const string ActorUserId = "c_2a6bae0c";
        public const string AtUtc = "c_29c24808";
        public const string Note = "c_268f7f43";
        public const string AuditStream = "c_fd2e47ac";
        public const string AuditSeq = "c_4684b80a";
        public const string PrimaryKey = "k_cf23fc0d";
        public const string ForeignKeyDocument = "k_1af8575f";
        public const string ForeignKeyAudit = "k_eb56ac03";
        public const string UniqueAuditEvent = "k_ed8d2288";
        public const string CheckValues = "k_851f3da2";
        public const string DocumentIndex = "k_9b16ac44";
        public const string GuardFunction = "f_08e6696d";
        public const string GuardRow = "r_1f81a29f";
        public const string GuardTruncate = "r_cae3df7d";
    }

    private static IReadOnlyDictionary<string, string> StockDocumentMap => stockDocumentMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> stockDocumentMap = new(() => new Dictionary<string, string>
    {
        ["StockDocument"] = StockDocument.Table,
        ["StockDocument.Id"] = StockDocument.Id,
        ["StockDocument.DocType"] = StockDocument.DocType,
        ["StockDocument.Status"] = StockDocument.Status,
        ["StockDocument.BranchId"] = StockDocument.BranchId,
        ["StockDocument.SourceWarehouseId"] = StockDocument.SourceWarehouseId,
        ["StockDocument.TargetWarehouseId"] = StockDocument.TargetWarehouseId,
        ["StockDocument.DocumentDate"] = StockDocument.DocumentDate,
        ["StockDocument.Reference"] = StockDocument.Reference,
        ["StockDocument.Notes"] = StockDocument.Notes,
        ["StockDocument.Number"] = StockDocument.Number,
        ["StockDocument.FiscalYearId"] = StockDocument.FiscalYearId,
        ["StockDocument.CreatedBy"] = StockDocument.CreatedBy,
        ["StockDocument.CreatedAtUtc"] = StockDocument.CreatedAtUtc,
        ["StockDocument.SubmittedBy"] = StockDocument.SubmittedBy,
        ["StockDocument.SubmittedAtUtc"] = StockDocument.SubmittedAtUtc,
        ["StockDocument.DecidedBy"] = StockDocument.DecidedBy,
        ["StockDocument.DecidedAtUtc"] = StockDocument.DecidedAtUtc,
        ["StockDocument.DecisionNote"] = StockDocument.DecisionNote,
        ["StockDocument.CancelledBy"] = StockDocument.CancelledBy,
        ["StockDocument.CancelledAtUtc"] = StockDocument.CancelledAtUtc,
        ["StockDocument.CancelReason"] = StockDocument.CancelReason,
        ["StockDocument.Revision"] = StockDocument.Revision,
        ["StockDocument.UpdatedAtUtc"] = StockDocument.UpdatedAtUtc,
        ["StockDocument.PrimaryKey"] = StockDocument.PrimaryKey,
        ["StockDocument.ForeignKeyBranch"] = StockDocument.ForeignKeyBranch,
        ["StockDocument.ForeignKeySource"] = StockDocument.ForeignKeySource,
        ["StockDocument.ForeignKeyTarget"] = StockDocument.ForeignKeyTarget,
        ["StockDocument.ForeignKeyYear"] = StockDocument.ForeignKeyYear,
        ["StockDocument.CheckValues"] = StockDocument.CheckValues,
        ["StockDocument.CheckWarehouses"] = StockDocument.CheckWarehouses,
        ["StockDocument.CheckNumber"] = StockDocument.CheckNumber,
        ["StockDocument.CheckSeparation"] = StockDocument.CheckSeparation,
        ["StockDocument.CheckRevision"] = StockDocument.CheckRevision,
        ["StockDocument.UniqueNumber"] = StockDocument.UniqueNumber,
        ["StockDocument.BranchIndex"] = StockDocument.BranchIndex,
        ["StockDocument.SourceIndex"] = StockDocument.SourceIndex,
        ["StockDocument.TargetIndex"] = StockDocument.TargetIndex,
        ["StockDocument.StatusIndex"] = StockDocument.StatusIndex,
        ["StockDocument.GuardFunction"] = StockDocument.GuardFunction,
        ["StockDocument.LinesRequiredFunction"] = StockDocument.LinesRequiredFunction,
        ["StockDocument.GuardRow"] = StockDocument.GuardRow,
        ["StockDocument.GuardTruncate"] = StockDocument.GuardTruncate,
        ["StockDocument.LinesRequiredTrigger"] = StockDocument.LinesRequiredTrigger,
        ["StockDocumentLine"] = StockDocumentLine.Table,
        ["StockDocumentLine.DocumentId"] = StockDocumentLine.DocumentId,
        ["StockDocumentLine.LineNo"] = StockDocumentLine.LineNo,
        ["StockDocumentLine.ItemId"] = StockDocumentLine.ItemId,
        ["StockDocumentLine.UnitId"] = StockDocumentLine.UnitId,
        ["StockDocumentLine.Quantity"] = StockDocumentLine.Quantity,
        ["StockDocumentLine.Factor"] = StockDocumentLine.Factor,
        ["StockDocumentLine.BaseQuantity"] = StockDocumentLine.BaseQuantity,
        ["StockDocumentLine.Notes"] = StockDocumentLine.Notes,
        ["StockDocumentLine.PrimaryKey"] = StockDocumentLine.PrimaryKey,
        ["StockDocumentLine.ForeignKeyDocument"] = StockDocumentLine.ForeignKeyDocument,
        ["StockDocumentLine.ForeignKeyItem"] = StockDocumentLine.ForeignKeyItem,
        ["StockDocumentLine.ForeignKeyUnit"] = StockDocumentLine.ForeignKeyUnit,
        ["StockDocumentLine.CheckValues"] = StockDocumentLine.CheckValues,
        ["StockDocumentLine.ItemIndex"] = StockDocumentLine.ItemIndex,
        ["StockDocumentLine.GuardFunction"] = StockDocumentLine.GuardFunction,
        ["StockDocumentLine.GuardRow"] = StockDocumentLine.GuardRow,
        ["StockDocumentLine.GuardTruncate"] = StockDocumentLine.GuardTruncate,
        ["StockDocumentEvent"] = StockDocumentEvent.Table,
        ["StockDocumentEvent.Id"] = StockDocumentEvent.Id,
        ["StockDocumentEvent.DocumentId"] = StockDocumentEvent.DocumentId,
        ["StockDocumentEvent.Kind"] = StockDocumentEvent.Kind,
        ["StockDocumentEvent.FromStatus"] = StockDocumentEvent.FromStatus,
        ["StockDocumentEvent.ToStatus"] = StockDocumentEvent.ToStatus,
        ["StockDocumentEvent.ActorUserId"] = StockDocumentEvent.ActorUserId,
        ["StockDocumentEvent.AtUtc"] = StockDocumentEvent.AtUtc,
        ["StockDocumentEvent.Note"] = StockDocumentEvent.Note,
        ["StockDocumentEvent.AuditStream"] = StockDocumentEvent.AuditStream,
        ["StockDocumentEvent.AuditSeq"] = StockDocumentEvent.AuditSeq,
        ["StockDocumentEvent.PrimaryKey"] = StockDocumentEvent.PrimaryKey,
        ["StockDocumentEvent.ForeignKeyDocument"] = StockDocumentEvent.ForeignKeyDocument,
        ["StockDocumentEvent.ForeignKeyAudit"] = StockDocumentEvent.ForeignKeyAudit,
        ["StockDocumentEvent.UniqueAuditEvent"] = StockDocumentEvent.UniqueAuditEvent,
        ["StockDocumentEvent.CheckValues"] = StockDocumentEvent.CheckValues,
        ["StockDocumentEvent.DocumentIndex"] = StockDocumentEvent.DocumentIndex,
        ["StockDocumentEvent.GuardFunction"] = StockDocumentEvent.GuardFunction,
        ["StockDocumentEvent.GuardRow"] = StockDocumentEvent.GuardRow,
        ["StockDocumentEvent.GuardTruncate"] = StockDocumentEvent.GuardTruncate,
    });
}
