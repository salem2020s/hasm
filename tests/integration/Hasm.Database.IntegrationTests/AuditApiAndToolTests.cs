using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;
using Us = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;

namespace Hasm.Database.IntegrationTests;

/// <summary>The HTTP API with the real audit wiring: file keys and checkpoints from configuration, and fail-closed behaviour when they are missing.</summary>
public class AuditApiTests : FileEvidenceTestBase
{
    private const string RootPassword = "Root-Passphrase-Strong-1";
    private readonly LogCapture logs = new();
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;

    public override async Task DisposeAsync()
    {
        factory?.Dispose();
        await base.DisposeAsync();
    }

    /// <summary>Starts the API. <paramref name="configureAudit"/>: use the file-based keys and checkpoint directory through configuration, exactly as in production.</summary>
    private async Task StartApi(bool configureAudit)
    {
        await StartAccess();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Hasm:Database:Host", Options.Host);
            b.UseSetting("Hasm:Database:Port", Options.Port.ToString());
            b.UseSetting("Hasm:Database:SystemDatabaseName", Options.SystemDatabaseName);
            b.UseSetting("Hasm:Database:Username", Options.Username);
            b.UseSetting("Hasm:Database:Password", Options.Password);
            b.UseSetting("Hasm:Database:AdminUsername", Options.AdminUsername);
            b.UseSetting("Hasm:Database:AdminPassword", Options.AdminPassword);
            b.UseSetting("Hasm:Database:SslMode", Options.SslMode.ToString());
            b.UseSetting("Hasm:Database:AllowInsecureTransport", "true");       // disposable loopback test server only
            b.UseSetting("Hasm:Auth:Pbkdf2Iterations", "100000");
            if (configureAudit)
            {
                b.UseSetting("Hasm:Audit:KeyDirectory", KeyDir);
                b.UseSetting("Hasm:Audit:ActiveKeyId", "k1");
                b.UseSetting("Hasm:Audit:CheckpointDirectory", CpDir);
            }
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            b.ConfigureServices(s => s.AddSingleton<TimeProvider>(Clock));
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    }

