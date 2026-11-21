using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// API input-hardening sweep (planning item 4): hostile input on EVERY endpoint that takes a query value or a body, discovered from the running API's route table and the handlers' own signatures (so a new
/// endpoint or a new field is covered without editing this file). Hostile means injection-looking text, LIKE wildcards, oversize values, invalid GUIDs and numbers, and Unicode edge cases (NUL, bidi controls,
/// zero-width characters, long combining-mark runs, astral characters). Every case must be answered with a mapped refusal or a harmless success, never a server error, and never with server internals in the body
/// (exception or database type names, SQL text, stack frames, connection settings). A failing case names the route and the exact input so it is a defect report, not a flaky test. Disposable cluster only.
/// </summary>
public class ApiInputHardeningTests : JournalTestBase
{
    private const string Prefix = "/api/v1/";
    private readonly string storage = Path.Combine(Path.GetTempPath(), "hasm-hardening-" + Guid.NewGuid().ToString("N"));

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(storage)) try { Directory.Delete(storage, true); } catch (IOException) { }
    }

    private async Task Start()
    {
        await StartApi(configure: b =>
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
    }

    // ------------------------------------------------------------------------------------------------------------------------------- hostile values

    private static readonly string[] HostileText =
    [
        "%", "_", "%_%", "\\", "\\%", "' OR '1'='1", "'; DROP TABLE t_x; --", "\" OR \"\"=\"", "${jndi:ldap://x/a}", "{{7*7}}", "../../etc/passwd", "<script>alert(1)</script>",
        "a\0b", "\0", "\u202Eevil\u202C", "\u2066x\u2069", "\u200Bzero\u200B", "e" + string.Concat(Enumerable.Repeat("\u0301", 300)), string.Concat(Enumerable.Repeat("\U0001F600", 60)),
        "\uFEFFbom", "\r\nInjected: header", new string('A', 5_000), "null", "-1", "9999999999999999999999", "NaN", "0x41",
    ];

    /// <summary>Server internals that must never reach a response body: exception, driver and namespace names, stack frames, SQL text and database error wording, physical (opaque) object names and connection settings.</summary>
    private static readonly Regex Leak = new(
        @"Npgsql|PostgresException|SqlState|SQLSTATE|\bException\b|\bat [A-Za-z_][A-Za-z0-9_.]*\(|\b(System|Microsoft|Hasm)\.[A-Z][A-Za-z]+\.[A-Za-z]|\b[tcfkr]_[0-9a-f]{8}\b|\b(Host|Password|Username|Port|Database)=|violates|duplicate key|syntax error at|invalid byte sequence|relation ""|\b(SELECT|INSERT INTO|DELETE FROM)\b.{0,60}\b(FROM|VALUES|WHERE)\b|\bUPDATE\b\s+\w+\s+SET\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorCode = new("^[a-z0-9_]{1,80}$", RegexOptions.Compiled);

    // ------------------------------------------------------------------------------------------------------------------------------- discovery

    private sealed record Target(string Method, string Pattern, string[] RouteParams, string[] QueryParams, Type? Body, bool AnonymousOk, bool FileBody);

    /// <summary>Every /api/v1 route with its route parameters, its query parameters and its body type, from the handler signature.</summary>
    private List<Target> Discover()
    {
        var sp = factory!.Services;
        using var scope = sp.CreateScope();
        var all = new List<Target>();
        foreach (var e in sp.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = e.RoutePattern.RawText ?? "";
            var pattern = raw.StartsWith('/') ? raw : "/" + raw;
            if (!pattern.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (pattern == "/api/v1/health" || pattern.EndsWith("/auth/logout", StringComparison.Ordinal)) continue;         // no input / would end the session under test
            if (pattern.Contains("/maintenance/", StringComparison.Ordinal)) continue;                                   // administrative sweeps over every file, not input handlers
            var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            var handler = e.Metadata.OfType<MethodInfo>().FirstOrDefault();
            var placeholders = e.RoutePattern.Parameters.Select(p => p.Name).ToHashSet();
            var routeParams = new List<string>();
            var queryParams = new List<string>();
            Type? body = null;
            foreach (var p in handler?.GetParameters() ?? [])
            {
                var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                if (type == typeof(HttpContext) || type == typeof(HttpRequest) || type == typeof(CancellationToken) || p.Name is null) continue;
                if (placeholders.Contains(p.Name)) { routeParams.Add(p.Name); continue; }
                if (type == typeof(string) || type == typeof(Guid) || type == typeof(bool) || type == typeof(DateOnly) || type.IsPrimitive || type.IsEnum || type == typeof(decimal)) { queryParams.Add(p.Name); continue; }
                if (scope.ServiceProvider.GetService(type) is not null) continue;                                         // an injected service
                body = type;
            }
            foreach (var m in methods) all.Add(new Target(m, pattern, [.. placeholders], [.. queryParams], body, pattern.Contains("/auth/login", StringComparison.Ordinal), pattern.EndsWith("/attachments/", StringComparison.Ordinal) || pattern.EndsWith("/imports/", StringComparison.Ordinal)));
        }
        return all;
    }

    /// <summary>A valid-looking value for a route placeholder (a fresh GUID, or a fixed token for the string placeholders).</summary>
    private static string RouteValue(string name, string? hostile = null) => hostile is not null && name is "code" or "docType" or "adapter" or "name"
        ? Uri.EscapeDataString(hostile)
        : name switch { "code" => "USD", "docType" => "journal", "adapter" => "inventory-units", "name" => "template-x", _ => Guid.NewGuid().ToString() };

    private static string Url(Target t, string? hostileRoute = null, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        var path = Regex.Replace(t.Pattern, @"\{([a-zA-Z]+)(:[a-z]+)?\}", m => RouteValue(m.Groups[1].Value, hostileRoute));
        var qs = string.Join("&", (query ?? []).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return qs.Length == 0 ? path : path + "?" + qs;
    }

    // ------------------------------------------------------------------------------------------------------------------------------- body generation

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static IEnumerable<(string Name, Type Type)> Members(Type t)
    {
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is not null && ctor.GetParameters().Length > 0) return ctor.GetParameters().Select(p => (p.Name!, p.ParameterType));
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => (p.Name, p.PropertyType));
    }

    private static object? ValidValue(Type type, int depth = 0)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return "x";
        if (t == typeof(Guid)) return Guid.NewGuid();
        if (t == typeof(bool)) return false;
        if (t == typeof(DateOnly)) return "2026-03-10";
        if (t == typeof(DateTimeOffset) || t == typeof(DateTime)) return "2026-03-10T00:00:00Z";
        if (t.IsEnum || t.IsPrimitive || t == typeof(decimal)) return 1;
        if (t.IsArray || (t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))) return Array.Empty<object>();
        if (depth < 2 && t.IsClass) return Members(t).ToDictionary(m => Camel(m.Name), m => ValidValue(m.Type, depth + 1));
        return null;
    }

    /// <summary>The body variants for a DTO: every string field set to each hostile text, every typed field made invalid, and structural abuse of the JSON itself.</summary>
    private static IEnumerable<(string Label, string Json)> BodyVariants(Type body)
    {
        var members = Members(body).ToList();
        Dictionary<string, object?> Base() => members.ToDictionary(m => Camel(m.Name), m => ValidValue(m.Type));
        string J(object? o) => JsonSerializer.Serialize(o);
        var strings = members.Where(m => (Nullable.GetUnderlyingType(m.Type) ?? m.Type) == typeof(string)).Select(m => Camel(m.Name)).ToList();
        foreach (var h in HostileText)
        {
            if (strings.Count == 0) break;
            var b = Base();
            foreach (var s in strings) b[s] = h;
            yield return ($"all text fields = {Show(h)}", J(b));
        }
        foreach (var m in members)
        {
            var t = Nullable.GetUnderlyingType(m.Type) ?? m.Type;
            var name = Camel(m.Name);
            if (t == typeof(Guid)) foreach (var bad in new[] { "not-a-guid", "", "00000000-0000-0000-0000-000000000000", "{" + Guid.NewGuid() + "}", new string('f', 100) }) { var b = Base(); b[name] = bad; yield return ($"{name} = {Show(bad)}", J(b)); }
            else if (t == typeof(DateOnly) || t == typeof(DateTimeOffset) || t == typeof(DateTime)) foreach (var bad in new[] { "2026-13-45", "x", "", "0000-00-00", "99999-01-01" }) { var b = Base(); b[name] = bad; yield return ($"{name} = {Show(bad)}", J(b)); }
            else if (t.IsPrimitive || t == typeof(decimal) || t.IsEnum) foreach (var bad in new[] { "-1", "9999999999999999999999999999", "0.0000000000000000000000000001", "\"abc\"", "null" }) { var raw = Base().Select(kv => $"{J(kv.Key)}:{(kv.Key == name ? bad : J(kv.Value))}"); yield return ($"{name} = {bad}", "{" + string.Join(",", raw) + "}"); }
            else if (t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) { var b = Base(); b[name] = Enumerable.Range(0, 3000).Select(_ => new Dictionary<string, object?>()).ToArray(); yield return ($"{name} = 3000 empty objects", J(b)); var c = Base(); c[name] = "not-an-array"; yield return ($"{name} = a string", J(c)); }
        }
        if (strings.Count > 0) { var b = Base(); b[strings[0]] = new string('x', 1_200_000); yield return ("first text field = 1.2 million characters", J(b)); }
        yield return ("empty object", "{}");
        yield return ("JSON null", "null");
        yield return ("empty body", "");
        yield return ("JSON array", "[]");
        yield return ("JSON string", "\"x\"");
        yield return ("malformed JSON", "{\"a\":");
        yield return ("nested 400 deep", new string('[', 400) + new string(']', 400));
        yield return ("5000 unknown fields", "{" + string.Join(",", Enumerable.Range(0, 5000).Select(i => $"\"f{i}\":1")) + "}");
        yield return ("duplicate keys", strings.Count > 0 ? $"{{\"{strings[0]}\":\"a\",\"{strings[0]}\":\"b\"}}" : "{\"a\":1,\"a\":2}");
        yield return ("invalid UTF-8 escape", "{\"x\":\"\\ud800\"}");
        yield return ("NUL escape in a key", "{\"a\\u0000b\":1}");
    }

    private static string Show(string s) => s.Length > 40 ? $"{s[..20].Replace("\0", "\\0")}...({s.Length} chars)" : s.Replace("\0", "\\0").Replace("\r", "\\r").Replace("\n", "\\n");

    // ------------------------------------------------------------------------------------------------------------------------------- sending and judging

    private async Task<(HttpStatusCode Status, string Body, string? ContentType)> Send(HttpMethod method, string url, string? cookie, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("X-Hasm-Request", "1");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        try
        {
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return ((HttpStatusCode)0, ex.GetType().Name + ": " + ex.Message, null);                                       // the test server aborted the request: reported as a failure below
        }
    }

    /// <summary>The verdict for one answer: null when acceptable, else why not.</summary>
    private static string? Judge(HttpStatusCode status, string body, string? contentType)
    {
        var code = (int)status;
        if (code == 0) return "the connection failed: " + (body.Length > 120 ? body[..120] : body);
        if (code >= 500) return $"server error {code}: {(body.Length > 160 ? body[..160] : body)}";
        if (Leak.Match(body) is { Success: true } m) return $"the body contains server internals ({m.Value}): {(body.Length > 160 ? body[..160] : body)}";
        if (code >= 400 && body.Length > 0 && contentType == "application/json")
        {
            try
            {
                var root = JsonDocument.Parse(body).RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && !ErrorCode.IsMatch(e.GetString()!)) return $"the error is not a short code: {e.GetString()}";
            }
            catch (JsonException) { return "an error status with an invalid JSON body"; }
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------------------------------------- tests

    [PostgresFact]
    public async Task Hostile_query_and_route_values_on_every_endpoint_give_a_mapped_answer_never_a_server_error_and_never_server_internals()
    {
        await Start();
        var admin = await SignIn(Admin);
        var targets = Discover();
        Assert.True(targets.Count > 250, $"the route table looks incomplete: {targets.Count} routes");
        var failures = new List<string>();
        var sent = 0;
        foreach (var t in targets)
        {
            var cookie = t.AnonymousOk ? null : admin;
            HttpContent? Content() => t.Method == "GET" || t.Method == "DELETE" ? null : t.FileBody ? new ByteArrayContent(Encoding.UTF8.GetBytes("code,name\nX,Y\n")) : new StringContent("{}", Encoding.UTF8, "application/json");
            // hostile query values, one query parameter at a time (the other parameters absent), and every hostile text as a string route value
            foreach (var q in t.QueryParams)
                foreach (var h in HostileText)
                {
                    var (s, b, ct) = await Send(new HttpMethod(t.Method), Url(t, null, [new(q, h)]), cookie, Content());
                    sent++;
                    if (Judge(s, b, ct) is { } why) failures.Add($"{t.Method} {t.Pattern}?{q}={Show(h)} -> {why}");
                }
            foreach (var h in HostileText.Where(x => !x.Contains('\0') && x.Length < 300))
            {
                if (!t.RouteParams.Any(p => p is "code" or "docType" or "adapter" or "name")) break;
                var (s, b, ct) = await Send(new HttpMethod(t.Method), Url(t, h), cookie, Content());
                sent++;
                if (Judge(s, b, ct) is { } why) failures.Add($"{t.Method} {t.Pattern} route value {Show(h)} -> {why}");
            }
            // an invalid GUID in a GUID route position never matches the route (a mapped 404/405), and an unknown extra query parameter is ignored
            if (t.RouteParams.Length > 0)
            {
                var bad = Regex.Replace(t.Pattern, @"\{([a-zA-Z]+):guid\}", "not-a-guid").Replace("{code}", "USD").Replace("{docType}", "journal").Replace("{adapter}", "inventory-units").Replace("{name}", "x");
                var (s, b, ct) = await Send(new HttpMethod(t.Method), bad, cookie, Content());
                sent++;
                if (Judge(s, b, ct) is { } why) failures.Add($"{t.Method} {t.Pattern} with an invalid GUID -> {why}");
            }
        }
        Assert.True(sent > 2000, $"only {sent} requests were sent");
        Assert.True(failures.Count == 0, $"{failures.Count} of {sent} hostile query/route requests were not answered with a mapped answer:\n" + string.Join("\n", failures.Take(80)));
    }

    [PostgresFact]
    public async Task Hostile_bodies_on_every_endpoint_that_takes_one_give_a_mapped_answer_never_a_server_error_and_never_server_internals()
    {
        await Start();
        var admin = await SignIn(Admin);
        var targets = Discover().Where(t => t.Method is "POST" or "PUT" or "PATCH").ToList();
        Assert.True(targets.Count(t => t.Body is not null) > 80, $"only {targets.Count(t => t.Body is not null)} endpoints with a body DTO were discovered");
        var failures = new List<string>();
        var sent = 0;
        foreach (var t in targets)
        {
            var cookie = t.AnonymousOk ? null : admin;
            if (t.FileBody)
            {
                foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 0, 0, 0, 0 }, Encoding.UTF8.GetBytes("code,name\n\0,\u202E\n"), new byte[2_000_000], Encoding.UTF8.GetBytes("\"unterminated,\n") })
                {
                    var (s, b, ct) = await Send(new HttpMethod(t.Method), Url(t, null, [new("adapter", "inventory-units"), new("fileName", "a\0b\u202E.csv"), new("subjectKind", "local_party"), new("subjectId", "not-a-guid")]), cookie, new ByteArrayContent(bytes));
                    sent++;
                    if (Judge(s, b, ct) is { } why) failures.Add($"{t.Method} {t.Pattern} file of {bytes.Length} bytes -> {why}");
                }
                continue;
            }
            var variants = t.Body is null ? new List<(string, string)> { ("empty object", "{}"), ("malformed JSON", "{\"a\":"), ("empty body", ""), ("JSON null", "null") } : BodyVariants(t.Body).ToList();
            foreach (var (label, json) in variants)
            {
                var (s, b, ct) = await Send(new HttpMethod(t.Method), Url(t), cookie, new StringContent(json, Encoding.UTF8, "application/json"));
                sent++;
                if (Judge(s, b, ct) is { } why) failures.Add($"{t.Method} {t.Pattern} [{label}] -> {why}");
            }
            // the wrong media type and no media type
            foreach (var content in new HttpContent[] { new StringContent("{}", Encoding.UTF8, "text/plain"), new StringContent("a=b", Encoding.UTF8, "application/x-www-form-urlencoded"), new ByteArrayContent([1, 2, 3]) })
            {
                var (s, b, ct) = await Send(new HttpMethod(t.Method), Url(t), cookie, content);
                sent++;
                if (Judge(s, b, ct) is { } why) failures.Add($"{t.Method} {t.Pattern} [content type {content.Headers.ContentType?.MediaType ?? "none"}] -> {why}");
            }
        }
        Assert.True(sent > 3000, $"only {sent} requests were sent");
        Assert.True(failures.Count == 0, $"{failures.Count} of {sent} hostile body requests were not answered with a mapped answer:\n" + string.Join("\n", failures.Take(80)));
    }
}
