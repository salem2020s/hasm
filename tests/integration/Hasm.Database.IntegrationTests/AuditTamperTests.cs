using System.Security.Cryptography;
using Hasm.Infrastructure.Audit;
using Au = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Each test plays an attacker with direct write access to the audited database (the append-only trigger is switched off) who does not
/// hold the audit key, and checks what the read-only verifier reports. Scenario: 6 events, all anchored in the external checkpoint store.
/// </summary>
public class AuditTamperDetectionTests : AuditTestBase
{

    [PostgresFact]
    public async Task An_untouched_stream_verifies()
    {
        var (_, _, stream) = await Scenario();
        AssertVerified(await Verify(stream), 6);
    }

    [PostgresFact]
    public async Task A_modified_payload_is_detected_at_the_exact_record()
    {
        var (_, _, stream) = await Scenario();
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'permissions: everything' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3", ("s", stream.Key));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "hash_mismatch" && f.Seq == 3);
        Assert.Equal(2, report.VerifiedThroughSeq);                                                        // the verified range stops before the damage
    }

    [PostgresFact]
    public async Task Modified_timestamps_identities_and_types_are_detected()
    {
        var (company, admin, stream) = await Scenario();
        var other = await NewPerson("someone");
        await Tamper($"UPDATE {Ev.Table} SET {Ev.OccurredAtUtc} = {Ev.OccurredAtUtc} + interval '1 second' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 2", ("s", stream.Key));
        await Tamper($"UPDATE {Ev.Table} SET {Ev.ActorUserId} = @u WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 4", ("s", stream.Key), ("u", other.Id));
        await Tamper($"UPDATE {Ev.Table} SET {Ev.EventType} = 7 WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 5", ("s", stream.Key));
        await Tamper($"UPDATE {Ev.Table} SET {Ev.EventId} = gen_random_uuid() WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 6", ("s", stream.Key));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Equal([2L, 4L, 5L, 6L], report.Findings.Where(f => f.Code == "hash_mismatch").Select(f => f.Seq!.Value).Order());
    }

    [PostgresFact]
    public async Task Recomputing_the_hash_without_the_key_is_still_detected()
    {
        var (_, _, stream) = await Scenario();
        var target = (await Events(stream))[2];
        var edited = target with { Detail = "forged detail" };
        var rehashed = AuditCanonical.ComputeHash(AuditCanonical.Serialize(edited with { Hash = [], Mac = [] }));
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = @d, {Ev.Hash} = @h WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3", ("d", "forged detail"), ("h", rehashed), ("s", stream.Key));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "mac_invalid" && f.Seq == 3);
        Assert.Contains(report.Findings, f => f.Code == "link_broken" && f.Seq == 4);                       // the next record still points at the old hash
    }

