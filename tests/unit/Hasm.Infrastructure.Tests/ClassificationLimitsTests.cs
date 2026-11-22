using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;

namespace Hasm.Infrastructure.Tests;

public class ClassificationLimitsTests
{
    [Fact]
    public void The_default_command_budget_covers_the_one_time_schema_gate_of_the_first_connection_with_a_fixed_margin()
    {
        // The schema gate sends one command per guarded table (CompanyRuntimePrivileges.Tables) to the shared quota of a read. The default must stay above that cost plus room for the read itself and the live permission
        // checks of a partially visible caller, so a company migration that adds tables fails HERE (and gets a larger default) instead of making an evidence read refuse with a budget error (hosted failure at 9fbe3a6).
        var gate = CompanyRuntimePrivileges.Tables.Count;
        Assert.True(new ClassificationLimits().Commands >= gate * 2 + 200, $"default {new ClassificationLimits().Commands} vs {gate} tables");
        Assert.Equal(ClassificationLimits.DefaultCommands, new ClassificationLimits().Commands);
    }
}
