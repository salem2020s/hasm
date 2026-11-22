using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

/// <summary>Phase 3.8D step 5: the pure binding of a planner result to a stored plan (closure hash form, canonical payload). Nothing here touches a database.</summary>
public class CompensationPlanBindingTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a"), B = Guid.Parse("00000000-0000-0000-0000-00000000000b"), C = Guid.Parse("ffffffff-0000-0000-0000-000000000001");

    [Fact]
    public void The_closure_hash_follows_the_byte_exact_rule_of_the_design_in_any_input_order()
    {
        // lowercase hyphenated text, a colon, one newline between pairs, no trailing newline, UTF-8, lowercase hex SHA-256, sorted in uuid order
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{A}:{B}\n{A}:{C}\n{B}:{A}")));
        Assert.Equal(expected, CompensationPlanBinding.ClosureHash([(B, A), (A, C), (A, B)]));
        Assert.Equal(expected, CompensationPlanBinding.ClosureHash([(A, B), (A, C), (B, A)]));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", CompensationPlanBinding.ClosureHash([]));   // an empty closure hashes the empty string
        Assert.NotEqual(CompensationPlanBinding.ClosureHash([(A, B)]), CompensationPlanBinding.ClosureHash([(B, A)]));
    }

    [Fact]
    public void The_payload_round_trips_exactly_and_is_canonical_in_the_scale_of_decimals()
    {
        var id = Guid.NewGuid();
        var op = new ProposedFlowOperation(id, new DateOnly(2026, 3, 11), CostOperationKind.Transfer, [new ProposedFlowLine(A, 10.50m, null, B), new ProposedFlowLine(C, 3m, 200.00m)]);
        var back = CompensationPlanBinding.Proposal(CompensationPlanBinding.PayloadOf(op))!;
        Assert.Equal((id, op.EffectiveDate, op.Kind), (back.Id, back.EffectiveDate, back.Kind));
        Assert.Equal(op.Lines.Select(l => (l.WarehouseId, l.Quantity, l.ReceiptAmount, l.DestinationWarehouseId)), back.Lines.Select(l => (l.WarehouseId, l.Quantity, l.ReceiptAmount, l.DestinationWarehouseId)));
        // 1.0 and 1.00 are the same payload
        Assert.Equal(CompensationPlanBinding.PayloadOf(op with { Lines = [new ProposedFlowLine(A, 1.0m)] }), CompensationPlanBinding.PayloadOf(op with { Lines = [new ProposedFlowLine(A, 1.00m)] }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"Contract\":\"other\",\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Date\":\"2026-03-11\",\"Kind\":1,\"Lines\":[]}")]
    [InlineData("{\"Contract\":\"stock-compensation-plan-payload-v1\",\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Date\":\"11/03/2026\",\"Kind\":1,\"Lines\":[]}")]
    public void A_stored_text_that_is_not_a_payload_of_this_contract_is_not_a_proposal(string text) => Assert.Null(CompensationPlanBinding.Proposal(text));
}

/// <summary>The refusals of the plan tables of migration 0036 and the codes they come back as (Phase 3.8D step 5).</summary>
public class CompensationPlanRefusalMappingTests
{
    private static Npgsql.PostgresException Raise(string message) => new(message, "ERROR", "ERROR", "P0001");

    [Theory]
    [InlineData("the approval of a compensation plan is not sound: approver_not_independent", "approver_not_independent")]
    [InlineData("the approval of a compensation plan is not sound: document_not_approved", "document_not_approved")]
    [InlineData("the approval of a compensation plan is not sound: document_already_posted", "document_already_posted")]
    [InlineData("the approval of a compensation plan is not sound: approval_fingerprint_mismatch", "approval_fingerprint_mismatch")]
    [InlineData("the approval of a compensation plan is not sound: closure_hash_mismatch", "closure_hash_mismatch")]
    [InlineData("document_already_posted: a plan is not written for a document that already has a stock posting", "document_already_posted")]
    [InlineData("plan_on_epoch_position: a position of the closure was already rewritten by an executed plan", "plan_on_epoch_position")]
    public void A_database_refusal_comes_back_as_its_result_code(string message, string code) =>
        Assert.Equal((Hasm.Infrastructure.CompanyConfiguration.ConfigStatus.Conflict, code), CompensationPlanStore.Refusal(Raise(message)));

    [Fact]
    public void An_unknown_refusal_or_another_error_class_is_not_mapped_so_it_is_never_hidden()
    {
        Assert.Null(CompensationPlanStore.Refusal(Raise("compensation plan and epoch records are append-only")));
        Assert.Null(CompensationPlanStore.Refusal(new Npgsql.PostgresException("approver_not_independent", "ERROR", "ERROR", "XX000")));
    }
}

/// <summary>The staleness comparison of the approval: ALL seven fingerprints and the closure hash count; a change in exactly one of them makes the plan stale (review L3 of the step 5 slices 1 and 2).</summary>
public class CompensationPlanStalenessComparisonTests
{
    private static string F(char c) => new(c, 64);
    private static readonly CompensationPlanFingerprints Stored = new(F('1'), F('2'), F('3'), F('4'), F('5'), F('6'), F('7'));
    private static StoredCompensationPlan Plan() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, 1, F('c'), Stored, [], "{}", Guid.NewGuid(), null, null);

    [Fact]
    public void The_exact_fingerprints_and_closure_hash_match() => Assert.True(CompensationPlanBinding.Matches(Plan(), Stored with { }, F('c')));

    [Theory]
    [InlineData(nameof(CompensationPlanFingerprints.Plan))]
    [InlineData(nameof(CompensationPlanFingerprints.Comparison))]
    [InlineData(nameof(CompensationPlanFingerprints.Evidence))]
    [InlineData(nameof(CompensationPlanFingerprints.Periods))]
    [InlineData(nameof(CompensationPlanFingerprints.Position))]
    [InlineData(nameof(CompensationPlanFingerprints.Policy))]
    [InlineData(nameof(CompensationPlanFingerprints.Accounting))]
    public void A_change_in_exactly_one_fingerprint_makes_the_plan_stale(string member)
    {
        var changed = member switch
        {
            nameof(CompensationPlanFingerprints.Plan) => Stored with { Plan = F('f') },
            nameof(CompensationPlanFingerprints.Comparison) => Stored with { Comparison = F('f') },
            nameof(CompensationPlanFingerprints.Evidence) => Stored with { Evidence = F('f') },
            nameof(CompensationPlanFingerprints.Periods) => Stored with { Periods = F('f') },
            nameof(CompensationPlanFingerprints.Position) => Stored with { Position = F('f') },
            nameof(CompensationPlanFingerprints.Policy) => Stored with { Policy = F('f') },
            _ => Stored with { Accounting = F('f') },
        };
        Assert.False(CompensationPlanBinding.Matches(Plan(), changed, F('c')));
    }

    [Fact]
    public void A_change_in_the_closure_hash_alone_makes_the_plan_stale() => Assert.False(CompensationPlanBinding.Matches(Plan(), Stored with { }, F('d')));
}
