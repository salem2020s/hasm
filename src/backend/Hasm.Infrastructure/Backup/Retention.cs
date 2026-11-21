using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Backup;

/// <summary>
/// Retention settings (section <c>Hasm:Backup:Retention</c>; Phase 4.3 slice 6, D-132, D-137). The D-086 minimums (7 daily, 4 weekly) may be raised, never lowered: a lower or invalid value is a
/// configuration error and is never clamped. The installation time zone (system part) is required; the server's local time zone is never used.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Hasm:Backup:Retention";
    /// <summary>D-086 minimums. Lowering them needs a new owner decision (pinned by a unit test).</summary>
    public const int MinimumDaily = 7, MinimumWeekly = 4;
    public const int MaximumKeep = 10_000;
    public const int DefaultMaxDeletionsPerRun = 7, MaximumDeletionsPerRun = 31;
    /// <summary>The exact per-installation opt-in to automatic deletion (D-137 layer 3). Any other non-empty value is a configuration error.</summary>
    public const string OptInValue = "yes-delete-eligible-backups";

    public int DailyKeep { get; init; } = MinimumDaily;
    public int WeeklyKeep { get; init; } = MinimumWeekly;
    public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;
    public string? InstallationTimeZone { get; init; }
    public int MaxDeletionsPerRun { get; init; } = DefaultMaxDeletionsPerRun;
    public string? AutomaticDeletion { get; init; }
    /// <summary>A setting that could not be parsed (reported by <see cref="Validate"/>; never silently replaced by a default).</summary>
    public string? InvalidSetting { get; init; }

    public static RetentionOptions FromConfiguration(IConfiguration configuration)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{key}"]) ? null : configuration[$"{SectionName}:{key}"]!.Trim();
        string? invalid = null;
        int Int(string key, int fallback)
        {
            if (Get(key) is not { } v) return fallback;
            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
            invalid ??= key;
            return fallback;
        }
        var weekStart = DayOfWeek.Monday;
        if (Get("WeekStart") is { } ws && !Enum.TryParse(ws, ignoreCase: false, out weekStart)) invalid ??= "WeekStart";
        if (!Enum.IsDefined(weekStart)) invalid ??= "WeekStart";
        return new RetentionOptions
        {
            DailyKeep = Int("DailyKeep", MinimumDaily), WeeklyKeep = Int("WeeklyKeep", MinimumWeekly), WeekStart = weekStart,
            InstallationTimeZone = Get("InstallationTimeZone"), MaxDeletionsPerRun = Int("MaxDeletionsPerRun", DefaultMaxDeletionsPerRun), AutomaticDeletion = Get("AutomaticDeletion"),
            InvalidSetting = invalid,
        };
    }

    /// <summary>Refuses any configuration that would lower a minimum or that cannot be read (<c>retention_below_minimum</c>, <c>retention_config_invalid</c>, <c>retention_time_zone_invalid</c>).</summary>
    public void Validate()
    {
        if (InvalidSetting is not null) throw new BackupException("retention_config_invalid");
        if (DailyKeep < MinimumDaily || WeeklyKeep < MinimumWeekly) throw new BackupException("retention_below_minimum");
        if (DailyKeep > MaximumKeep || WeeklyKeep > MaximumKeep || MaxDeletionsPerRun is < 1 or > MaximumDeletionsPerRun) throw new BackupException("retention_config_invalid");
        if (AutomaticDeletion is not null && !string.Equals(AutomaticDeletion, OptInValue, StringComparison.Ordinal)) throw new BackupException("retention_config_invalid");
        if (RetentionTime.FindZone(InstallationTimeZone) is null) throw new BackupException("retention_time_zone_invalid");
    }

    public TimeZoneInfo InstallationZone() => RetentionTime.FindZone(InstallationTimeZone) ?? throw new BackupException("retention_time_zone_invalid");

    /// <summary>D-137 layer 3: this installation explicitly opted in to automatic deletion.</summary>
    public bool OptedIn => string.Equals(AutomaticDeletion, OptInValue, StringComparison.Ordinal);
}

/// <summary>
/// D-137 layer 1, the CODE GATE of automatic backup deletion. It is <c>false</c> in this slice and pinned by a unit test. Changing it is an activation PR that agents never self-merge: it needs an explicit
/// owner instruction and the recorded evidence that the encryption, retention and restore safeguards are complete and independently tested (D-086, `docs/PHASE-4.3.md` slice 6).
/// </summary>
public static class BackupRetention
{
    public const bool AutomaticDeletionActivated = false;

