using Hasm.Infrastructure.Audit;

namespace Hasm.Api.Auth;

public static class AuditSetup
{
    /// <summary>
    /// Registers the tamper-evident audit trail (keys and checkpoint directory from <c>Hasm:Audit</c>). Configuration is validated on first
    /// use, not at startup, so anonymous endpoints keep working; every audited mutation fails closed (503 audit_unavailable) until the
    /// keys and the checkpoint directory are configured.
    /// </summary>
    public static IServiceCollection AddHasmAudit(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(sp => AuditTrail.FromOptions(
            AuditOptions.FromConfiguration(configuration),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Hasm.Audit")));
        return services;
    }
}
