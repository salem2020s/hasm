using System.Runtime.CompilerServices;
using Hasm.Infrastructure.Security;

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
        // Identity integrity S-1: every authenticated request needs the identity keys, which come from the existing key ring. The assembly gets ONE disposable ring (a random key in a temporary directory, never
        // committed) as an environment default, so a suite that registers no ring of its own still has one; a suite that sets its own ring or its own variables keeps them.
        if (Environment.GetEnvironmentVariable("Hasm__Encryption__KeyDirectory") is null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hasm-test-identity-keys-" + Guid.NewGuid().ToString("N"));
            FileKekRing.Generate(dir, "k1");
            Environment.SetEnvironmentVariable("Hasm__Encryption__KeyDirectory", dir);
            Environment.SetEnvironmentVariable("Hasm__Encryption__ActiveKeyId", "k1");
            Environment.SetEnvironmentVariable("Hasm__Encryption__AllowInsecureFilePermissions", "true");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, true); } catch { } };
        }
    }
}

/// <summary>The identity keys of the assembly's disposable ring (the same ring the API under test reads from the environment), for tests that construct the stores directly.</summary>
internal static class TestIdentityKeys
{
    public static readonly IdentityKeys Instance = new(new FileKekRing(new EncryptionOptions
    {
        KeyDirectory = Environment.GetEnvironmentVariable("Hasm__Encryption__KeyDirectory"),
        ActiveKeyId = Environment.GetEnvironmentVariable("Hasm__Encryption__ActiveKeyId"),
        AllowInsecureFilePermissions = true,
    }));
}
