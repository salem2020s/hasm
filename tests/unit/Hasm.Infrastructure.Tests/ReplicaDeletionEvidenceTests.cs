using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Backup;
using static Hasm.Infrastructure.Tests.RetentionClassifierTests;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 8, S8d-1 (read only): the destination token of the 2015 to 2017 events, the exact-token deletion facts, the tombstone classification with its three-condition exception and the two
/// alert states, the <c>replication_pending</c> keep reason (it can only keep more), the global recovery proof, and the deletion-readiness lines (never ready while the code gate is closed). Real signed
/// tombstones on temporary folders; the audit states are built from rows (the authenticated read itself is covered by the PostgreSQL class ReplicaAuditVerifiedReadTests).
/// </summary>
public sealed class ReplicaDeletionEvidenceTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-rde-" + Guid.NewGuid().ToString("N"));
    private readonly string signDir;
    private readonly string signingId, signingPem;
    private readonly SigningTrust trust;
    private static readonly DateTimeOffset At = new(2026, 3, 3, 2, 0, 0, TimeSpan.Zero);
    private const string RunA = "20260303T020000Z-0a0b0c0d", RunB = "20260304T020000Z-0a0b0c0d";

    public ReplicaDeletionEvidenceTests()
    {
        Directory.CreateDirectory(work);
        signDir = Path.Combine(work, "sign");
        (signingId, signingPem) = BackupSigningKeys.Generate(signDir);
        trust = new SigningTrust([new TrustedSigningKey(signingId, signingPem, SigningKeyStatus.Active)]);
    }

    public void Dispose() { try { Directory.Delete(work, true); } catch (IOException) { } }

    private string Root(string name) { var p = Path.Combine(work, name); Directory.CreateDirectory(p); return p; }

    /// <summary>Writes a really signed run status for <paramref name="runId"/> into <paramref name="dir"/> and returns its SHA-256.</summary>
    private string SignedStatus(string dir, string runId)
    {
        Directory.CreateDirectory(dir);
        using var signer = BackupSigningKeys.Load(signDir, signingId, true);
        var status = new RunStatus(2, RunStatus.KindValue, runId, At, At.AddMinutes(5), 1, "ok", 1, 0, 0, 0, 0, Rk, signingId, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId))),
            ["session"], [], true, [new RunStatusCompany(C, "ok", ["k1"])], ["a1"]);
        SignedDocuments.WriteRunStatus(dir, status, signer);
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, SignedDocuments.RunStatusFileName))));
    }

    private string Tombstone(string root, string run) => SignedStatus(Path.Combine(root, ".retention", "tombstones", run), run);

    private static ReplicaAuditState State(bool authenticated, params (short Type, string Detail)[] rows) =>
        ReplicaAuditReader.Build(rows.Select((r, i) => ((long)i + 1, r.Type, r.Detail, At))) with { Authenticated = authenticated };

    private static (short, string) Start(string dest, string run, string hash) => (BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run={run} status={hash} reasons=none destination={dest}");
    private static (short, string) Done(string dest, string run, string hash) => (BackupAuditEvents.Deleted, $"backup deleted operator=os:t run={run} status={hash} files=1 bytes=1 tombstone=yes resumed=no destination={dest}");
    private static (short, string) Copied(string dest, string run) => (BackupAuditEvents.ReplicaVerified, $"replica verified operator=os:t run={run} destination={dest} kind=network status=x files=1 bytes=1");

    private TombstoneScan Classify(string root, IReadOnlyDictionary<string, string> primary, ReplicaAuditState? audit, params string[] deleting) =>
        ReplicaTombstones.Classify(root, "nas", primary, trust, audit, deleting);

    [Fact]
    public void The_three_condition_exception_explains_only_a_verified_matching_pair_with_no_later_copy_and_no_folder()
    {
        var root = Root("nas1");
        var h = Tombstone(root, RunA);
        var primary = new Dictionary<string, string> { [RunA] = h };

        Assert.Equal([RunA], Classify(root, primary, State(true, Start("nas", RunA, h), Done("nas", RunA, h))).Explained);
        Assert.Empty(Classify(root, primary, State(true, Start("nas-1", RunA, h), Done("nas-1", RunA, h))).Explained);            // another destination id (nas-1 is never nas)
        Assert.Empty(Classify(root, primary, State(true, Start("primary", RunA, h), Done("primary", RunA, h))).Explained);         // the primary's pair is not this destination's
        Assert.Equal([RunA], Classify(root, primary, State(true, Start("nas", RunA, h), Done("nas", RunA, h))).Explained);
        // a 2016 alone (no 2015), or a 2015 with another status hash, explains nothing
        Assert.Equal([RunA], Classify(root, primary, State(true, Done("nas", RunA, h))).Unexplained);
        Assert.Equal([RunA], Classify(root, primary, State(true, Start("nas", RunA, new string('b', 64)), Done("nas", RunA, new string('b', 64)))).Unexplained);
        // a tombstone whose hash is not the primary's, or a run the primary never had: unexplained
        Assert.Equal([RunA], Classify(root, new Dictionary<string, string> { [RunA] = new string('c', 64) }, State(true, Start("nas", RunA, h), Done("nas", RunA, h))).Unexplained);
        Assert.Equal([RunA], Classify(root, new Dictionary<string, string>(), State(true, Start("nas", RunA, h), Done("nas", RunA, h))).Unexplained);
        // the replay: a copy published AFTER the pair voids it; a copy BEFORE the pair does not
        var replay = Classify(root, primary, State(true, Start("nas", RunA, h), Done("nas", RunA, h), Copied("nas", RunA)));
        Assert.Empty(replay.Explained); Assert.Empty(replay.Unexplained); Assert.Empty(replay.Incomplete);                         // pending again, no alert: the old pair stays history
        Assert.Equal([RunA], Classify(root, primary, State(true, Copied("nas", RunA), Start("nas", RunA, h), Done("nas", RunA, h))).Explained);
        // condition 3: the run folder or a .deleting-* folder exists
        Directory.CreateDirectory(Path.Combine(root, RunA));
        var withFolder = Classify(root, primary, State(true, Start("nas", RunA, h), Done("nas", RunA, h)));
        Assert.Empty(withFolder.Explained); Assert.Empty(withFolder.Unexplained); Assert.Empty(withFolder.Incomplete);               // an ordinary copy
        Directory.Delete(Path.Combine(root, RunA));
        Assert.Equal([RunA], Classify(root, primary, State(true, Start("nas", RunA, h), Done("nas", RunA, h)), RunA + ".deleting-0123456789abcdef").Incomplete);
        // a 2016 of the recovery path without a status token counts only together with its 2015
        var recovered = (BackupAuditEvents.Deleted, $"backup deleted operator=os:t run={RunA} resumed=yes recovered=audit folder=absent destination=nas");
        Assert.Equal([RunA], Classify(root, primary, State(true, Start("nas", RunA, h), recovered)).Explained);
        Assert.Equal([RunA], Classify(root, primary, State(true, recovered)).Unexplained);
    }

    [Fact]
    public void An_unfinished_deletion_is_incomplete_and_everything_nothing_accounts_for_is_unexplained_never_resumed()
    {
        var root = Root("nas2");
        var h = Tombstone(root, RunA);
        var primary = new Dictionary<string, string> { [RunA] = h };
        var open = State(true, Start("nas", RunA, h));
        Assert.Equal([RunA], Classify(root, primary, open).Incomplete);                                                              // a 2015, no 2016, the folder is gone
        Assert.Equal([RunA], Classify(root, primary, open, RunA + ".deleting-0123456789abcdef").Incomplete);
        // no audit state at all: S8a, every tombstone and every .deleting-* is unexplained
        var none = Classify(root, primary, null, RunB + ".deleting-0123456789abcdef");
        Assert.Equal([RunA, RunB], none.Unexplained);
        // a .deleting-* with no tombstone is unexplained even with a state
        Assert.Equal([RunB], Classify(root, primary, open, RunB + ".deleting-0123456789abcdef").Unexplained);
        // a planted tombstone (unsigned file, a link, a stray file) is unexplained, whatever the audit says
        var planted = Root("nas3");
        Directory.CreateDirectory(Path.Combine(planted, ".retention", "tombstones", RunA));
        File.WriteAllText(Path.Combine(planted, ".retention", "tombstones", RunA, SignedDocuments.RunStatusFileName), "{}");
        File.WriteAllText(Path.Combine(planted, ".retention", "tombstones", "stray.txt"), "x");
        Assert.Equal([RunA, "stray.txt"], Classify(planted, primary, State(true, Start("nas", RunA, h), Done("nas", RunA, h))).Unexplained);
        // a tombstone signed by a key outside the trust list does not verify
        var foreign = Root("nas4");
        var otherDir = Path.Combine(work, "sign2");
        var (otherId, _) = BackupSigningKeys.Generate(otherDir);
        using (var signer = BackupSigningKeys.Load(otherDir, otherId, true))
        {
            var dir = Path.Combine(foreign, ".retention", "tombstones", RunA);
            Directory.CreateDirectory(dir);
            SignedDocuments.WriteRunStatus(dir, new RunStatus(2, RunStatus.KindValue, RunA, At, At.AddMinutes(5), 1, "ok", 1, 0, 0, 0, 0, Rk, otherId, new string('d', 64), ["session"], [], true, [new RunStatusCompany(C, "ok", ["k1"])], ["a1"]), signer);
        }
        Assert.Equal([RunA], Classify(foreign, primary, open).Unexplained);
        Assert.Null(ReplicaTombstones.VerifiedHash(foreign, RunA, trust));
        Assert.Null(ReplicaTombstones.VerifiedHash(foreign, RunA, null));
        Assert.NotNull(ReplicaTombstones.VerifiedHash(root, RunA, trust));
    }

    [Fact]
    public void The_deletion_facts_are_parsed_exactly_and_an_event_without_a_destination_token_belongs_to_the_primary()
    {
        var h = new string('a', 64);
        var s = State(false,
            (BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run={RunA} status={h} reasons=none"),                       // written before S8d: no token = the primary
            (BackupAuditEvents.Deleted, $"backup deleted operator=os:t run={RunA} status={h} files=1 bytes=1 tombstone=yes resumed=no"),
            (BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run={RunB} status=NOTAHASH reasons=none destination=nas"),     // malformed status: ignored
            (BackupAuditEvents.DeletionStarted, $"xbackup deletion started operator=os:t run={RunB} status={h} destination=nas"),                    // not exactly this event: ignored
            (BackupAuditEvents.DeletionStarted, $"backup deletion started operator=os:t run=not-a-run status={h} destination=nas"));
        Assert.NotNull(s.CompletedPairSeq("primary", RunA, h));
        Assert.Null(s.CompletedPairSeq("nas", RunA, h));
        Assert.False(s.HasStart("nas", RunB, h));
        Assert.False(s.HasStart("nas", "not-a-run", h));
        Assert.False(s.Authenticated);
        Assert.Null(ReplicaAuditReader.Token("xdestination=nas", "destination"));
    }

    [Fact]
    public void A_2016_without_a_status_closes_only_the_newest_open_2015_of_that_destination_and_run()
    {
        var h = new string('a', 64); var g = new string('b', 64);
        var bare = (BackupAuditEvents.Deleted, $"backup deleted operator=os:t run={RunA} resumed=yes destination=nas");              // the resume path names only the run
        // two open starts, one bare 2016: only the NEWEST start is closed, the older one stays open (review A2 of PR 232)
        var s = State(true, Start("nas", RunA, h), Start("nas", RunA, h), bare);
        Assert.Equal(2L, s.CompletedPairSeq("nas", RunA, h));
        Assert.Equal(1L, s.OpenStartSeq("nas", RunA, h));
        // a second bare 2016 closes the other one
        var both = State(true, Start("nas", RunA, h), Start("nas", RunA, h), bare, bare);
        Assert.Null(both.OpenStartSeq("nas", RunA, h));
        // with two statuses the bare 2016 closes the newest start whatever its status; the other stays open
        var mixed = State(true, Start("nas", RunA, h), Start("nas", RunA, g), bare);
        Assert.Null(mixed.CompletedPairSeq("nas", RunA, h));
        Assert.Equal(2L, mixed.CompletedPairSeq("nas", RunA, g));
        Assert.Equal(1L, mixed.OpenStartSeq("nas", RunA, h));
        // a 2016 that names a status closes the newest open start with THAT status, not a newer one with another
        var named = State(true, Start("nas", RunA, h), Start("nas", RunA, g), Done("nas", RunA, h));
        Assert.Equal(1L, named.CompletedPairSeq("nas", RunA, h));
        Assert.Equal(2L, named.OpenStartSeq("nas", RunA, g));
        // a 2016 before any 2015, or for another destination, closes nothing
        Assert.Null(State(true, bare, Start("nas", RunA, h)).CompletedPairSeq("nas", RunA, h));
        Assert.Null(State(true, Start("nas-1", RunA, h), bare).CompletedPairSeq("nas", RunA, h));
    }

    [Fact]
    public void Replication_pending_only_keeps_more_and_a_recovery_test_on_any_copy_counts_for_its_key()
    {
        var runs = Enumerable.Range(2, 12).Select(d => Run(new DateTimeOffset(2026, 3, d, 2, 0, 0, TimeSpan.Zero), proof: d == 13)).ToList();
        var now = new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero);
        var without = RetentionClassifier.Plan(runs, [Company()], [], [], Opt(), now);
        Assert.NotEmpty(without.Eligible);                                                                                          // some runs are deletable at the primary
        var victim = without.Eligible.First();
        var pending = new HashSet<string>(StringComparer.Ordinal) { victim.RunId };
        var with = RetentionClassifier.Plan(runs, [Company()], [], [], Opt(), now, pending);
        var d = with.Runs.Single(r => r.RunId == victim.RunId);
        Assert.False(d.Eligible);
        Assert.Contains("replication_pending", d.Reasons);
        Assert.Equal(without.Eligible.Count() - 1, with.Eligible.Count());                                                          // exactly that run became a keep, nothing else changed
        foreach (var r in without.Runs.Where(r => r.RunId != victim.RunId)) Assert.Equal(r.Reasons, with.Runs.Single(x => x.RunId == r.RunId).Reasons);
        // an unknown run id in the set changes nothing
        Assert.Equal(without.PlanHash, RetentionClassifier.Plan(runs, [Company()], [], [], Opt(), now, new HashSet<string> { "20200101T000000Z-00000000" }).PlanHash);

        // a destination's runs carry no proof of their own: the key counts as tested through a passed test anywhere else
        var noProof = Enumerable.Range(2, 12).Select(day => Run(new DateTimeOffset(2026, 3, day, 2, 0, 0, TimeSpan.Zero))).ToList();
        Assert.All(RetentionClassifier.Plan(noProof, [Company()], [], [], Opt(), now).Runs, r => Assert.Contains("recovery_key_untested", r.Reasons));
        var shared = RetentionClassifier.Plan(noProof, [Company()], [], [], Opt(), now, null, new HashSet<string>(StringComparer.Ordinal) { Rk });
        Assert.All(shared.Runs, r => Assert.DoesNotContain("recovery_key_untested", r.Reasons));
    }

    [Fact]
    public void Deletion_readiness_never_reads_as_ready_while_the_code_gate_is_closed_and_prints_codes_and_locations_only()
    {
        var opt = new RetentionOptions { InstallationTimeZone = "UTC", AutomaticDeletion = RetentionOptions.OptInValue };       // even opted in
        var reasons = DeletionReadiness.Reasons(opt, true, State(true), true, 0);
        Assert.Equal("retention_deletion_inactive", reasons[0]);
        Assert.Contains("restore_safeguards_missing", reasons);
        Assert.DoesNotContain("opt_in_absent", reasons);
        Assert.DoesNotContain("audit_unverified", reasons);
        Assert.Contains("opt_in_absent", DeletionReadiness.Reasons(new RetentionOptions { InstallationTimeZone = "UTC" }, true, State(true), true, 0));
        Assert.Contains("destination_protection_missing", DeletionReadiness.Reasons(opt, false, null, true, 0));                 // no destination at all (owner answer Q1)
        Assert.Contains("audit_unreadable", DeletionReadiness.Reasons(opt, true, null, false, 0));
        Assert.Contains("audit_unverified", DeletionReadiness.Reasons(opt, true, State(false), true, 0));                          // a raw read never authorises
        Assert.Contains("audit_unverified", DeletionReadiness.Reasons(opt, true, State(true) with { UnverifiedRemainder = true }, true, 0));
        Assert.Contains("replication_pending", DeletionReadiness.Reasons(opt, true, State(true), true, 2));

        var locations = new List<(string, IReadOnlyList<string>)> { ("primary", ["retention_deletion_inactive", "replication_pending"]), ("nas", []) };
        Assert.Equal(["Deletion readiness: primary: blocked (retention_deletion_inactive, replication_pending)", "Deletion readiness: nas: ready"], DeletionReadiness.Lines(locations));
        Assert.Equal(["Deletion blocked:", "  retention_deletion_inactive location=primary", "  replication_pending location=primary"], DeletionReadiness.Notice(locations));
        Assert.Empty(DeletionReadiness.Notice([("nas", [])]));
    }
}
