using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using P = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalPeriod;
using Y = Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear;
using D = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;

namespace Hasm.Database.IntegrationTests;

public class CostSourceFiscalTests : StockPostingTestBase
{
    private CostFlowSnapshotService Reader(CostFlowSnapshotLimits? limits = null) => new(Resolver, Rbac, limits);
    private async Task<FiscalPeriodView> Period(int month) => Assert.Single(Assert.Single(await Calendar.ListAsync(Company.Id)).Periods, p => p.Number == month);
    private async Task SetState(FiscalPeriodView period, int state)
    {
        // Same disposable SQL fixture convention as FiscalMigrationTests; no close guard is changed.
        if (state == 3)
            await Sql($"UPDATE {P.Table} SET {P.Status}=2,{P.Revision}={P.Revision}+1 WHERE {P.Id}='{period.Id}'");
        await Sql($"UPDATE {P.Table} SET {P.Status}={state},{P.FinalClosedBy}={(state == 3 ? $"'{Admin.Id}'" : "NULL")},{P.Revision}={P.Revision}+1 WHERE {P.Id}='{period.Id}'");
    }
    private Task<ConfigResult<CostSourceEvidence>> Read(CostFlowSnapshotService reader, StockDocumentView doc, Guid? user = null)
        => reader.LoadSourceEvidenceAsync(user ?? Admin.Id, Company.Id, doc.Id, doc.Revision, Amounts(10));
    private static CostSourceEvidence Value(ConfigResult<CostSourceEvidence> result)
    { Assert.True(result.IsOk, result.Code); return result.Value!; }
    private static void Reject(ConfigResult<CostSourceEvidence> result, string code)
    { Assert.Equal((ConfigStatus.Conflict, code), (result.Status, result.Code)); Assert.Null(result.Value); Assert.Null(result.Fields); }

