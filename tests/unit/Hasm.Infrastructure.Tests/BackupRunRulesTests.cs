using System.Security.Cryptography;
using System.Text.Json;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 4 (D-069..D-073): the pure rules of a backup run (run id, report, system manifest, status, retention preview, secret-store exclusions, path rules). No database; the run itself is covered by
/// the PostgreSQL suite (<c>BackupRunTests</c>).
/// </summary>
public sealed class BackupRunRulesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-brr-" + Guid.NewGuid().ToString("N"));
    public BackupRunRulesTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
    private string P(params string[] parts) => Path.Combine([root, .. parts]);

    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 3, 0, 0, TimeSpan.Zero);

    private static BackupRunReport Report(string runId, string verdict = "ok", DateTimeOffset? finished = null, params RunCompany[] companies) =>
        new(1, runId, BackupRunFiles.StartOf(runId)!.Value, finished ?? BackupRunFiles.StartOf(runId)!.Value.AddMinutes(5), 300000,
            new RunDump("ok", null, T0, T0, 1, 10), companies, 0, verdict, [], ["session", "login_throttle"], 16, false);

    private static RunCompany C(Guid id, string outcome) => new(id, outcome, outcome is "busy" ? "company_busy" : outcome is "failed" ? "company_database_missing" : null, T0, T0, 1, 10, 0, 0);

    private static string Id(DateTimeOffset t) => BackupRunFiles.NewRunId(t);
    // status and retention rules are exercised on ENCRYPTED runs of a recovery-tested key (slice 5); an unencrypted run always gives unencrypted_dev_test (D-095) and an untested key recovery_untested
    // (D-085), asserted separately
    private const string Rk = "rk-0123456789abcdef";
    private static RunEntry Entry(BackupRunReport r, string recoveryKey = Rk, bool tested = true) => new(r.RunId, r with { Encrypted = true }, true, r.FinishedUtc, recoveryKey, tested);

    // ---- run id ----------------------------------------------------------------------------------

    [Fact]
    public void Run_ids_have_the_fixed_format_and_sort_by_time()
    {
        var a = Id(T0); var b = Id(T0.AddSeconds(1)); var c = Id(T0.AddDays(1));
        Assert.Matches("^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$", a);
        Assert.Equal(new[] { a, b, c }, new[] { c, a, b }.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(T0, BackupRunFiles.StartOf(a));
        Assert.False(BackupRunFiles.IsRunId("20261121T030000Z-XYZ"));
        Assert.False(BackupRunFiles.IsRunId("../20261121T030000Z-0000000a"));
        Assert.Null(BackupRunFiles.StartOf("nope"));
    }

    // ---- run.json --------------------------------------------------------------------------------

    [Fact]
    public void The_run_report_round_trips_owner_only_and_malformed_unknown_or_path_bearing_reports_are_refused()
    {
        var id = Id(T0);
        var dir = P(id); Directory.CreateDirectory(dir);
        var report = Report(id, "partial", null, C(Guid.NewGuid(), "ok"), C(Guid.NewGuid(), "busy"));
        BackupRunFiles.WriteReport(dir, report);
        var read = BackupRunFiles.ReadReport(dir)!;
        Assert.Equal(report.RunId, read.RunId);
        Assert.Equal(2, read.Companies.Count);
        Assert.Equal("partial", read.Verdict);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, BackupRunFiles.ReportFileName)));
        Assert.Throws<IOException>(() => BackupRunFiles.WriteReport(dir, report));                                   // never overwritten

        var json = File.ReadAllText(Path.Combine(dir, BackupRunFiles.ReportFileName));
        void Bad(string content)
        {
            var other = P(Id(T0.AddHours(1))); Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, BackupRunFiles.ReportFileName), content.Replace(id, Path.GetFileName(other)));
            Assert.Null(BackupRunFiles.ReadReport(other));
        }
        Bad("not json");
        Bad(json.Replace("\"version\": 1", "\"version\": 2"));
        Bad(json.Replace("\"verdict\": \"partial\"", "\"verdict\": \"great\""));
        Bad(json.Replace("\"code\": \"company_busy\"", "\"code\": \"/var/lib/hasm/attachments\""));          // a path is not a code
        Bad(json.Replace("\"outcome\": \"busy\"", "\"outcome\": \"maybe\""));
        Bad(json.Replace("\"encrypted\": false", "\"encrypted\": true"));
        Bad(json.Replace("\"login_throttle\"", "\"Login Throttle\""));
        var moved = P(Id(T0.AddHours(2))); Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, BackupRunFiles.ReportFileName), json);                                 // a report of another run id
        Assert.Null(BackupRunFiles.ReadReport(moved));
        Assert.Null(BackupRunFiles.ReadReport(P("absent")));
    }

    [Fact]
    public void A_run_lock_warning_round_trips_and_a_warning_that_is_not_a_code_is_refused()
    {
        var id = Id(T0);
        var dir = P(id); Directory.CreateDirectory(dir);
        BackupRunFiles.WriteReport(dir, Report(id, "partial") with { Warnings = ["run_lock_lost"] });
        Assert.Equal(new[] { "run_lock_lost" }, BackupRunFiles.ReadReport(dir)!.Warnings);
        var other = P(Id(T0.AddHours(3))); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, BackupRunFiles.ReportFileName), File.ReadAllText(Path.Combine(dir, BackupRunFiles.ReportFileName)).Replace(id, Path.GetFileName(other)).Replace("run_lock_lost", "lost: /var/x"));
        Assert.Null(BackupRunFiles.ReadReport(other));
    }

    [Fact]
    public async Task An_unexpected_exception_in_the_keep_alive_marks_it_failed_and_dispose_never_throws()
    {
        var keepAlive = new SessionKeepAlive(() => throw new ArgumentException("unexpected type"), TimeSpan.FromMilliseconds(5));   // review L7
        for (var i = 0; i < 200 && !keepAlive.Failed; i++) await Task.Delay(10);
        Assert.True(keepAlive.Failed);
        await keepAlive.DisposeAsync();
        await keepAlive.DisposeAsync();                                                                               // idempotent, never throws
        var faultingAsync = new SessionKeepAlive(async () => { await Task.Yield(); throw new NotSupportedException(); }, TimeSpan.FromMilliseconds(5));
        for (var i = 0; i < 200 && !faultingAsync.Failed; i++) await Task.Delay(10);
        Assert.True(faultingAsync.Failed);
        await faultingAsync.DisposeAsync();
    }

    [Fact]
    public void The_verdict_is_partial_with_run_lock_lost_whenever_the_lock_is_not_confirmed()
    {
        var ok = new RunDump("ok", null, T0, T0, 1, 10);
        var failed = new RunDump("failed", "dump_failed", T0, T0, 1, 0);
        RunCompany[] fine = [C(Guid.NewGuid(), "ok")];
        Assert.Equal(("ok", (IReadOnlyList<string>?)null), BackupRunService.ComputeVerdict(ok, fine, lockHeld: true));
        var lost = BackupRunService.ComputeVerdict(ok, fine, lockHeld: false);                                        // review L6: never ok without the synchronous lock check
        Assert.Equal("partial", lost.Verdict);
        Assert.Equal(new[] { "run_lock_lost" }, lost.Warnings);
        Assert.Equal("partial", BackupRunService.ComputeVerdict(ok, [C(Guid.NewGuid(), "busy")], true).Verdict);
        Assert.Equal("failed", BackupRunService.ComputeVerdict(failed, fine, true).Verdict);
        Assert.Equal("partial", BackupRunService.ComputeVerdict(ok, [C(Guid.NewGuid(), "failed")], true).Verdict);     // a failed company with the system ok is partial
        Assert.Equal("failed", BackupRunService.ComputeVerdict(failed, [], false).Verdict);
    }

    [Fact]
    public async Task The_keep_alive_reports_a_lost_session_and_stops_when_disposed()
    {
        await using (var lost = new Npgsql.NpgsqlConnection("Host=127.0.0.1;Port=1"))                                // never opened: every no-op fails
        {
            var keepAlive = new SessionKeepAlive(lost, TimeSpan.FromMilliseconds(10));
            for (var i = 0; i < 200 && !keepAlive.Failed; i++) await Task.Delay(10);
            Assert.True(keepAlive.Failed);
            await keepAlive.DisposeAsync();
        }
        await using var idle = new Npgsql.NpgsqlConnection("Host=127.0.0.1;Port=1");
        var quiet = new SessionKeepAlive(idle, TimeSpan.FromHours(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await quiet.DisposeAsync();                                                                                   // stops at once, before any no-op
        Assert.False(quiet.Failed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    // ---- system manifest -------------------------------------------------------------------------

    [Fact]
    public void The_system_manifest_round_trips_and_verification_detects_missing_altered_and_unlisted_files()
    {
        var dir = P("system"); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, BackupSet.DatabaseFileName), RandomNumberGenerator.GetBytes(2048));
        var m = SystemBackupSet.WriteManifest(dir, T0, new DatabaseLocale("UTF8", "C", "C", "c"), 31, ["session", "login_throttle"]);
        Assert.Equal(m.Database.Sha256, SystemBackupSet.ReadManifest(dir)!.Database.Sha256);
        Assert.Equal(new[] { "session", "login_throttle" }, SystemBackupSet.ReadManifest(dir)!.Excluded);
        Assert.True(SystemBackupSet.Verify(dir));
        File.WriteAllText(Path.Combine(dir, "extra"), "x");
        Assert.False(SystemBackupSet.Verify(dir));                                                                    // unlisted
        File.Delete(Path.Combine(dir, "extra"));
        var bytes = File.ReadAllBytes(Path.Combine(dir, BackupSet.DatabaseFileName)); bytes[100] ^= 1;
        File.WriteAllBytes(Path.Combine(dir, BackupSet.DatabaseFileName), bytes);
        Assert.False(SystemBackupSet.Verify(dir));                                                                    // altered
        File.Delete(Path.Combine(dir, BackupSet.DatabaseFileName));
        Assert.False(SystemBackupSet.Verify(dir));                                                                    // missing
        var path = Path.Combine(dir, BackupSet.ManifestFileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"kind\": \"system\"", "\"kind\": \"company\""));
        Assert.Null(SystemBackupSet.ReadManifest(dir));
    }

    // ---- status (D-071) --------------------------------------------------------------------------

    [Fact]
    public void Status_maps_ok_partial_failed_missing_and_stale_to_exit_codes_and_skips_an_incomplete_newest_folder()
    {
        var now = T0.AddHours(10);
        var max = BackupRunStatus.DefaultMaxAge;
        var ok = Report(Id(T0));
        Assert.Equal((0, "ok"), Code(BackupRunStatus.Evaluate([Entry(ok)], now, max)));
        Assert.Equal((8, "backup_partial"), Code(BackupRunStatus.Evaluate([Entry(Report(Id(T0), "partial"))], now, max)));
        Assert.Equal((1, "backup_failed"), Code(BackupRunStatus.Evaluate([Entry(Report(Id(T0), "failed"))], now, max)));
        Assert.Equal((1, "backup_missing"), Code(BackupRunStatus.Evaluate([], now, max)));
        Assert.Equal((8, "unencrypted_dev_test"), Code(BackupRunStatus.Evaluate([new RunEntry(ok.RunId, ok, true, ok.FinishedUtc)], now, max)));   // D-095: never green
        Assert.Equal((1, "status_unsigned"), Code(BackupRunStatus.Evaluate([new RunEntry(ok.RunId, ok with { Verdict = "failed", Encrypted = true, Warnings = ["status_unsigned"] }, true, ok.FinishedUtc)], now, max)));
        Assert.Equal((1, "backup_stale"), Code(BackupRunStatus.Evaluate([Entry(ok)], T0.AddHours(27), max)));
        Assert.Equal((1, "backup_stale"), Code(BackupRunStatus.Evaluate([Entry(ok)], now, TimeSpan.FromHours(1))));

        // D-085, D-117: the newest run's recovery key must have passed a recovery test on SOME run encrypted to it; a tested OTHER key does not count
        var older = Report(Id(T0.AddHours(-5)));
        Assert.Equal((8, "recovery_untested"), Code(BackupRunStatus.Evaluate([Entry(ok, tested: false)], now, max)));
        Assert.Equal((0, "ok"), Code(BackupRunStatus.Evaluate([Entry(ok, tested: false), Entry(older)], now, max)));
        Assert.Equal((8, "recovery_untested"), Code(BackupRunStatus.Evaluate([Entry(ok, "rk-fedcba9876543210", tested: false), Entry(older)], now, max)));   // a rotated key is not production-ready yet
        Assert.Equal((8, "recovery_untested"), Code(BackupRunStatus.Evaluate([Entry(ok, tested: false), new RunEntry(older.RunId, null, false, T0, Rk, true)], now, max)));   // only complete runs count

        // an incomplete newer folder (no run.json) is skipped; reported once older than 24 hours; an unreadable report is counted, never used
        var incomplete = new RunEntry(Id(T0.AddHours(1)), null, false, T0.AddHours(1));
        var unreadable = new RunEntry(Id(T0.AddHours(2)), null, true, T0.AddHours(2));
        var s = BackupRunStatus.Evaluate([incomplete, unreadable, Entry(ok)], now, max);
        Assert.Equal((8, "unreadable_reports"), Code(s));                                                             // review L2: an unreadable report never passes silently
        Assert.Equal((0, "ok"), Code(BackupRunStatus.Evaluate([incomplete, Entry(ok)], now, max)));                  // a young incomplete folder alone does not warn
        Assert.Equal((1, "backup_failed"), Code(BackupRunStatus.Evaluate([unreadable, Entry(Report(Id(T0), "failed"))], now, max)));   // a failure still wins
        Assert.Equal(ok.RunId, s.Latest!.RunId);
        Assert.Equal(0, s.IncompleteRuns);                                                                            // younger than 24 hours
        Assert.Equal(1, s.UnreadableRuns);
        Assert.Equal(1, BackupRunStatus.Evaluate([incomplete, Entry(ok)], T0.AddHours(26), TimeSpan.FromDays(2)).IncompleteRuns);
    }

    [Fact]
    public void Consecutive_busy_or_failed_misses_per_company_are_counted_and_two_or_more_warn()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var r1 = Report(Id(T0), "ok", null, C(a, "ok"), C(b, "ok"));
        var r2 = Report(Id(T0.AddDays(1)), "partial", null, C(a, "busy"), C(b, "ok"));
        var r3 = Report(Id(T0.AddDays(2)), "partial", null, C(a, "failed"), C(b, "busy"));
        var r4 = Report(Id(T0.AddDays(3)), "ok", null, C(a, "ok"), C(b, "ok"));
        var missed = BackupRunStatus.MissedRuns([r3, r2, r1]);
        Assert.Equal(2, missed[a]);
        Assert.False(missed.ContainsKey(b));                                                                           // one miss only
        Assert.Empty(BackupRunStatus.MissedRuns([r4, r3, r2, r1]));                                                   // the newest run succeeded
        // an ok verdict with a company that missed twice before... (a run where the company is absent breaks the streak)
        var r5 = Report(Id(T0.AddDays(4)), "ok", null, C(b, "ok"));
        Assert.Empty(BackupRunStatus.MissedRuns([r5, r3, r2]));
        var s = BackupRunStatus.Evaluate([Entry(r3), Entry(r2), Entry(r1)], r3.FinishedUtc.AddHours(1), BackupRunStatus.DefaultMaxAge);
        Assert.Equal((8, "backup_partial"), Code(s));
        Assert.Equal(2, s.MissedRuns[a]);
    }

    private static (int, string) Code(BackupStatusResult r) => (r.ExitCode, r.Code);

    // ---- retention preview (D-072) ---------------------------------------------------------------

    [Fact]
    public void Retention_preview_selects_only_complete_runs_beyond_the_policy_and_never_the_newest_incomplete_or_unreadable()
    {
        var runs = Enumerable.Range(0, 6).Select(i => Entry(Report(Id(T0.AddDays(i))))).ToList();                 // day 0 .. day 5
        var now = T0.AddDays(5).AddHours(1);
        var all = runs.Select(r => r.RunId).OrderByDescending(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(all.Skip(2), BackupRunStatus.RetentionPreview(runs, now, keepLast: 2, keepDays: null));
        Assert.Equal(all.Skip(3), BackupRunStatus.RetentionPreview(runs, now, keepLast: 1, keepDays: 3));           // days 3..5 are younger than 3 days
        Assert.Equal(all.Skip(2), BackupRunStatus.RetentionPreview(runs, now, keepLast: 1, keepDays: 2));           // a run kept by either rule is kept (days 4..5)
        Assert.Empty(BackupRunStatus.RetentionPreview(runs, now, keepLast: 10, keepDays: null));
        var withOthers = runs.Concat([new RunEntry(Id(T0.AddDays(-5)), null, false, T0), new RunEntry(Id(T0.AddDays(-6)), null, true, T0)]).ToList();
        var selected = BackupRunStatus.RetentionPreview(withOthers, now, keepLast: 1, keepDays: null);
        Assert.Equal(5, selected.Count);
        Assert.DoesNotContain(all[0], selected);                                                                      // the newest complete run
        Assert.All(withOthers.Where(r => !r.IsComplete), r => Assert.DoesNotContain(r.RunId, selected));
        Assert.Equal("keep_last_invalid", Assert.Throws<BackupException>(() => BackupRunStatus.RetentionPreview(runs, now, 0, null)).Code);
        Assert.All(runs, r => Assert.True(r.IsComplete));                                                             // nothing was touched
    }

    // ---- secret-store exclusions (D-070, review R2-L2) -------------------------------------------

    [Fact]
    public void The_system_dump_exclusions_equal_the_secret_store_flag_in_both_directions()
    {
        var excluded = BackupRunService.SystemDumpExclusions.Select(e => e.LogicalTable).ToHashSet(StringComparer.Ordinal);
        Assert.True(excluded.SetEquals(SystemSchema.SecretStoreTables), "every secret-store table must be excluded from the system dump, and only those");
        Assert.All(excluded, t => Assert.True(SystemSchema.Map.ContainsKey(t), t));
        Assert.Equal(BackupRunService.SystemDumpExclusions.Count, BackupRunService.SystemDumpExclusions.Select(e => e.Label).Distinct().Count());
        Assert.All(BackupRunService.SystemDumpExclusions, e => Assert.Matches("^[a-z0-9_]{1,64}$", e.Label));
        Assert.Contains("Session.Table", SystemSchema.SecretStoreTables);
        Assert.Contains("LoginThrottle.Table", SystemSchema.SecretStoreTables);
    }

    // ---- path rules ------------------------------------------------------------------------------

    [Fact]
    public void A_run_root_inside_or_containing_the_attachment_root_or_the_key_directory_is_refused()
    {
        var storage = P("storage"); var keys = P("keys"); Directory.CreateDirectory(storage); Directory.CreateDirectory(keys);
        var att = new AttachmentOptions { StorageDirectory = storage };
        var enc = new EncryptionOptions { KeyDirectory = keys };
        string Refused(string r, AttachmentOptions? a = null) => Assert.Throws<BackupException>(() => BackupRunService.CheckRoot(r, a ?? att, enc)).Code;
        Assert.Equal("destination_overlaps_source", Refused(Path.Combine(storage, "runs")));
        Assert.Equal("destination_overlaps_source", Refused(Path.Combine(keys, "runs")));
        Assert.Equal("destination_overlaps_source", Refused(root));                                                  // contains both
        Assert.Equal("path_not_absolute", Refused("relative"));
        Assert.Equal("attachment_storage_not_configured", Refused(P("runs"), new AttachmentOptions()));
        Assert.Equal(P("runs"), BackupRunService.CheckRoot(P("runs"), att, enc));
    }

    [Fact]
    public void A_run_folder_is_fresh_and_owner_only()
    {
        var dir = P(Id(T0));
        CompanyBackupService.CreatePrivateDirectory(dir, "run_exists", "run_not_private");
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        Assert.Equal("run_exists", Assert.Throws<BackupException>(() => CompanyBackupService.CreatePrivateDirectory(dir, "run_exists", "run_not_private")).Code);
    }
}
