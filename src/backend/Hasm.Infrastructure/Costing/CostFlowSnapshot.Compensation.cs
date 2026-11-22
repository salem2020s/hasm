using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using SP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockPosting;

namespace Hasm.Infrastructure.Costing;

/// <summary>Everything a later approval needs to bind, from ONE read-only snapshot. NOT an approval or execution token; nothing is written.</summary>
internal sealed record CompensationPlanResult(CostFlowComparisonResult Comparison, ClassificationEvidence Evidence, CompensationPlan Plan,
    ImmutableArray<CostFiscalPeriodEvidence> Periods, string PlanFingerprint, CostMethod Method, string CurrencyCode, int MoneyDecimals, ImmutableArray<Guid> PositionWarehouses);

internal sealed partial class CostFlowSnapshotService
{
    private ClassificationEvidenceService? classification;

    /// <summary>
    /// Read-only back-dated planning for ONE item over its warehouse roots (matrix cells M1/M2, working baseline of the 3.8D decisions): the authorized
    /// consistent history snapshot, the comparison with the proposed ordinary event, the classification evidence of the postings whose cost changes, the
    /// compensation calculation and the fiscal check of every compensation date, all in ONE RepeatableRead read-only transaction with one command budget.
    /// Authorization: stock balance view on every warehouse root, plus the ledger permissions of the classification evidence for every member of the closure
    /// (any invisible member is one indistinguishable forbidden); both are re-checked live at the end. Every unsupported or inconsistent case fails closed.
    /// No journal, approval, persistence or execution exists here.
    /// </summary>
    internal Task<ConfigResult<CompensationPlanResult>> LoadCompensationPlanAsync(Guid user, Guid company, Guid item, ImmutableArray<Guid> warehouses,
        ProposedFlowOperation proposal, CancellationToken ct = default)
    {
        ClassificationEvidenceService.Loaded? loaded = null;
        var evidenceService = classification ??= new ClassificationEvidenceService(resolver, access);
        return RunBoundedReadAsync(user, company, async (budget, scopes, token) =>
        {
            if (item == Guid.Empty || warehouses.IsDefaultOrEmpty || warehouses.Distinct().Count() != warehouses.Length || warehouses.Any(w => w == Guid.Empty))
                return ConfigResult<CompensationPlanResult>.Fail(ConfigStatus.Conflict, "invalid_impact_roots");
            var permissions = await access.GetEffectivePermissionsAsync(user, company, budget.Sql.Reserve, token);
            if (!ClassificationEvidenceService.Bootstrap(permissions)) throw new Denied();
            foreach (var root in warehouses)
            {
                if (scopes.Add(root) && !await Can(user, company, root, budget.Sql, token)) throw new Denied();
                budget.Position(item, root);
            }
            await using var conn = await resolver.OpenAsync(company, budget.Sql.Reserve, token);
            budget.Sql.Reserve(2);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await using (var q = budget.Command("SET TRANSACTION READ ONLY", conn, tx)) await q.ExecuteNonQueryAsync(token);
            if (SnapshotEstablished is { } observe) await observe(conn, tx, token);
            var snapshots = await ReadItemsInTransactionAsync(conn, tx, company, [new CostFlowItemRoots(item, warehouses)], scopes,
                w => Can(user, company, w, budget.Sql, token), budget, null, token);
            if (!snapshots.IsOk) return ConfigResult<CompensationPlanResult>.Fail(snapshots.Status, snapshots.Code!);
            var comparison = CostFlowComparison.Calculate(snapshots.Value[0], proposal, token);

            // The postings of every operation whose cost changed (a change without its posting is refused by the calculator as unavailable).
            var operations = comparison.Changes.Select(c => c.OperationId).Distinct().ToArray();
            var postingIds = new List<Guid>();
            // A diagnostic that depends on the posting table is only specific for a caller who can see everything; otherwise it is the single forbidden.
            Exception Masked(Exception specific) => ClassificationEvidenceService.FullyVisible(permissions) ? specific : new Denied();
            if (operations.Length > 0)
            {
                try
                {
                await using var q = budget.Command($"SELECT {SP.Id} FROM {SP.Table} WHERE {SP.CostOperationId}=ANY(@ops) LIMIT @limit", conn, tx);
                q.Parameters.AddWithValue("ops", operations); q.Parameters.AddWithValue("limit", operations.Length + 1);
                await using var r = await q.ExecuteReaderAsync(token);
                while (await r.ReadAsync(token))
                {
                    if (postingIds.Count == operations.Length) throw Masked(Inconsistent());   // one posting per cost operation
                    postingIds.Add(r.GetGuid(0));
                }
                }
                catch (ReadCommandBudgetException) when (!ClassificationEvidenceService.FullyVisible(permissions)) { throw new Denied(); }
                if (postingIds.Count == 0) throw Masked(new CostingException("classification_unavailable"));   // changed operations without any posting
            }
            ClassificationEvidence evidence;
            if (postingIds.Count == 0) evidence = new([], [], [], [], [], "no-postings");   // only when nothing changed at all
            else
            {
                try
                {
                    var read = await evidenceService.ReadInTransactionAsync(conn, tx, company, [.. postingIds], permissions, budget.Sql, l => loaded = l, token);
                    evidence = read.Evidence;
                }
                catch (ClassificationEvidenceService.Denied) { throw new Denied(); }
                catch (ClassificationEvidenceService.Refusal refusal) { throw new CostingException(refusal.Code); }
            }
            var plan = CompensationCalculator.Calculate(company, comparison.ComparisonFingerprint, comparison.Changes, evidence);

            // Dates that must lie in an Open period: the proposed event, EVERY changed history entry (even without a ledger line, even if only its
            // trajectory changed) and every compensation line. Nothing is moved to another date.
            var entryDates = new Dictionary<long, DateOnly>();
            foreach (var entry in snapshots.Value[0].Entries) if (!entryDates.TryAdd(entry.Id, entry.Date)) throw Inconsistent();
            var dateSet = new HashSet<DateOnly>(plan.Lines.Select(l => l.Date)) { proposal.EffectiveDate };
            foreach (var change in comparison.Changes) dateSet.Add(entryDates.TryGetValue(change.EntryId, out var changed) ? changed : throw Inconsistent());
            var dates = dateSet.Order().ToArray();
            var periods = await ReadPeriodRows(conn, tx, dates, budget, token);
            CompensationDateGuard.Validate(plan, periods);   // includes ValidateDates for the compensation dates
            CompensationDateGuard.ValidateDates(dates, periods);
            var used = periods.Where(p => dates.Any(d => p.Start <= d && d <= p.End)).OrderBy(p => p.Start).ToImmutableArray();

            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                { Contract = "stock-compensation-plan-result-v1", Company = company, Plan = plan.Fingerprint, Periods = used }, ClassificationEvidenceService.Canonical))).ToLowerInvariant();
            token.ThrowIfCancellationRequested();
            var head = snapshots.Value[0];
            return ConfigResult<CompensationPlanResult>.Ok(new(comparison, evidence, plan, used, fingerprint, head.Method, head.CurrencyCode, head.MoneyDecimals, [.. head.Positions.Select(p => p.WarehouseId).Order()]));
        }, async (budget, token) =>
        {
            // Final live recheck of the ledger permissions on success AND on caught failures (stock roots are rechecked by RunBoundedReadAsync itself).
            var fresh = await access.GetEffectivePermissionsAsync(user, company, budget.Sql.Reserve, token);
            if (!ClassificationEvidenceService.Bootstrap(fresh)) return false;
            if (loaded is not null)
            {
                try { ClassificationEvidenceService.Authorize(fresh, loaded); }
                catch (ClassificationEvidenceService.Denied) { return false; }
            }
            return true;
        }, ct);
    }
}