    /// <summary>Layers 1 and 3 together (review M1 of #130: also checked inside the executor's lowest-level delete routine).</summary>
    public static bool GateOpen(RetentionOptions options) => AutomaticDeletionActivated && options.OptedIn;
}

/// <summary>Explicit time-zone rules of the classification (D-131). The server's local time zone is never used.</summary>
public static class RetentionTime
{
    /// <summary>The zone of an IANA (or platform) id; null when absent, unknown or unresolvable (the caller then classifies as unknown and keeps the run).</summary>
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return null; }
    }

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// The instant at which local <paramref name="date"/> begins: local 00:00; a local time skipped by a daylight-saving change becomes the first valid instant after it; a repeated local time takes the
    /// EARLIER instant.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(local); i++) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local)) return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max());   // the larger offset is the earlier instant
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The first day of the week containing <paramref name="date"/> (week starting on <paramref name="start"/>; ISO 8601 with Monday).</summary>
    public static DateOnly WeekOf(DateOnly date, DayOfWeek start) => date.AddDays(-((7 + (int)date.DayOfWeek - (int)start) % 7));
}

/// <summary>
/// One run as retention sees it (from the root scan and its VERIFIED signed status; nothing is decrypted). <see cref="Keys"/> is null when its key needs are unknown (review M2 of #130: a version 1, unverifiable,
/// missing or unreadable status, or an incomplete run).
/// </summary>
public sealed record RetentionRun(string RunId, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, bool Complete, bool Verified, bool Encrypted, string? Verdict, string? RecoveryKeyId,
    bool ProofValid, IReadOnlyList<RunStatusCompany> Companies, string? StatusSha256, bool Clean, bool KeptTrial, RunKeyNeeds? Keys);

/// <summary>The key ids a run needs (run status version 2, D-138): attachment master keys per company and audit keys.</summary>
public sealed record RunKeyNeeds(IReadOnlyList<string> EncryptionKeyIds, IReadOnlyList<string> AuditKeyIds);

/// <summary>A fiscal year of a company calendar: its dates, its own time zone and its periods (D-131).</summary>
public sealed record RetentionFiscalYear(DateOnly Start, DateOnly End, string? TimeZoneId, IReadOnlyList<RetentionPeriod> Periods);
public sealed record RetentionPeriod(DateOnly Start, DateOnly End);

/// <summary>
/// What retention knows about a company: whether it could be read, its configured time zone and fiscal calendar, and a company-wide keep code from its attachment holds (<c>attachment_hold</c>,
/// <c>d114_hold</c>, <c>hold_check_failed</c>; D-134) or null.
/// </summary>
public sealed record RetentionCompany(Guid CompanyId, bool Readable, string? TimeZoneId, IReadOnlyList<RetentionFiscalYear> Years, string? HoldCode = null);

/// <summary>A backup legal hold (D-134): scope 1 one run, scope 2 a company's runs started within an optional local-date range.</summary>
public sealed record BackupLegalHoldRow(Guid Id, int Scope, string? RunId, Guid? CompanyId, DateOnly? From, DateOnly? To, bool Released);

/// <summary>A recovery-chain pin (D-135).</summary>
public sealed record RetentionPinRow(Guid Id, string RunId, int Kind, DateTimeOffset ExpiresUtc);

/// <summary>The decision for one run: eligible for deletion, or the keep reasons; the class tags per part (<c>system</c> or a company id).</summary>
public sealed record RunDecision(string RunId, bool Eligible, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, IReadOnlyList<string>> Tags, string? StatusSha256);

/// <summary>
/// The retention plan: a decision per run (newest first), the plan hash (SHA-256 over the eligible run ids and their status hashes), and the key inventory of the RETAINED runs (D-138): complete only when
/// every retained run's key needs are known.
/// </summary>
public sealed record RetentionPlan(IReadOnlyList<RunDecision> Runs, string PlanHash, bool KeyInventoryComplete, IReadOnlyList<string> EncryptionKeysNeeded, IReadOnlyList<string> AuditKeysNeeded,
    IReadOnlyList<string> RecoveryKeysNeeded)
{
    public IEnumerable<RunDecision> Eligible => Runs.Where(r => r.Eligible);
}

/// <summary>
/// The pure D-086 classification (D-130..D-135): every rule adds a keep reason; a run is eligible only when no rule keeps it for any part. Never deletes anything and never reads a clock of its own
/// (<paramref name="now"/> is given).
/// </summary>
public static class RetentionClassifier
{
    public const string SystemPart = "system";

