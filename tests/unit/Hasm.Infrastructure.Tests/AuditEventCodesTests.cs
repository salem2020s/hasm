using System.Reflection;
using Hasm.Infrastructure.Imports;

namespace Hasm.Infrastructure.Tests;

/// <summary>Persisted audit event codes are never renumbered and never shared: two different events with one code would be indistinguishable in the audit stream.</summary>
public class AuditEventCodesTests
{
    [Fact]
    public void Business_audit_event_codes_of_the_company_modules_are_unique()
    {
        // the system-stream events of the party registry (1310-1314) live in another stream and are listed explicitly
        var systemStream = new HashSet<string> { "PartyAuditEvents" };
        var all = typeof(ImportTemplateAuditEvents).Assembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("AuditEvents", StringComparison.Ordinal) && !systemStream.Contains(t.Name))
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(short)).Select(f => (Name: $"{t.Name}.{f.Name}", Code: (short)f.GetRawConstantValue()!)))
            .ToList();
        Assert.True(all.Count > 40);
        var duplicates = all.Where(x => x.Name != "ImportAuditEvents.Base").GroupBy(x => x.Code).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Name))}").ToList();
        Assert.Empty(duplicates);
    }
}
