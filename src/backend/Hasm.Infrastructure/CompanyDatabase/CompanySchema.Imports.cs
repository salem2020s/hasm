namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0010 (Phase 3.2D, generic import framework). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class ImportBatch
    {
        public const string Table = "t_153392c3";
        public const string Id = "c_bf254544";
        public const string AdapterCode = "c_09d09dd9";
        public const string AdapterVersion = "c_8fafc1ac";
        public const string Status = "c_fb0dcab8";
        public const string FileName = "c_4fe5cb24";
        public const string FileKind = "c_fa83dd88";
        public const string FileSize = "c_01407ab9";
        public const string FileSha256 = "c_071a0752";
        public const string StorageName = "c_fbb212f0";
        public const string KeyId = "c_546fb474";
        public const string Options = "c_83a86bb8";
        public const string Sheets = "c_286033ff";
        public const string Mapping = "c_acae102c";
        public const string RowCount = "c_e67e2dc1";
        public const string RowsHash = "c_7b8ea1ad";
        public const string PlanHash = "c_fbc75ef6";
        public const string Plan = "c_a6725cfa";
        public const string Revision = "c_2e738c03";
        public const string IdempotencyKey = "c_91f9f5d5";
        public const string CreatedBy = "c_6c662d5d";
        public const string CreatedAtUtc = "c_e6a8b09a";
        public const string UpdatedAtUtc = "c_68c70c41";
        public const string SubmittedBy = "c_83a915fd";
        public const string SubmittedAtUtc = "c_dacd5ab3";
        public const string ExpiresAtUtc = "c_c130ecd5";
        public const string TerminalAtUtc = "c_6af863fd";
        public const string OriginalPurgedAtUtc = "c_6d2647de";
        public const string RowsPurgedAtUtc = "c_74823892";
        public const string Result = "c_c6666cf4";
        public const string ApprovalRevision = "c_bd7e35c3";
        public const string CheckApproval = "k_a8f55915";
        public const string PrimaryKey = "k_1b8c15c2";
        public const string UniqueIdempotency = "k_51a0c819";
        public const string UniqueStorage = "k_4c690e46";
        public const string CheckValues = "k_3333a1db";
        public const string CheckLifecycle = "k_88fd7e4b";
        public const string CheckPlan = "k_dfd28252";
        public const string IndexExpiry = "k_b51a645d";
        public const string GuardFunction = "f_1d952876";
        public const string GuardRow = "r_5fecf89f";
        public const string GuardTruncate = "r_1b9d677b";
    }

    public static class ImportRow
    {
        public const string Table = "t_863ea0e3";
        public const string BatchId = "c_32965e1f";
        public const string RowNo = "c_e026b5e7";
        public const string Raw = "c_9d1ea166";
        public const string RowHash = "c_d92532f0";
        public const string Normalized = "c_e9835db7";
        public const string Status = "c_78c427bb";
        public const string Errors = "c_aa830dd8";
        public const string Resolution = "c_d5784b67";
        public const string Analysis = "c_451e180b";
        public const string Sequence = "c_d3b66a5c";
        public const string IndexSequence = "k_47e681bf";
        public const string PrimaryKey = "k_5849e3f8";
        public const string ForeignKeyBatch = "k_09bddbe2";
        public const string CheckValues = "k_ec11ddf4";
        public const string IndexStatus = "k_6f38ce4b";
        public const string GuardFunction = "f_bc416a22";
        public const string GuardRow = "r_48c1ce71";
        public const string GuardTruncate = "r_90085969";
    }

    public static class ImportEvent
    {
        public const string Table = "t_c0e5f7b5";
        public const string Id = "c_cb757793";
        public const string BatchId = "c_3320c60b";
        public const string Kind = "c_fb9ffb64";
        public const string Revision = "c_2c1b0a9d";
        public const string ActorUserId = "c_34bfefc6";
        public const string AtUtc = "c_d59455f6";
        public const string Detail = "c_b9457705";
        public const string AuditStream = "c_b4596169";
        public const string AuditSeq = "c_32a27469";
        public const string PrimaryKey = "k_b3f26402";
        public const string ForeignKeyBatch = "k_3a8abcc7";
        public const string UniqueAuditEvent = "k_346a3500";
        public const string ForeignKeyAudit = "k_2f57aaa8";
        public const string CheckValues = "k_85514d93";
        public const string IndexBatch = "k_3d53edde";
        public const string GuardFunction = "f_384516a4";
        public const string GuardRow = "r_8670411f";
        public const string GuardTruncate = "r_1f644842";
    }

    public static class ImportApproval
    {
        public const string DecisionFunction = "f_1425577b";
        public const string DecisionTrigger = "r_94be4b81";
    }

    public static class ImportSettings
    {
        public const string Table = "t_2b1f2f8b";
        public const string Singleton = "c_bbe100fd";
        public const string RetentionDays = "c_4961c4dd";
        public const string PendingExpiryDays = "c_629dd306";
        public const string Revision = "c_fd7097a7";
        public const string UpdatedAtUtc = "c_6067a496";
        public const string PrimaryKey = "k_bb8c3d2f";
        public const string CheckValues = "k_bc473b3c";
        public const string GuardFunction = "f_d1eedf38";
        public const string GuardRow = "r_09ffaa9f";
        public const string GuardTruncate = "r_583f09bb";
    }

    private static IReadOnlyDictionary<string, string> ImportMap => importMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> importMap = new(() => new Dictionary<string, string>
    {
        ["ImportBatch"] = ImportBatch.Table,
        ["ImportBatch.Id"] = ImportBatch.Id,
        ["ImportBatch.AdapterCode"] = ImportBatch.AdapterCode,
        ["ImportBatch.AdapterVersion"] = ImportBatch.AdapterVersion,
        ["ImportBatch.Status"] = ImportBatch.Status,
        ["ImportBatch.FileName"] = ImportBatch.FileName,
        ["ImportBatch.FileKind"] = ImportBatch.FileKind,
        ["ImportBatch.FileSize"] = ImportBatch.FileSize,
        ["ImportBatch.FileSha256"] = ImportBatch.FileSha256,
        ["ImportBatch.StorageName"] = ImportBatch.StorageName,
        ["ImportBatch.KeyId"] = ImportBatch.KeyId,
        ["ImportBatch.Options"] = ImportBatch.Options,
        ["ImportBatch.Sheets"] = ImportBatch.Sheets,
        ["ImportBatch.Mapping"] = ImportBatch.Mapping,
        ["ImportBatch.RowCount"] = ImportBatch.RowCount,
        ["ImportBatch.RowsHash"] = ImportBatch.RowsHash,
        ["ImportBatch.PlanHash"] = ImportBatch.PlanHash,
        ["ImportBatch.Plan"] = ImportBatch.Plan,
        ["ImportBatch.Revision"] = ImportBatch.Revision,
        ["ImportBatch.IdempotencyKey"] = ImportBatch.IdempotencyKey,
        ["ImportBatch.CreatedBy"] = ImportBatch.CreatedBy,
        ["ImportBatch.CreatedAtUtc"] = ImportBatch.CreatedAtUtc,
        ["ImportBatch.UpdatedAtUtc"] = ImportBatch.UpdatedAtUtc,
        ["ImportBatch.SubmittedBy"] = ImportBatch.SubmittedBy,
        ["ImportBatch.SubmittedAtUtc"] = ImportBatch.SubmittedAtUtc,
        ["ImportBatch.ExpiresAtUtc"] = ImportBatch.ExpiresAtUtc,
        ["ImportBatch.TerminalAtUtc"] = ImportBatch.TerminalAtUtc,
        ["ImportBatch.OriginalPurgedAtUtc"] = ImportBatch.OriginalPurgedAtUtc,
        ["ImportBatch.RowsPurgedAtUtc"] = ImportBatch.RowsPurgedAtUtc,
        ["ImportBatch.Result"] = ImportBatch.Result,
        ["ImportBatch.ApprovalRevision"] = ImportBatch.ApprovalRevision,
        ["ImportBatch.CheckApproval"] = ImportBatch.CheckApproval,
        ["ImportBatch.PrimaryKey"] = ImportBatch.PrimaryKey,
        ["ImportBatch.UniqueIdempotency"] = ImportBatch.UniqueIdempotency,
        ["ImportBatch.UniqueStorage"] = ImportBatch.UniqueStorage,
        ["ImportBatch.CheckValues"] = ImportBatch.CheckValues,
        ["ImportBatch.CheckLifecycle"] = ImportBatch.CheckLifecycle,
        ["ImportBatch.CheckPlan"] = ImportBatch.CheckPlan,
        ["ImportBatch.IndexExpiry"] = ImportBatch.IndexExpiry,
        ["ImportBatch.GuardFunction"] = ImportBatch.GuardFunction,
        ["ImportBatch.GuardRow"] = ImportBatch.GuardRow,
        ["ImportBatch.GuardTruncate"] = ImportBatch.GuardTruncate,
        ["ImportRow"] = ImportRow.Table,
        ["ImportRow.BatchId"] = ImportRow.BatchId,
        ["ImportRow.RowNo"] = ImportRow.RowNo,
        ["ImportRow.Raw"] = ImportRow.Raw,
        ["ImportRow.RowHash"] = ImportRow.RowHash,
        ["ImportRow.Normalized"] = ImportRow.Normalized,
        ["ImportRow.Status"] = ImportRow.Status,
        ["ImportRow.Errors"] = ImportRow.Errors,
        ["ImportRow.Resolution"] = ImportRow.Resolution,
        ["ImportRow.Analysis"] = ImportRow.Analysis,
        ["ImportRow.Sequence"] = ImportRow.Sequence,
        ["ImportRow.IndexSequence"] = ImportRow.IndexSequence,
        ["ImportRow.PrimaryKey"] = ImportRow.PrimaryKey,
        ["ImportRow.ForeignKeyBatch"] = ImportRow.ForeignKeyBatch,
        ["ImportRow.CheckValues"] = ImportRow.CheckValues,
        ["ImportRow.IndexStatus"] = ImportRow.IndexStatus,
        ["ImportRow.GuardFunction"] = ImportRow.GuardFunction,
        ["ImportRow.GuardRow"] = ImportRow.GuardRow,
        ["ImportRow.GuardTruncate"] = ImportRow.GuardTruncate,
        ["ImportEvent"] = ImportEvent.Table,
        ["ImportEvent.Id"] = ImportEvent.Id,
        ["ImportEvent.BatchId"] = ImportEvent.BatchId,
        ["ImportEvent.Kind"] = ImportEvent.Kind,
        ["ImportEvent.Revision"] = ImportEvent.Revision,
        ["ImportEvent.ActorUserId"] = ImportEvent.ActorUserId,
        ["ImportEvent.AtUtc"] = ImportEvent.AtUtc,
        ["ImportEvent.Detail"] = ImportEvent.Detail,
        ["ImportEvent.AuditStream"] = ImportEvent.AuditStream,
        ["ImportEvent.AuditSeq"] = ImportEvent.AuditSeq,
        ["ImportEvent.PrimaryKey"] = ImportEvent.PrimaryKey,
        ["ImportEvent.ForeignKeyBatch"] = ImportEvent.ForeignKeyBatch,
        ["ImportEvent.UniqueAuditEvent"] = ImportEvent.UniqueAuditEvent,
        ["ImportEvent.ForeignKeyAudit"] = ImportEvent.ForeignKeyAudit,
        ["ImportEvent.CheckValues"] = ImportEvent.CheckValues,
        ["ImportEvent.IndexBatch"] = ImportEvent.IndexBatch,
        ["ImportEvent.GuardFunction"] = ImportEvent.GuardFunction,
        ["ImportEvent.GuardRow"] = ImportEvent.GuardRow,
        ["ImportEvent.GuardTruncate"] = ImportEvent.GuardTruncate,
        ["ImportApproval.DecisionFunction"] = ImportApproval.DecisionFunction,
        ["ImportApproval.DecisionTrigger"] = ImportApproval.DecisionTrigger,
        ["ImportSettings"] = ImportSettings.Table,
        ["ImportSettings.Singleton"] = ImportSettings.Singleton,
        ["ImportSettings.RetentionDays"] = ImportSettings.RetentionDays,
        ["ImportSettings.PendingExpiryDays"] = ImportSettings.PendingExpiryDays,
        ["ImportSettings.Revision"] = ImportSettings.Revision,
        ["ImportSettings.UpdatedAtUtc"] = ImportSettings.UpdatedAtUtc,
        ["ImportSettings.PrimaryKey"] = ImportSettings.PrimaryKey,
        ["ImportSettings.CheckValues"] = ImportSettings.CheckValues,
        ["ImportSettings.GuardFunction"] = ImportSettings.GuardFunction,
        ["ImportSettings.GuardRow"] = ImportSettings.GuardRow,
        ["ImportSettings.GuardTruncate"] = ImportSettings.GuardTruncate,
    });
}
