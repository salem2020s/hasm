using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Configuration;
using static Hasm.Infrastructure.Tests.RetentionClassifierTests;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 6 (D-136, D-137; review M1 of #130): automatic deletion is INACTIVE by three layers, the gate is checked in the lowest-level rename/delete routine, only the internal test seam can
/// pass it; the executor (through the seam, on temporary roots) is audit-first, re-validates every run, writes a verifiable tombstone, resumes, and refuses on stale backups, a clock problem or a
/// suspicious volume; the root scan reads only verified statuses and finds unclean runs and kept trials.
/// </summary>
public sealed class RetentionExecutorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hasm-ret-" + Guid.NewGuid().ToString("N"));
    private readonly List<(short Code, string Detail)> audit = [];

    public RetentionExecutorTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static DateTimeOffset T(int day, int hour = 2) => new(2026, 3, day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>Daily runs 2..25 March as folders with the slice 5 files (content is irrelevant here: the classifier reads the inputs given).</summary>
    private List<RetentionRun> Runs(int last = 25)
    {
        var runs = new List<RetentionRun>();
        for (var d = 2; d <= last; d++)
        {
            var r = Run(T(d), proof: d == last);
            Directory.CreateDirectory(Path.Combine(root, r.RunId, "system"));
            File.WriteAllText(Path.Combine(root, r.RunId, SignedDocuments.RunStatusFileName), "{\"run\":\"" + r.RunId + "\"}");
            File.WriteAllText(Path.Combine(root, r.RunId, SignedDocuments.RunStatusFileName + ".sig"), "sig");
            File.WriteAllText(Path.Combine(root, r.RunId, "canary.hbe"), "c");
            File.WriteAllText(Path.Combine(root, r.RunId, "system", "database.dump.hbe"), "dump");
            runs.Add(r);
        }
        return runs;
    }

    private RetentionExecutor.Context Ctx(List<RetentionRun> runs, string status = "ok", DateTimeOffset? now = null, RetentionOptions? options = null, Func<RetentionInputs>? read = null) =>
        new(root, options ?? Opt(), now ?? T(26, 0), status,
            _ => Task.FromResult(read?.Invoke() ?? new RetentionInputs([.. runs.Where(r => Directory.Exists(Path.Combine(root, r.RunId)))], [Company()], [], [],
                [.. Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(n => n!.Contains(".deleting-")).Select(n => n!)])),
            (code, detail, _) => { audit.Add((code, detail)); return Task.CompletedTask; }, "os:test");

    // ---- the gate (D-137 layers 1 and 3; review M1) ----------------------------------------------

    [Fact]
    public void The_code_gate_is_off_and_no_repository_configuration_opts_in()
    {
        Assert.False(BackupRetention.AutomaticDeletionActivated);
        Assert.False(BackupRetention.GateOpen(new RetentionOptions { AutomaticDeletion = RetentionOptions.OptInValue, InstallationTimeZone = "UTC" }));
        Assert.False(RestoreSafeguards.CutoverAvailable);
        var repo = RepositoryRoot();
        var shipped = new[] { "*.json", "*.env", "*.yml", "*.yaml", "*.toml", "*.ps1", "*.sh", "*.service", "*.xml", "*.config", "*.wxs", "*.nsi" };
        string S(string part) => $"{Path.DirectorySeparatorChar}{part}{Path.DirectorySeparatorChar}";
        var offenders = shipped.SelectMany(p => Directory.EnumerateFiles(repo, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains(S("node_modules")) && !f.Contains(S("bin")) && !f.Contains(S("obj")) && !f.Contains(S(".git")) && !f.Contains(S(".claude")))
            .Where(f => File.ReadAllText(f) is var t && (t.Contains(RetentionOptions.OptInValue, StringComparison.Ordinal) || t.Contains("Retention__AutomaticDeletion", StringComparison.Ordinal)))
            .ToList();
        Assert.True(offenders.Count == 0, "no shipped configuration may opt in to automatic backup deletion: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Only_tests_create_the_gate_override()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var users = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && File.ReadAllText(f).Contains("RetentionGateOverride.ForTests", StringComparison.Ordinal)).ToList();
        Assert.Empty(users);
    }

    [Fact]
    public void The_phase_r_legal_hold_table_is_read_and_any_unknown_hold_table_fails_closed()
    {
        // review H2 of #134: the exact R1 keys retention reads; a rename or a new hold table breaks this test (and the build of the reader) instead of failing open
        Assert.Equal("t_282abaa0", Hasm.Infrastructure.CompanyDatabase.CompanySchema.Map["LegalHold"]);
        Assert.Equal("c_71754c89", Hasm.Infrastructure.CompanyDatabase.CompanySchema.Map["LegalHold.ReleasedAtUtc"]);
        var holdTables = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Map.Keys.Where(k => !k.Contains('.') && k.Contains("Hold", StringComparison.OrdinalIgnoreCase)).ToHashSet();
        Assert.True(holdTables.SetEquals(RetentionService.KnownHoldTables), string.Join(", ", holdTables));
        Assert.False(RetentionService.UnknownHoldTables());
    }

    [Fact]
    public async Task Every_non_test_entry_point_refuses_while_the_gate_is_off()
    {
        var runs = Runs();
        var optedIn = new RetentionOptions { InstallationTimeZone = "UTC", AutomaticDeletion = RetentionOptions.OptInValue };
        // the service (the command's only path), with and without the per-installation opt-in
        var db = new HasmDatabaseOptions { Host = "127.0.0.1", Port = 1, AdminUsername = "admin", AdminPassword = "pw-not-a-secret", Username = "runtime", Password = "pw-not-a-secret", SystemDatabaseName = "hasm_sys", SslMode = Npgsql.SslMode.Disable, AllowInsecureTransport = true };
        foreach (var options in new[] { Opt(), optedIn })
        {
            var service = new RetentionService(db, options);
            var ex = await Assert.ThrowsAsync<BackupException>(() => service.ApplyCoreAsync(root, null, new BackupOptions(), (c, d, _) => { audit.Add((c, d)); return Task.CompletedTask; }, "os:test", null, default));
            Assert.Equal("retention_deletion_inactive", ex.Code);
        }
        Assert.All(audit, a => Assert.Equal(BackupAuditEvents.DeletionRefused, a.Code));
        Assert.All(audit, a => Assert.Contains("reason=retention_deletion_inactive", a.Detail));
        // the executor itself and its lowest-level routines
        Assert.Equal("retention_deletion_inactive", (await Assert.ThrowsAsync<BackupException>(() => RetentionExecutor.ExecuteAsync(Ctx(runs, options: optedIn), null, default))).Code);
        var dir = Path.Combine(root, runs[3].RunId);
        Assert.Equal("retention_deletion_inactive", Assert.Throws<BackupException>(() => RetentionExecutor.RenameForDeletion(dir, optedIn, null)).Code);
        var deleting = dir + ".deleting-0123456789abcdef";
        Directory.CreateDirectory(deleting);
        Assert.Equal("retention_deletion_inactive", Assert.Throws<BackupException>(() => RetentionExecutor.DeleteRunFolder(deleting, optedIn, null)).Code);
        Assert.True(Directory.Exists(dir) && Directory.Exists(deleting));
        Assert.All(runs, r => Assert.True(Directory.Exists(Path.Combine(root, r.RunId))));
    }

    // ---- the executor through the test seam --------------------------------------------------------

    [Fact]
    public async Task Through_the_seam_exactly_the_eligible_runs_are_deleted_audit_first_with_a_tombstone()
    {
        var runs = Runs(12);                                                                                         // 11 runs: few enough for the brake
        var plan = RetentionClassifier.Plan(runs, [Company()], [], [], Opt(), T(26, 0));
        var eligible = plan.Eligible.Select(e => e.RunId).ToHashSet();
        Assert.NotEmpty(eligible);
        var result = await RetentionExecutor.ExecuteAsync(Ctx(runs), RetentionGateOverride.ForTests(), default);
        Assert.True(eligible.SetEquals(result.Deleted));
        foreach (var r in runs)
            Assert.Equal(!eligible.Contains(r.RunId), Directory.Exists(Path.Combine(root, r.RunId)));
        Assert.Empty(Directory.EnumerateDirectories(root, "*.deleting-*"));
        // audit first: every 2016 is preceded by its 2015
        foreach (var id in eligible)
        {
            var started = audit.FindIndex(a => a.Code == BackupAuditEvents.DeletionStarted && a.Detail.Contains($"run={id}"));
            var done = audit.FindIndex(a => a.Code == BackupAuditEvents.Deleted && a.Detail.Contains($"run={id}"));
            Assert.True(started >= 0 && done > started);
            var tomb = Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder, id);
            Assert.Equal("{\"run\":\"" + id + "\"}", File.ReadAllText(Path.Combine(tomb, SignedDocuments.RunStatusFileName)));
            Assert.True(File.Exists(Path.Combine(tomb, SignedDocuments.RunStatusFileName + ".sig")));
            Assert.True(File.Exists(Path.Combine(tomb, RetentionInventory.DeletedMarker)));
        }
        // a second execution finds nothing more (the clock check sees the recorded deletion time, equal to now: allowed)
        var again = await RetentionExecutor.ExecuteAsync(Ctx(runs), RetentionGateOverride.ForTests(), default);
        Assert.Empty(again.Deleted);
    }

    [Fact]
    public async Task When_the_audit_event_cannot_be_written_nothing_is_touched()
    {
        var runs = Runs(12);
        var ctx = Ctx(runs) with { Audit = (code, _, _) => code == BackupAuditEvents.DeletionStarted ? throw new InvalidOperationException("audit down") : Task.CompletedTask };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RetentionExecutor.ExecuteAsync(ctx, RetentionGateOverride.ForTests(), default));
        Assert.All(runs, r => Assert.True(Directory.Exists(Path.Combine(root, r.RunId))));
        Assert.False(Directory.Exists(Path.Combine(root, RetentionInventory.RetentionFolder)));
    }

    [Fact]
    public async Task Stale_backups_a_clock_behind_and_a_suspicious_volume_delete_nothing()
    {
        var runs = Runs();
        Assert.Empty((await RetentionExecutor.ExecuteAsync(Ctx(runs, status: "recovery_untested"), RetentionGateOverride.ForTests(), default)).Deleted);
        Assert.Contains(audit, a => a.Detail.Contains("reason=backups_not_current"));
        Assert.Empty((await RetentionExecutor.ExecuteAsync(Ctx(runs, now: T(20)), RetentionGateOverride.ForTests(), default)).Deleted);   // now is before the newest run finished
        Assert.Contains(audit, a => a.Detail.Contains("reason=clock_inconsistent"));
        Assert.Empty((await RetentionExecutor.ExecuteAsync(Ctx(runs), RetentionGateOverride.ForTests(), default)).Deleted);              // 24 runs, 14 eligible > 7
        Assert.Contains(audit, a => a.Detail.Contains("reason=deletion_volume_suspicious"));
        Assert.All(runs, r => Assert.True(Directory.Exists(Path.Combine(root, r.RunId))));
    }

    [Fact]
    public async Task A_run_that_changed_was_held_or_is_unclean_since_the_plan_is_skipped()
    {
        var runs = Runs(12);
        var plan = RetentionClassifier.Plan(runs, [Company()], [], [], Opt(), T(26, 0));
        var victims = plan.Eligible.Select(e => e.RunId).Take(3).ToList();
        File.WriteAllText(Path.Combine(root, victims[2], "unexpected.txt"), "x");                                // unclean on disk
        var reads = 0;
        RetentionInputs Read()
        {
            reads++;
            var live = runs.Where(r => Directory.Exists(Path.Combine(root, r.RunId))).ToList();
            // after the first read the status of victims[0] changes and victims[1] is held
            if (reads > 1) live = [.. live.Select(r => r.RunId == victims[0] ? r with { StatusSha256 = new string('f', 64) } : r)];
            IReadOnlyList<BackupLegalHoldRow> holds = reads > 1 ? [new(Guid.NewGuid(), 1, victims[1], null, null, null, false)] : [];
            return new RetentionInputs(live, [Company()], holds, [], []);
        }
        var result = await RetentionExecutor.ExecuteAsync(Ctx(runs, read: Read), RetentionGateOverride.ForTests(), default);
        Assert.Contains((victims[0], "status_changed"), result.Skipped);
        Assert.Contains((victims[1], "run_no_longer_eligible"), result.Skipped);
        Assert.Contains((victims[2], "run_not_clean"), result.Skipped);
        Assert.All(victims, v => Assert.True(Directory.Exists(Path.Combine(root, v))));
    }

    [Fact]
    public async Task An_interrupted_deletion_is_completed_by_the_next_execution_and_links_are_never_followed()
    {
        var runs = Runs(12);
        var outside = Path.Combine(Path.GetTempPath(), "hasm-ret-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
        try
        {
            var deleting = Path.Combine(root, "20260301T020000Z-0000abcd.deleting-0123456789abcdef");
            Directory.CreateDirectory(Path.Combine(deleting, "system"));
            File.WriteAllText(Path.Combine(deleting, "system", "database.dump.hbe"), "x");
            if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(deleting, "link"), outside);
            Assert.Equal(1, RetentionInventory.DeletingFolderCount(root));                                          // backup-status reports retention_deletion_incomplete
            // review L3 of #134: without the engine's tombstone the folder is NOT resumed
            var unverified = await RetentionExecutor.ExecuteAsync(Ctx(runs), RetentionGateOverride.ForTests(), default);
            Assert.Equal(0, unverified.Resumed);
            Assert.Contains(("20260301T020000Z-0000abcd", "resume_unverified"), unverified.Skipped);
            Assert.True(Directory.Exists(deleting));
            var tomb = Path.Combine(root, RetentionInventory.RetentionFolder, RetentionInventory.TombstonesFolder, "20260301T020000Z-0000abcd");
            Directory.CreateDirectory(tomb);
            File.WriteAllText(Path.Combine(tomb, RetentionInventory.DeletedMarker), "{\"version\":1,\"runId\":\"20260301T020000Z-0000abcd\",\"deletedUtc\":\"2026-03-01T03:00:00+00:00\",\"statusSha256\":\"x\"}");
            var result = await RetentionExecutor.ExecuteAsync(Ctx(runs), RetentionGateOverride.ForTests(), default);
            Assert.Equal(1, result.Resumed);
            Assert.Equal(0, RetentionInventory.DeletingFolderCount(root));
            Assert.False(Directory.Exists(deleting));
            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));                                        // the link was removed, never its target
            Assert.Contains(audit, a => a.Code == BackupAuditEvents.Deleted && a.Detail.Contains("resumed=yes"));
            Assert.Equal("not_a_deleting_folder", Assert.Throws<BackupException>(() => RetentionExecutor.DeleteRunFolder(Path.Combine(root, runs[0].RunId), Opt(), RetentionGateOverride.ForTests())).Code);
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public void Keepers_must_survive_the_simulated_removal()
    {
        var runs = Runs(12);
        var plan = RetentionClassifier.Plan(runs, [Company()], [], [], Opt(), T(26, 0));
        var eligible = plan.Eligible.First().RunId;
        var without = RetentionClassifier.Plan([.. runs.Where(r => r.RunId != eligible)], [Company()], [], [], Opt(), T(26, 0));
        Assert.True(RetentionExecutor.KeepersPreserved(plan, without, eligible));
        var newest = runs[^1].RunId;
        var withoutNewest = RetentionClassifier.Plan([.. runs.Where(r => r.RunId != newest)], [Company()], [], [], Opt(), T(26, 0));
        Assert.False(RetentionExecutor.KeepersPreserved(plan, withoutNewest, runs[^2].RunId));               // removing a keeper changes the keepers
    }

    // ---- the root scan (D-130, review M2 of #130) ------------------------------------------------------

    [Fact]
    public void The_scan_reads_only_verified_statuses_and_reports_unknown_key_needs_unclean_runs_and_kept_trials()
    {
        var sign = Path.Combine(root, "..", "hasm-ret-sign-" + Guid.NewGuid().ToString("N"));
        var (id, pem) = BackupSigningKeys.Generate(sign);
        try
        {
            using var signer = BackupSigningKeys.Load(sign, id, true);
            var trust = new SigningTrust([new TrustedSigningKey(id, pem, SigningKeyStatus.Active)]);
            RunStatus Status(string runId, int version, Guid company) => new(version, RunStatus.KindValue, runId, T(3), T(3).AddMinutes(5), 1, "ok", 1, 0, 0, 0, 0, Rk, id,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId))), ["session"], [], true,
                [new RunStatusCompany(company, "ok", version == 2 ? ["k1"] : null)], version == 2 ? ["a1"] : null);
            string Make(string runId, int version)
            {
                var dir = Path.Combine(root, runId);
                Directory.CreateDirectory(Path.Combine(dir, "companies", C.ToString("N")));
                SignedDocuments.WriteRunStatus(dir, Status(runId, version, C), signer);
                File.WriteAllText(Path.Combine(dir, "companies", C.ToString("N"), "database.dump.hbe"), "x");
                return dir;
            }
            Make("20260303T020000Z-00000001", 2);
            Make("20260304T020000Z-00000002", 1);
            var kept = Make("20260305T020000Z-00000003", 2);
            File.WriteAllText(Path.Combine(kept, "companies", C.ToString("N") + ".trial-" + Guid.NewGuid().ToString("N") + ".json"), "{}");
            var dirty = Make("20260306T020000Z-00000004", 2);
            File.WriteAllText(Path.Combine(dirty, "notes.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "20260307T020000Z-00000005"));                               // incomplete
            Directory.CreateDirectory(Path.Combine(root, "20260308T020000Z-00000006.deleting-0123456789abcdef"));
            Directory.CreateDirectory(Path.Combine(root, "not-a-run"));

            var (scanned, deletingFolders) = RetentionInventory.Scan(root, trust);
            RetentionRun R(string runId) => scanned.Single(r => r.RunId == runId);
            Assert.Equal(5, scanned.Count);
            Assert.Equal(["20260308T020000Z-00000006.deleting-0123456789abcdef"], deletingFolders);
            Assert.True(R("20260303T020000Z-00000001") is { Verified: true, Clean: true, KeptTrial: false, Keys: not null });
            Assert.Null(R("20260304T020000Z-00000002").Keys);                                                      // version 1: unknown key needs
            Assert.True(R("20260305T020000Z-00000003").KeptTrial);
            Assert.False(R("20260306T020000Z-00000004").Clean);
            Assert.True(R("20260307T020000Z-00000005") is { Complete: false, Keys: null });
            // without a trust anchor, or with another key, nothing verifies and every key need is unknown
            Assert.All(RetentionInventory.Scan(root, null).Runs, r => Assert.True(!r.Verified && r.Keys is null));
            var (otherId, otherPem) = BackupSigningKeys.Generate(sign + "-other");
            Assert.All(RetentionInventory.Scan(root, new SigningTrust([new TrustedSigningKey(otherId, otherPem, SigningKeyStatus.Active)])).Runs, r => Assert.True(!r.Verified && r.Keys is null));
            Directory.Delete(sign + "-other", true);
        }
        finally { Directory.Delete(sign, true); }
    }
}
