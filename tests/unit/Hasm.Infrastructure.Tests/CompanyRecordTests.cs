using Hasm.Infrastructure.Companies;
using Hasm.Infrastructure.Naming;

namespace Hasm.Infrastructure.Tests;

public class CompanyRecordTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatePending_generates_identity_and_opaque_name()
    {
        var c = CompanyRecord.CreatePending(T0);
        Assert.NotEqual(Guid.Empty, c.Id);
        Assert.True(PhysicalIdentifier.IsValid(c.PhysicalDatabaseName));
        Assert.Equal(ProvisioningState.Pending, c.State);
    }

    [Fact]
    public void Empty_id_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new CompanyRecord(Guid.Empty, "d1", ProvisioningState.Pending, T0, T0));

    [Theory]
    [InlineData("")]
    [InlineData("Acme Ltd")]
    [InlineData("pg_x")]
    public void Invalid_database_name_is_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => new CompanyRecord(Guid.NewGuid(), name, ProvisioningState.Pending, T0, T0));

    [Fact]
    public void Undefined_state_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompanyRecord(Guid.NewGuid(), "d1", (ProvisioningState)99, T0, T0));

    [Fact]
    public void State_change_cannot_precede_creation() =>
        Assert.Throws<ArgumentException>(() => new CompanyRecord(Guid.NewGuid(), "d1", ProvisioningState.Pending, T0, T0.AddSeconds(-1)));

    [Fact]
    public void State_values_are_pinned_because_they_are_persisted()
    {
        Assert.Equal(0, (short)ProvisioningState.Pending);
        Assert.Equal(1, (short)ProvisioningState.Provisioning);
        Assert.Equal(2, (short)ProvisioningState.Ready);
        Assert.Equal(3, (short)ProvisioningState.Failed);
    }

    [Theory]
    [InlineData(ProvisioningState.Pending, ProvisioningState.Provisioning, true)]
    [InlineData(ProvisioningState.Provisioning, ProvisioningState.Ready, true)]
    [InlineData(ProvisioningState.Provisioning, ProvisioningState.Failed, true)]
    [InlineData(ProvisioningState.Failed, ProvisioningState.Provisioning, true)]
    [InlineData(ProvisioningState.Pending, ProvisioningState.Ready, false)]
    [InlineData(ProvisioningState.Ready, ProvisioningState.Provisioning, false)]
    [InlineData(ProvisioningState.Ready, ProvisioningState.Failed, false)]
    [InlineData(ProvisioningState.Pending, ProvisioningState.Pending, false)]
    public void Transitions_follow_the_state_machine(ProvisioningState from, ProvisioningState to, bool allowed)
        => Assert.Equal(allowed, CompanyRecord.CanTransition(from, to));

    [Fact]
    public void TransitionTo_preserves_identity_and_rejects_illegal_moves()
    {
        var c = CompanyRecord.CreatePending(T0);
        var next = c.TransitionTo(ProvisioningState.Provisioning, T0.AddMinutes(1));
        Assert.Equal(c.Id, next.Id);
        Assert.Equal(c.PhysicalDatabaseName, next.PhysicalDatabaseName);
        Assert.Equal(T0.AddMinutes(1), next.StateChangedAtUtc);
        Assert.Throws<InvalidOperationException>(() => c.TransitionTo(ProvisioningState.Ready, T0));
    }
}
