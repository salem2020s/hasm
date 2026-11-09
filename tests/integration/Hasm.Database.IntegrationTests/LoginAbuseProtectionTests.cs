using Hasm.Infrastructure.Identity;
using Microsoft.Extensions.Logging;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 2.3C-1: login abuse protection without account-lockout denial of service. Real PostgreSQL, injectable clock.
/// The per-(login, client) counter blocks only the offending client; the per-client counter bounds credential stuffing;
/// a high per-login ceiling is the documented last resort against distributed guessing.
/// </summary>
public class LoginAbuseProtectionTests : IdentityTestBase
{
    private readonly LogCapture logs = new();

    private AuthService ServiceWith(AuthOptions options) =>
        new(Store, Passwords, options, LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs)).CreateLogger<AuthService>());

    /// <summary>Relentless guessing: every attempt is made, whatever the outcome (it turns into 429 once blocked).</summary>
    private static async Task Hammer(AuthService auth, string login, string ip, int times)
    {
        for (var i = 0; i < times; i++) await auth.LoginAsync(login, "wrong-password-" + i, SessionClient.Web, ip);
    }

    private async Task Fail(AuthService auth, string login, string ip, int times)
    {
        for (var i = 0; i < times; i++) Assert.IsType<LoginOutcome.InvalidCredentials>(await auth.LoginAsync(login, "wrong-password-" + i, SessionClient.Web, ip));
    }

    [PostgresFact]
    public async Task An_attacker_cannot_lock_a_known_user_out_of_sign_in_from_other_addresses()
    {
        await Start();
        await NewUser("alice");

        await Hammer(Auth, "alice", "203.0.113.66", 50);                // far beyond every per-pair limit, all from one address

        Assert.IsType<LoginOutcome.Throttled>(await Login("alice", Pw, "203.0.113.66"));     // the attacker is blocked
        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "198.51.100.10"));      // the real user, elsewhere, is not
    }

    [PostgresFact]
    public async Task An_attacker_using_many_addresses_below_the_per_client_limit_cannot_block_the_user()
    {
        await Start();
        await NewUser("alice");

        await NewUser("bob");
        for (var i = 0; i < 20; i++) await Fail(Auth, "bob", $"203.0.114.{i + 1}", AuthSettings.MaxFailuresPerLoginSource - 1);       // 80 failures < 100

        Assert.IsType<LoginOutcome.Success>(await Login("bob", Pw, "198.51.100.10"));
    }

    [PostgresFact]
    public async Task Distributed_guessing_beyond_the_ceiling_blocks_the_login_for_everyone_identically_for_unknown_logins_and_raises_an_operator_signal()
    {
        await Start();
        var options = new AuthOptions { MaxFailuresPerLoginSource = 3, MaxFailuresPerLoginGlobal = 12 };
        var auth = ServiceWith(options);
        var real = await NewUser("alice");

        foreach (var login in new[] { "alice", "ghost-account" })
            for (var i = 0; i < 6; i++) await Fail(auth, login, $"203.0.113.{i + 1}", 2);     // 12 failures from 6 clients, none near its own limit

        var a = Assert.IsType<LoginOutcome.Throttled>(await auth.LoginAsync("alice", Pw, SessionClient.Web, "198.51.100.10"));
        var g = Assert.IsType<LoginOutcome.Throttled>(await auth.LoginAsync("ghost-account", Pw, SessionClient.Web, "198.51.100.10"));
        Assert.Equal(a.RetryAfter, g.RetryAfter);                                              // the block does not reveal which login exists
        Assert.Contains(logs.Entries, e => e.Contains("auth.login_distributed_attack_suspected"));
        Assert.DoesNotContain(logs.Entries, e => e.Contains("ghost-account") || e.Contains(Pw));

        Clock.Advance(options.Block + TimeSpan.FromSeconds(1));
        Assert.IsType<LoginOutcome.Success>(await auth.LoginAsync("alice", Pw, SessionClient.Web, "198.51.100.10"));
        Assert.NotEqual(Guid.Empty, real.Id);
    }

    [PostgresFact]
    public async Task One_client_spraying_many_logins_is_stopped_by_the_per_client_limit_without_affecting_others()
    {
        await Start();
        await NewUser("alice");
        for (var i = 0; i < AuthSettings.MaxFailuresPerSource; i++)
            Assert.IsType<LoginOutcome.InvalidCredentials>(await Login($"victim{i}", "leaked-password-1", "203.0.113.5"));

        Assert.IsType<LoginOutcome.Throttled>(await Login("alice", Pw, "203.0.113.5"));
        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "198.51.100.7"));
    }

    [PostgresFact]
    public async Task A_success_by_the_real_user_does_not_reset_the_attackers_counter()
    {
        await Start();
        await NewUser("alice");
        await Fail(Auth, "alice", "203.0.113.66", AuthSettings.MaxFailuresPerLoginSource - 1);

        Assert.IsType<LoginOutcome.Success>(await Login("alice", Pw, "198.51.100.10"));     // the victim signs in from elsewhere

        Assert.IsType<LoginOutcome.InvalidCredentials>(await Login("alice", "wrong-password-x", "203.0.113.66"));
        Assert.IsType<LoginOutcome.Throttled>(await Login("alice", "wrong-password-y", "203.0.113.66"));
    }

    [PostgresFact]
    public async Task A_success_does_not_reset_the_global_counter()
    {
        await Start();
        var options = new AuthOptions { MaxFailuresPerLoginSource = 3, MaxFailuresPerLoginGlobal = 12 };
        var auth = ServiceWith(options);
        await NewUser("alice");

        for (var i = 0; i < 5; i++) await Fail(auth, "alice", $"203.0.113.{i + 1}", 2);                // 10 of 12
        Assert.IsType<LoginOutcome.Success>(await auth.LoginAsync("alice", Pw, SessionClient.Web, "198.51.100.10"));
        await Fail(auth, "alice", "203.0.113.200", 2);                                                  // 12th failure, still inside the window

        Assert.IsType<LoginOutcome.Throttled>(await auth.LoginAsync("alice", Pw, SessionClient.Web, "198.51.100.11"));
    }

    [PostgresFact]
    public async Task Sign_in_outcomes_do_not_reveal_whether_an_account_exists_or_why_it_failed()
    {
        await Start();
        var disabled = await NewUser("disabled-user");
        var locked = await NewUser("locked-user");
        await NewUser("alice");
        await Store.SetStateAsync(null, disabled.Id, UserState.Disabled);
        await Store.SetStateAsync(null, locked.Id, UserState.Locked);

        var outcomes = new List<LoginOutcome>
        {
            await Login("alice", "wrong-password-1", "10.0.0.1"),
            await Login("no-such-user", "wrong-password-1", "10.0.0.2"),
            await Login("disabled-user", Pw, "10.0.0.3"),            // correct password, disabled account
            await Login("locked-user", Pw, "10.0.0.4"),
            await Login("Not A Valid Login!", "wrong-password-1", "10.0.0.5"),
        };
        Assert.All(outcomes, o => Assert.IsType<LoginOutcome.InvalidCredentials>(o));

        // after blocking, the answers are identical too (same status and the same retry time for the same failure history)
        var retry = new List<TimeSpan>();
        foreach (var login in new[] { "alice", "no-such-user", "disabled-user", "locked-user" })
        {
            var ip = "198.51.100." + (retry.Count + 1);
            await Fail(Auth, login, ip, AuthSettings.MaxFailuresPerLoginSource);
            retry.Add(Assert.IsType<LoginOutcome.Throttled>(await Login(login, Pw, ip)).RetryAfter);
        }
        Assert.Single(retry.Distinct());
    }

    [PostgresFact]
    public async Task Failed_sign_ins_by_others_never_block_the_real_users_password_change()
    {
        await Start();
        await NewUser("alice");
        var session = (await Auth.AuthenticateAsync((await LoginOk("alice", Pw, "198.51.100.10")).Token))!;
        await Hammer(Auth, "alice", "203.0.113.66", 200);

        var changed = await Auth.ChangePasswordAsync(session, Pw, "A-Brand-New-Passphrase-1");

        Assert.Equal(ChangePasswordStatus.Changed, changed.Status);
    }

    [PostgresFact]
    public async Task Existing_sessions_survive_any_amount_of_abuse()
    {
        await Start();
        await NewUser("alice");
        var ok = await LoginOk("alice", Pw, "198.51.100.10");
        await Hammer(Auth, "alice", "203.0.113.66", 120);
        Assert.NotNull(await Auth.AuthenticateAsync(ok.Token));
    }

    [PostgresFact]
    public async Task Throttle_state_stores_only_digests_for_every_counter()
    {
        await Start();
        await NewUser("alice");
        await Fail(Auth, "alice", "203.0.113.66", 1);
        var rows = await Scalar<long>($"SELECT count(*) FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.LoginThrottle.Table}");
        Assert.Equal(3L, rows);     // pair, global login, source
        Assert.Equal(0L, await Scalar<long>(
            $"SELECT count(*) FROM {Hasm.Infrastructure.SystemDatabase.SystemSchema.LoginThrottle.Table} WHERE octet_length({Hasm.Infrastructure.SystemDatabase.SystemSchema.LoginThrottle.KeyHash}) <> 32"));
    }
}
