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
        typeof(EpochContent), typeof(EpochContentResult), typeof(EpochEntryContent), typeof(EpochConsumption), typeof(EpochLayerCreation), typeof(EpochLayerKey), typeof(EpochLayerRemaining), typeof(EpochPositionTotal), typeof(EpochLayerState)];

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
            // no nested type except the compiler's own closure and display classes, whose methods are lambdas and local functions only (names start with '<') and whose fields pass the same check
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.True(nested.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null, type.Name + " declares the nested type " + nested.Name);
                Assert.Empty(nested.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
                Assert.All(nested.GetMethods(all).Where(m => !m.Name.StartsWith('<') && !m.IsSpecialName && m.DeclaringType == nested && m.Name is not ("ToString" or "Equals" or "GetHashCode")), m => Assert.Fail(type.Name + "/" + nested.Name + "." + m.Name + " is not a lambda"));
                foreach (var f in nested.GetFields(all).Where(f => !f.Name.StartsWith("<>9", StringComparison.Ordinal)))      // <>9 is the static cache of the nested closure class itself and of its lambdas
                    AssertPureType(f.FieldType, type.Name + "/" + nested.Name + "." + f.Name);
            }
            // a record or an enum-free data type holds no static state and no field of an unknown type
            if (!type.IsEnum && functions.All(f => f.Type != type))
                Assert.All(type.GetFields(all), f => Assert.False(f.IsStatic, type.Name + "." + f.Name + " is static state"));
            if (functions.All(f => f.Type != type))
            {
                Assert.Empty(type.GetMethods(all).Where(m => !Generated(m)));            // records and the enum: compiler-generated members only
                Assert.Empty(type.GetEvents(all));
            }
            foreach (var method in type.GetMethods(all).Where(m => !Generated(m)))      // the name rule, on every method that is not compiler-generated
                Assert.DoesNotMatch("Execute|Executor|Replay|Storno|Rebook", method.Name);
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method is MethodInfo mi) { Assert.False(typeof(Task).IsAssignableFrom(mi.ReturnType) || mi.ReturnType.Name.StartsWith("ValueTask", StringComparison.Ordinal), type.Name + "." + method.Name + " returns a task"); AssertPureType(mi.ReturnType, type.Name + "." + method.Name, null, member: false); }
                foreach (var p in method.GetParameters()) AssertPureType(p.ParameterType, type.Name + "." + method.Name + "(" + p.Name + ")", null, member: false);
            }
            foreach (var f in type.GetFields(all)) AssertPureType(f.FieldType, type.Name + "." + f.Name);
            foreach (var p in type.GetProperties(all)) AssertPureType(p.PropertyType, type.Name + "." + p.Name);
        }
    }

    // ---- T2 (ODC N6): the CALLEES of the pure derivation. Every call, constructor call and function pointer in the compiled code of Derive, its helpers and its closures must target a permitted type: a reflective
    // Activator.CreateInstance, a Type or MethodInfo invocation, a Process, a file, a socket or a service cannot hide in it. The scan has a positive control (it must see calls it is known to contain).
    private static readonly Dictionary<short, System.Reflection.Emit.OpCode> OpCodeTable = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    private static IEnumerable<MethodBase> CalleesOf(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        var generics = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodGenerics = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var i = 0; i < il.Length;)
        {
            short code = il[i++];
            if (code == 0xFE) code = (short)(0xFE00 | il[i++]);
            var op = OpCodeTable[code];
            int size = op.OperandType switch
            {
                System.Reflection.Emit.OperandType.InlineNone => 0,
                System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                System.Reflection.Emit.OperandType.InlineVar => 2,
                System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
            if (op.OperandType == System.Reflection.Emit.OperandType.InlineSig) throw new Xunit.Sdk.XunitException(method.DeclaringType!.Name + "." + method.Name + " uses calli");
            if (op.OperandType == System.Reflection.Emit.OperandType.InlineMethod)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, i), generics, methodGenerics)!;
            i += size;
        }
    }

    private static bool PermittedCallee(MethodBase callee)
    {
        var type = callee.DeclaringType!;
        if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)) return true;          // the anonymous objects the fingerprint serialises: compiler-generated plain data
        var ns = type.Namespace ?? "";
        var name = type.IsGenericType ? type.GetGenericTypeDefinition().Name : type.Name;
        if (ns.StartsWith("Hasm.", StringComparison.Ordinal))
        {
            // only plain data (records, enums, structs), the exception code and the pure static functions of the costing arithmetic and of this derivation
            return type.IsEnum || type.IsValueType || type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null ||
                (type.DeclaringType is { } outer && PureEpochTypes.Contains(outer)) || PureEpochTypes.Contains(type) ||
                (ns == "Hasm.Infrastructure.Costing" && name is "CostingMath" or "CostReplay" or "CostHistoryEntry" or "CostingException" or "CostingMath+Take");
        }
        if (ns.StartsWith("System.Collections", StringComparison.Ordinal) || ns.StartsWith("System.Linq", StringComparison.Ordinal) || ns.StartsWith("System.Numerics", StringComparison.Ordinal) ||
            ns == "System.Text.Json" || ns.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal) || ns == "System.Text") return true;
        if (ns == "System.Runtime.InteropServices") return name == "ImmutableCollectionsMarshal";        // what a collection expression of an ImmutableArray compiles to
        if (ns == "System.Security.Cryptography") return name == "SHA256";
        if (ns == "System.Threading") return name == "CancellationToken";
        if (ns != "System") return false;
        if (name == "IDisposable") return callee.Name == "Dispose";                                     // the end of every foreach
        if (name == "Type") return callee.Name == "GetTypeFromHandle";                                // typeof(...) in the compiler-generated record code
        return name is "Object" or "ValueType" or "Enum" or "String" or "Guid" or "Decimal" or "DateOnly" or "Math" or "Convert" or "Boolean" or "Int32" or "Int64" or "Int16" or "StringComparer" or "StringComparison" or
            "Nullable`1" or "Tuple" or "Exception" or "ArgumentException" or "InvalidOperationException" or "OperationCanceledException" or "Array" or "IEquatable`1" or "IComparable" or "IComparable`1" ||
            name.StartsWith("Func`", StringComparison.Ordinal) || name == "Predicate`1" || name == "Comparison`1" || name.StartsWith("Action`", StringComparison.Ordinal) || name.StartsWith("ValueTuple`", StringComparison.Ordinal);
    }

    private static void AssertCalleesPermitted()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var scanned = new HashSet<string>(); var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { typeof(EpochSchedule), typeof(EpochContent) })
        {
            var types = new List<Type> { root };
            for (var n = 0; n < types.Count; n++) types.AddRange(types[n].GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (var type in types)
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                    foreach (var callee in CalleesOf(method))
                    {
                        scanned.Add(callee.DeclaringType!.Name + "." + callee.Name);
                        if (!PermittedCallee(callee)) offenders.Add(callee.DeclaringType + "::" + callee.Name);
                    }
        }
        Assert.True(offenders.Count == 0, "callees outside the permitted set: " + string.Join("; ", offenders));
        // positive control: the scan sees the calls these functions are known to make
        Assert.Contains("SHA256.HashData", scanned); Assert.Contains("JsonSerializer.SerializeToUtf8Bytes", scanned); Assert.Contains("CostingException..ctor", scanned);
    }

    // the plain non-backend types a member, parameter or return type of the pure derivation may have (primitives are allowed by IsPrimitive): values, immutable and generic collections, tuples
    private static readonly HashSet<Type> PermittedSystemTypes =
    [
        typeof(Guid), typeof(decimal), typeof(string), typeof(DateOnly), typeof(System.Numerics.BigInteger), typeof(CancellationToken), typeof(Type), typeof(System.Text.StringBuilder),
        typeof(Nullable<>), typeof(System.Collections.Immutable.ImmutableArray<>), typeof(System.Collections.Immutable.ImmutableArray<>.Builder),
        typeof(IGrouping<,>), typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(KeyValuePair<,>), typeof(IReadOnlyList<>), typeof(IEnumerable<>),
        typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>), typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>),
    ];

    private static void AssertPureType(Type type, string where, HashSet<Type>? visited = null, bool member = true)
    {
        visited ??= [];
        if (type == typeof(void)) return;
        if (type.HasElementType) { AssertPureType(type.GetElementType()!, where, visited, member); return; }
        if (type.IsGenericType) { foreach (var argument in type.GetGenericArguments()) AssertPureType(argument, where, visited, member); }
        var ns = type.Namespace ?? "";
        Assert.False(type == typeof(IServiceProvider) || typeof(IServiceProvider).IsAssignableFrom(type), where + ": " + type);
        Assert.False(typeof(Delegate).IsAssignableFrom(type), where + ": a delegate type: " + type);
        Assert.False(member && type == typeof(object), where + ": an untyped member");
        Assert.False(ns.StartsWith("Npgsql", StringComparison.Ordinal) || ns.StartsWith("System.Data", StringComparison.Ordinal) || ns.StartsWith("System.Net", StringComparison.Ordinal) ||
            ns.StartsWith("System.IO", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal) || ns.StartsWith("System.Threading.Tasks", StringComparison.Ordinal), where + ": " + type);
        // T1: a type that is not a backend type must be on a short allow-list (no interface such as IDisposable, no System.Array, no MethodInfo or other reflection type, no object as a member)
        if (!ns.StartsWith("Hasm.", StringComparison.Ordinal))
        {
            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            Assert.True(type.IsPrimitive || PermittedSystemTypes.Contains(definition) || (member == false && type == typeof(object)), where + ": " + type + " is not on the allow-list of plain member types");
            return;
        }
        if (!visited.Add(type)) return;
        // a backend type may appear only as plain data (an enum or a record) or as the static function itself or an exception code: a service, store, resolver or transaction never does
        var isFunction = type == typeof(EpochSchedule) || type == typeof(EpochContent) || type == typeof(CostingException);
        var data = (type.DeclaringType is { } outer && PureEpochTypes.Contains(outer) && type.IsNestedPrivate) || type.IsEnum || isFunction || type.IsValueType ||      // a struct (a record struct) is data too: its fields are checked below
            type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;
        Assert.True(data, where + ": " + type + " is not plain data");
        if (type.IsEnum || isFunction) return;
        // and its own members are plain too, transitively: a record wrapping a service is found here
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var f in type.GetFields(all)) { Assert.False(f.IsStatic, where + ": " + type.Name + "." + f.Name + " is static state"); AssertPureType(f.FieldType, where + " > " + type.Name + "." + f.Name, visited); }
        foreach (var p in type.GetProperties(all)) AssertPureType(p.PropertyType, where + " > " + type.Name + "." + p.Name, visited, member: p.Name != "EqualityContract");
        foreach (var c in type.GetConstructors(all)) foreach (var p in c.GetParameters()) AssertPureType(p.ParameterType, where + " > " + type.Name + ".ctor(" + p.Name + ")", visited);
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
        AssertCalleesPermitted();
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