    public static RetentionPlan Plan(IReadOnlyList<RetentionRun> runs, IReadOnlyList<RetentionCompany> companies, IReadOnlyList<BackupLegalHoldRow> holds, IReadOnlyList<RetentionPinRow> pins,
        RetentionOptions options, DateTimeOffset now)
    {
        var installation = options.InstallationZone();
        var reasons = runs.ToDictionary(r => r.RunId, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var tags = runs.ToDictionary(r => r.RunId, _ => new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal), StringComparer.Ordinal);
        void Keep(RetentionRun r, string reason) => reasons[r.RunId].Add(reason);
        void Tag(RetentionRun r, string part, string tag)
        {
            if (!tags[r.RunId].TryGetValue(part, out var set)) tags[r.RunId][part] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(tag);
        }
        static IEnumerable<RetentionRun> NewestFirst(IEnumerable<RetentionRun> xs) => xs.OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RunId, StringComparer.Ordinal);

        // D-133: run-level states that are never deleted automatically (and never count)
        var testedKeys = runs.Where(r => r.Complete && r.Verified && r.ProofValid && r.RecoveryKeyId is not null).Select(r => r.RecoveryKeyId!).ToHashSet(StringComparer.Ordinal);
        foreach (var r in runs)
        {
            if (!r.Complete) Keep(r, "incomplete");
            else if (!r.Verified) Keep(r, "status_unverifiable");
            else if (!r.Encrypted) Keep(r, "unencrypted");
            else if (r.RecoveryKeyId is null || !testedKeys.Contains(r.RecoveryKeyId)) Keep(r, "recovery_key_untested");
            if (!r.Clean) Keep(r, "run_not_clean");
            if (r.KeptTrial) Keep(r, "recovery_chain");
        }
        bool Usable(RetentionRun r) => r.Complete && r.Verified && r.Encrypted && r.RecoveryKeyId is not null && testedKeys.Contains(r.RecoveryKeyId);
        var usable = runs.Where(Usable).ToList();
        var complete = runs.Where(r => r.Complete).ToList();

        // hard keeps: the newest complete run; the newest run with a passed recovery test per recovery key (D-133)
        if (NewestFirst(complete).FirstOrDefault() is { } newest) Keep(newest, "newest");
        foreach (var g in runs.Where(r => r.Complete && r.Verified && r.ProofValid && r.RecoveryKeyId is not null).GroupBy(r => r.RecoveryKeyId!, StringComparer.Ordinal))
            Keep(NewestFirst(g).First(), "recovery_test_proof");

        // the system part: daily, weekly, last recoverable (installation time zone)
        ApplyMinimums(SystemPart, [.. usable.Where(r => r.Verdict is not "failed")], installation, options, Keep, Tag);

        // every company part
        var byId = companies.ToDictionary(c => c.CompanyId);
        foreach (var companyId in complete.SelectMany(r => r.Companies).Select(c => c.CompanyId).Distinct().OrderBy(id => id))
        {
            var part = companyId.ToString("D");
            var containing = complete.Where(r => r.Companies.Any(c => c.CompanyId == companyId)).ToList();
            var company = byId.GetValueOrDefault(companyId);
            // D-131 and review L1 of #130: an unreadable company, or a company time zone that is not configured, unknown or unresolvable, is classification_unknown: its runs are kept
            var zone = company is { Readable: true } ? RetentionTime.FindZone(company.TimeZoneId) : null;
            if (company is null || zone is null)
            {
                foreach (var r in containing) { Keep(r, "classification_unknown"); Tag(r, part, "unknown"); }
                continue;
            }
            if (company.HoldCode is { } holdCode) foreach (var r in containing) Keep(r, holdCode);

            var recoverable = usable.Where(r => r.Companies.Any(c => c.CompanyId == companyId && c.Outcome == "ok")).ToList();
            ApplyMinimums(part, recoverable, zone, options, Keep, Tag);

            // fiscal coverage: a run date outside every fiscal year (or in a year whose zone cannot be resolved) is classification_unknown for period-end and year-end
            var years = company.Years.Select(y => (Year: y, Zone: RetentionTime.FindZone(y.TimeZoneId))).ToList();
            foreach (var r in containing)
                if (!years.Any(y => y.Zone is not null && RetentionTime.LocalDate(r.StartedUtc, y.Zone) is var d && d >= y.Year.Start && d <= y.Year.End))
                { Keep(r, "classification_unknown"); Tag(r, part, "unknown"); }

            // boundaries: every fiscal period end and fiscal year end (year's zone), and every calendar month end (company zone), up to now (D-131, owner questions Q2 and Q3: both kept)
            var boundaries = new List<(DateTimeOffset At, string Tag)>();
            foreach (var (year, yz) in years.Where(y => y.Zone is not null))
            {
                foreach (var p in year.Periods) boundaries.Add((RetentionTime.StartOfDay(p.End.AddDays(1), yz!), "period_end"));
                boundaries.Add((RetentionTime.StartOfDay(year.End.AddDays(1), yz!), "year_end"));
            }
            if (recoverable.Count > 0)
            {
                var first = RetentionTime.LocalDate(recoverable.Min(r => r.StartedUtc), zone);
                var last = RetentionTime.LocalDate(now, zone);
                for (var m = new DateOnly(first.Year, first.Month, 1); m <= last; m = m.AddMonths(1))
                    boundaries.Add((RetentionTime.StartOfDay(m.AddMonths(1), zone), "period_end"));
            }
            foreach (var (at, tag) in boundaries.Where(b => b.At <= now))
            {
                var before = NewestFirst(recoverable.Where(r => r.StartedUtc < at)).FirstOrDefault();
                var after = recoverable.Where(r => r.FinishedUtc >= at).OrderBy(r => r.StartedUtc).ThenBy(r => r.RunId, StringComparer.Ordinal).FirstOrDefault();
                foreach (var r in new[] { before, after })
                    if (r is not null) { Keep(r, tag); Tag(r, part, tag); }   // D-133: never deleted automatically (no legal policy until owner questions Q1-Q3)
            }

            // company-scoped legal holds (local date of the run start in the company's zone)
            foreach (var h in holds.Where(h => !h.Released && h.Scope == 2 && h.CompanyId == companyId))
                foreach (var r in containing)
                    if (RetentionTime.LocalDate(r.StartedUtc, zone) is var d && (h.From is null || d >= h.From) && (h.To is null || d <= h.To)) Keep(r, "legal_hold");
        }