    private async Task<ApiResponse> Send(HttpMethod method, string path, object? body = null, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Hasm-Request", "1");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> DesktopLogin(string login, string password)
    {
        var r = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login, password, client = "desktop" });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Json.GetProperty("token").GetString()!;
    }

    private (AuthService Auth, IdentityStore Store) FileBackedIdentity()
    {
        var store = new IdentityStore(Options.BuildSystemConnectionString(admin: true), Clock, AuditTrail.FromOptions(Opts("k1"), Clock), keys: TestIdentityKeys.Instance);
        return (new AuthService(store, Passwords, AuthSettings), store);
    }

    [PostgresFact]
    public async Task Without_audit_configuration_audited_mutations_fail_closed_and_anonymous_endpoints_keep_working()
    {
        await StartApi(configureAudit: false);
        var root = (await Auth.BootstrapAdministratorAsync("root", "Root", RootPassword))!.User!;     // seeded through the suite's own trail
        var token = await DesktopLogin("root", RootPassword);
        var before = await EventCount();

        var create = await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "newuser", displayName = "New", password = "New-Passphrase-Strong-3", isIdentityAdministrator = false }, token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.Status);
        Assert.Equal("audit_unavailable", create.Error);
        Assert.Null(await Store.FindUserByLoginAsync("newuser"));                                          // the mutation did not happen
        Assert.Equal(before, await EventCount());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/health")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/auth/status", bearer: token)).Status);
        // The client learns nothing about the cause; the operator finds the short code in the log, without any path or secret.
        var everything = create.Body + string.Join("|", logs.Entries);
        Assert.DoesNotContain("KeyDirectory", create.Body);
        Assert.DoesNotContain(Root, everything);
        Assert.Contains(logs.Entries, e => e.Contains("audit.unavailable") && e.Contains("audit_not_configured"));
        _ = root;
    }

    [PostgresFact]
    public async Task Api_mutations_are_chained_anchored_and_verifiable_with_the_configured_files()
    {
        await StartApi(configureAudit: true);
        FileAuditKeyRing.Generate(KeyDir, "k1");
        var (auth, store) = FileBackedIdentity();
        await using var _s = store;
        var root = (await auth.BootstrapAdministratorAsync("root", "Root", RootPassword))!.User!;
        var token = await DesktopLogin("root", RootPassword);

        var created = await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "dave", displayName = "Dave", password = "Dave-Passphrase-Strong-4", isIdentityAdministrator = false }, token);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var daveId = created.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/admin/users/{daveId}/state", new { state = "disabled" }, token)).Status);

        var events = await Events(AuditStream.System);
        Assert.Equal([AuditEventTypes.InitialAdministratorCreated, AuditEventTypes.UserCreated, AuditEventTypes.UserStateChanged], events.Select(e => e.EventType));
        Assert.Equal(root.Id, events[1].ActorUserId);                                                      // the acting administrator comes from the session
        Assert.Equal(daveId, events[2].TargetUserId);
        AssertVerified(await FileVerifier().VerifyAsync(AuditStream.System), 3);

        var everything = created.Body + string.Join("|", logs.Entries);
        Assert.DoesNotContain(Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(KeyDir, "k1.hkey"))).TrimEnd('='), everything);
        Assert.DoesNotContain(KeyDir, everything);
        Assert.DoesNotContain(CpDir, everything);
        Assert.DoesNotContain(Options.SystemDatabaseName!, everything);
        Assert.DoesNotContain("Dave-Passphrase-Strong-4", everything);
    }

    [PostgresFact]
    public async Task A_tampered_stream_makes_the_api_refuse_audited_changes_with_a_generic_error()
    {
        await StartApi(configureAudit: true);
        FileAuditKeyRing.Generate(KeyDir, "k1");
        var (auth, store) = FileBackedIdentity();
        await using var _s = store;
        await auth.BootstrapAdministratorAsync("root", "Root", RootPassword);
        var token = await DesktopLogin("root", RootPassword);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "erin", displayName = "Erin", password = "Erin-Passphrase-Strong-5", isIdentityAdministrator = false }, token)).Status);
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'tampered' WHERE {Ev.StreamKey} = 'system' AND {Ev.Seq} = 2");

        var refused = await Send(HttpMethod.Post, "/api/v1/admin/users", new { login = "frank", displayName = "Frank", password = "Frank-Passphrase-Strong-6", isIdentityAdministrator = false }, token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
        Assert.Equal("audit_unavailable", refused.Error);
        Assert.DoesNotContain("tamper", refused.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await Store.FindUserByLoginAsync("frank"));
        Assert.Contains(logs.Entries, e => e.Contains("audit_head_corrupt"));
    }
}

/// <summary>The command-line verification tool: exit codes, read-only behaviour and what it prints.</summary>
public class AuditAdminToolTests : FileEvidenceTestBase
{
    private static string? adminDll;