    [PostgresFact]
    public async Task Rewriting_the_whole_tail_with_a_recomputed_chain_but_a_foreign_key_is_detected()
    {
        var (_, _, stream) = await Scenario();
        var events = await Events(stream);
        var attacker = new InMemoryAuditKeyRing("k1");                                                      // knows the identifier, not the secret
        var prev = events[1].Hash;
        var forged = new List<AuditRecord>();
        foreach (var e in events.Skip(2))
        {
            var rec = AuditCanonical.Seal(e with { Detail = "rewritten", PrevHash = prev, Hash = [], Mac = [] }, attacker.GetActive());
            forged.Add(rec); prev = rec.Hash;
        }
        foreach (var r in forged)
            await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = @d, {Ev.PrevHash} = @p, {Ev.Hash} = @h, {Ev.Mac} = @m WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = @q",
                ("d", r.Detail!), ("p", r.PrevHash), ("h", r.Hash), ("m", r.Mac), ("s", stream.Key), ("q", r.Seq));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Equal([3L, 4L, 5L, 6L], report.Findings.Where(f => f.Code == "mac_invalid").Select(f => f.Seq!.Value).Order());
        Assert.Contains(report.Findings, f => f.Code == "checkpoint_mismatch");                             // the external checkpoints also disagree
    }

    [PostgresFact]
    public async Task A_deleted_middle_event_is_detected()
    {
        var (_, _, stream) = await Scenario();
        await Tamper($"DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3", ("s", stream.Key));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "seq_gap" && f.Seq == 4 && f.Message.Contains('3'));
        Assert.Contains(report.Findings, f => f.Code == "link_broken" && f.Seq == 4);
        Assert.Contains(report.Findings, f => f.Code == "checkpoint_unmatched" && f.Seq == 3);
    }

    [PostgresFact]
    public async Task Reordered_events_are_detected()
    {
        var (_, _, stream) = await Scenario();
        await Tamper(
            $"UPDATE {Ev.Table} SET {Ev.Seq} = 1000 WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3; " +
            $"UPDATE {Ev.Table} SET {Ev.Seq} = 3 WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 4; " +
            $"UPDATE {Ev.Table} SET {Ev.Seq} = 4 WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 1000", ("s", stream.Key));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "hash_mismatch");                                   // the sequence number is part of every record
        Assert.Contains(report.Findings, f => f.Code == "link_broken");
        Assert.Contains(report.Findings, f => f.Code == "checkpoint_mismatch");
    }

    [PostgresFact]
    public async Task Inserted_events_without_the_key_are_detected_at_the_tail_and_in_the_middle()
    {
        var (company, admin, stream) = await Scenario();
        var events = await Events(stream);
        var attacker = new InMemoryAuditKeyRing("k1").GetActive();
        var tail = AuditCanonical.Seal(new AuditRecord(stream.Key, 7, Guid.NewGuid(), events[^1].OccurredMicros, AuditEventTypes.RoleAssigned, company, admin.Id, admin.Id,
            null, null, "scope: company; forged", 1, "k1", events[^1].Hash, [], []), attacker);
        await InsertRaw(tail);

        var appended = await Verify(stream);
        Assert.Equal(AuditVerdict.Failed, appended.Verdict);
        Assert.Contains(appended.Findings, f => f.Code == "mac_invalid" && f.Seq == 7);

        // Middle insertion: make room by moving the later events away, then forge a record that links correctly to its predecessor.
        await Tamper($"DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 7; UPDATE {Ev.Table} SET {Ev.Seq} = {Ev.Seq} + 100 WHERE {Ev.StreamKey} = @s AND {Ev.Seq} >= 4", ("s", stream.Key));
        var middle = AuditCanonical.Seal(new AuditRecord(stream.Key, 4, Guid.NewGuid(), events[2].OccurredMicros, AuditEventTypes.RoleAssigned, company, admin.Id, admin.Id,
            null, null, "scope: company; forged", 1, "k1", events[2].Hash, [], []), attacker);
        await InsertRaw(middle);

        var inserted = await Verify(stream);
        Assert.Equal(AuditVerdict.Failed, inserted.Verdict);
        Assert.Contains(inserted.Findings, f => f.Code == "mac_invalid" && f.Seq == 4);
        Assert.Contains(inserted.Findings, f => f.Code == "seq_gap");
        Assert.Equal(3, inserted.VerifiedThroughSeq);
    }

    [PostgresFact]
    public async Task A_broken_hash_chain_link_is_detected()
    {
        var (_, _, stream) = await Scenario();
        await Tamper($"UPDATE {Ev.Table} SET {Ev.PrevHash} = @p WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 4", ("p", RandomNumberGenerator.GetBytes(32)), ("s", stream.Key));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "link_broken" && f.Seq == 4);
    }

    [PostgresFact]
    public async Task Deleting_the_last_events_is_detected_only_thanks_to_the_external_checkpoint()
    {
        var (_, _, stream) = await Scenario();
        var removed = (await Events(stream)).Where(e => e.Seq >= 5).Select(e => e.LegacyRef!.Value).ToArray();
        await TamperLegacy($"DELETE FROM {Au.Table} WHERE {Au.Id} = ANY(@ids)", ("ids", removed));                       // a careful attacker removes the readable projection too
        await Tamper($"DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s AND {Ev.Seq} >= 5", ("s", stream.Key));

        // The remaining chain is internally perfect: a database-only hash chain cannot show the truncation...
        var withoutAnchor = await new AuditVerifier(Options.BuildSystemConnectionString(admin: true), AuditKeys, new InMemoryCheckpointStore()).VerifyAsync(stream);
        Assert.Empty(Codes(withoutAnchor, FindingSeverity.Failure));
        Assert.Equal(AuditVerdict.Incomplete, withoutAnchor.Verdict);                                      // ...so it is never reported as Verified
        Assert.Contains("no_checkpoints", Codes(withoutAnchor));

        // ...but the protected checkpoints expose it.
        var withAnchor = await Verify(stream);
        Assert.Equal(AuditVerdict.Failed, withAnchor.Verdict);
        Assert.Contains(withAnchor.Findings, f => f.Code == "tail_truncated" && f.Seq == 6);
        Assert.Equal(4, withAnchor.VerifiedThroughSeq);
    }

    [PostgresFact]
    public async Task Truncating_the_whole_table_is_detected()
    {
        var (_, _, stream) = await Scenario();
        await Exec($"TRUNCATE {Ev.Table}");                                                                 // not preventable by a trigger for the table owner

        var anchored = (await AuditCheckpoints.ReadAllAsync(stream.Key)).Checkpoints.Count;

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Equal(0, report.Events);
        Assert.True(anchored >= 5);
        Assert.Equal(anchored, report.Findings.Count(f => f.Code == "tail_truncated"));                    // every anchored head now points at a missing record
    }

    [PostgresFact]
    public async Task Altered_protected_metadata_is_detected()
    {
        var (_, _, stream) = await Scenario();
        AuditKeys.Add("k9", RandomNumberGenerator.GetBytes(32));
        await Tamper($"UPDATE {Ev.Table} SET {Ev.KeyId} = 'k9' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 2", ("s", stream.Key));          // swap the key identifier
        await Tamper($"UPDATE {Ev.Table} SET {Ev.LegacyRef} = {Ev.LegacyRef} + 1 WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3", ("s", stream.Key));   // re-point the projection link
        await Tamper($"UPDATE {Ev.Table} SET {Ev.FormatVersion} = 2 WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 5", ("s", stream.Key));      // pretend a newer format
        await Tamper($"UPDATE {Ev.Table} SET {Ev.EventId} = gen_random_uuid() WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 4", ("s", stream.Key));

        var report = await Verify(stream);

        Assert.NotEqual(AuditVerdict.Verified, report.Verdict);
        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Equal([2L, 3L, 4L], report.Findings.Where(f => f.Code == "hash_mismatch").Select(f => f.Seq!.Value).Order());
        Assert.Contains(report.Findings, f => f.Code == "unsupported_format" && f.Seq == 5 && f.Severity == FindingSeverity.Incomplete);
    }

    [PostgresFact]
    public async Task An_event_moved_into_another_companys_stream_is_detected_in_both_streams()
    {
        await StartAccess();
        var a = await NewCompany(); var b = await NewCompany();
        var adminA = await NewAdmin(a); await NewAdmin(b);
        Ok(await Access.CreateRoleAsync(adminA.Id, a, "R", [Hasm.Infrastructure.Access.PermissionCatalog.Codes.MembersView]));
        var sa = AuditStream.ForCompany(a); var sb = AuditStream.ForCompany(b);
        await Tamper($"UPDATE {Ev.Table} SET {Ev.StreamKey} = @sb, {Ev.CompanyId} = @b, {Ev.Seq} = 3 WHERE {Ev.StreamKey} = @sa AND {Ev.Seq} = 3", ("sa", sa.Key), ("sb", sb.Key), ("b", b));

        var reportA = await Verify(sa);
        var reportB = await Verify(sb);

        Assert.Equal(AuditVerdict.Failed, reportA.Verdict);
        Assert.Contains("tail_truncated", Codes(reportA));
        Assert.Equal(AuditVerdict.Failed, reportB.Verdict);
        Assert.Contains("hash_mismatch", Codes(reportB));
    }

    [PostgresFact]
    public async Task A_missing_key_makes_the_evidence_incomplete_never_verified()
    {
        var (_, _, stream) = await Scenario();
        AuditKeys.Unavailable = true;

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Incomplete, report.Verdict);
        Assert.Empty(Codes(report, FindingSeverity.Failure));
        Assert.Contains("key_missing", Codes(report));
        Assert.Contains("checkpoint_key_missing", Codes(report));
        Assert.Equal(0, report.VerifiedThroughSeq);                                                        // nothing could be authenticated
    }

    [PostgresFact]
    public async Task Missing_checkpoint_evidence_is_incomplete()
    {
        var (_, _, stream) = await Scenario();

        var none = await new AuditVerifier(Options.BuildSystemConnectionString(admin: true), AuditKeys, new InMemoryCheckpointStore()).VerifyAsync(stream);
        Assert.Equal(AuditVerdict.Incomplete, none.Verdict);
        Assert.Contains("no_checkpoints", Codes(none));
        Assert.Equal(6, none.VerifiedThroughSeq);                                                          // the chain itself is intact and authenticated
    }

    [PostgresFact]
    public async Task Tampering_with_the_legacy_projection_is_detected_through_the_authenticated_events()
    {
        var (company, _, stream) = await Scenario();
        var first = await Scalar<long>($"SELECT min({Au.Id}) FROM {Au.Table} WHERE {Au.CompanyId} = @c", ("c", company));
        var last = await Scalar<long>($"SELECT max({Au.Id}) FROM {Au.Table} WHERE {Au.CompanyId} = @c", ("c", company));
        await TamperLegacy($"UPDATE {Au.Table} SET {Au.Detail} = 'rewritten' WHERE {Au.Id} = @i", ("i", first));
        await TamperLegacy($"DELETE FROM {Au.Table} WHERE {Au.Id} = @i", ("i", last));
        await TamperLegacy($"INSERT INTO {Au.Table} ({Au.OccurredAtUtc},{Au.CompanyId},{Au.EventCode},{Au.Detail}) VALUES (now(), @c, 6, 'unaudited grant')", ("c", company));

        var report = await Verify(stream);

        Assert.Equal(AuditVerdict.Failed, report.Verdict);
        Assert.Contains("projection_mismatch", Codes(report));
        Assert.Contains("projection_missing", Codes(report));
        Assert.Contains("projection_unchained", Codes(report));
        Assert.Equal(6, report.VerifiedThroughSeq);                                                        // the chained events themselves are intact
    }

    [PostgresFact]
    public async Task Verification_is_read_only_and_never_repairs_anything()
    {
        var (_, _, stream) = await Scenario();
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'x' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3; DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 6", ("s", stream.Key));
        var snapshot = $"SELECT md5(string_agg(t::text, '|' ORDER BY {Ev.Seq})) FROM {Ev.Table} t";
        var legacy = $"SELECT md5(string_agg(t::text, '|' ORDER BY {Au.Id})) FROM {Au.Table} t";
        var before = (await Scalar<string>(snapshot), await Scalar<string>(legacy), AuditCheckpoints.ReadAllAsync(stream.Key).Result.Checkpoints.Count);

        for (var i = 0; i < 3; i++) Assert.Equal(AuditVerdict.Failed, (await Verify(stream)).Verdict);

        Assert.Equal(before, (await Scalar<string>(snapshot), await Scalar<string>(legacy), AuditCheckpoints.ReadAllAsync(stream.Key).Result.Checkpoints.Count));
        // And the verification transaction itself cannot write, even by mistake.
        await using var conn = new Npgsql.NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var ro = new Npgsql.NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await using var w = new Npgsql.NpgsqlCommand($"INSERT INTO {Au.Table} ({Au.OccurredAtUtc},{Au.CompanyId},{Au.EventCode}) VALUES (now(), gen_random_uuid(), 1)", conn, tx);
            await w.ExecuteNonQueryAsync();
        });
    }

    [PostgresFact]
    public async Task The_report_never_contains_payloads_keys_or_physical_names()
    {
        var (company, admin, stream) = await Scenario();
        Ok(await Access.CreateRoleAsync(admin.Id, company, "Payroll", [Hasm.Infrastructure.Access.PermissionCatalog.Codes.AuditView]));
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'secret-payload-marker' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 3", ("s", stream.Key));

        var report = await Verify(stream);
        var text = string.Join("|", report.Findings.Select(f => $"{f.Code}{f.Seq}{f.Message}")) + report.Stream;

        Assert.DoesNotContain("secret-payload-marker", text);
        Assert.DoesNotContain("permissions:", text);
        Assert.DoesNotContain(Options.SystemDatabaseName!, text);
        Assert.DoesNotContain(Options.Password!, text);
    }
}
