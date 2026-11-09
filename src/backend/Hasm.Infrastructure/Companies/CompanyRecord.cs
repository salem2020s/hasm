using Hasm.Infrastructure.Naming;

namespace Hasm.Infrastructure.Companies;

/// <summary>Numeric values are persisted in the system database; never renumber.</summary>
public enum ProvisioningState : short
{
    /// <summary>Registered; its physical database has not been created.</summary>
    Pending = 0,
    Provisioning = 1,
    Ready = 2,
    Failed = 3,
}

/// <summary>
/// Minimal central registry entry. Holds only identity and provisioning state; company
/// names, tax data, accounting and operational data are not stored centrally (see docs/DEVELOPMENT.md).
/// </summary>
public sealed record CompanyRecord
{
    public Guid Id { get; }
    public string PhysicalDatabaseName { get; }
    public ProvisioningState State { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset StateChangedAtUtc { get; }
    /// <summary>Number of provisioning attempts started so far.</summary>
    public int ProvisioningAttempts { get; }
    /// <summary>Short sanitized failure code of the last failed attempt; never an error message.</summary>
    public string? LastErrorCode { get; }

    public CompanyRecord(Guid id, string physicalDatabaseName, ProvisioningState state,
        DateTimeOffset createdAtUtc, DateTimeOffset stateChangedAtUtc, int provisioningAttempts = 0, string? lastErrorCode = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Company id must not be empty.", nameof(id));
        PhysicalIdentifier.Validate(physicalDatabaseName, nameof(physicalDatabaseName));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (provisioningAttempts < 0) throw new ArgumentOutOfRangeException(nameof(provisioningAttempts));
        if (stateChangedAtUtc < createdAtUtc)
            throw new ArgumentException("State change cannot precede creation.", nameof(stateChangedAtUtc));
        Id = id;
        PhysicalDatabaseName = physicalDatabaseName;
        State = state;
        CreatedAtUtc = createdAtUtc;
        StateChangedAtUtc = stateChangedAtUtc;
        ProvisioningAttempts = provisioningAttempts;
        LastErrorCode = lastErrorCode;
    }

    public static CompanyRecord CreatePending(DateTimeOffset nowUtc, Func<string>? nameGenerator = null) =>
        new(Guid.NewGuid(), (nameGenerator ?? PhysicalIdentifier.GenerateDatabaseName)(), ProvisioningState.Pending, nowUtc, nowUtc);

    public static bool CanTransition(ProvisioningState from, ProvisioningState to) => (from, to) switch
    {
        (ProvisioningState.Pending, ProvisioningState.Provisioning) => true,
        (ProvisioningState.Provisioning, ProvisioningState.Ready) => true,
        (ProvisioningState.Provisioning, ProvisioningState.Failed) => true,
        (ProvisioningState.Failed, ProvisioningState.Provisioning) => true,
        _ => false,
    };

    public CompanyRecord TransitionTo(ProvisioningState next, DateTimeOffset nowUtc)
    {
        if (!CanTransition(State, next))
            throw new InvalidOperationException($"Transition {State} -> {next} is not allowed.");
        return new CompanyRecord(Id, PhysicalDatabaseName, next, CreatedAtUtc, nowUtc < CreatedAtUtc ? CreatedAtUtc : nowUtc, ProvisioningAttempts, LastErrorCode);
    }
}
