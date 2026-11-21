using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Backup;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 6 (D-086; D-130..D-133): the pure retention classification. Minimums (raise-only), daily and weekly representatives over the days and weeks that HAVE a recoverable run, the last
/// recoverable run, period-end and fiscal-year-end runs on both sides of every boundary, unknown classifications kept, non-recoverable runs never deleted, holds, pins and the key inventory.
/// </summary>
public sealed class RetentionClassifierTests
{
    internal static readonly Guid C = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal const string Rk = "rk-0123456789abcdef";
    private static int counter;

    internal static RetentionOptions Opt(int daily = 7, int weekly = 4, int max = 7) => new() { DailyKeep = daily, WeeklyKeep = weekly, InstallationTimeZone = "UTC", MaxDeletionsPerRun = max };

    internal static string Id(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Interlocked.Increment(ref counter).ToString("x8");

    internal static RetentionRun Run(DateTimeOffset start, string outcome = "ok", bool proof = false, string? rk = Rk, bool verified = true, bool encrypted = true, bool complete = true,
        RunKeyNeeds? keys = null, bool noKeys = false, TimeSpan? duration = null, Guid? company = null)
    {
        var id = Id(start);
        return new RetentionRun(id, start, start + (duration ?? TimeSpan.FromMinutes(10)), complete, verified, encrypted, "ok", rk, proof, [new RunStatusCompany(company ?? C, outcome)],
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))), true, false, noKeys ? null : keys ?? new RunKeyNeeds(["k1"], ["a1"]));
    }

    internal static IReadOnlyList<RetentionPeriod> Monthly(DateOnly start, int months) =>
        [.. Enumerable.Range(0, months).Select(i => new RetentionPeriod(start.AddMonths(i), start.AddMonths(i + 1).AddDays(-1)))];

    internal static RetentionFiscalYear CalendarYear(int year, string zone = "UTC") => new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), zone, Monthly(new DateOnly(year, 1, 1), 12));

    internal static RetentionCompany Company(string? zone = "UTC", IReadOnlyList<RetentionFiscalYear>? years = null, bool readable = true, string? hold = null) =>
        new(C, readable, zone, years ?? [CalendarYear(2026)], hold);

    private static DateTimeOffset T(int month, int day, int hour = 2, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>Daily runs from <paramref name="first"/> to <paramref name="last"/> (inclusive, 02:00 UTC); the newest carries the recovery-test proof.</summary>
    private static List<RetentionRun> Daily(DateTimeOffset first, DateTimeOffset last, int step = 1)
    {
        var runs = new List<RetentionRun>();
        for (var t = first; t <= last; t = t.AddDays(step)) runs.Add(Run(t));
        runs[^1] = runs[^1] with { ProofValid = true };
        return runs;
    }

    private static RetentionPlan Plan(IReadOnlyList<RetentionRun> runs, DateTimeOffset now, RetentionCompany? company = null, IReadOnlyList<BackupLegalHoldRow>? holds = null,
        IReadOnlyList<RetentionPinRow>? pins = null, RetentionOptions? options = null) =>
        RetentionClassifier.Plan(runs, [company ?? Company()], holds ?? [], pins ?? [], options ?? Opt(), now);

    private static RunDecision D(RetentionPlan p, RetentionRun r) => p.Runs.Single(x => x.RunId == r.RunId);

    // ---- configuration (D-132) -----------------------------------------------------------------

    [Fact]
    public void The_minimums_are_pinned_and_a_lower_or_unreadable_setting_is_refused_never_clamped()
    {
        Assert.Equal(7, RetentionOptions.MinimumDaily);
        Assert.Equal(4, RetentionOptions.MinimumWeekly);
        RetentionOptions Read(params (string Key, string Value)[] settings) =>
            RetentionOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
                settings.Select(s => new KeyValuePair<string, string?>($"{RetentionOptions.SectionName}:{s.Key}", s.Value)).Append(new($"{RetentionOptions.SectionName}:InstallationTimeZone", "UTC"))).Build());
        var defaults = Read();
        defaults.Validate();
        Assert.Equal((7, 4, DayOfWeek.Monday, 7), (defaults.DailyKeep, defaults.WeeklyKeep, defaults.WeekStart, defaults.MaxDeletionsPerRun));
        Assert.Equal("retention_below_minimum", Assert.Throws<BackupException>(() => Read(("DailyKeep", "6")).Validate()).Code);
        Assert.Equal("retention_below_minimum", Assert.Throws<BackupException>(() => Read(("WeeklyKeep", "3")).Validate()).Code);
        Assert.Equal("retention_config_invalid", Assert.Throws<BackupException>(() => Read(("DailyKeep", "seven")).Validate()).Code);
        Assert.Equal("retention_config_invalid", Assert.Throws<BackupException>(() => Read(("DailyKeep", "-8")).Validate()).Code);
        Assert.Equal("retention_config_invalid", Assert.Throws<BackupException>(() => Read(("WeekStart", "Funday")).Validate()).Code);
        Assert.Equal("retention_config_invalid", Assert.Throws<BackupException>(() => Read(("MaxDeletionsPerRun", "32")).Validate()).Code);
        Assert.Equal("retention_config_invalid", Assert.Throws<BackupException>(() => Read(("AutomaticDeletion", "yes")).Validate()).Code);
        var raised = Read(("DailyKeep", "30"), ("WeeklyKeep", "12"), ("WeekStart", "Sunday"));
        raised.Validate();
        Assert.Equal((30, 12, DayOfWeek.Sunday), (raised.DailyKeep, raised.WeeklyKeep, raised.WeekStart));
        // the installation time zone is required; the server's local zone is never a fallback
        Assert.Equal("retention_time_zone_invalid", Assert.Throws<BackupException>(() => new RetentionOptions().Validate()).Code);
        Assert.Equal("retention_time_zone_invalid", Assert.Throws<BackupException>(() => new RetentionOptions { InstallationTimeZone = "Not/AZone" }.Validate()).Code);
    }

    // ---- minimums and representatives (D-132, D-133) ---------------------------------------------

    [Fact]
    public void The_latest_seven_daily_and_four_weekly_representatives_and_the_last_recoverable_run_are_kept()
    {
        var runs = Daily(T(3, 2), T(3, 25));                                                                     // Monday 2 March .. Wednesday 25 March 2026
        var plan = Plan(runs, T(3, 26, 0));
        string Day(RetentionRun r) => r.StartedUtc.ToString("dd");
        Assert.Equal(new[] { "19", "20", "21", "22", "23", "24", "25" }, runs.Where(r => D(plan, r).Reasons.Contains("minimum_daily")).Select(Day));
        Assert.Equal(new[] { "08", "15", "22", "25" }, runs.Where(r => D(plan, r).Reasons.Contains("minimum_weekly")).Select(Day));
        Assert.Contains("last_recoverable", D(plan, runs[^1]).Reasons);
        Assert.Contains("newest", D(plan, runs[^1]).Reasons);
        Assert.Contains("recovery_test_proof", D(plan, runs[^1]).Reasons);
        Assert.Contains("period_end", D(plan, runs[0]).Reasons);                                                // the first run after the February period end
        Assert.Equal(new[] { "03", "04", "05", "06", "07", "09", "10", "11", "12", "13", "14", "16", "17", "18" }, runs.Where(r => D(plan, r).Eligible).Select(Day));
        Assert.Contains("daily", D(plan, runs[^1]).Tags[C.ToString("D")]);
        Assert.Contains("daily", D(plan, runs[^1]).Tags[RetentionClassifier.SystemPart]);

        // a raised minimum keeps more; nothing is ever kept LESS than the minimum
        var raised = Plan(runs, T(3, 26, 0), options: Opt(daily: 14));
        Assert.Equal(14, runs.Count(r => D(raised, r).Reasons.Contains("minimum_daily")));
    }

    [Fact]
    public void Days_without_a_run_never_shrink_the_kept_history_and_only_the_newest_run_of_a_day_is_its_daily_backup()
    {
        var sparse = Daily(T(3, 1), T(3, 29), step: 2);
        var plan = Plan(sparse, T(3, 30, 0));
        Assert.Equal(new[] { "17", "19", "21", "23", "25", "27", "29" }, sparse.Where(r => D(plan, r).Reasons.Contains("minimum_daily")).Select(r => r.StartedUtc.ToString("dd")));

        var runs = Daily(T(3, 2), T(3, 24));
        var morning = Run(T(3, 25, 2));
        var evening = Run(T(3, 25, 14)) with { ProofValid = true };
        runs.AddRange([morning, evening]);
        var p2 = Plan(runs, T(3, 26, 0));
        Assert.Contains("minimum_daily", D(p2, evening).Reasons);
        Assert.DoesNotContain("minimum_daily", D(p2, morning).Reasons);
        Assert.True(D(p2, morning).Eligible);
    }

    // ---- period ends and fiscal-year ends (D-131) --------------------------------------------------

    [Fact]
    public void The_last_run_before_and_the_first_run_after_every_period_and_fiscal_year_end_are_kept()
    {
        // a non-calendar fiscal year (1 March - 28 February) with monthly periods: its year end coincides with a period end and a calendar month end
        IReadOnlyList<RetentionFiscalYear> years =
        [
            new(new DateOnly(2025, 3, 1), new DateOnly(2026, 2, 28), "UTC", Monthly(new DateOnly(2025, 3, 1), 12)),
            new(new DateOnly(2026, 3, 1), new DateOnly(2027, 2, 28), "UTC", Monthly(new DateOnly(2026, 3, 1), 12)),
        ];
        var runs = Daily(T(2, 10), T(3, 20));
        var plan = Plan(runs, T(3, 21, 0), Company(years: years));
        var before = runs.Single(r => r.StartedUtc == T(2, 28));
        var after = runs.Single(r => r.StartedUtc == T(3, 1));
        foreach (var r in new[] { before, after })
        {
            Assert.Contains("period_end", D(plan, r).Reasons);
            Assert.Contains("year_end", D(plan, r).Reasons);
            Assert.False(D(plan, r).Eligible);
        }
        Assert.DoesNotContain("period_end", D(plan, runs.Single(r => r.StartedUtc == T(2, 27))).Reasons);
        Assert.DoesNotContain("period_end", D(plan, runs.Single(r => r.StartedUtc == T(3, 2))).Reasons);

        // a run whose window spans the boundary is both the last before and the first after it
        var spanning = new List<RetentionRun> { Run(T(2, 27)), Run(T(2, 28, 23, 55), duration: TimeSpan.FromMinutes(25)), Run(T(3, 2)) with { ProofValid = true } };
        var p2 = Plan(spanning, T(3, 3, 0), Company(years: years));
        Assert.Contains("period_end", D(p2, spanning[1]).Reasons);
        Assert.DoesNotContain("period_end", D(p2, spanning[2]).Reasons);
        // (the oldest run is the first run after every earlier boundary of the fiscal year, so it is kept as well: fail closed, owner question Q2)
        Assert.Contains("period_end", D(p2, spanning[0]).Reasons);
    }

    [Fact]
    public void Calendar_month_ends_count_in_addition_to_fiscal_period_ends()
    {
        // one fiscal period per year: only the calendar month end (company zone) marks the end of March
        IReadOnlyList<RetentionFiscalYear> years = [new(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "UTC", [new RetentionPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31))])];
        var runs = Daily(T(3, 20), T(4, 12));
        var plan = Plan(runs, T(4, 13, 0), Company(years: years));
        Assert.Contains("period_end", D(plan, runs.Single(r => r.StartedUtc == T(3, 31))).Reasons);
        Assert.Contains("period_end", D(plan, runs.Single(r => r.StartedUtc == T(4, 1))).Reasons);
    }

    // ---- unknown classifications are kept (D-131, review L1 of #130) -------------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData("Not/AZone", true)]
    [InlineData("UTC", false)]
    public void An_unconfigured_unknown_or_unreadable_company_classification_keeps_every_run(string? zone, bool readable)
    {
        var runs = Daily(T(3, 2), T(3, 25));
        var plan = Plan(runs, T(3, 26, 0), Company(zone, readable: readable));
        Assert.All(runs, r => Assert.Contains("classification_unknown", D(plan, r).Reasons));
        Assert.Empty(plan.Eligible);
    }

    [Fact]
    public void A_run_outside_every_fiscal_year_is_classification_unknown_and_kept()
    {
        var runs = Daily(T(3, 2), T(3, 25));
        var plan = Plan(runs, T(3, 26, 0), Company(years: [CalendarYear(2025)]));
        Assert.All(runs, r => Assert.Contains("classification_unknown", D(plan, r).Reasons));
        var missingZone = Plan(runs, T(3, 26, 0), Company(years: [CalendarYear(2026, "Not/AZone")]));
        Assert.All(runs, r => Assert.Contains("classification_unknown", D(missingZone, r).Reasons));
        Assert.Empty(missingZone.Eligible);
    }

    // ---- non-recoverable runs never count and are never deleted (D-133) -----------------------------

    [Fact]
    public void Incomplete_unverifiable_unencrypted_and_untested_key_runs_are_kept_and_never_count()
    {
        var runs = Daily(T(3, 2), T(3, 25));
        var incomplete = Run(T(3, 3, 5), complete: false);
        var unverifiable = Run(T(3, 4, 5), verified: false);
        var unencrypted = Run(T(3, 5, 5), encrypted: false);
        var untested = Run(T(3, 6, 5), rk: "rk-fedcba9876543210");
        var missingKeys = Run(T(3, 7, 5), outcome: "ok_missing_keys");
        runs.AddRange([incomplete, unverifiable, unencrypted, untested, missingKeys]);
        var plan = Plan(runs, T(3, 26, 0));
        Assert.Contains("incomplete", D(plan, incomplete).Reasons);
        Assert.Contains("status_unverifiable", D(plan, unverifiable).Reasons);
        Assert.Contains("unencrypted", D(plan, unencrypted).Reasons);
        Assert.Contains("recovery_key_untested", D(plan, untested).Reasons);
        Assert.All(new[] { incomplete, unverifiable, unencrypted, untested }, r => Assert.False(D(plan, r).Eligible));
        // the company outcome ok_missing_keys is not a recoverable company backup: it never represents a day (its system part still may)
        Assert.DoesNotContain("daily", D(plan, missingKeys).Tags.GetValueOrDefault(C.ToString("D")) ?? []);

        // when the only tested key's runs are gone, nothing counts and nothing is eligible
        var untestedOnly = Daily(T(3, 2), T(3, 25)).Select(r => r with { ProofValid = false }).ToList();
        Assert.Empty(Plan(untestedOnly, T(3, 26, 0)).Eligible);
    }

    // ---- holds and pins (D-134, D-135) --------------------------------------------------------------

    [Fact]
    public void Run_company_and_attachment_holds_and_unexpired_pins_keep_runs_and_released_or_expired_ones_do_not()
    {
        var runs = Daily(T(3, 2), T(3, 25));
        var a = runs.Single(r => r.StartedUtc == T(3, 4));
        var b = runs.Single(r => r.StartedUtc == T(3, 10));
        var c = runs.Single(r => r.StartedUtc == T(3, 12));
        IReadOnlyList<BackupLegalHoldRow> holds =
        [
            new(Guid.NewGuid(), 1, a.RunId, null, null, null, false),
            new(Guid.NewGuid(), 2, null, C, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 11), false),
            new(Guid.NewGuid(), 1, c.RunId, null, null, null, true),                                                  // released: no longer holds
        ];
        IReadOnlyList<RetentionPinRow> pins = [new(Guid.NewGuid(), runs[5].RunId, 1, T(3, 27)), new(Guid.NewGuid(), runs[6].RunId, 1, T(3, 25))];   // runs[6]'s pin expired
        var plan = Plan(runs, T(3, 26, 0), holds: holds, pins: pins);
        Assert.Contains("legal_hold", D(plan, a).Reasons);
        Assert.Contains("legal_hold", D(plan, b).Reasons);
        Assert.Contains("legal_hold", D(plan, runs.Single(r => r.StartedUtc == T(3, 11))).Reasons);
        Assert.DoesNotContain("legal_hold", D(plan, c).Reasons);
        Assert.Contains("recovery_chain", D(plan, runs[5]).Reasons);
        Assert.DoesNotContain("recovery_chain", D(plan, runs[6]).Reasons);

        // an attachment hold, a D-114 hold or an unreadable hold source of the company keeps every run containing it
        foreach (var code in new[] { "attachment_hold", "d114_hold", "hold_check_failed" })
        {
            var held = Plan(runs, T(3, 26, 0), Company(hold: code));
            Assert.All(runs, r => Assert.Contains(code, D(held, r).Reasons));
            Assert.Empty(held.Eligible);
        }
        // a kept trial inside the run pins it
        var kept = runs.Select(r => r.RunId == c.RunId ? r with { KeptTrial = true } : r).ToList();
        Assert.Contains("recovery_chain", D(Plan(kept, T(3, 26, 0)), c).Reasons);
    }

    // ---- key inventory (D-138, review M2 of #130) ----------------------------------------------------

    [Fact]
    public void The_key_inventory_is_complete_only_when_every_retained_run_has_known_key_needs()
    {
        var runs = Daily(T(3, 2), T(3, 25));
        var plan = Plan(runs, T(3, 26, 0));
        Assert.True(plan.KeyInventoryComplete);
        Assert.Equal(["k1"], plan.EncryptionKeysNeeded);
        Assert.Equal(["a1"], plan.AuditKeysNeeded);
        Assert.Equal([Rk], plan.RecoveryKeysNeeded);

        // version 1 (no key needs), unverifiable, incomplete: each makes the inventory incomplete (backup_inventory_incomplete)
        foreach (var unknown in new[] { Run(T(3, 25, 7), noKeys: true), Run(T(3, 3, 8), verified: false, noKeys: true), Run(T(3, 3, 9), complete: false, noKeys: true) })
            Assert.False(Plan([.. runs, unknown], T(3, 26, 0)).KeyInventoryComplete);
        // a key needed only by a retained (here: held) run stays needed; an eligible run's keys are not counted
        var special = runs.Select(r => r.StartedUtc == T(3, 4) ? r with { Keys = new RunKeyNeeds(["k-old"], ["a1"]) } : r).ToList();
        Assert.DoesNotContain("k-old", Plan(special, T(3, 26, 0)).EncryptionKeysNeeded);
        Assert.Contains("k-old", Plan(special, T(3, 26, 0), holds: [new(Guid.NewGuid(), 1, special.Single(r => r.StartedUtc == T(3, 4)).RunId, null, null, null, false)]).EncryptionKeysNeeded);
    }

    [Fact]
    public void The_plan_hash_binds_the_eligible_runs_and_their_status_hashes()
    {
        var runs = Daily(T(3, 2), T(3, 25));
        var p1 = Plan(runs, T(3, 26, 0));
        Assert.Equal(p1.PlanHash, Plan(runs, T(3, 26, 0)).PlanHash);
        var eligible = p1.Eligible.First().RunId;
        var changed = runs.Select(r => r.RunId == eligible ? r with { StatusSha256 = new string('0', 64) } : r).ToList();
        Assert.NotEqual(p1.PlanHash, Plan(changed, T(3, 26, 0)).PlanHash);
    }

    // ---- explicit time-zone rules (D-131) -------------------------------------------------------------

    [Fact]
    public void A_skipped_local_midnight_starts_at_the_first_valid_instant_and_a_repeated_one_at_the_earlier_instant()
    {
        // a test zone (UTC+0, daylight +1h) whose daylight time starts at local 00:00 on 1 March (00:00-01:00 skipped) and ends at local 01:00 on 1 October (00:00-01:00 repeated)
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Midnight", TimeSpan.Zero, "Test/Midnight", "Std", "Dst", [rule]);
        Assert.True(zone.IsInvalidTime(new DateTime(2026, 3, 1, 0, 0, 0)));
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 1, 0, 0, TimeSpan.FromHours(1)), RetentionTime.StartOfDay(new DateOnly(2026, 3, 1), zone));
        Assert.True(zone.IsAmbiguousTime(new DateTime(2026, 10, 1, 0, 0, 0)));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), RetentionTime.StartOfDay(new DateOnly(2026, 10, 1), zone).ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 6, 2), RetentionTime.LocalDate(new DateTimeOffset(2026, 6, 1, 23, 30, 0, TimeSpan.Zero), zone));
        Assert.Equal(new DateOnly(2026, 3, 23), RetentionTime.WeekOf(new DateOnly(2026, 3, 25), DayOfWeek.Monday));
        Assert.Equal(new DateOnly(2026, 3, 22), RetentionTime.WeekOf(new DateOnly(2026, 3, 25), DayOfWeek.Sunday));
        Assert.Null(RetentionTime.FindZone(null));
        Assert.Null(RetentionTime.FindZone("Not/AZone"));
    }

    [Fact]
    public void Retention_never_uses_the_servers_local_time_zone()
    {
        var root = RepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "backend", "Hasm.Infrastructure", "Backup"), "*.cs")
            .Where(f => File.ReadAllText(f).Contains("TimeZoneInfo.Local", StringComparison.Ordinal)).ToList();
        Assert.Empty(offenders);
    }

    internal static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".git")) && !File.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
