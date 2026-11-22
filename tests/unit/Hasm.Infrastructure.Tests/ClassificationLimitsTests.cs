using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class ClassificationLimitsTests
{
    [Fact]
    public void The_default_command_budget_of_an_evidence_read_is_400()
    {
        // The company schema gate of a cold connection is a constant number of commands now (its privilege check is one set-based query, see
        // ClassificationEvidenceTests.The_schema_gate_of_a_cold_connection_costs_a_constant_number_of_commands...), so the budget does not grow with the number of tables and stays at its original bound.
        Assert.Equal(400, ClassificationLimits.DefaultCommands);
        Assert.Equal(400, new ClassificationLimits().Commands);
    }
}
