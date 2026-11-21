using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Npgsql;
using AB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.DocumentApprovalBinding;
using AD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalDecision;
using AG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ApprovalStaging;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialPolicy;

namespace Hasm.Infrastructure.Commercial;

/// <summary>The approval plan of a submitted commercial document for its current revision, with the progress of every stage, and the decisions cast so far. Empty plan: no policy matches (D-141).</summary>
public sealed record CommercialApprovalProgress(IReadOnlyList<ApprovalStageProgress> Plan, IReadOnlyList<ApprovalDecisionView> Decisions);

/// <summary>
/// Approval policies for commercial documents (Phase 3.13 S5a, decisions D-140 and D-141). The ordered policy engine of 3.2C decides from the SEALED total at submit: the plan is read from the SQL functions of the
/// commercial-policies migration (the same functions the database gate uses), the plan of a submitted revision is bound as an append-only floor, every decision is cast for the first open stage, and the document
/// becomes approved only when the plan of its revision is satisfied. With NO matching policy the plan is empty and the single independent approval of S1b applies unchanged.
/// </summary>
public sealed partial class CommercialDocumentService
{
    /// <summary>Snapshots the plan this revision was submitted under (an append-only floor: a policy changed later can only add requirements for it). Writes nothing when no policy matches.</summary>
    private async Task BindPlanAsync(CompanyAuditedWork work, Guid documentId, string docType, long revision, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {AB.Table} ({AB.DocType}, {AB.DocumentId}, {AB.DocumentRevision}, {AB.StageNo}, {AB.Required}, {AB.RoleId}, {AB.PolicyId}, {AB.PolicyRevision}, {AB.BoundAtUtc}) " +
            $"SELECT @t, @d, @r, pl.stage_no, pl.required, pl.role_id, pk.policy_id, pk.policy_revision, @now FROM {CP.FnPlan}(@d, @r) pl LEFT JOIN {CP.FnPick}(@d) pk ON true " +
            $"ON CONFLICT ({AB.DocType}, {AB.DocumentId}, {AB.DocumentRevision}, {AB.StageNo}) DO NOTHING", work.Connection, work.Transaction);
        cmd.Parameters.AddWithValue("t", docType); cmd.Parameters.AddWithValue("d", documentId); cmd.Parameters.AddWithValue("r", revision); cmd.Parameters.AddWithValue("now", Now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<ApprovalStageProgress>> PlanAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid documentId, string docType, long revision, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT pl.stage_no, pl.required, pl.role_id, (SELECT count(DISTINCT d.{AD.Approver}) FROM {AD.Table} d WHERE d.{AD.DocType} = @t AND d.{AD.DocumentId} = @d AND d.{AD.DocumentRevision} = @r AND d.{AD.Decision} = 1 AND d.{AG.DecisionStage} = pl.stage_no) " +
            $"FROM {CP.FnPlan}(@d, @r) pl ORDER BY pl.stage_no", conn, tx);
        cmd.Parameters.AddWithValue("t", docType); cmd.Parameters.AddWithValue("d", documentId); cmd.Parameters.AddWithValue("r", revision);
        var list = new List<ApprovalStageProgress>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ApprovalStageProgress(r.GetInt32(0), r.GetInt32(1), (int)r.GetInt64(3), r.IsDBNull(2) ? null : r.GetGuid(2)));
        return list;
    }

    /// <summary>The approval plan of the document's current revision and the decisions cast on it (empty plan when the document is not submitted or no policy matches).</summary>
    public async Task<CommercialApprovalProgress?> ApprovalProgressAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        var doc = await LoadAsync(conn, null, id, false, false, ct);
        if (doc is null) return null;
        var plan = doc.Status == CommercialStatus.Submitted ? await PlanAsync(conn, null, id, doc.DocType, doc.Revision, ct) : [];
        var decisions = (await DocumentApprovals.ListAsync(conn, null, doc.DocType, id, ct)).Where(d => d.DocumentRevision == doc.Revision).ToList();
        return new CommercialApprovalProgress(plan, decisions);
    }
}
