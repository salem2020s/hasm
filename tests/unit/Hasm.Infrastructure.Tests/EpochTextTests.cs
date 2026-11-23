using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 3.8D executor slice E1b: the byte-exact epoch text of the T2 design (4.4, <c>hasm-replay-v4</c>) with T2's known-answer vector, determinism across cultures and input orders, and the assembly of the text from a
/// derived epoch. The vector is the design's own (eleven lines, 1777 bytes, digest 719a44...): the same bytes a SQL builder must produce.
/// </summary>
public partial class EpochScheduleTests
{
    private static Guid U(char d) => Guid.Parse($"{new string(d, 8)}-{new string(d, 4)}-4{new string(d, 3)}-8{new string(d, 3)}-{new string(d, 12)}");
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The input of the design's vector: I = item 7, W = warehouse 8, O = original operation e, X = executor 9, plan 2.</summary>
    private static EpochTextInput VectorInput()
    {
        Guid I = U('7'), W = U('8'), O = U('e'), X = U('9'); var plan = U('2'); var date = new DateOnly(2026, 3, 11);
        return new EpochTextInput(
            [new(1, O, 2, date, null, "replay-storno", null, $"epoch:{plan}:1:{O}", X), new(2, null, 1, date, null, "stock-posting", "execution-posting", $"epoch:{plan}:2", X), new(3, O, 2, date, null, "replay-rebook", null, $"epoch:{plan}:3:{O}", X)],
            [new(I, W, 41, 1, 17, 2, 1, date, 5m, 62.5m, 105m, 1312.5m), new(I, W, 42, 2, null, 1, 1, date, 20m, 240m, 125m, 1552.5m), new(I, W, 43, 3, 17, 2, 2, date, 5m, 60m, 120m, 1492.5m)],
            [new(I, W, 9, 42, 20m, 240m, null, null, null)],
            [new(I, W, 41, I, W, 3, 5m, 62.5m, true), new(I, W, 43, I, W, 3, 5m, 60m, false)],
            [new(I, W, 3, 10m, 120m), new(I, W, 9, 20m, 240m)],
            [new(I, W, 40, 43, 120m, 1492.5m, 2)]);
    }

    [Fact]
    public void The_text_of_the_T2_known_answer_vector_has_the_designs_length_and_digest()
    {
        var result = EpochText.Build(VectorInput());
        Assert.Equal(1888, Encoding.UTF8.GetByteCount(result.Text));
        Assert.Equal("4329df10c3039a9f9e66941607a58ebd1474826d1c4ac2cc7a82a11284b7aa42", result.Digest);
        Assert.Equal(12, result.Text.Split('\n').Length);
        Assert.DoesNotContain('\r', result.Text); Assert.False(result.Text.EndsWith('\n'));
        // the three source types of the design
        Assert.Contains("ccafea491d240486f275df9056349c5d84f517dafe92fec1cc99defaea3b2a10", result.Text);
        Assert.Contains("2e894c7e770e61084d405fbf348f2fc5a0651051ea182d5a84aa9de8ad81ad1d", result.Text);
        Assert.Contains("0397383eedde7014d68ceb3b5cf095472a3d1ad576436b60388de7ce5a22e6bc", result.Text);
        Assert.StartsWith("O|1|eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee|2|2026-03-11|-|ccafea49", result.Text);
        Assert.Contains("\nE|77777777-7777-4777-8777-777777777777|88888888-8888-4888-8888-888888888888|41|1|17|2|1|2026-03-11|5.0000000000|62.500000|105.0000000000|1312.500000\n", result.Text);
        Assert.Contains("\nR|77777777-7777-4777-8777-777777777777|88888888-8888-4888-8888-888888888888|9|20.0000000000|240.000000\n", result.Text);
        Assert.EndsWith("\nP|77777777-7777-4777-8777-777777777777|88888888-8888-4888-8888-888888888888|40|43|120.0000000000|1492.500000|2", result.Text);
    }

