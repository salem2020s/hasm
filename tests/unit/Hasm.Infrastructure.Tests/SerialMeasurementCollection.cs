namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Tests that MEASURE the process (managed heap size, CPU time) cannot run next to other tests: xUnit runs test classes in parallel, so another test's allocations or CPU use would be
/// counted as the measured code's. This collection has parallelization disabled, so its classes run one after another and ALONE (xUnit runs such collections after the parallel ones).
/// Measurement thresholds in these classes are unchanged by being here; only the interference is removed (decision D-063). <c>DatabaseDropTests</c> is here for the same reason: it reads a counter of
/// the whole process and asserts its exact steps.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialMeasurementCollection
{
    public const string Name = "Serial measurement";
}
