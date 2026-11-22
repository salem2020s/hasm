using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 3.8D executor slice E1: the pure derivation of an epoch's write plan (<see cref="EpochSchedule"/>). The fixture builds a history with the REAL replay arithmetic (<see cref="CostReplay"/>), inserts a
/// proposal exactly as <c>CostFlowComparison</c> does, and hands the result to the derivation; the assertions are independent of it (signed sums, mirrors, orders, bijections). No database.
/// </summary>
public class EpochScheduleTests
{
    private static readonly DateOnly Day0 = new(2026, 3, 10);
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static readonly Guid W1 = Id(1001), W2 = Id(1002), W3 = Id(1003);

    private sealed record Op(int Day, CostOperationKind Kind, Guid Warehouse, decimal Quantity, decimal? Amount = null, Guid? Destination = null);

    /// <summary>The snapshot and the comparison of one proposal over one history, built the way the planner builds them (same calls of the same pure core).</summary>
    private sealed record Fixture(CostFlowSnapshot Snapshot, CostFlowComparisonResult Comparison);

    private static Fixture Build(CostMethod method, IReadOnlyList<Op> history, Op proposal, bool reverseEntryIds = false)
    {
        var warehouses = history.SelectMany(o => new[] { o.Warehouse, o.Destination ?? o.Warehouse }).Append(proposal.Warehouse).Append(proposal.Destination ?? proposal.Warehouse).Distinct().OrderBy(w => w.ToString("N"), StringComparer.Ordinal).ToImmutableArray();
        var positionGuid = warehouses.ToDictionary(w => w, w => Id(2000 + Array.IndexOf(warehouses.ToArray(), w)));
        var ordinals = new Dictionary<Guid, long>();
        long Next(Guid w) { ordinals[w] = ordinals.GetValueOrDefault(w) + 1; return ordinals[w]; }
        var entryIds = new List<long>();
        var counter = 0L;
        long NextId() { counter++; return reverseEntryIds ? 1000 - counter : counter; }       // reversed ids run against the replay order: a malformed history the derivation must refuse
        var blocks = ImmutableArray.CreateBuilder<ReplayFlowBlock>();
        var entryPlan = new List<(long Id, Guid Op, CostOperationKind Kind, CostEntryKind EntryKind, int Direction, Guid Warehouse, decimal Quantity, DateOnly Date, Guid EventId, long Ordinal)>();
        for (var i = 0; i < history.Count; i++)
        {
            var op = history[i]; var opId = Id(i + 1); var date = Day0.AddDays(op.Day);
            if (op.Kind == CostOperationKind.Transfer)
            {
                long a = NextId(), b = NextId();
                var oa = Next(op.Warehouse); var ob = Next(op.Destination!.Value);
                entryPlan.Add((a, opId, op.Kind, CostEntryKind.TransferOut, -1, op.Warehouse, op.Quantity, date, opId, oa));
                entryPlan.Add((b, opId, op.Kind, CostEntryKind.TransferIn, 1, op.Destination!.Value, op.Quantity, date, opId, ob));
                blocks.Add(new ReplayFlowBlock(new(opId, date, op.Kind, [new(op.Warehouse, oa), new(op.Destination!.Value, ob)]),
                    [new(opId, 1, oa, new(opId, op.Kind, op.Warehouse, op.Quantity, null, op.Destination), ob)]));
            }
            else
            {
                var entry = NextId(); var ordinal = Next(op.Warehouse);
                var eventId = CostHistoryEntry.SimulationId(entry);
                entryPlan.Add((entry, opId, op.Kind, op.Kind == CostOperationKind.Receipt ? CostEntryKind.Receipt : CostEntryKind.Issue, op.Kind == CostOperationKind.Receipt ? 1 : -1, op.Warehouse, op.Quantity, date, eventId, ordinal));
                blocks.Add(new ReplayFlowBlock(new(opId, date, op.Kind, [new(op.Warehouse, ordinal)]), [new(opId, 1, ordinal, new(eventId, op.Kind, op.Warehouse, op.Quantity, op.Amount))]));
            }
        }
        var baseline = CostReplay.CalculateBlocks(method, 2, warehouses, blocks.ToImmutable());
        var entries = ImmutableArray.CreateBuilder<CostFlowEntry>();
        var sequence = new Dictionary<Guid, long>();
        foreach (var step in baseline.Flow.Steps)
            foreach (var leg in step.Legs)
            {
                var plan = entryPlan.Single(e => e.EventId == step.EventId && e.Warehouse == leg.PositionId);
                sequence[leg.PositionId] = sequence.GetValueOrDefault(leg.PositionId) + 1;
                entries.Add(new CostFlowEntry(plan.Id, plan.Op, positionGuid[plan.Warehouse], Id(3000), plan.Warehouse, sequence[leg.PositionId], plan.Date, plan.EntryKind, plan.Direction, plan.Quantity,
                    leg.Step.Amount, leg.Step.QuantityAfter, leg.Step.ValueAfter, plan.Kind, plan.Date, null, null, false, false));
            }
        var positions = baseline.Flow.Positions.Select(p => new CostFlowPosition(p.PositionId, positionGuid[p.PositionId], method, sequence.GetValueOrDefault(p.PositionId), null, p.Quantity, p.Value)).ToImmutableArray();
        var snapshot = new CostFlowSnapshot(Id(4000), Id(3000), method, "USD", 2, positions, entries.ToImmutable(), [], [], [], blocks.ToImmutable(), "history-fingerprint");

        // the proposal, inserted as CostFlowComparison does
        var counts = new Dictionary<Guid, long>();
        long NextProposed(Guid w) { counts[w] = counts.GetValueOrDefault(w) + 1; return counts[w]; }
        var proposalId = Id(900);
        var line = new ProposedFlowLine(proposal.Warehouse, proposal.Quantity, proposal.Amount, proposal.Destination);
        var pOrdinal = NextProposed(proposal.Warehouse); long? pDest = proposal.Destination is { } d ? NextProposed(d) : null;
        var childId = proposal.Kind == CostOperationKind.Transfer ? proposalId : Id(901);
        var proposedBlock = new ReplayFlowBlock(new(proposalId, Day0.AddDays(proposal.Day), proposal.Kind, [.. counts.OrderBy(p => p.Key.ToString("N"), StringComparer.Ordinal).Select(p => new ReplayOrderLeg(p.Key, 1, p.Value))]),
            [new(proposalId, 1, pOrdinal, new(childId, proposal.Kind, proposal.Warehouse, proposal.Quantity, proposal.Amount, proposal.Destination), pDest)]);
        var revised = CostReplay.CalculateBlocksWithInsertion(method, 2, warehouses, blocks.ToImmutable(), proposedBlock).Flow;
        var original = entries.ToDictionary(e => (e.OperationKind == CostOperationKind.Transfer ? e.OperationId : CostHistoryEntry.SimulationId(e.Id), e.WarehouseId));
        var changes = ImmutableArray.CreateBuilder<CostFlowEntryImpact>();
        var proposedSteps = ImmutableArray.CreateBuilder<ReplayFlowStep>();
        foreach (var step in revised.Steps)
        {
            if (step.EventId == childId) { proposedSteps.Add(step); continue; }
            foreach (var leg in step.Legs)
            {
                var entry = original[(step.EventId, leg.PositionId)];
                if ((entry.Amount, entry.QuantityAfter, entry.ValueAfter) != (leg.Step.Amount, leg.Step.QuantityAfter, leg.Step.ValueAfter))
                    changes.Add(new(entry.Id, entry.OperationId, entry.WarehouseId, entry.Amount, leg.Step.Amount, entry.QuantityAfter, leg.Step.QuantityAfter, entry.ValueAfter, leg.Step.ValueAfter));
            }
        }
        var finals = revised.Positions.ToDictionary(p => p.PositionId);
        var impacts = positions.Select(p => new CostFlowPositionImpact(p.WarehouseId, p.Quantity, finals[p.WarehouseId].Quantity, p.Value, finals[p.WarehouseId].Value)).ToImmutableArray();
        var comparison = new CostFlowComparisonResult("history-fingerprint", "comparison-fingerprint", new ProposedFlowOperation(proposalId, Day0.AddDays(proposal.Day), proposal.Kind, [line]),
            proposedSteps.ToImmutable(), [.. changes.OrderBy(c => c.EntryId)], impacts, revised);
        return new Fixture(snapshot, comparison);
    }