    [PostgresFact]
    public async Task Open_source_binds_used_period_and_year_identity_revisions_without_writing()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); var period = await Period(3);
        var before = await Counts(); var audits = await Count(CompanySchema.BusinessAudit.Table);
        var evidence = Value(await Read(Reader(), doc)); var bound = Assert.Single(evidence.Periods);
        Assert.Equal((period.Id, period.YearId, period.Revision, period.StartDate, period.EndDate, FiscalPeriodState.Open),
            (bound.Id, bound.YearId, bound.Revision, bound.Start, bound.End, bound.State));
        Assert.Equal(doc.FiscalYearId, bound.YearId); Assert.False(bound.YearClosed); Assert.True(bound.YearRevision > 0);
        Assert.Equal(before, await Counts()); Assert.Equal(audits, await Count(CompanySchema.BusinessAudit.Table));
    }

    [PostgresTheory]
    [InlineData(2, PostingPeriodCheck.SoftClosed)]
    [InlineData(3, PostingPeriodCheck.FinalClosed)]
    public async Task The_source_date_has_no_implicit_closed_period_bypass(int state, string code)
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); await SetState(await Period(3), state);
        Reject(await Read(Reader(), doc), code);
        await using var conn = await Resolver.OpenAsync(Company.Id); await using var tx = await conn.BeginTransactionAsync();
        var normal = await FiscalPeriodGate.CheckAsync(conn, tx, D1, false);
        Assert.False(normal.Allowed); Assert.Equal(code, normal.Reason);
    }

    [PostgresTheory]
    [InlineData(2, PostingPeriodCheck.SoftClosed)]
    [InlineData(3, PostingPeriodCheck.FinalClosed)]
    public async Task Changed_running_balances_are_affected_even_when_the_historical_issue_cost_is_unchanged(int state, string code)
    {
        await PreparePosting(); await Receive(Fifo, 10, 100); var date = new DateOnly(2026, 4, 10); var issue = await IssueOf(Fifo, 1, date: date);
        var doc = await Approved(Receipt(lines: [L(Fifo)]));
        var snapshot = await Reader().LoadAsync(Admin.Id, Company.Id, Fifo.Id, W1.Id); Assert.True(snapshot.IsOk, snapshot.Code);
        var comparison = CostFlowComparison.Calculate(snapshot.Value!, new(Guid.NewGuid(), D1, CostOperationKind.Receipt, [new(W1.Id, 1, 10)]));
        var changed = Assert.Single(comparison.Changes, c => c.OperationId == issue.OperationId);
        Assert.Equal(changed.OriginalAmount, changed.ProposedAmount); Assert.NotEqual(changed.OriginalQuantityAfter, changed.ProposedQuantityAfter);
        await SetState(await Period(4), state); Reject(await Read(Reader(), doc), code);
    }

    [PostgresFact]
    public async Task Unchanged_earlier_closed_history_does_not_block_a_later_open_source()
    {
        await PreparePosting(); await Receive(Fifo, 10, 100);
        var doc = await Approved(Receipt(W1, "2026-04-10", L(Fifo))); await SetState(await Period(3), 3);
        var evidence = Value(await Read(Reader(), doc));
        Assert.Empty(Assert.Single(evidence.Comparisons).Changes); Assert.Equal((await Period(4)).Id, Assert.Single(evidence.Periods).Id);
    }

    [PostgresFact]
    public async Task An_unaffected_intervening_period_does_not_block_or_change_the_fingerprint()
    {
        await PreparePosting(); await Receive(Fifo, 10, 100); await IssueOf(Fifo, 1, date: new DateOnly(2026, 5, 10));
        var doc = await Approved(Receipt(lines: [L(Fifo)])); var before = Value(await Read(Reader(), doc));
        Assert.Equal(2, before.Periods.Length); var intervening = await Period(4); await SetState(intervening, 3);
        var after = Value(await Read(Reader(), doc)); Assert.DoesNotContain(after.Periods, p => p.Id == intervening.Id);
        Assert.Equal(before.EvidenceFingerprint, after.EvidenceFingerprint);
    }

    [PostgresFact]
    public async Task A_reopened_used_period_invalidates_the_old_evidence_even_when_it_is_open_again()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); var before = Value(await Read(Reader(), doc));
        var period = await Period(3); await SetState(period, 2);
        Ok(await Calendar.ReopenAsync(Admin.Id, Company.Id, period.Id, period.Revision + 1));
        var after = Value(await Read(Reader(), doc)); Assert.Equal(FiscalPeriodState.Open, Assert.Single(after.Periods).State);
        Assert.NotEqual(before.EvidenceFingerprint, after.EvidenceFingerprint); Assert.True(after.Periods[0].Revision > before.Periods[0].Revision);
    }

    [PostgresFact]
    public async Task Closing_and_approved_reopening_a_used_year_changes_its_bound_revision()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); var before = Value(await Read(Reader(), doc));
        var year = Assert.Single(await Calendar.ListAsync(Company.Id));
        foreach (var period in year.Periods) await SetState(period, 3);
        var management = new FiscalCalendarManagementService(Rbac, Calendar);
        var closed = Ok(await management.CloseYearAsync(Admin.Id, Company.Id, year.Id, year.Revision));
        var approver = await NewUser("source-fiscal-approver");
        Assert.True((await Rbac.GrantMembershipAsync(Admin.Id, Company.Id, approver.Login)).IsOk);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantFiscalApproverOfflineAsync(approver.Login, Company.Id));
        var reason = new ReasonInput("Correction found in source fiscal evidence review");
        var request = Ok(await management.RequestYearReopenAsync(Admin.Id, Company.Id, year.Id, closed.Revision, reason));
        Ok(await management.ApproveRequestAsync(approver.Id, Company.Id, request.Id, request.Revision));
        var sourcePeriod = await Period(3);
        var periodRequest = Ok(await management.RequestPeriodReopenAsync(Admin.Id, Company.Id, sourcePeriod.Id, sourcePeriod.Revision, reason));
        Ok(await management.ApproveRequestAsync(approver.Id, Company.Id, periodRequest.Id, periodRequest.Revision));
        sourcePeriod = await Period(3); Ok(await management.ReopenAsync(Admin.Id, Company.Id, sourcePeriod.Id, sourcePeriod.Revision));
        var after = Value(await Read(Reader(), doc)); Assert.NotEqual(before.EvidenceFingerprint, after.EvidenceFingerprint);
        Assert.True(after.Periods[0].YearRevision > before.Periods[0].YearRevision);
    }

    [PostgresFact]
    public async Task Concurrent_close_is_consistent_in_the_old_snapshot_and_refused_in_a_fresh_read()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)])); var period = await Period(3);
        var reader = Reader(); var observed = false;
        reader.SnapshotEstablished = async (conn, tx, token) =>
        {
            await using var check = new NpgsqlCommand("SHOW transaction_read_only", conn, tx);
            Assert.Equal("on", (string)(await check.ExecuteScalarAsync(token))!); observed = true;
            await SetState(period, 3);
        };
        var snapshot = Value(await Read(reader, doc)); Assert.True(observed); Assert.Equal(FiscalPeriodState.Open, Assert.Single(snapshot.Periods).State);
        Reject(await Read(Reader(), doc), PostingPeriodCheck.FinalClosed); // Read evidence never authorizes later execution.
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_source_visibility_revocation_masks_success_and_fiscal_failure(bool closed)
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)]));
        if (closed) await SetState(await Period(3), 3);
        var (viewer, _) = await Member([PermissionCatalog.Codes.StockBalanceView], ScopeKind.Company, null);
        var role = await Rbac.CreateRoleAsync(Admin.Id, Company.Id, "fiscal-source-view", [PermissionCatalog.Codes.StockDocsView]); Assert.True(role.IsOk);
        var assignment = await Rbac.AssignRoleAsync(Admin.Id, Company.Id, viewer.Id, role.Value!.Id, ScopeKind.Warehouse, W1.Id); Assert.True(assignment.IsOk);
        var reader = Reader(); reader.BeforeFinalAuthorization = async _ =>
            Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment.Value!.Id)).IsOk);
        var result = await Read(reader, doc, viewer.Id);
        Assert.Equal((ConfigStatus.Forbidden, "forbidden"), (result.Status, result.Code)); Assert.Null(result.Value); Assert.Null(result.Fields);
    }

    [PostgresFact]
    public async Task Both_transfer_legs_are_affected_and_their_closed_period_is_a_barrier()
    {
        await PreparePosting(); await Receive(Avg, 10, 100); var movement = await Move(Avg, 1, date: new DateOnly(2026, 4, 10));
        var doc = await Approved(Receipt(lines: [L(Avg)]));
        var snapshot = await Reader().LoadAsync(Admin.Id, Company.Id, Avg.Id, W1.Id); Assert.True(snapshot.IsOk);
        var comparison = CostFlowComparison.Calculate(snapshot.Value!, new(Guid.NewGuid(), D1, CostOperationKind.Receipt, [new(W1.Id, 1, 30)]));
        Assert.Equal(2, comparison.Changes.Count(c => c.OperationId == movement.OperationId));
        Assert.Equal(2, comparison.Changes.Where(c => c.OperationId == movement.OperationId).Select(c => c.WarehouseId).Distinct().Count());
        await SetState(await Period(4), 3); Reject(await Read(Reader(), doc), PostingPeriodCheck.FinalClosed);
    }

    [PostgresFact]
    public async Task A_changed_entry_without_a_period_is_not_silently_omitted()
    {
        await PreparePosting(); await Receive(Fifo, 10, 100); await IssueOf(Fifo, 1, date: new DateOnly(2027, 1, 10));
        var doc = await Approved(Receipt(lines: [L(Fifo)])); Reject(await Read(Reader(), doc), PostingPeriodCheck.NoPeriod);
    }

    [PostgresFact]
    public async Task Normal_posting_retains_its_period_row_lock()
    {
        await PreparePosting(); var period = await Period(3);
        await using var conn = await Resolver.OpenAsync(Company.Id); await using var tx = await conn.BeginTransactionAsync();
        Assert.True((await FiscalPeriodGate.CheckAsync(conn, tx, D1, false)).Allowed);
        await using var peer = await Resolver.OpenAsync(Company.Id); await using var peerTx = await peer.BeginTransactionAsync();
        await using var probe = new NpgsqlCommand($"SELECT {P.Id} FROM {P.Table} WHERE {P.Id}=@id FOR UPDATE NOWAIT", peer, peerTx);
        probe.Parameters.AddWithValue("id", period.Id);
        Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(() => probe.ExecuteScalarAsync())).SqlState);
    }

    [PostgresFact]
    public async Task Fiscal_rows_are_reserved_before_materialization_using_the_shared_budget()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)]));
        // Header1 + line1 + validation4 + fiscal1; empty history contributes no discovery rows.
        Reject(await Read(Reader(new(DiscoveryRows: 6)), doc), "impact_history_too_large");
        Assert.Single(Value(await Read(Reader(new(DiscoveryRows: 7)), doc)).Periods);
    }

    [PostgresFact]
    public async Task The_database_does_not_allow_rebinding_an_approved_source_to_another_fiscal_year()
    {
        await PreparePosting(); var doc = await Approved(Receipt(lines: [L(Fifo)]));
        var other = Ok(await Calendar.CreateYearAsync(Admin.Id, Company.Id, new("FY2027", new DateOnly(2027, 1, 1), "monthly")));
        var error = await Assert.ThrowsAsync<PostgresException>(() => Sql($"UPDATE {D.Table} SET {D.FiscalYearId}='{other.Id}',{D.Revision}={D.Revision}+1 WHERE {D.Id}='{doc.Id}'"));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(doc.FiscalYearId, Assert.Single(Value(await Read(Reader(), doc)).Periods).YearId);
    }
}
