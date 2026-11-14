using System.Text.Json.Nodes;
using Hasm.Infrastructure.CompanyDatabase;

namespace Hasm.Infrastructure.Imports;

/// <summary>Life cycle of an import batch. The numeric values are persisted and enforced by PostgreSQL; never renumber.</summary>
public enum ImportStatus : short
{
    Uploaded = 1, Staged = 2, Mapped = 3, Validated = 4, Submitted = 5, Approved = 6, Applying = 7, Applied = 8, Failed = 9, Rejected = 10, Cancelled = 11, Expired = 12,
}

public static class ImportStatusRules
{
    public static bool IsTerminal(ImportStatus s) => s is ImportStatus.Applied or ImportStatus.Rejected or ImportStatus.Cancelled or ImportStatus.Expired;
    public static string Name(ImportStatus s) => s switch { ImportStatus.Uploaded => "uploaded", ImportStatus.Staged => "staged", ImportStatus.Mapped => "mapped", ImportStatus.Validated => "validated", ImportStatus.Submitted => "submitted", ImportStatus.Approved => "approved", ImportStatus.Applying => "applying", ImportStatus.Applied => "applied", ImportStatus.Failed => "failed", ImportStatus.Rejected => "rejected", ImportStatus.Cancelled => "cancelled", _ => "expired" };
}

/// <summary>History event kinds of an import batch (persisted; never renumber). They are also the business-audit event types offset by <see cref="ImportAuditEvents.Base"/>.</summary>
public enum ImportEventKind : short
{
    Uploaded = 1, Staged = 2, Cancelled = 3, Expired = 4, Renewed = 5, Purged = 6, Rewrapped = 7,
    Mapped = 8, Validated = 9, ResolutionChanged = 10, Submitted = 11, Approved = 12, Rejected = 13, Withdrawn = 14, ApplyStarted = 15, Applied = 16, ApplyFailed = 17,
}

/// <summary>Business audit event types of the import framework (the 1000+ range of the company business stream).</summary>
public static class ImportAuditEvents
{
    public const short Base = 1069;                    // event type = Base + ImportEventKind (1070 ...)
    public const short SettingsChanged = 1100;
    public static short Of(ImportEventKind kind) => (short)(Base + (short)kind);
}

/// <summary>What a batch looks like to a client. File evidence (SHA-256, size, key id) and the plan hash stay verifiable after the encrypted original and the staged rows were purged.</summary>
public sealed record ImportBatchView(
    Guid Id, string AdapterCode, int AdapterVersion, ImportStatus Status, string FileName, ImportFileKind FileKind, long FileSize, string FileSha256, string KeyId,
    JsonNode Options, IReadOnlyList<SheetInfo> Sheets, int RowCount, string? RowsHash, string? PlanHash, long Revision, Guid CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    Guid? SubmittedBy, DateTimeOffset? SubmittedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? TerminalAtUtc, bool OriginalAvailable, bool RowsAvailable, DateTimeOffset? PurgeAfterUtc,
    JsonNode? Mapping = null, JsonNode? Plan = null, JsonNode? Result = null);

/// <summary>How to stage a file: worksheet index, header row (1-based), CSV encoding (utf-8, utf-16, windows-1256) and delimiter (a single character, empty = detect).</summary>
public sealed record StageInput(int? Sheet, int? HeaderRow, string? Encoding, string? Delimiter);

public sealed record ImportRowView(int RowNo, IReadOnlyList<string?> Cells, short Status, JsonNode? Normalized = null, JsonNode? Issues = null, string? Resolution = null, JsonNode? Operation = null, string? Explanation = null, IReadOnlyList<string>? AllowedResolutions = null);
public sealed record ImportRowsPage(IReadOnlyList<string> Headers, int Total, int Offset, IReadOnlyList<ImportRowView> Rows);
public sealed record ImportEventView(long Id, ImportEventKind Kind, long Revision, Guid? ActorUserId, DateTimeOffset AtUtc, JsonNode Detail);
public sealed record ImportListFilter(ImportStatus? Status, string? AdapterCode, int Limit = 50, int Offset = 0);