    private static Op Receipt(int day, Guid w, decimal q, decimal amount) => new(day, CostOperationKind.Receipt, w, q, amount);
    private static Op Issue(int day, Guid w, decimal q) => new(day, CostOperationKind.Issue, w, q);
    private static Op Transfer(int day, Guid from, Guid to, decimal q) => new(day, CostOperationKind.Transfer, from, q, null, to);

    // ------------------------------------------------------------------------------------------------------------ worked examples

    [Fact]
    public void Average_a_backdated_receipt_storno_and_rebook_the_later_issue_at_the_new_average()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        Assert.Equal([EpochRole.Storno, EpochRole.Event, EpochRole.Rebook], plan.Operations.Select(o => o.Role));
        var storno = plan.Operations[0].Entries.Single(); var evt = plan.Operations[1].Entries.Single(); var rebook = plan.Operations[2].Entries.Single();
        // the issue (entry 2, 40.00 at the old average of 10.00) is taken back on its own date: +4 / 40.00, the position is back at 10 / 100.00
        Assert.Equal((CostEntryKind.Issue, 1, 4m, Day0.AddDays(2), 40m, 10m, 100m, 2L), (storno.Kind, storno.Direction, storno.Quantity, storno.Date, storno.Amount, storno.QuantityAfter, storno.ValueAfter, storno.OriginalEntryId!.Value));
        // the event on its own date: 20 / 300.00
        Assert.Equal((CostEntryKind.Receipt, 1, Day0.AddDays(1), 200m, 20m, 300m, (long?)null), (evt.Kind, evt.Direction, evt.Date, evt.Amount, evt.QuantityAfter, evt.ValueAfter, evt.OriginalEntryId));
        // the issue again, at the new average 15.00 = 60.00, on its ORIGINAL date: 16 / 240.00
        Assert.Equal((CostEntryKind.Issue, -1, 4m, Day0.AddDays(2), 60m, 16m, 240m, 2L), (rebook.Kind, rebook.Direction, rebook.Quantity, rebook.Date, rebook.Amount, rebook.QuantityAfter, rebook.ValueAfter, rebook.OriginalEntryId!.Value));
        Assert.Equal([3L, 4L, 5L], plan.Operations.SelectMany(o => o.Entries).Select(e => e.Sequence));      // the position had 2 entries: storno 3, event 4, rebook 5
        var line = Assert.Single(plan.Lineage);
        Assert.Equal((2L, 0, 0, 2, 0), (line.OriginalEntryId, line.StornoOperation, line.StornoEntry, line.RebookOperation, line.RebookEntry));
        Assert.Equal((16m, 240m), (plan.ExpectedPositions.Single().Quantity, plan.ExpectedPositions.Single().Value));
    }

    [Fact]
    public void Fifo_the_same_amount_is_rebooked_when_only_the_running_figures_change()
    {
        // FIFO: the issue still consumes the first layer (10.00 a piece), so its amount stays 40.00 but its quantity-after changes: it is still stornoed and rebooked
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        var rebook = plan.Operations[2].Entries.Single();
        Assert.Equal((40m, 16m, 260m), (rebook.Amount, rebook.QuantityAfter, rebook.ValueAfter));
        Assert.Equal((16m, 260m), (plan.ExpectedPositions.Single().Quantity, plan.ExpectedPositions.Single().Value));
    }

    [Fact]
    public void An_event_with_nothing_after_it_is_an_event_only_epoch()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100)], Receipt(1, W1, 5, 50));
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        Assert.Equal(EpochRole.Event, Assert.Single(plan.Operations).Role);
        Assert.Empty(plan.Lineage);
    }

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void A_two_warehouse_transfer_chain_in_both_directions_with_equal_dates_keeps_its_original_sequence(CostMethod method)
    {
        // N29, derivation part: W1 -> W2 and W2 -> W1 on the same day, then an issue; the event is back-dated before all of them
        var f = Build(method, [Receipt(0, W1, 10, 100), Receipt(0, W2, 10, 150), Transfer(2, W1, W2, 3), Transfer(2, W2, W1, 4), Issue(3, W1, 2)], Receipt(method == CostMethod.Fifo ? -1 : 1, W1, 10, 200));   // FIFO: before every layer, so the consumed layers change
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        var stornoOps = plan.Operations.Where(o => o.Role == EpochRole.Storno).ToList();
        var rebookOps = plan.Operations.Where(o => o.Role == EpochRole.Rebook).ToList();
        // storno: highest entry id first; the later transfer, then the earlier one, in descending entry order
        Assert.Equal(stornoOps.SelectMany(o => o.Entries).Select(e => e.OriginalEntryId!.Value).OrderByDescending(x => x), stornoOps.SelectMany(o => o.Entries).Select(e => e.OriginalEntryId!.Value));
        // rebook: ascending (date, id): the same-date legs keep their original sequence
        var rebooked = rebookOps.SelectMany(o => o.Entries).Select(e => (e.Date, Id: e.OriginalEntryId!.Value)).ToList();
        Assert.Equal(rebooked.OrderBy(x => x.Date).ThenBy(x => x.Id), rebooked);
        Assert.Equal(f.Comparison.Changes.Select(c => c.EntryId).OrderBy(x => x), plan.Lineage.Select(l => l.OriginalEntryId).OrderBy(x => x));
        AssertOracle(f, plan);
    }

    // ------------------------------------------------------------------------------------------------------------ refusals

    [Fact]
    public void A_transfer_with_exactly_one_changed_leg_is_refused()
    {
        // average, same unit cost: the out leg only shifts its running quantity, the in leg at the other warehouse does not change at all
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Transfer(2, W1, W2, 2)], Receipt(1, W1, 10, 100));
        Assert.Single(f.Comparison.Changes);
        Assert.Equal("epoch_partial_transfer_unsupported", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison)).Code);
    }

    [Fact]
    public void Changed_entries_that_are_not_a_contiguous_suffix_of_their_position_are_refused()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 1), Issue(3, W1, 1)], Receipt(1, W1, 10, 200));
        var withoutLast = f.Comparison with { Changes = [.. f.Comparison.Changes.Where(c => c.EntryId != f.Comparison.Changes.Max(x => x.EntryId))] };
        Assert.Equal("epoch_not_a_suffix", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, withoutLast)).Code);
    }

    [Fact]
    public void A_reversal_in_the_history_an_unsupported_kind_and_an_entry_outside_the_closure_are_refused()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        CostFlowSnapshot With(Func<CostFlowEntry, CostFlowEntry> change) => f.Snapshot with { Entries = [.. f.Snapshot.Entries.Select(change)] };
        Assert.Equal("epoch_reversal_in_history", Assert.Throws<CostingException>(() => EpochSchedule.Derive(With(e => e.Id == 2 ? e with { HasReversal = true } : e), f.Comparison)).Code);
        Assert.Equal("impact_history_unsupported", Assert.Throws<CostingException>(() => EpochSchedule.Derive(With(e => e.Id == 2 ? e with { OperationKind = CostOperationKind.Reversal } : e), f.Comparison)).Code);
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(With(e => e.Id == 2 ? e with { WarehouseId = Id(9999) } : e), f.Comparison)).Code);
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison with { Changes = [.. f.Comparison.Changes, f.Comparison.Changes[0]] })).Code);   // a change twice
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison with { Changes = [f.Comparison.Changes[0] with { EntryId = 77 }] })).Code);   // a change of no entry
    }

    [Fact]
    public void Malformed_input_is_refused_as_an_invalid_snapshot_or_an_unsupported_proposal()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        Assert.Equal("invalid_impact_snapshot", Assert.Throws<CostingException>(() => EpochSchedule.Derive(null!, f.Comparison)).Code);
        Assert.Equal("invalid_impact_snapshot", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, null!)).Code);
        Assert.Equal("invalid_impact_snapshot", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot with { Method = (CostMethod)9 }, f.Comparison)).Code);
        Assert.Equal("impact_history_unsupported", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison with { Proposal = f.Comparison.Proposal with { Kind = CostOperationKind.Reversal } })).Code);
        Assert.Throws<OperationCanceledException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison, new CancellationToken(true)));
    }

    [Fact]
    public void An_order_that_is_not_ascending_by_date_and_original_entry_id_is_refused()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 1), Issue(2, W1, 1)], Receipt(1, W1, 10, 200), reverseEntryIds: true);
        Assert.Equal("epoch_order_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison)).Code);
    }

    [Fact]
    public void A_replay_whose_figures_or_final_state_do_not_match_the_plan_is_refused()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        // the revised trace states another amount for the rewritten issue: the signed-sum ledger proof catches it
        var steps = f.Comparison.RevisedTrace.Steps.Select(s => s.EventId == CostHistoryEntry.SimulationId(2) ? s with { Legs = [s.Legs[0] with { Step = s.Legs[0].Step with { Amount = s.Legs[0].Step.Amount + 1m } }] } : s).ToImmutableArray();
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison with { RevisedTrace = f.Comparison.RevisedTrace with { Steps = steps } })).Code);
        // the revised trace states another running quantity for the rewritten issue while the totals stay right: the figure the entry will carry is checked entry by entry
        var figures = f.Comparison.RevisedTrace.Steps.Select(s => s.EventId == CostHistoryEntry.SimulationId(2) ? s with { Legs = [s.Legs[0] with { Step = s.Legs[0].Step with { QuantityAfter = s.Legs[0].Step.QuantityAfter + 1m } }] } : s).ToImmutableArray();
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison with { RevisedTrace = f.Comparison.RevisedTrace with { Steps = figures } })).Code);
        // the comparison states another final value
        var impacts = f.Comparison.Positions.Select(p => p with { ProposedValue = p.ProposedValue + 1m }).ToImmutableArray();
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, f.Comparison with { Positions = impacts })).Code);
        // a baseline smaller than the history it claims: taking back the receipt would drive the position below zero
        var g = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Receipt(2, W1, 5, 50), Issue(3, W1, 3)], Receipt(1, W1, 10, 200));
        var dropped = g.Snapshot with { Positions = [.. g.Snapshot.Positions.Select(p => p with { Quantity = 0m, Value = 0m })] };
        Assert.Equal("epoch_storno_negative", Assert.Throws<CostingException>(() => EpochSchedule.Derive(dropped, g.Comparison)).Code);
    }

    [Fact]
    public void An_entry_of_another_item_is_refused()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var other = f.Snapshot with { Entries = [.. f.Snapshot.Entries.Select(e => e.Id == 2 ? e with { ItemId = Id(7777) } : e)] };
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(other, f.Comparison)).Code);
    }

    [Fact]
    public void The_end_state_must_cover_every_closure_position_in_the_comparison_and_in_the_revised_trace()
    {
        // two positions through a transfer: both are in the closure and both must be proven
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Transfer(1, W1, W2, 4)], Receipt(-1, W1, 10, 200));
        Assert.Equal(2, f.Comparison.Positions.Length);
        var noImpact = f.Comparison with { Positions = [.. f.Comparison.Positions.Where(p => p.WarehouseId == W1)] };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, noImpact)).Code);
        var noTrace = f.Comparison with { RevisedTrace = f.Comparison.RevisedTrace with { Positions = [.. f.Comparison.RevisedTrace.Positions.Where(p => p.PositionId == W1)] } };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, noTrace)).Code);
        var foreign = f.Comparison with { Positions = [.. f.Comparison.Positions, new CostFlowPositionImpact(Id(8888), 0m, 0m, 0m, 0m)] };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, foreign)).Code);
        var twice = f.Comparison with { Positions = [.. f.Comparison.Positions, f.Comparison.Positions[0]] };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, twice)).Code);
        var traceTwice = f.Comparison with { RevisedTrace = f.Comparison.RevisedTrace with { Positions = [.. f.Comparison.RevisedTrace.Positions, f.Comparison.RevisedTrace.Positions[0]] } };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, traceTwice)).Code);
        // the revised trace proves another end for one position than the plan reaches (the comparison agrees with the plan)
        var otherEnd = f.Comparison with { RevisedTrace = f.Comparison.RevisedTrace with { Positions = [.. f.Comparison.RevisedTrace.Positions.Select(p => p.PositionId == W2 ? p with { Value = p.Value + 1m } : p)] } };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(f.Snapshot, otherEnd)).Code);
        Assert.NotNull(EpochSchedule.Derive(f.Snapshot, f.Comparison));      // the untouched input is accepted
    }

    [Fact]
    public void The_written_order_of_the_rebooks_is_checked_not_only_the_trace_order()
    {
        // one operation holds entries 1 and 4 (a two-line document) with the transfer's entries between them: in the trace the entries are ascending, but WRITTEN grouped by operation they are 1, 4, 2, 3
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Transfer(1, W1, W2, 4), Receipt(2, W1, 5, 50)], Receipt(-1, W1, 10, 200));
        var first = f.Snapshot.Entries.Single(e => e.Id == 1).OperationId;
        var tampered = f.Snapshot with { Entries = [.. f.Snapshot.Entries.Select(e => e.Id == 4 ? e with { OperationId = first } : e)] };
        Assert.Equal("epoch_order_inconsistent", Assert.Throws<CostingException>(() => EpochSchedule.Derive(tampered, f.Comparison)).Code);
    }

    [Fact]
    public void The_derivation_is_deterministic_and_its_fingerprint_follows_every_figure()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var a = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        Assert.Equal(a.Fingerprint, EpochSchedule.Derive(f.Snapshot, f.Comparison).Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", a.Fingerprint);
        var other = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 300));
        Assert.NotEqual(a.Fingerprint, EpochSchedule.Derive(other.Snapshot, other.Comparison).Fingerprint);
    }

    // ------------------------------------------------------------------------------------------------------------ property test

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void For_random_histories_and_proposals_the_plan_meets_every_invariant_or_is_refused_with_a_known_code(CostMethod method)
    {
        var allowedRefusals = new HashSet<string> { "epoch_partial_transfer_unsupported" };
        int derived = 0, refused = 0, skipped = 0;
        for (var seed = 1; seed <= 400; seed++)
        {
            var rnd = new Random(seed * (method == CostMethod.Fifo ? 7 : 13));
            var warehouses = new[] { W1, W2, W3 }.Take(rnd.Next(1, 4)).ToArray();
            var stock = warehouses.ToDictionary(w => w, _ => 0m);
            var history = new List<Op>();
            var day = 0;
            for (var n = rnd.Next(2, 9); n > 0; n--)
            {
                day += rnd.Next(0, 3);
                var w = warehouses[rnd.Next(warehouses.Length)];
                var roll = rnd.Next(100);
                if (roll < 45 || stock[w] < 1m) { var q = rnd.Next(1, 11); history.Add(Receipt(day, w, q, rnd.Next(10, 201))); stock[w] += q; }
                else if (roll < 75 || warehouses.Length == 1) { var q = rnd.Next(1, (int)stock[w] + 1); history.Add(Issue(day, w, q)); stock[w] -= q; }
                else { var to = warehouses.Where(x => x != w).ElementAt(rnd.Next(warehouses.Length - 1)); var q = rnd.Next(1, (int)stock[w] + 1); history.Add(Transfer(day, w, to, q)); stock[w] -= q; stock[to] += q; }
            }
            var pw = warehouses[rnd.Next(warehouses.Length)];
            var proposal = rnd.Next(100) < 75 ? Receipt(rnd.Next(-1, day + 2), pw, rnd.Next(1, 8), rnd.Next(10, 201)) : Issue(rnd.Next(-1, day + 2), pw, rnd.Next(1, 3));
            Fixture f;
            try { f = Build(method, history, proposal); }
            catch (CostingException) { skipped++; continue; }       // a shortage or another refusal of the planner itself: nothing to derive
            try
            {
                var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
                AssertOracle(f, plan);
                derived++;
            }
            catch (CostingException e) when (allowedRefusals.Contains(e.Code)) { refused++; }
        }
        Assert.True(derived >= 120, $"derived {derived}, refused {refused}, skipped {skipped}: the generator must exercise the derivation");
    }

    /// <summary>The independent oracle: nothing here calls the derivation's own logic, only signed sums and the data of the fixture.</summary>
    private static void AssertOracle(Fixture f, EpochScheduleResult plan)
    {
        var entries = f.Snapshot.Entries.ToDictionary(e => e.Id);
        var changes = f.Comparison.Changes.ToDictionary(c => c.EntryId);
        var roles = plan.Operations.Select(o => o.Role).ToList();
        // order of the three phases
        Assert.Single(roles, r => r == EpochRole.Event);
        var eventAt = roles.IndexOf(EpochRole.Event);
        Assert.All(roles.Take(eventAt), r => Assert.Equal(EpochRole.Storno, r));
        Assert.All(roles.Skip(eventAt + 1), r => Assert.Equal(EpochRole.Rebook, r));
        // the storno mirrors every changed entry exactly, on its original date, once
        var stornoed = plan.Operations.Where(o => o.Role == EpochRole.Storno).SelectMany(o => o.Entries).ToList();
        Assert.Equal(changes.Keys.OrderBy(x => x), stornoed.Select(e => e.OriginalEntryId!.Value).OrderBy(x => x));
        foreach (var s in stornoed)
        {
            var original = entries[s.OriginalEntryId!.Value];
            Assert.Equal((original.EntryKind, -original.Direction, original.Quantity, original.Amount, original.Date, original.WarehouseId), (s.Kind, s.Direction, s.Quantity, s.Amount, s.Date, s.WarehouseId));
        }
        // storno order: operations by highest entry id descending, entries of an operation descending
        var stornoOps = plan.Operations.Where(o => o.Role == EpochRole.Storno).ToList();
        Assert.Equal(stornoOps.Select(o => o.Entries.Max(e => e.OriginalEntryId!.Value)).OrderByDescending(x => x), stornoOps.Select(o => o.Entries.Max(e => e.OriginalEntryId!.Value)));
        foreach (var o in stornoOps) Assert.Equal(o.Entries.Select(e => e.OriginalEntryId!.Value).OrderByDescending(x => x), o.Entries.Select(e => e.OriginalEntryId!.Value));
        // the rebook rewrites every stornoed entry once, with the replayed amount and figures, on the original date, in ascending (date, id) order
        var rebooked = plan.Operations.Where(o => o.Role == EpochRole.Rebook).SelectMany(o => o.Entries).ToList();
        Assert.Equal(stornoed.Select(e => e.OriginalEntryId!.Value).OrderBy(x => x), rebooked.Select(e => e.OriginalEntryId!.Value).OrderBy(x => x));
        foreach (var r in rebooked)
        {
            var original = entries[r.OriginalEntryId!.Value]; var change = changes[original.Id];
            Assert.Equal((original.EntryKind, original.Direction, original.Quantity, original.Date), (r.Kind, r.Direction, r.Quantity, r.Date));
            Assert.Equal((change.ProposedAmount, change.ProposedQuantityAfter, change.ProposedValueAfter), (r.Amount, r.QuantityAfter, r.ValueAfter));
        }
        var keys = rebooked.Select(e => (e.Date, Id: e.OriginalEntryId!.Value)).ToList();
        Assert.Equal(keys.OrderBy(k => k.Date).ThenBy(k => k.Id), keys);
        // a rebook operation rewrites exactly what its storno operation took back
        foreach (var rebook in plan.Operations.Where(o => o.Role == EpochRole.Rebook))
            Assert.Equal(stornoOps.Single(s => s.OriginalOperationId == rebook.OriginalOperationId).Entries.Select(e => e.OriginalEntryId!.Value).OrderBy(x => x), rebook.Entries.Select(e => e.OriginalEntryId!.Value).OrderBy(x => x));
        // lineage: one row per changed entry, pointing at the right storno and rebook entries
        Assert.Equal(changes.Keys.OrderBy(x => x), plan.Lineage.Select(l => l.OriginalEntryId).OrderBy(x => x));
        foreach (var l in plan.Lineage)
        {
            Assert.Equal((EpochRole.Storno, l.OriginalEntryId), (plan.Operations[l.StornoOperation].Role, plan.Operations[l.StornoOperation].Entries[l.StornoEntry].OriginalEntryId!.Value));
            Assert.Equal((EpochRole.Rebook, l.OriginalEntryId), (plan.Operations[l.RebookOperation].Role, plan.Operations[l.RebookOperation].Entries[l.RebookEntry].OriginalEntryId!.Value));
        }
        // the ledger, independently: baseline plus every signed entry, no step below zero, the end state is the proposed one
        var qty = f.Snapshot.Positions.ToDictionary(p => p.WarehouseId, p => p.Quantity);
        var val = f.Snapshot.Positions.ToDictionary(p => p.WarehouseId, p => p.Value);
        foreach (var e in plan.Operations.SelectMany(o => o.Entries))
        {
            qty[e.WarehouseId] += e.Direction * e.Quantity; val[e.WarehouseId] += e.Direction * e.Amount;
            Assert.True(qty[e.WarehouseId] >= 0m && val[e.WarehouseId] >= 0m);
        }
        foreach (var impact in f.Comparison.Positions) Assert.Equal((impact.ProposedQuantity, impact.ProposedValue), (qty[impact.WarehouseId], val[impact.WarehouseId]));
        foreach (var p in plan.ExpectedPositions) Assert.Equal((p.Quantity, p.Value), (qty[p.PositionId], val[p.PositionId]));
        // the deterministic key of every entry: (position, sequence), contiguous per position from the baseline's last sequence in write order
        var expectedSequence = f.Snapshot.Positions.ToDictionary(p => p.WarehouseId, p => p.LastSequence);
        foreach (var e in plan.Operations.SelectMany(o => o.Entries))
            Assert.Equal(++expectedSequence[e.WarehouseId], e.Sequence);
        Assert.Equal(plan.Operations.SelectMany(o => o.Entries).Count(), plan.Operations.SelectMany(o => o.Entries).Select(e => (e.WarehouseId, e.Sequence)).Distinct().Count());
        // every entry of a non-storno phase meets the running figures it states
        // (checked entry by entry above through the same signed sums by the derivation; here once more, independently)
        var q2 = f.Snapshot.Positions.ToDictionary(p => p.WarehouseId, p => p.Quantity);
        var v2 = f.Snapshot.Positions.ToDictionary(p => p.WarehouseId, p => p.Value);
        foreach (var e in plan.Operations.SelectMany(o => o.Entries))
        {
            q2[e.WarehouseId] += e.Direction * e.Quantity; v2[e.WarehouseId] += e.Direction * e.Amount;
            Assert.Equal((q2[e.WarehouseId], v2[e.WarehouseId]), (e.QuantityAfter, e.ValueAfter));
        }
    }
}

