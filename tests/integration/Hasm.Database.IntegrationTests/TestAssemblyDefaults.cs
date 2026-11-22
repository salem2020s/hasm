using System.Runtime.CompilerServices;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// The suites of this assembly start the API against a DISPOSABLE cluster with ONE account: the runtime account is the administrative (owner) account. Since owner decision D-235 the API refuses
/// that account for the TOTP secret store, so the assembly sets the explicit opt-out the product provides for exactly this case (<c>Hasm:Database:AllowOverprivilegedSystemRuntime</c>), once,
/// here, as an environment default. It is never set by the product. <c>SystemRuntimeLeastPrivilegeTests</c> overrides it explicitly (off) and proves the refusal and the real restricted role.
/// </summary>
internal static class TestAssemblyDefaults
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (Environment.GetEnvironmentVariable("Hasm__Database__AllowOverprivilegedSystemRuntime") is null)
            Environment.SetEnvironmentVariable("Hasm__Database__AllowOverprivilegedSystemRuntime", "true");
    }
}