public sealed record ImportSettingsView(int RetentionDays, int PendingExpiryDays, long Revision, bool Configured);
public sealed record ImportSettingsInput(int? RetentionDays, int? PendingExpiryDays);

public sealed record ImportMaintenanceResult(int Expired, int Purged, int Rewrapped);

/// <summary>
/// An import adapter: ALL business knowledge about one kind of data lives behind this interface, never in the generic framework. The descriptor identifies it and its permissions; the fields drive mapping
/// and normalization; <see cref="AnalyzeAsync"/> validates, detects conflicts against existing records and says exactly what application would do; later slices add application.
/// </summary>
public interface IImportAdapter
{
    /// <summary>Stable lowercase identifier (e.g. <c>chart-of-accounts</c>); persisted on every batch.</summary>
    string Code { get; }
    /// <summary>Adapter contract version (persisted; a change of rules bumps it so old plans can never be applied by new rules).</summary>
    int Version { get; }
    string NameAr { get; }
    string NameEn { get; }
    /// <summary>Company-wide permission needed to upload, stage, map and submit data for this adapter (and which the adapter's apply step relies on).</summary>
    string UploadPermission { get; }
    /// <summary>Company-wide permission needed to see the staged data of this adapter.</summary>
    string ViewPermission { get; }

    /// <summary>The target fields (what the user maps columns to). Default: none.</summary>
    IReadOnlyList<ImportField> Fields => [];

    /// <summary>The conflict resolutions this adapter supports (always include <c>skip</c> if rows may be skipped). The framework never invents one.</summary>
    IReadOnlyList<string> Resolutions => [ImportResolutions.Skip];

    /// <summary>True when the whole approved batch is applied in ONE transaction (all or nothing). Chunked adapters must declare durable checkpoints, idempotency and valid partial states; none exists yet.</summary>
    bool Atomic => true;

    /// <summary>
    /// Validates the normalized rows against the business rules and the CURRENT business data (read-only), detects conflicts with existing records, and returns for every row its outcome and the exact operation
    /// application would perform. Must be deterministic: the same rows and data give the same answer.
    /// </summary>
    Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct) => throw new NotSupportedException("This adapter does not analyse rows.");

    /// <summary>
    /// Called first inside the application transaction: take the adapter's own domain locks here (the ones every writer of that data takes), so that the re-validation that follows and the changes after it
    /// see data nobody can change in between. Default: nothing to lock.
    /// </summary>
    Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Performs the approved operations INSIDE the application transaction (all or nothing: an exception rolls back every change of the adapter and of the framework). Every change must go through the
    /// module's own audited domain operations so its business rules, restrictions and audit events still apply; an adapter must never write business data by any other route. Throw
    /// <see cref="ImportApplyException"/> for a failure whose code and message are safe to keep.
    /// </summary>
    Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct) => throw new NotSupportedException("This adapter does not apply imports.");
}

/// <summary>One approved operation of the plan, in plan order.</summary>
public sealed record ImportApplyOperation(int RowNo, int Sequence, JsonObject Operation);

/// <summary>What the application transaction hands to the adapter: the audited unit of work, the person applying and the approved operations (re-validated against the current data a moment before).</summary>
public sealed record ImportApplyContext(CompanyAuditedWork Work, Guid Actor, Guid BatchId, IReadOnlyList<ImportApplyOperation> Operations);

/// <summary>The adapter's summary of what was done (kept on the batch as the reconciliation of the application).</summary>
public sealed record ImportApplyResult(JsonObject Summary);

/// <summary>A failure of an adapter's application whose <see cref="Code"/> and message are safe to store and show (never data, never secrets).</summary>
public sealed class ImportApplyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ImportResolutions
{
    /// <summary>Leave the row out: nothing is created or changed for it. The only resolution the framework itself understands.</summary>
    public const string Skip = "skip";
}

public sealed class ImportAdapterRegistry(IEnumerable<IImportAdapter> adapters)
{
    private readonly Dictionary<string, IImportAdapter> byCode = adapters.ToDictionary(a => a.Code, StringComparer.Ordinal);
    public IReadOnlyCollection<IImportAdapter> All => byCode.Values;
    public IImportAdapter? Find(string? code) => code is not null && byCode.TryGetValue(code, out var a) ? a : null;
}