/// <summary>Slice E1 is inert: nothing in the backend calls, registers or even names the derivation. The later slices (E2, E3) replace this pin with the list of their allowed callers.</summary>
public class EpochScheduleReachabilityTests
{
    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "backend", "Hasm.Infrastructure"))) return Path.Combine(dir.FullName, "src", "backend");
        throw new InvalidOperationException("the backend sources were not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void No_backend_source_outside_the_derivation_itself_references_it()
    {
        var offenders = Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && Path.GetFileName(f) != "EpochSchedule.cs")
            .Where(f => File.ReadAllText(f).Contains("EpochSchedule", StringComparison.Ordinal) || File.ReadAllText(f).Contains("EpochScheduleResult", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_derivation_types_are_internal_and_have_no_database_or_clock_dependency()
    {
        var assembly = typeof(EpochSchedule).Assembly;
        foreach (var type in new[] { typeof(EpochSchedule), typeof(EpochScheduleResult), typeof(EpochOperationPlan), typeof(EpochEntryPlan), typeof(EpochLineagePlan), typeof(EpochRole) })
            Assert.False(type.IsPublic, type.Name);
        // it names no connection, transaction, command, time provider or random source: only costing types and the framework's collections and hashing
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Hasm.Infrastructure", "Costing", "EpochSchedule.cs"));
        foreach (var forbidden in new[] { "Npgsql", "DbConnection", "TimeProvider", "DateTime.", "Random", "Guid.NewGuid", "File.", "HttpClient" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.NotNull(assembly);
    }
}