    private static string AdminDll()
    {
        if (adminDll is not null) return adminDll;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = dir.Parent!.Name;                                                               // .../bin/<Configuration>/net10.0/
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "backend", "Hasm.Admin", "bin", configuration, dir.Name, "Hasm.Admin.dll");
            if (File.Exists(candidate)) return adminDll = candidate;
        }
        throw new FileNotFoundException("Hasm.Admin.dll was not built; build the solution first.");
    }

    private sealed record Run(int Exit, string Out, string Err) { public string All => Out + Err; }

    private async Task<Run> Tool(Dictionary<string, string?>? extra, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        info.ArgumentList.Add(AdminDll());
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Hasm__", StringComparison.OrdinalIgnoreCase)).ToList()) info.Environment.Remove(key);
        var env = new Dictionary<string, string?>
        {
            ["Hasm__Database__Host"] = Options.Host, ["Hasm__Database__Port"] = Options.Port.ToString(),
            ["Hasm__Database__SystemDatabaseName"] = Options.SystemDatabaseName, ["Hasm__Database__Username"] = Options.Username,
            ["Hasm__Database__Password"] = Options.Password, ["Hasm__Database__SslMode"] = Options.SslMode.ToString(),
            ["Hasm__Database__AllowInsecureTransport"] = "true",
            ["Hasm__Audit__KeyDirectory"] = KeyDir, ["Hasm__Audit__ActiveKeyId"] = "k1", ["Hasm__Audit__CheckpointDirectory"] = CpDir,
        };
        foreach (var (k, v) in extra ?? []) env[k] = v;
        foreach (var (k, v) in env) if (v is null) info.Environment.Remove(k); else info.Environment[k] = v;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        return new Run(process.ExitCode, await stdout, await stderr);
    }

    [PostgresFact]
    public async Task Verification_of_an_intact_stream_exits_zero_and_prints_the_verified_range_without_secrets()
    {
        var (company, admin, stream, service) = await Written();
        await using var _s = service;
        Ok(await service.CreateRoleAsync(admin.Id, company, "Payroll approvers", [PermissionCatalog.Codes.AuditView]));

        var run = await Tool(null, "audit-verify", stream.Key);

        Assert.Equal(0, run.Exit);
        Assert.Contains("Verdict: VERIFIED", run.Out);
        Assert.Contains("sequence 1..6", run.Out);
        Assert.Contains("Verified through sequence: 6", run.Out);
        Assert.DoesNotContain("Payroll", run.All);                                                         // no event payloads
        Assert.DoesNotContain("permissions:", run.All);
        Assert.DoesNotContain(Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(KeyDir, "k1.hkey"))).TrimEnd('='), run.All);
        Assert.DoesNotContain(Options.Password!, run.All);
        Assert.DoesNotContain(Options.SystemDatabaseName!, run.All);
        Assert.DoesNotContain(KeyDir, run.All);
    }

    [PostgresFact]
    public async Task Verification_works_with_the_runtime_account_alone_and_with_an_unqualified_company_id()
    {
        var (company, _, _, service) = await Written();
        await using var _s = service;

        var run = await Tool(new() { ["Hasm__Database__AdminUsername"] = null, ["Hasm__Database__AdminPassword"] = null }, "audit-verify", company.ToString());

        Assert.Equal(0, run.Exit);
        Assert.Contains($"Stream: company:{company:N}", run.Out);
    }

    [PostgresFact]
    public async Task Tampering_makes_the_tool_exit_six_naming_the_broken_records_but_not_their_content()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'confidential-marker' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3", ("s", stream.Key));
        await Tamper($"DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 4", ("s", stream.Key));

        var run = await Tool(null, "audit-verify", stream.Key);

        Assert.Equal(6, run.Exit);
        Assert.Contains("Verdict: FAILED", run.Out);
        Assert.Contains("[FAILURE] hash_mismatch seq=3", run.Out);
        Assert.Contains("seq_gap", run.Out);
        Assert.DoesNotContain("confidential-marker", run.All);
    }

    [PostgresFact]
    public async Task Missing_checkpoint_evidence_makes_the_tool_exit_seven_not_zero()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        var empty = Path.Combine(Root, "empty-checkpoints");
        Directory.CreateDirectory(empty);

        var run = await Tool(null, "audit-verify", stream.Key, "--checkpoint-dir", empty);

        Assert.Equal(7, run.Exit);
        Assert.Contains("Verdict: INCOMPLETE", run.Out);
        Assert.Contains("no_checkpoints", run.Out);
    }

    [PostgresFact]
    public async Task A_missing_key_makes_the_tool_exit_seven_and_bad_usage_or_configuration_exit_two_or_three()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;

        var noKey = Path.Combine(Root, "no-keys");
        Directory.CreateDirectory(noKey);
        Assert.Equal(7, (await Tool(new() { ["Hasm__Audit__KeyDirectory"] = noKey }, "audit-verify", stream.Key)).Exit);

        Assert.Equal(2, (await Tool(null, "audit-verify")).Exit);
        Assert.Equal(2, (await Tool(null, "audit-verify", "not-a-stream")).Exit);
        Assert.Equal(2, (await Tool(null, "audit-verify", stream.Key, "--bogus", "x")).Exit);
        var unconfigured = await Tool(new() { ["Hasm__Audit__KeyDirectory"] = null, ["Hasm__Audit__CheckpointDirectory"] = null }, "audit-verify", stream.Key);
        Assert.Equal(3, unconfigured.Exit);
        Assert.Contains("Hasm:Audit:KeyDirectory", unconfigured.Err);
    }

    [PostgresFact]
    public async Task Verification_never_modifies_the_database_or_the_checkpoints()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'x' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 2", ("s", stream.Key));
        var snapshot = $"SELECT md5(string_agg(t::text, '|' ORDER BY {Ev.StreamKey}, {Ev.Seq})) FROM {Ev.Table} t";
        var users = $"SELECT md5(string_agg(t::text, '|' ORDER BY {Us.Id})) FROM {Us.Table} t";
        var before = (await Scalar<string>(snapshot), await Scalar<string>(users), await File.ReadAllTextAsync(CheckpointFile(stream)));

        var run = await Tool(null, "audit-verify", stream.Key);

        Assert.Equal(6, run.Exit);
        Assert.Equal(before, (await Scalar<string>(snapshot), await Scalar<string>(users), await File.ReadAllTextAsync(CheckpointFile(stream))));
    }

    [PostgresFact]
    public async Task Anchoring_catches_up_a_sound_stream_but_refuses_a_damaged_one_and_key_generation_never_overwrites()
    {
        var (_, _, stream, service) = await Written();
        await using var _s = service;
        var file = CheckpointFile(stream);
        File.WriteAllLines(file, File.ReadAllLines(file).Take(2));                                          // newest checkpoints lost
        Assert.Equal(7, (await Tool(null, "audit-verify", stream.Key)).Exit);

        var anchor = await Tool(null, "audit-anchor", stream.Key);
        Assert.Equal(0, anchor.Exit);
        Assert.Contains("anchored at sequence 5", anchor.Out);
        Assert.Equal(0, (await Tool(null, "audit-verify", stream.Key)).Exit);

        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'x' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 2", ("s", stream.Key));
        var linesBefore = await File.ReadAllLinesAsync(file);
        var refused = await Tool(null, "audit-anchor", stream.Key);
        Assert.Equal(6, refused.Exit);
        Assert.Equal(linesBefore, await File.ReadAllLinesAsync(file));                                      // a damaged chain is never blessed

        // With no checkpoint at all, anchoring needs the explicit flag: audit-anchor must not create a trust baseline on its own.
        File.Delete(file);
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'restored' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 2", ("s", stream.Key));
        Assert.Equal(6, (await Tool(null, "audit-anchor", stream.Key, "--establish-baseline")).Exit);       // still refused: the chain is damaged
        Assert.False(File.Exists(file));

        var keyFile = Path.Combine(KeyDir, "k1.hkey");
        var keyBefore = await File.ReadAllTextAsync(keyFile);
        var again = await Tool(null, "audit-keygen", "k1");
        Assert.Equal(1, again.Exit);
        Assert.Equal(keyBefore, await File.ReadAllTextAsync(keyFile));
        var fresh = await Tool(null, "audit-keygen", "k3");
        Assert.Equal(0, fresh.Exit);
        Assert.Contains("'k3'", fresh.Out);
        var newKey = (await File.ReadAllTextAsync(Path.Combine(KeyDir, "k3.hkey"))).Trim();
        Assert.DoesNotContain(newKey, fresh.All);                                                           // the secret is never printed
        Assert.True(Convert.FromBase64String(newKey).Length >= 32);
        Assert.Equal(2, (await Tool(null, "audit-keygen", "../bad")).Exit);
    }
}
