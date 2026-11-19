using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using D = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;
using L = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocumentLine;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Infrastructure.Costing;

internal sealed record CostSourceHeader(Guid Id, int Type, int Status, Guid BranchId, Guid? Source, Guid? Target,
    DateOnly Date, long Revision, string? Reference, string? Notes, string? Number, Guid? FiscalYearId,
    Guid CreatedBy, DateTimeOffset CreatedAt, Guid? SubmittedBy, DateTimeOffset? SubmittedAt,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionNote, DateTimeOffset UpdatedAt, bool AlreadyPosted);
internal sealed record CostSourceLine(int LineNo, Guid ItemId, Guid UnitId, decimal Quantity, decimal Factor,
    decimal BaseQuantity, string? Notes, decimal? ReceiptAmount = null);
internal sealed record CostSourceEvidence(CostSourceHeader Header, ImmutableArray<CostSourceLine> Lines,
    ImmutableArray<CostFlowComparisonResult> Comparisons, string EvidenceFingerprint);

internal sealed partial class CostFlowSnapshotService
{
    // Read-only ordinary evidence, NOT a fiscal/account-policy-bound plan or execution token.
    internal Task<ConfigResult<CostSourceEvidence>> LoadSourceEvidenceAsync(Guid user, Guid company, Guid document,
        long? expectedRevision, StockPostInput? input, CancellationToken ct = default)
    {
        var sourceScopes = ImmutableArray<Guid>.Empty;
        return RunBoundedReadAsync(user, company, async (budget, scopes, token) =>
        {
            if (expectedRevision is null) return ConfigResult<CostSourceEvidence>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
            if (document == Guid.Empty) return ConfigResult<CostSourceEvidence>.Fail(ConfigStatus.Conflict, "invalid_impact_source");
            var frozen = FreezeAmounts(input, token);
            var effective = await access.GetEffectivePermissionsAsync(user, company, budget.Sql.Reserve, token);
            var code = PermissionCatalog.Codes.StockDocsView;
            if (!effective.HasCompanyWide(code) && !effective.Grants.Any(g => g.Scope == ScopeKind.Warehouse &&
                    g.ScopeId is { } id && effective.Has(code, ScopeTarget.Warehouse(id)))) throw new Denied();
            await using var conn = await resolver.OpenAsync(company, budget.Sql.Reserve, token);
            budget.Sql.Reserve(2);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await using (var q = budget.Command("SET TRANSACTION READ ONLY", conn, tx)) await q.ExecuteNonQueryAsync(token);
            var header = await ReadSourceHeader(conn, tx, document, budget, token);
            if (header is null) throw new Denied(); // Never expose existence before actual source visibility.
            sourceScopes = new[] { header.Source, header.Target }.Where(w => w.HasValue).Select(w => w!.Value).Distinct().ToImmutableArray();
            if (!await SourceVisible(user, company, sourceScopes, budget, token)) throw new Denied();
            foreach (var root in sourceScopes)
                if (scopes.Add(root) && !await Can(user, company, root, budget.Sql, token)) throw new Denied();
            if (SnapshotEstablished is { } observe) await observe(conn, tx, token);
            var lines = await ReadSourceLines(conn, tx, document, budget, token);
            var roots = lines.Select(l => l.ItemId).Distinct().Select(item => new CostFlowItemRoots(item, sourceScopes)).ToImmutableArray();
            foreach (var root in roots) foreach (var warehouse in root.Warehouses) budget.Position(root.ItemId, warehouse);
            Dictionary<int, decimal>? amounts = null;
            async Task ValidateSource(CancellationToken cancellation)
            {
                if (header.Status != (int)StockDocStatus.Approved)
                    throw new StockDocumentService.Abort(ConfigStatus.Conflict, "document_not_approved");
                if (expectedRevision != header.Revision)
                    throw new StockDocumentService.Abort(ConfigStatus.PreconditionFailed, "revision_conflict", null, header.Revision);
                if (header.AlreadyPosted) throw new StockDocumentService.Abort(ConfigStatus.Conflict, "already_posted");
                if (header.SubmittedBy is null || header.DecidedBy is null || header.SubmittedBy == header.DecidedBy) throw Inconsistent();
                for (var i = 0; i < lines.Length; i++) if (lines[i].LineNo != i + 1) throw Inconsistent();
                var normal = new StockDocumentInput(header.Type, header.Source, header.Target,
                    header.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), header.Reference, header.Notes,
                    lines.Select(l => new StockLineInput(l.ItemId, l.UnitId, l.Quantity, l.Notes)).ToImmutableArray());
                var validated = await StockDocumentService.ValidateReadOnlyAsync(conn, tx, normal, budget.Sql.Reserve, budget.Discovery, cancellation);
                if (validated.BranchId != header.BranchId) throw new StockDocumentService.Abort(ConfigStatus.Conflict, "source_branch_changed");
                for (var i = 0; i < lines.Length; i++)
                    if (validated.Lines[i].Factor != lines[i].Factor || validated.Lines[i].Base != lines[i].BaseQuantity)
                        throw new StockDocumentService.Abort(ConfigStatus.Conflict, "line_conversion_changed");
                amounts = StockPostingService.ReadSourceAmounts(header.Type, lines.Select(l => l.LineNo).ToArray(), frozen);
            }
            var snapshots = await ReadItemsInTransactionAsync(conn, tx, company, roots, scopes,
                w => Can(user, company, w, budget.Sql, token), budget, ValidateSource, token);
            if (!snapshots.IsOk) return ConfigResult<CostSourceEvidence>.Fail(snapshots.Status, snapshots.Code!);
            var pricedLines = lines.Select(l => l with { ReceiptAmount = header.Type == (int)StockDocType.Receipt ? amounts![l.LineNo] : null }).ToImmutableArray();
            var comparisons = ImmutableArray.CreateBuilder<CostFlowComparisonResult>(snapshots.Value.Length);
            foreach (var snapshot in snapshots.Value)
            {
                token.ThrowIfCancellationRequested();
                var children = pricedLines.Where(l => l.ItemId == snapshot.ItemId).ToArray();
                var proposalId = SourceProposalId(company, header, snapshot.ItemId);
                budget.Group(proposalId);
                var kind = (CostOperationKind)header.Type;
                var warehouse = (header.Type == (int)StockDocType.Receipt ? header.Target : header.Source)!.Value;
                var proposed = header.Type == (int)StockDocType.Transfer
                    ? ImmutableArray.Create(new ProposedFlowLine(warehouse, children.Sum(l => l.BaseQuantity), DestinationWarehouseId: header.Target))
                    : children.Select(l => new ProposedFlowLine(warehouse, l.BaseQuantity, l.ReceiptAmount)).ToImmutableArray();
                for (var n = 0; n < (kind == CostOperationKind.Transfer ? 2 : proposed.Length); n++) budget.Entry();
                CostFlowComparisonResult comparison;
                try
                {
                    foreach (var child in children)
                        if (child.ReceiptAmount is { } amount) _ = InventoryCostingService.Amount(amount, snapshot.MoneyDecimals);
                    comparison = CostFlowComparison.Calculate(snapshot, new(proposalId, header.Date, kind, proposed), token);
                }
                catch (CostingException error) when (error.Code is "amount_precision_exceeded" or "invalid_amount")
                    { throw StockDocumentService.Invalid("lines", error.Code); }
                // The source operation also bounds retained revised output globally, not once per item.
                foreach (var layer in comparison.RevisedTrace.CreatedLayers) budget.Component();
                foreach (var step in comparison.RevisedTrace.Steps)
                    foreach (var leg in step.Legs) foreach (var consumption in leg.Step.Consumptions) budget.Component();
                comparisons.Add(comparison);
            }
            var complete = comparisons.ToImmutable();
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                { Contract = "stock-source-evidence-v1", Company = company, Header = header, Lines = pricedLines,
                    Comparisons = complete.Select(c => c.ComparisonFingerprint).ToArray() }))).ToLowerInvariant();
            token.ThrowIfCancellationRequested();
            return ConfigResult<CostSourceEvidence>.Ok(new(header, pricedLines, complete, fingerprint));
        }, (budget, token) => sourceScopes.IsEmpty ? Task.FromResult(true) : SourceVisible(user, company, sourceScopes, budget, token), ct);
    }

    private async Task<bool> SourceVisible(Guid user, Guid company, ImmutableArray<Guid> warehouses, Budget budget, CancellationToken ct)
    {
        foreach (var warehouse in warehouses)
            if (await access.IsAllowedAsync(user, company, PermissionCatalog.Codes.StockDocsView, ScopeTarget.Warehouse(warehouse), budget.Sql.Reserve, ct)) return true;
        return false;
    }

    private static StockPostInput? FreezeAmounts(StockPostInput? input, CancellationToken ct)
    {
        if (input?.Lines is not { } lines) return input is null ? null : new StockPostInput(null);
        var frozen = ImmutableArray.CreateBuilder<StockPostLineInput>();
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested(); if (frozen.Count == 500) throw TooLarge(); frozen.Add(line);
        }
        return new(frozen.ToImmutable());
    }

    private static Guid SourceProposalId(Guid company, CostSourceHeader source, Guid item)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"stock-source-proposal-v1/{company:N}/{source.Id:N}/{source.Revision.ToString(CultureInfo.InvariantCulture)}/{item:N}"))[..16]);

    private static async Task<CostSourceHeader?> ReadSourceHeader(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid id, Budget budget, CancellationToken ct)
    {
        await using var q = budget.Command($"SELECT d.{D.DocType},d.{D.Status},d.{D.BranchId},d.{D.SourceWarehouseId},d.{D.TargetWarehouseId},d.{D.DocumentDate},d.{D.Revision},d.{D.Reference},d.{D.Notes},d.{D.Number},d.{D.FiscalYearId},d.{D.CreatedBy},d.{D.CreatedAtUtc},d.{D.SubmittedBy},d.{D.SubmittedAtUtc},d.{D.DecidedBy},d.{D.DecidedAtUtc},d.{D.DecisionNote},d.{D.UpdatedAtUtc},EXISTS(SELECT 1 FROM {P.Table} p WHERE p.{P.DocumentId}=d.{D.Id}) FROM {D.Table} d WHERE d.{D.Id}=@id", conn, tx);
        q.Parameters.AddWithValue("id", id); await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null; budget.Discovery();
        Guid? G(int n) => r.IsDBNull(n) ? null : r.GetGuid(n);
        string? S(int n) => r.IsDBNull(n) ? null : r.GetString(n);
        DateTimeOffset? T(int n) => r.IsDBNull(n) ? null : r.GetFieldValue<DateTimeOffset>(n);
        return new(id, r.GetInt16(0), r.GetInt16(1), r.GetGuid(2), G(3), G(4), r.GetFieldValue<DateOnly>(5), r.GetInt64(6),
            S(7), S(8), S(9), G(10), r.GetGuid(11), r.GetFieldValue<DateTimeOffset>(12), G(13), T(14), G(15), T(16), S(17),
            r.GetFieldValue<DateTimeOffset>(18), r.GetBoolean(19));
    }

    private static async Task<ImmutableArray<CostSourceLine>> ReadSourceLines(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid document, Budget budget, CancellationToken ct)
    {
        await using var q = budget.Command($"SELECT {L.LineNo},{L.ItemId},{L.UnitId},{L.Quantity},{L.Factor},{L.BaseQuantity},{L.Notes} FROM {L.Table} WHERE {L.DocumentId}=@id ORDER BY {L.LineNo} LIMIT 501", conn, tx);
        q.Parameters.AddWithValue("id", document); await using var r = await q.ExecuteReaderAsync(ct);
        var lines = ImmutableArray.CreateBuilder<CostSourceLine>();
        while (await r.ReadAsync(ct))
        {
            budget.Discovery(); if (lines.Count == 500) throw TooLarge();
            lines.Add(new(r.GetInt32(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return lines.ToImmutable();
    }
}
