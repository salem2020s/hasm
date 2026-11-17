using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

/// <summary>Numerical contract fixtures only. These do not implement conversion, returns, basis persistence or execution gates.</summary>
public class CostConversionContractTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);
    private static ReplayEvent Receipt(int n, decimal qty, decimal amount) => new(Id(n), CostOperationKind.Receipt, qty, amount);
    private static ReplayEvent Issue(int n, decimal qty) => new(Id(n), CostOperationKind.Issue, qty);

    [Fact]
    public void Proposed_average_to_fifo_basis_and_old_outflow_restitution_preserve_exact_carrying_value()
    {
        // Current epoch only: opening8/120, then restore an original average issue2/30.
        var result = CostReplay.Calculate(CostMethod.Fifo, 2, [Receipt(1, 8, 120), Receipt(2, 2, 30), Issue(3, 6)]);
        Assert.Equal(90m, result.Steps[^1].Amount);
        Assert.Equal((4m, 60m), (result.QuantityAfter, result.ValueAfter));
        Assert.Equal(150m, result.ValueAfter + result.Steps[^1].Amount);
        Assert.Equal(Id(1), Assert.Single(result.Steps[^1].Consumptions).LayerId);
    }

    [Fact]
    public void Proposed_fifo_to_average_restitution_restores_original_cost_instead_of_repricing_at_current_average()
    {
        var result = CostReplay.Calculate(CostMethod.MovingAverage, 2, [Receipt(1, 8, 120), Receipt(2, 2, 20), Issue(3, 5)]);
        Assert.Equal((10m, 140m), (result.Steps[1].QuantityAfter, result.Steps[1].ValueAfter));
        Assert.Equal(70m, result.Steps[^1].Amount);
        Assert.Equal((5m, 70m), (result.QuantityAfter, result.ValueAfter));
    }

    [Fact]
    public void Proposed_fifo_average_fifo_restitution_queues_components_after_current_opening_without_double_counting_old_layers()
    {
        var old = CostReplay.Calculate(CostMethod.Fifo, 2, [Receipt(10, 2, 20), Receipt(11, 4, 80), Issue(12, 3)]);
        Assert.Equal((3m, 60m), (old.QuantityAfter, old.ValueAfter));
        Assert.Equal([Id(10), Id(11)], old.Steps[^1].Consumptions.Select(c => c.LayerId));
        var intermediate = CostReplay.Calculate(CostMethod.MovingAverage, 2, [Receipt(20, 3, 60), Receipt(21, 1, 40)]);
        Assert.Equal((4m, 100m), (intermediate.QuantityAfter, intermediate.ValueAfter));
        var current = CostReplay.Calculate(CostMethod.Fifo, 2,
            [Receipt(30, intermediate.QuantityAfter, intermediate.ValueAfter), Receipt(31, 2, 20), Receipt(32, 1, 20), Issue(33, 5)]);
        Assert.Equal((7m, 140m), (current.Steps[2].QuantityAfter, current.Steps[2].ValueAfter));
        Assert.Equal(110m, current.Steps[^1].Amount);
        Assert.Equal([Id(30), Id(31)], current.Steps[^1].Consumptions.Select(c => c.LayerId));
        Assert.Equal((2m, 30m), (current.QuantityAfter, current.ValueAfter));
        Assert.Equal((3m, 60m), (old.QuantityAfter, old.ValueAfter));
    }

    [Fact]
    public void Proposed_compensated_partial_return_entitlement_reconciles_multiple_rounds_and_the_final_remainder()
    {
        const decimal originalCost = 100m, firstReturn = 33.33m;
        const decimal sourceRoundOne = 1.01m, sourceRoundTwo = -0.02m;
        const decimal returnRoundOne = 0.34m, returnRoundTwo = -0.01m;
        var entitlement = originalCost + sourceRoundOne + sourceRoundTwo;
        var alreadyReturned = firstReturn + returnRoundOne + returnRoundTwo;
        Assert.Equal(100.99m, entitlement); Assert.Equal(33.66m, alreadyReturned);
        var allocation = CostReplay.Calculate(CostMethod.Fifo, 2, [Receipt(1, 2, entitlement - alreadyReturned), Issue(2, 1), Issue(3, 1)]);
        Assert.Equal([33.67m, 33.66m], allocation.Steps.Skip(1).Select(s => s.Amount));
        Assert.Equal(entitlement, alreadyReturned + allocation.Steps.Skip(1).Sum(s => s.Amount));
        Assert.Equal((0m, 0m), (allocation.QuantityAfter, allocation.ValueAfter));
    }

    [Fact]
    public void Proposed_partial_fifo_entitlements_use_original_component_order_and_exact_component_remainders()
    {
        var allocation = CostReplay.Calculate(CostMethod.Fifo, 2,
            [Receipt(1, 2, 20), Receipt(2, 2, 40), Issue(3, 1), Issue(4, 2), Issue(5, 1)]);
        Assert.Equal([10m, 30m, 20m], allocation.Steps.Skip(2).Select(s => s.Amount));
        Assert.Equal(60m, allocation.Steps.Skip(2).Sum(s => s.Amount));
        Assert.Equal((0m, 0m), (allocation.QuantityAfter, allocation.ValueAfter));
        Assert.Equal([Id(1), Id(2)], allocation.Steps[3].Consumptions.Select(c => c.LayerId));
    }
}
