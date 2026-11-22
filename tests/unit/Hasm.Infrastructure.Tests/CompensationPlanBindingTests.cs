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