    [Fact]
    public void The_text_does_not_depend_on_the_culture_or_on_the_order_the_rows_arrive_in()
    {
        var input = VectorInput(); var expected = EpochText.Build(input).Digest;
        var shuffled = new EpochTextInput([.. input.Operations.Reverse()], [.. input.Entries.Reverse()], [.. input.Layers.Reverse()], [.. input.Consumptions.Reverse()], [.. input.Remaining.Reverse()], [.. input.Positions.Reverse()]);
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            foreach (var name in new[] { "ar-SA", "de-DE", "fa-IR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, EpochText.Build(input).Digest);
                Assert.Equal(expected, EpochText.Build(shuffled).Digest);
            }
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved; }
    }

    [Fact]
    public void An_empty_epoch_hashes_as_the_empty_string_and_every_field_the_design_signs_changes_the_digest()
    {
        var empty = EpochText.Build(new EpochTextInput([], [], [], [], [], []));
        Assert.Equal(("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), (empty.Text, empty.Digest));
        var v = VectorInput(); var d = EpochText.Build(v).Digest;
        var changes = new Dictionary<string, EpochTextInput>
        {
            ["op key"] = v with { Operations = [.. v.Operations.Select(o => o.Role == 2 ? o with { Key = o.Key + "x" } : o)] },
            ["op source type"] = v with { Operations = [.. v.Operations.Select(o => o.Role == 1 ? o with { SourceType = "stock-posting" } : o)] },
            ["op created by"] = v with { Operations = [.. v.Operations.Select(o => o.Role == 3 ? o with { CreatedBy = U('1') } : o)] },
            ["entry amount"] = v with { Entries = [.. v.Entries.Select(e => e.Sequence == 42 ? e with { Amount = 240.01m } : e)] },
            ["entry origin"] = v with { Entries = [.. v.Entries.Select(e => e.Sequence == 41 ? e with { Origin = 18 } : e)] },
            ["layer original"] = v with { Layers = [.. v.Layers.Select(l => l with { OriginalQuantity = 21m })] },
            ["layer source"] = v with { Layers = [.. v.Layers.Select(l => l with { SourceItem = U('7'), SourceWarehouse = U('8'), SourceLayerSequence = 3 })] },
            ["consumption restores"] = v with { Consumptions = [.. v.Consumptions.Select(c => c.EntrySequence == 43 ? c with { Restores = true } : c)] },
            ["remaining"] = v with { Remaining = [.. v.Remaining.Select(r => r.LayerSequence == 3 ? r with { Amount = 120.01m } : r)] },
            ["position final sequence"] = v with { Positions = [.. v.Positions.Select(p => p with { FinalLastSequence = 44 })] },
            ["position baseline"] = v with { Positions = [.. v.Positions.Select(p => p with { BaselineLastSequence = 39 })] },
            ["position open layers"] = v with { Positions = [.. v.Positions.Select(p => p with { OpenLayers = 3 })] },
            ["position value"] = v with { Positions = [.. v.Positions.Select(p => p with { FinalValue = 1492.51m })] },
            ["a position less"] = v with { Positions = [] },
            ["a row less"] = v with { Remaining = [.. v.Remaining.Skip(1)] },
        };
        foreach (var (name, input) in changes) Assert.NotEqual(d, EpochText.Build(input).Digest);
        Assert.Throws<CostingException>(() => EpochText.Build(null!));
    }

    [Fact]
    public void The_text_of_a_derived_epoch_names_every_row_of_the_content_with_the_deterministic_keys()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Issue(2, W1, 4)], Receipt(1, W1, 10, 200));
        var (plan, content) = Content(f);
        var planId = U('2'); var creator = U('5');
        var input = EpochText.FromEpoch(f.Snapshot, plan, content, planId, creator);
        var text = EpochText.Build(input).Text.Split('\n');
        var item = f.Snapshot.ItemId.ToString("D"); var w1 = W1.ToString("D");
        Assert.Equal(3, text.Count(l => l.StartsWith("O|"))); Assert.Equal(3, text.Count(l => l.StartsWith("E|")));
        Assert.Equal(1, text.Count(l => l.StartsWith("L|"))); Assert.Equal(2, text.Count(l => l.StartsWith("C|"))); Assert.Equal(2, text.Count(l => l.StartsWith("R|")));
        Assert.Single(text, l => l == $"P|{f.Snapshot.ItemId:D}|{W1:D}|2|5|16.0000000000|260.000000|2");          // the position the epoch wrote on: last sequence 2 before, 5 after, 16 / 260.00, two open layers
        Assert.Contains(text, l => l == $"O|1|{plan.Operations[0].OriginalOperationId}|{(short)plan.Operations[0].Kind}|{plan.Operations[0].Date:yyyy-MM-dd}|-|{Sha("replay-storno")}|-|{Sha($"epoch:{planId}:1:{plan.Operations[0].OriginalOperationId}")}|{creator}");
        Assert.Contains(text, l => l == $"O|2|-|{(short)plan.Operations[1].Kind}|{plan.Operations[1].Date:yyyy-MM-dd}|-|{Sha("stock-posting")}|execution-posting|{Sha($"epoch:{planId}:2")}|{creator}");
        // the storno restores 4 / 40 to layer 1; the new layer is layer 2 (arrival rank), entry sequence 4; the rebook consumes layer 1 again
        Assert.Contains(text, l => l == $"C|{item}|{w1}|3|{item}|{w1}|1|4.0000000000|40.000000|t");
        Assert.Contains(text, l => l == $"C|{item}|{w1}|5|{item}|{w1}|1|4.0000000000|40.000000|f");
        Assert.Contains(text, l => l == $"L|{item}|{w1}|2|4|10.0000000000|200.000000|-");
        Assert.Contains(text, l => l == $"R|{item}|{w1}|1|6.0000000000|60.000000");
        Assert.Contains(text, l => l == $"R|{item}|{w1}|2|10.0000000000|200.000000");
        Assert.Equal(EpochText.Build(input).Digest, EpochText.Build(EpochText.FromEpoch(f.Snapshot, plan, content, planId, creator)).Digest);       // deterministic
        Assert.NotEqual(EpochText.Build(input).Digest, EpochText.Build(EpochText.FromEpoch(f.Snapshot, plan, content, U('3'), creator)).Digest);       // the plan id is in every key
    }

    [Fact]
    public void Every_group_is_sorted_by_the_designs_keys_whatever_the_input_order()
    {
        Guid I = U('7'), W1x = U('1'), W2x = U('2'); var date = new DateOnly(2026, 3, 11);
        var layers = new EpochTextLayer[] { new(I, W2x, 1, 5, 1m, 1m, null, null, null), new(I, W1x, 2, 4, 1m, 1m, null, null, null), new(I, W1x, 1, 3, 1m, 1m, null, null, null) };
        var consumptions = new EpochTextConsumption[]
        {
            new(I, W1x, 7, I, W1x, 1, 1m, 1m, true), new(I, W1x, 7, I, W1x, 1, 1m, 1m, false), new(I, W1x, 7, I, W1x, 2, 1m, 1m, false), new(I, W1x, 6, I, W2x, 1, 1m, 1m, false),
        };
        var remaining = new EpochTextRemaining[] { new(I, W2x, 1, 1m, 1m), new(I, W1x, 2, 1m, 1m), new(I, W1x, 1, 1m, 1m) };
        var entries = new EpochTextEntry[] { new(I, W2x, 2, 3, 1, 1, -1, date, 1m, 1m, 1m, 1m), new(I, W1x, 9, 3, 1, 1, -1, date, 1m, 1m, 1m, 1m), new(I, W1x, 8, 3, 1, 1, -1, date, 1m, 1m, 1m, 1m) };
        var ops = new EpochTextOperation[] { new(3, U('b'), 1, date, null, null, null, "k", U('9')), new(3, U('a'), 1, date, null, null, null, "k", U('9')), new(1, U('c'), 1, date, null, null, null, "k", U('9')), new(2, null, 1, date, null, null, null, "k", U('9')) };
        var text = EpochText.Build(new EpochTextInput([.. ops], [.. entries], [.. layers], [.. consumptions], [.. remaining], [new(I, W2x, 0, 1, 1m, 1m, 0), new(I, W1x, 7, 9, 1m, 1m, 1)])).Text.Split('\n');
        string Group(string g) => string.Join("\n", text.Where(l => l.StartsWith(g + "|")).Select(l => string.Join('|', l.Split('|').Skip(1).Take(g == "O" ? 2 : 4))));
        Assert.Equal($"1|{U('c')}\n2|-\n3|{U('a')}\n3|{U('b')}", Group("O"));
        Assert.Equal($"{I}|{W1x}|8|3\n{I}|{W1x}|9|3\n{I}|{W2x}|2|3", Group("E"));
        Assert.Equal($"{I}|{W1x}|1|3\n{I}|{W1x}|2|4\n{I}|{W2x}|1|5", Group("L"));
        var c = text.Where(l => l.StartsWith("C|")).Select(l => l.Split('|')).ToList();
        Assert.Equal(["6", "7", "7", "7"], c.Select(x => x[3]));
        Assert.Equal(["f", "t"], c.Where(x => x[3] == "7" && x[6] == "1").Select(x => x[9]));        // the same layer: not restoring before restoring
        Assert.Equal($"{I}|{W1x}|1\n{I}|{W1x}|2\n{I}|{W2x}|1", string.Join("\n", text.Where(l => l.StartsWith("R|")).Select(l => string.Join('|', l.Split('|').Skip(1).Take(3)))));
        Assert.Equal($"{I}|{W1x}|7|9\n{I}|{W2x}|0|1", string.Join("\n", text.Where(l => l.StartsWith("P|")).Select(l => string.Join('|', l.Split('|').Skip(1).Take(4)))));
    }

    [Fact]
    public void A_consumed_mirror_layer_is_named_by_its_arrival_rank_not_by_the_sequence_of_its_entry()
    {
        // the rebooked transfer in makes two NEW mirror layers on W2 (the stornoed ones had ranks 1 and 2, so these are 3 and 4) from ONE entry: the rebooked issue consumes both, and its C lines must name ranks 3 and 4, not the entry's sequence
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Receipt(1, W1, 10, 200), Transfer(2, W1, W2, 15), Issue(3, W2, 12)], Receipt(-1, W1, 5, 25));
        var (plan, content) = Content(f);
        var lines = EpochText.Build(EpochText.FromEpoch(f.Snapshot, plan, content, U('2'), U('5'))).Text.Split('\n');
        var w2 = W2.ToString("D");
        var issueEntry = plan.Operations.Single(o => o.Role == EpochRole.Rebook && o.Kind == CostOperationKind.Issue).Entries.Single();
        var consumed = lines.Where(l => l.StartsWith($"C|{f.Snapshot.ItemId:D}|{w2}|{issueEntry.Sequence}|")).Select(l => l.Split('|')[6]).ToList();
        Assert.Equal(["3", "4"], consumed);
        Assert.True(lines.Count(l => l.StartsWith("L|") && l.Split('|')[2] == w2) >= 2);
    }

    [Fact]
    public void A_transfer_mirror_layer_names_its_source_layer_by_item_warehouse_and_sequence()
    {
        var f = Build(CostMethod.Fifo, [Receipt(0, W1, 10, 100), Receipt(1, W1, 10, 200), Transfer(2, W1, W2, 15)], Receipt(-1, W1, 5, 25));
        var (plan, content) = Content(f);
        var text = EpochText.Build(EpochText.FromEpoch(f.Snapshot, plan, content, U('2'), U('5'))).Text.Split('\n');
        var mirrors = text.Where(l => l.StartsWith("L|") && !l.EndsWith("|-")).ToList();
        Assert.Equal(2, mirrors.Count);                                                      // the rebooked transfer makes two mirror layers (one per consumed layer); the stornoed original's mirrors only leave
        Assert.All(mirrors, l => Assert.Matches("/[0-9a-f-]{36}/[0-9]+$", l));
        Assert.Equal(1, text.Count(l => l.StartsWith("O|2|")));
    }
}
