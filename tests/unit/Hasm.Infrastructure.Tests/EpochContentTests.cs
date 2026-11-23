using System.Collections.Immutable;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 3.8D executor slice E1b: layers and consumptions of an epoch as pure data (<see cref="EpochContent"/>). Same fixture as E1 (the history is built with the real replay arithmetic and now carries the actual
/// layers and consumptions the engine stores). The oracle below keeps its own layer ledger from the snapshot and the emitted lines only. The comparison with the REAL engine is the PostgreSQL oracle test
/// (<c>EpochContentEngineOracleTests</c>).
/// </summary>
public partial class EpochScheduleTests
{
    private static (EpochScheduleResult Plan, EpochContentResult Content) Content(Fixture f)
    {
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        return (plan, EpochContent.Derive(f.Snapshot, plan));
    }

    // ------------------------------------------------------------------------------------------------------------ worked examples

    [Fact]
    public void Fifo_worked_example_the_issue_gives_back_its_layer_the_event_makes_a_new_one_and_the_issue_is_consumed_again()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var (plan, c) = Content(f);
        var layer1 = new EpochLayerKey(W1, 1, 0); var layer4 = new EpochLayerKey(W1, 4, 0);
        Assert.Equal(3, c.Entries.Length);
        // storno (sequence 3): the issue's consumption comes back to its layer
        Assert.Equal((W1, 3L, 2L), (c.Entries[0].WarehouseId, c.Entries[0].Sequence, c.Entries[0].OriginalEntryId!.Value));
        var first = Assert.Single(c.Entries[0].Consumptions); Assert.Equal(layer1, first.Layer); Assert.Equal((4m, 40m, true), (first.Quantity, first.Amount, first.Restores));
        Assert.Empty(c.Entries[0].Layers);
        // event (sequence 4): a new layer, arrival rank 2 (the first layer is rank 1)
        Assert.Equal([new EpochLayerCreation(layer4, 2, 10m, 200m, null)], c.Entries[1].Layers.ToArray());
        Assert.Empty(c.Entries[1].Consumptions);
        // rebook (sequence 5): the oldest layer again
        Assert.Equal([new EpochConsumption(layer1, 4m, 40m, false)], c.Entries[2].Consumptions.ToArray());
        // the R lines: the touched layers before and after
        Assert.Equal(2, c.Layers.Length);
        var r1 = c.Layers.Single(l => l.Key == layer1); var r4 = c.Layers.Single(l => l.Key == layer4);
        Assert.Equal((1L, 1L, false, 10m, 100m, 6m, 60m, 6m, 60m), (r1.ArrivalRank, r1.OriginalEntryId!.Value, r1.Created, r1.OriginalQuantity, r1.OriginalAmount, r1.BeforeQuantity, r1.BeforeAmount, r1.AfterQuantity, r1.AfterAmount));
        Assert.Equal((2L, (long?)null, true, 10m, 200m, 0m, 0m, 10m, 200m), (r4.ArrivalRank, r4.OriginalEntryId, r4.Created, r4.OriginalQuantity, r4.OriginalAmount, r4.BeforeQuantity, r4.BeforeAmount, r4.AfterQuantity, r4.AfterAmount));
        // the P line
        var p = Assert.Single(c.Positions);
        Assert.Equal((W1, 6m, 60m, 2L, 16m, 260m, 5L, 2), (p.WarehouseId, p.BaselineQuantity, p.BaselineValue, p.BaselineLastSequence, p.Quantity, p.Value, p.LastSequence, p.OpenLayers));
        Assert.NotEmpty(plan.Fingerprint);
    }

    [Fact]
    public void Fifo_a_stornoed_receipt_takes_its_whole_untouched_layer_out_and_a_transfer_in_takes_its_mirrors()
    {
        // R1 10/100 and R2 10/200 on W1, transfer of 15 to W2 (10 from layer 1, 5 from layer 2: two mirrors), a receipt before everything: everything is affected
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Receipt(1, W1, 10, 200), Transfer(2, W1, W2, 15)], Receipt(-1, W1, 5, 25));
        var (plan, c) = Content(f);
        // entries of the plan in write order: storno of the transfer's in (W2), out (W1), storno of R2, R1; event; rebooks
        var stornoIn = c.Entries[0]; var stornoOut = c.Entries[1];
        Assert.Equal(W2, stornoIn.WarehouseId);
        Assert.Equal([new EpochConsumption(new(W2, 1, 0), 10m, 100m, false), new EpochConsumption(new(W2, 1, 1), 5m, 100m, false)], stornoIn.Consumptions.ToArray());      // the two mirrors leave whole
        Assert.Equal([new EpochConsumption(new(W1, 1, 0), 10m, 100m, true), new EpochConsumption(new(W1, 2, 0), 5m, 100m, true)], stornoOut.Consumptions.ToArray());   // the out leg gives back what it took: 10 from layer 1, 5 from layer 2
        AssertContentOracle(f, plan, c);
    }

    [Fact]
    public void Average_has_no_layers_or_consumptions_but_keeps_keys_and_position_totals()
    {
        var f = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var (_, c) = Content(f);
        Assert.All(c.Entries, e => { Assert.Empty(e.Consumptions); Assert.Empty(e.Layers); });
        Assert.Equal([3L, 4L, 5L], c.Entries.Select(e => e.Sequence));
        Assert.Empty(c.Layers);
        var p = Assert.Single(c.Positions);
        Assert.Equal((16m, 240m, 5L, 0), (p.Quantity, p.Value, p.LastSequence, p.OpenLayers));
    }

    // ------------------------------------------------------------------------------------------------------------ refusals

    [Fact]
    public void A_cost_the_engines_planner_computes_differently_from_the_replay_is_refused()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        var tampered = plan with { Operations = [.. plan.Operations.Select(o => o.Role == EpochRole.Rebook ? o with { Entries = [.. o.Entries.Select(e => e with { Amount = e.Amount + 0.01m })] } : o)] };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(f.Snapshot, tampered)).Code);
    }

    [Fact]
    public void Layers_that_do_not_add_up_to_the_position_or_to_the_proven_trace_are_refused()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        // a layer holding more than the position says
        var bigger = f.Snapshot with { Layers = [.. f.Snapshot.Layers.Select(l => l with { RemainingAmount = l.RemainingAmount + 1m })] };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(bigger, plan)).Code);
        // the trace proves another open layer than the plan leaves
        var other = plan with { ExpectedPositions = [.. plan.ExpectedPositions.Select(p => p with { OpenLayers = [.. p.OpenLayers.Skip(1)] })] };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(f.Snapshot, other)).Code);
        // a consumption missing for an outflow that must be given back
        var lost = f.Snapshot with { Consumptions = [] };
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(lost, plan)).Code);
        // a consumption that restores (a reversal in the history) is not an epoch input
        var restore = f.Snapshot with { Consumptions = [.. f.Snapshot.Consumptions.Select(c => c with { Restores = true })] };
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(restore, plan)).Code);
        // average with layers, a layer of no entry, a plan on another key sequence
        var avg = Build(CostMethod.MovingAverage, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var avgPlan = EpochSchedule.Derive(avg.Snapshot, avg.Comparison);
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(avg.Snapshot with { Layers = f.Snapshot.Layers }, avgPlan)).Code);
        Assert.Equal("impact_history_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(f.Snapshot with { Layers = [.. f.Snapshot.Layers.Select(l => l with { EntryId = 999 })] }, plan)).Code);
        var skewed = plan with { Operations = [.. plan.Operations.Select(o => o with { Entries = [.. o.Entries.Select(e => e with { Sequence = e.Sequence + 1 })] })] };
        Assert.Equal("epoch_schedule_inconsistent", Assert.Throws<CostingException>(() => EpochContent.Derive(f.Snapshot, skewed)).Code);
    }

    [Fact]
    public void A_receipt_layer_that_is_not_untouched_when_its_storno_comes_is_refused()
    {
        // R1 10/100 on W1, issue 4 BEFORE the epoch's reach: the issue is unaffected (event after it) but a tampered snapshot claims the affected receipt layer was consumed by it
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(1, W1, 4), Receipt(3, W1, 5, 50)], Receipt(2, W1, 2, 20));
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        var receipt = f.Snapshot.Entries.Single(e => e.EntryKind == CostEntryKind.Receipt && e.Date == Day0.AddDays(3));
        var layer = f.Snapshot.Layers.Single(l => l.EntryId == receipt.Id);
        var consumed = f.Snapshot with
        {
            Layers = [.. f.Snapshot.Layers.Select(l => l.Id == layer.Id ? l with { RemainingQuantity = l.RemainingQuantity - 1m, RemainingAmount = l.RemainingAmount - 10m } : l.Id == f.Snapshot.Layers.First().Id ? l with { RemainingQuantity = l.RemainingQuantity + 1m, RemainingAmount = l.RemainingAmount + 10m } : l)],
        };
        Assert.Equal("epoch_layer_consumed", Assert.Throws<CostingException>(() => EpochContent.Derive(consumed, plan)).Code);
    }

    [Fact]
    public void Malformed_content_input_and_cancellation_are_refused()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var plan = EpochSchedule.Derive(f.Snapshot, f.Comparison);
        Assert.Equal("invalid_impact_snapshot", Assert.Throws<CostingException>(() => EpochContent.Derive(null!, plan)).Code);
        Assert.Equal("invalid_impact_snapshot", Assert.Throws<CostingException>(() => EpochContent.Derive(f.Snapshot, null!)).Code);
        Assert.Equal("invalid_impact_snapshot", Assert.Throws<CostingException>(() => EpochContent.Derive(f.Snapshot with { Method = (CostMethod)9 }, plan)).Code);
        Assert.Throws<OperationCanceledException>(() => EpochContent.Derive(f.Snapshot, plan, new CancellationToken(true)));
    }

    [Fact]
    public void The_content_is_deterministic_and_its_fingerprint_follows_the_schedule_and_every_line()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var (_, a) = Content(f);
        Assert.Equal(a.Fingerprint, Content(f).Content.Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", a.Fingerprint);
        var other = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 300));
        Assert.NotEqual(a.Fingerprint, Content(other).Content.Fingerprint);
    }

    // ------------------------------------------------------------------------------------------------------------ property test

    [Theory]
    [InlineData(CostMethod.Fifo)]
    [InlineData(CostMethod.MovingAverage)]
    public void For_random_histories_the_content_meets_the_layer_ledger_invariants(CostMethod method)
    {
        int derived = 0;
        for (var seed = 1; seed <= 400; seed++)
        {
            var rnd = new Random(seed * (method == CostMethod.Fifo ? 17 : 19));
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
            catch (CostingException) { continue; }
            EpochScheduleResult plan;
            try { plan = EpochSchedule.Derive(f.Snapshot, f.Comparison); }
            catch (CostingException e) when (e.Code is "epoch_partial_transfer_unsupported" or "epoch_not_a_suffix") { continue; }       // the fail-closed refusals of E1 itself
            var content = EpochContent.Derive(f.Snapshot, plan);
            AssertContentOracle(f, plan, content);
            derived++;
        }
        Assert.True(derived >= 120, $"derived {derived}");
    }

    private sealed class Ledger { public long Rank; public decimal Quantity, Amount; }

    /// <summary>The independent oracle: its own layer ledger from the snapshot and the emitted lines.</summary>
    private static void AssertContentOracle(Fixture f, EpochScheduleResult plan, EpochContentResult content)
    {
        var s = f.Snapshot;
        var entries = s.Entries.ToDictionary(e => e.Id);
        // the key of an existing layer, computed here independently
        EpochLayerKey KeyOf(CostFlowLayer l)
        {
            var creator = entries[l.EntryId];
            return new(l.WarehouseId, creator.Sequence, s.Layers.Where(x => x.EntryId == l.EntryId).OrderBy(x => x.Sequence).ToList().FindIndex(x => x.Id == l.Id));
        }
        var keyById = s.Layers.ToDictionary(l => l.Id, KeyOf);
        var ledger = s.Layers.ToDictionary(KeyOf, l => new Ledger { Rank = l.Sequence, Quantity = l.RemainingQuantity, Amount = l.RemainingAmount });
        var before = ledger.ToDictionary(x => x.Key, x => (x.Value.Quantity, x.Value.Amount));
        var created = new HashSet<EpochLayerKey>();
        var flat = plan.Operations.SelectMany((o, oi) => o.Entries.Select((e, ei) => (Op: oi, Entry: ei, Plan: e))).ToList();
        Assert.Equal(flat.Select(x => (x.Op, x.Entry, x.Plan.WarehouseId, x.Plan.Sequence, x.Plan.OriginalEntryId)), content.Entries.Select(x => (x.Operation, x.Entry, x.WarehouseId, x.Sequence, x.OriginalEntryId)));
        var maxRank = s.Positions.ToDictionary(p => p.WarehouseId, p => s.Layers.Where(l => l.WarehouseId == p.WarehouseId).Select(l => l.Sequence).DefaultIfEmpty(0L).Max());
        List<EpochConsumption>? previousOut = null;
        foreach (var (item, entry) in content.Entries.Zip(flat.Select(x => x.Plan)))
        {
            Assert.Equal(item.Sequence, entry.Sequence);
            if (s.Method == CostMethod.MovingAverage) { Assert.Empty(item.Consumptions); Assert.Empty(item.Layers); continue; }
            if (entry.Role == EpochRole.Storno)
            {
                var original = entries[entry.OriginalEntryId!.Value];
                if (original.Direction < 0)
                {
                    // restores exactly the original's consumptions, in order, to the same layers
                    var expected = s.Consumptions.Where(c => c.EntryId == original.Id).OrderBy(c => c.Id).Select(c => new EpochConsumption(keyById[c.LayerId], c.Quantity, c.Amount, true)).ToList();
                    Assert.Equal(expected, item.Consumptions.ToArray());
                    foreach (var l in item.Consumptions) { ledger[l.Layer].Quantity += l.Quantity; ledger[l.Layer].Amount += l.Amount; }
                }
                else
                {
                    // takes the whole untouched layers of the original out
                    var layers = s.Layers.Where(l => l.EntryId == original.Id).OrderBy(l => l.Sequence).Select(KeyOf).ToList();
                    Assert.Equal(layers, item.Consumptions.Select(c => c.Layer));
                    foreach (var l in item.Consumptions)
                    {
                        var source = s.Layers.Single(x => KeyOf(x) == l.Layer);
                        Assert.False(l.Restores);
                        Assert.Equal((source.OriginalQuantity, source.OriginalAmount), (l.Quantity, l.Amount));           // whole, untouched
                        Assert.Equal((ledger[l.Layer].Quantity, ledger[l.Layer].Amount), (l.Quantity, l.Amount));
                        ledger[l.Layer].Quantity = 0; ledger[l.Layer].Amount = 0;
                    }
                }
                Assert.Empty(item.Layers);
                continue;
            }
            if (entry.Kind == CostEntryKind.Receipt)
            {
                var l = Assert.Single(item.Layers);
                Assert.Equal(new EpochLayerKey(entry.WarehouseId, entry.Sequence, 0), l.Key);
                Assert.Equal((entry.Quantity, entry.Amount, (EpochLayerKey?)null), (l.Quantity, l.Amount, l.Source));
                Assert.Equal(maxRank[entry.WarehouseId] + 1, l.ArrivalRank); maxRank[entry.WarehouseId]++;
                ledger[l.Key] = new Ledger { Rank = l.ArrivalRank, Quantity = l.Quantity, Amount = l.Amount }; created.Add(l.Key);
                Assert.Empty(item.Consumptions);
            }
            else if (entry.Kind is CostEntryKind.Issue or CostEntryKind.TransferOut)
            {
                Assert.Empty(item.Layers);
                Assert.Equal(entry.Quantity, item.Consumptions.Sum(c => c.Quantity)); Assert.Equal(entry.Amount, item.Consumptions.Sum(c => c.Amount));
                Assert.All(item.Consumptions, c => Assert.False(c.Restores));
                // the oldest open layers, each consumed fully before the next
                var openBefore = ledger.Where(x => x.Value.Quantity > 0m && x.Key.WarehouseId == entry.WarehouseId).OrderBy(x => x.Value.Rank).Select(x => x.Key).ToList();
                Assert.Equal(openBefore.Take(item.Consumptions.Length), item.Consumptions.Select(c => c.Layer));
                foreach (var c in item.Consumptions.Take(item.Consumptions.Length - 1)) Assert.Equal(ledger[c.Layer].Quantity, c.Quantity);
                foreach (var c in item.Consumptions) { ledger[c.Layer].Quantity -= c.Quantity; ledger[c.Layer].Amount -= c.Amount; Assert.True(ledger[c.Layer].Quantity >= 0m && ledger[c.Layer].Amount >= 0m); }
                previousOut = entry.Kind == CostEntryKind.TransferOut ? [.. item.Consumptions] : null;
                continue;
            }
            else
            {
                // transfer in: one mirror per consumed layer of the out leg, in order, same figures, source = the consumed layer
                Assert.NotNull(previousOut);
                Assert.Equal(previousOut!.Select(c => (c.Quantity, c.Amount, (EpochLayerKey?)c.Layer)), item.Layers.Select(l => (l.Quantity, l.Amount, l.Source)));
                Assert.Equal(Enumerable.Range(0, item.Layers.Length), item.Layers.Select(l => l.Key.Ordinal));
                foreach (var l in item.Layers)
                {
                    Assert.Equal((entry.WarehouseId, entry.Sequence), (l.Key.WarehouseId, l.Key.EntrySequence));
                    Assert.Equal(maxRank[entry.WarehouseId] + 1, l.ArrivalRank); maxRank[entry.WarehouseId]++;
                    ledger[l.Key] = new Ledger { Rank = l.ArrivalRank, Quantity = l.Quantity, Amount = l.Amount }; created.Add(l.Key);
                }
                Assert.Empty(item.Consumptions);
            }
            previousOut = null;
        }
        // the R lines are exactly the touched layers, with the figures of the ledger
        var touched = content.Layers.Select(l => l.Key).ToHashSet();
        Assert.Equal(content.Layers.Length, touched.Count);
        foreach (var r in content.Layers)
        {
            Assert.Equal((ledger[r.Key].Quantity, ledger[r.Key].Amount, ledger[r.Key].Rank), (r.AfterQuantity, r.AfterAmount, r.ArrivalRank));
            Assert.Equal(created.Contains(r.Key), r.Created);
            if (!r.Created) Assert.Equal(before[r.Key], (r.BeforeQuantity, r.BeforeAmount)); else Assert.Equal((0m, 0m), (r.BeforeQuantity, r.BeforeAmount));
        }
        foreach (var key in ledger.Keys) if (!touched.Contains(key)) Assert.Equal(before[key], (ledger[key].Quantity, ledger[key].Amount));      // an untouched layer did not change
        // the P lines: totals and open layers
        foreach (var p in content.Positions)
        {
            var expected = plan.ExpectedPositions.Single(x => x.PositionId == p.WarehouseId);
            Assert.Equal((expected.Quantity, expected.Value), (p.Quantity, p.Value));
            if (s.Method == CostMethod.Fifo)
            {
                var open = ledger.Where(x => x.Key.WarehouseId == p.WarehouseId && x.Value.Quantity > 0m).ToList();
                Assert.Equal(open.Count, p.OpenLayers);
                Assert.Equal((p.Quantity, p.Value), (open.Sum(x => x.Value.Quantity), open.Sum(x => x.Value.Amount)));
            }
        }
    }
}