        // a company-scoped hold on a company that appears in no complete run keeps nothing further; run-scoped holds and pins
        foreach (var h in holds.Where(h => !h.Released && h.Scope == 1 && h.RunId is not null))
            if (reasons.ContainsKey(h.RunId!)) reasons[h.RunId!].Add("legal_hold");
        foreach (var p in pins.Where(p => p.ExpiresUtc > now))
            if (reasons.ContainsKey(p.RunId)) reasons[p.RunId].Add("recovery_chain");

        var decisions = NewestFirst(runs).Select(r => new RunDecision(r.RunId, r.Complete && reasons[r.RunId].Count == 0, [.. reasons[r.RunId]],
            tags[r.RunId].ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value], StringComparer.Ordinal), r.StatusSha256)).ToList();

        // D-138 and review M2 of #130: the key inventory of the retained runs; any unknown key needs make it incomplete
        var retained = runs.Where(r => !decisions.Single(d => d.RunId == r.RunId).Eligible).ToList();
        var complete2 = retained.All(r => r.Complete && r.Verified && r.Keys is not null);
        return new RetentionPlan(decisions, Hash(decisions), complete2,
            [.. retained.SelectMany(r => r.Keys?.EncryptionKeyIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. retained.SelectMany(r => r.Keys?.AuditKeyIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. retained.Where(r => r.RecoveryKeyId is not null).Select(r => r.RecoveryKeyId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>Daily and weekly minimums and the last recoverable run of one part (D-132, D-133): counted over the latest distinct local days and weeks that HAVE a recoverable run.</summary>
    private static void ApplyMinimums(string part, IReadOnlyList<RetentionRun> recoverable, TimeZoneInfo zone, RetentionOptions options, Action<RetentionRun, string> keep, Action<RetentionRun, string, string> tag)
    {
        if (recoverable.Count == 0) return;
        static RetentionRun Newest(IEnumerable<RetentionRun> xs) => xs.OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RunId, StringComparer.Ordinal).First();
        foreach (var day in recoverable.GroupBy(r => RetentionTime.LocalDate(r.StartedUtc, zone)).OrderByDescending(g => g.Key).Take(options.DailyKeep))
        { var r = Newest(day); keep(r, "minimum_daily"); tag(r, part, "daily"); }
        foreach (var week in recoverable.GroupBy(r => RetentionTime.WeekOf(RetentionTime.LocalDate(r.StartedUtc, zone), options.WeekStart)).OrderByDescending(g => g.Key).Take(options.WeeklyKeep))
        { var r = Newest(week); keep(r, "minimum_weekly"); tag(r, part, "weekly"); }
        keep(Newest(recoverable), "last_recoverable");
    }

    private static string Hash(IEnumerable<RunDecision> decisions)
    {
        var sb = new StringBuilder();
        foreach (var d in decisions.Where(d => d.Eligible).OrderBy(d => d.RunId, StringComparer.Ordinal)) sb.Append(d.RunId).Append(':').Append(d.StatusSha256).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
