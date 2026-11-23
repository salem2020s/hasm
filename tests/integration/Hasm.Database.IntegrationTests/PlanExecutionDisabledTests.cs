using System.Reflection;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Owner condition for the re-scoped PR 48 (checkpoint with execution DISABLED): the unfinished execution path cannot be activated or called. This class proves the APPLICATION layer only: no route of the running API, no
/// service of its container, no executor type or entry point in the backend assemblies, no code that appends the execution audit event (an IL scan, because constants are inlined), and no preset role (catalog and system
/// database) holding a plan permission. The DATABASE layer is proven by PlanExecutionRuntimeRoleDisabledTests: with the production grants the per-company runtime role holds SELECT only on the four execution tables and
/// a complete, well-formed epoch written by it is refused. N2, N4, N8, N9 and N22 (PlanExecutionEpochTests, PlanExecutionGuardTests) prove that a writer who COULD insert (the owner, or a role with the future executor
/// grant) is still refused when the execution is malformed or lacks its approved plan, audit event, period or position rules.
/// </summary>
public class PlanExecutionDisabledTests : JournalTestBase
{
    private readonly string storage = Path.Combine(Path.GetTempPath(), "hasm-pe-disabled-" + Guid.NewGuid().ToString("N"));

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(storage)) try { Directory.Delete(storage, true); } catch (IOException) { }
    }

    private Task Start() => StartApi(configure: b =>
    {
        b.UseSetting("Hasm:Attachments:StorageDirectory", Path.Combine(storage, "attachments"));
        b.UseSetting("Hasm:Imports:StorageDirectory", Path.Combine(storage, "imports"));
        b.ConfigureServices(s =>
        {
            s.AddSingleton<KekRing>(new InMemoryKekRing("k1"));
            s.AddSingleton(new EncryptionOptions { AllowInsecureFilePermissions = true });
            s.AddSingleton<ICommercialDocumentTypeRegistry>(new CommercialDocumentTypeRegistry([CommercialTestType.Name]));
        });
    });

    private static readonly Regex PlanWords = new(@"compensation|epoch|execut|replay", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool IsPlanRoute(string route) => PlanWords.IsMatch(route) || route.Split('/').Any(seg => seg.Equals("plan", StringComparison.OrdinalIgnoreCase) || seg.Equals("plans", StringComparison.OrdinalIgnoreCase));

    [PostgresFact]
    public async Task No_route_of_the_running_API_plans_approves_or_executes_a_compensation_plan()
    {
        await Start();
        var routes = factory!.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "").ToList();
        Assert.True(routes.Count > 100, "the route table must be the real one");
        Assert.Empty(routes.Where(IsPlanRoute));
    }

    [PostgresFact]
    public async Task No_service_of_the_running_API_stores_approves_or_executes_a_compensation_plan()
    {
        await Start();
        var provider = factory!.Services;
        // the two services of slices 1 and 2 are internal, unregistered and have no caller: the container cannot produce them
        Assert.Null(provider.GetService(typeof(CompensationPlanService)));
        Assert.Null(provider.GetService(typeof(CompensationPlanApprovalService)));
        // and nothing registered in the container is a plan, epoch or execution type
        var registered = provider.GetRequiredService<IServiceProviderIsService>();
        foreach (var type in typeof(CompensationPlanService).Assembly.GetTypes().Where(t => PlanTypeName.IsMatch(t.Name) && t.DeclaringType != typeof(CompanySchema) && !t.IsAbstract))
            Assert.False(registered.IsService(type), type.FullName);
    }

    private static readonly Regex PlanTypeName = new(@"Execut(or|ion)|CompensationPlan|Epoch|Replay(Executor|Writer)", RegexOptions.CultureInvariant);

    // the ONLY types named like a plan, epoch or execution that may exist besides the services above: values and one static function (slice E1), allowed by identity
    private static readonly Type[] PureEpochTypes = [typeof(EpochSchedule), typeof(EpochScheduleResult), typeof(EpochOperationPlan), typeof(EpochEntryPlan), typeof(EpochLineagePlan), typeof(EpochRole),
        typeof(EpochContent), typeof(EpochContentResult), typeof(EpochEntryContent), typeof(EpochConsumption), typeof(EpochLayerCreation), typeof(EpochLayerKey), typeof(EpochLayerRemaining), typeof(EpochPositionTotal)];

    /// <summary>
    /// Pins the exact shape of the allowed types so the exception cannot hide an executor: EpochSchedule is a static class whose only own methods are Derive (exact signature) and the private Inconsistent; the
    /// records and the enum declare nothing but compiler-generated members; nothing returns a Task or ValueTask; no field, property, constructor, method parameter or return type comes from Npgsql, System.Data,
    /// System.Net, IServiceProvider or any backend type that is not a data record, enum or the static function itself (a service, store or resolver); and the execute/replay/storno/rebook method-name rule applies to them.
    /// </summary>
    private static void AssertPureEpochShape()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        bool Generated(MemberInfo m) => m.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null || m.Name.StartsWith('<');
        // the static functions: exactly these own methods, Derive with its exact signature
        var functions = new (Type Type, string[] Methods, Type[] Parameters, Type Result)[]
        {
            (typeof(EpochSchedule), ["Derive", "Inconsistent"], [typeof(CostFlowSnapshot), typeof(CostFlowComparisonResult), typeof(CancellationToken)], typeof(EpochScheduleResult)),
            (typeof(EpochContent), ["A", "CheckSums", "Derive", "Disagree", "Inconsistent", "Q"], [typeof(CostFlowSnapshot), typeof(EpochScheduleResult), typeof(CancellationToken)], typeof(EpochContentResult)),
        };
        var schedule = typeof(EpochSchedule);
        foreach (var (type, methods, parameters, result) in functions)
        {
            Assert.True(type.IsAbstract && type.IsSealed, type.Name + " must be a static class");
            var own = type.GetMethods(all).Where(m => !Generated(m)).ToList();
            Assert.Equal(methods.Order(StringComparer.Ordinal), own.Select(m => m.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal));
            var derive = own.Single(m => m.Name == "Derive");
            Assert.True(derive.IsStatic && derive.IsAssembly, "Derive is internal static");
            Assert.Equal(parameters, derive.GetParameters().Select(p => p.ParameterType));
            Assert.Equal(result, derive.ReturnType);
            Assert.Empty(type.GetConstructors(all)); Assert.Empty(type.GetProperties(all)); Assert.Empty(type.GetEvents(all));
            Assert.All(type.GetFields(all), f => Assert.True(f.IsLiteral, type.Name + " holds constants only: " + f.Name));
        }
        foreach (var type in PureEpochTypes)
        {
            Assert.False(type.IsPublic, type.FullName);
            if (functions.All(f => f.Type != type))
            {
                Assert.Empty(type.GetMethods(all).Where(m => !Generated(m)));            // records and the enum: compiler-generated members only
                Assert.Empty(type.GetEvents(all));
            }
            foreach (var method in type.GetMethods(all).Where(m => !Generated(m)))      // the name rule, on every method that is not compiler-generated
                Assert.DoesNotMatch("Execute|Executor|Replay|Storno|Rebook", method.Name);
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method is MethodInfo mi) { Assert.False(typeof(Task).IsAssignableFrom(mi.ReturnType) || mi.ReturnType.Name.StartsWith("ValueTask", StringComparison.Ordinal), type.Name + "." + method.Name + " returns a task"); AssertPureType(mi.ReturnType, type.Name + "." + method.Name); }
                foreach (var p in method.GetParameters()) AssertPureType(p.ParameterType, type.Name + "." + method.Name + "(" + p.Name + ")");
            }
            foreach (var f in type.GetFields(all)) AssertPureType(f.FieldType, type.Name + "." + f.Name);
            foreach (var p in type.GetProperties(all)) AssertPureType(p.PropertyType, type.Name + "." + p.Name);
        }
    }

    private static void AssertPureType(Type type, string where)
    {
        if (type == typeof(void)) return;
        if (type.HasElementType) { AssertPureType(type.GetElementType()!, where); return; }
        if (type.IsGenericType) { foreach (var argument in type.GetGenericArguments()) AssertPureType(argument, where); }
        var ns = type.Namespace ?? "";
        Assert.False(type == typeof(IServiceProvider) || typeof(IServiceProvider).IsAssignableFrom(type), where + ": " + type);
        Assert.False(ns.StartsWith("Npgsql", StringComparison.Ordinal) || ns.StartsWith("System.Data", StringComparison.Ordinal) || ns.StartsWith("System.Net", StringComparison.Ordinal) ||
            ns.StartsWith("System.IO", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal) || ns.StartsWith("System.Threading.Tasks", StringComparison.Ordinal), where + ": " + type);
        if (ns.StartsWith("Hasm.", StringComparison.Ordinal))
        {
            // a backend type may appear only as plain data (an enum or a record) or as the static function itself: a service, store, resolver or transaction never does
            var data = (type.DeclaringType is { } outer && PureEpochTypes.Contains(outer) && type.IsNestedPrivate) || type.IsEnum || type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null || type == typeof(EpochSchedule) || type == typeof(EpochContent) || type == typeof(CostingException);   // the refusal the derivation throws is a plain exception with a code
            Assert.True(data, where + ": " + type + " is not plain data");
        }
    }

    [PostgresFact]
    public void No_executor_type_or_entry_point_exists_in_the_backend()
    {
        // the schema dictionary (CompanySchema.CompensationPlanExecution and friends) is names of database objects, not code that runs
        var assemblies = new[] { typeof(CompensationPlanService).Assembly, typeof(Program).Assembly };
        var types = assemblies.SelectMany(a => a.GetTypes()).Where(t => t.DeclaringType != typeof(CompanySchema) && !t.Name.StartsWith('<') && t.Namespace?.Contains(".Backup", StringComparison.Ordinal) != true).ToList();   // the backup retention sweep has its own executor, unrelated to plans
        // the services of this PR: the only plan types that may exist (storage, approval, their stored shape, bindings and inputs), none of which executes
        var allowed = new HashSet<string> { nameof(CompensationPlanService), nameof(CompensationPlanApprovalService), nameof(CompensationPlanStore), nameof(CompensationPlanBinding), nameof(CompensationPlanInput),
            nameof(StoredCompensationPlan), nameof(CompensationPlanFingerprints), nameof(CompensationPlan), nameof(CompensationPlanResult), nameof(CompensationLine), nameof(CompensationCalculator), nameof(CompensationDateGuard) };
        // slice E1: the pure derivation types are allowed BY TYPE IDENTITY (never by name: a same-named type elsewhere is an offender) and only in the exact shape AssertPureEpochShape pins
        var offenders = types.Where(t => PlanTypeName.IsMatch(t.Name) && !allowed.Contains(t.Name) && !PureEpochTypes.Contains(t) && !t.Name.StartsWith("DocumentState", StringComparison.Ordinal)).Select(t => t.FullName).ToList();
        Assert.Empty(offenders);
        AssertPureEpochShape();
        // no method of the services or of anything in a Costing/plan namespace is an execution entry point
        foreach (var type in new[] { typeof(CompensationPlanService), typeof(CompensationPlanApprovalService), typeof(CompensationPlanStore), typeof(CompensationPlanBinding) })
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly), m => Regex.IsMatch(m.Name, "Execute|Executor|Replay|Storno|Rebook"));
        // and nothing in the code base appends the audit event of an execution (1154): constants are inlined, so the compiled code of every method is scanned for the constant; a positive control proves the scan sees
        // the same kind of constant (1155, the plan-proposed event the storage service appends)
        var everything = assemblies.SelectMany(a => a.GetTypes()).ToList();      // closures and async state machines hold the compiled code of lambdas and awaits
        Assert.True(UsesConstant(everything, CostingAuditEvents.PlanProposed), "the scan must find a constant that IS used");
        Assert.False(UsesConstant(everything, CostingAuditEvents.PlanExecuted), "nothing may append the plan-executed audit event");
    }

    private static bool UsesConstant(IEnumerable<Type> types, short value)
    {
        // ldc.i4 (0x20) followed by the 32-bit little-endian value: how the compiler loads an inlined constant that does not fit ldc.i4.s
        byte[] pattern = [0x20, .. BitConverter.GetBytes((int)value)];
        foreach (var type in types)
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                for (var i = 0; i + pattern.Length <= il.Length; i++)
                    if (il.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return true;
            }
        return false;
    }

    [PostgresFact]
    public async Task No_preset_role_holds_a_plan_permission_in_the_catalog_or_in_the_system_database()
    {
        await Start();
        string[] codes = [PermissionCatalog.Codes.CompensationPlansCreate, PermissionCatalog.Codes.CompensationPlansApprove];
        foreach (var (id, (key, permissions)) in PresetRoles.Definitions) Assert.DoesNotContain(permissions, p => codes.Contains(p));
        // the migrated system database: no row of any role (preset or not) names them; an approver or executor preset is the open owner question X4
        var rows = await Scalar<long>($"SELECT count(*) FROM {SystemSchema.RolePermission.Table} WHERE {SystemSchema.RolePermission.Permission} = ANY(@codes)", ("codes", codes));
        Assert.Equal(0L, rows);
        // the codes exist in the catalog and are privileged (D-096), so a role that holds one can never be a non-privileged role
        Assert.All(codes, c => { Assert.True(PermissionCatalog.Default.TryGet(c, out _)); Assert.True(PrivilegedAccess.IsPrivilegedPermission(c)); });
    }
}
