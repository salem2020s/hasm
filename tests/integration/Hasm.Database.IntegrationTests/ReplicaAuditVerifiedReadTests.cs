using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 4.3 slice 8, S8d-1 (plan section 4B): the 2015, 2016 and 2040 facts a deletion decision relies on are read ONLY through the chain-verified prefix of the system audit stream, in the verifier's own
/// snapshot. A forged row (right shape, invalid MAC) and everything above a sequence gap are never evidence, genuine facts below them are, and the raw status read (informational) is not authenticated.
/// Real PostgreSQL, real <see cref="AuditTrail"/> writes, rows forged as an attacker who holds INSERT but not the audit key.
/// </summary>
public class ReplicaAuditVerifiedReadTests : AuditTestBase
{
    private const string RunA = "20260301T020000Z-0a0b0c0d", RunB = "20260302T020000Z-0a0b0c0d", RunC = "20260303T020000Z-0a0b0c0d";
    private static readonly string Hash = new('a', 64);
    private string Conn => Options.BuildSystemConnectionString(admin: true);

    private Task Write(short type, string detail) => BackupAudit.WriteAsync(Conn, Audit, type, detail);
    private static string Started(string dest, string run) => $"backup deletion started operator=os:t run={run} status={Hash} reasons=none destination={dest}";
    private static string Deleted(string dest, string run) => $"backup deleted operator=os:t run={run} status={Hash} files=1 bytes=1 tombstone=yes resumed=no destination={dest}";
    private static string Copied(string dest, string run) => $"replica verified operator=os:t run={run} destination={dest} kind=network status={Hash} files=1 bytes=1";

    private async Task ForgeAboveHead(short type, string detail)
    {
        var head = (await Events(AuditStream.System))[^1];
        var attacker = new InMemoryAuditKeyRing("k1");                                                            // the key id is known, the secret is not
        var forged = AuditCanonical.Seal(head with { Seq = head.Seq + 1, EventId = Guid.NewGuid(), EventType = type, Detail = detail, PrevHash = head.Hash, Hash = [], Mac = [] }, attacker.GetActive());
        await InsertRaw(forged);
    }

    [PostgresFact]
    public async Task Genuine_facts_are_read_through_the_verified_prefix_with_exact_destination_tokens_and_the_raw_read_is_not_authenticated()
    {
        await StartAccess();
        await Write(BackupAuditEvents.DeletionStarted, Started("nas", RunA));
        await Write(BackupAuditEvents.Deleted, Deleted("nas", RunA));
        await Write(BackupAuditEvents.ReplicaVerified, Copied("nas-10", RunB));
        await Write(BackupAuditEvents.DeletionStarted, Started("nas", RunC));                                      // started, never finished

        var state = await ReplicaAuditReader.ReadVerifiedAsync(Conn, AuditKeys, AuditCheckpoints);
        var total = await EventCount(AuditStream.System);
        Assert.True(state.Authenticated);
        Assert.Equal(total, state.VerifiedThroughSeq);
        Assert.False(state.UnverifiedRemainder);
        Assert.NotNull(state.CompletedPairSeq("nas", RunA, Hash));
        Assert.Null(state.CompletedPairSeq("nas-1", RunA, Hash));                                                  // nas-1 is never nas
        Assert.Null(state.CompletedPairSeq("primary", RunA, Hash));
        Assert.NotNull(state.OpenStartSeq("nas", RunC, Hash));
        Assert.Null(state.OpenStartSeq("nas", RunA, Hash));
        Assert.True(state.LastReplicaVerifiedSeq.ContainsKey(("nas-10", RunB)));
        Assert.False(state.LastReplicaVerifiedSeq.ContainsKey(("nas", RunB)));

        var raw = await ReplicaAuditReader.ReadAsync(Conn);                                                         // the status read: as stored, informational
        Assert.False(raw.Authenticated);
        Assert.Null(raw.VerifiedThroughSeq);
        Assert.Equal(state.CompletedPairSeq("nas", RunA, Hash), raw.CompletedPairSeq("nas", RunA, Hash));
    }

    [PostgresFact]
    public async Task A_forged_pair_above_the_head_is_not_evidence_the_prefix_ends_below_it_and_only_the_raw_read_sees_it()
    {
        await StartAccess();
        await Write(BackupAuditEvents.DeletionStarted, Started("nas", RunA));
        await Write(BackupAuditEvents.Deleted, Deleted("nas", RunA));
        var genuineHead = (await Events(AuditStream.System))[^1].Seq;
        await ForgeAboveHead(BackupAuditEvents.DeletionStarted, Started("nas", RunB));
        await ForgeAboveHead(BackupAuditEvents.Deleted, Deleted("nas", RunB));

        var state = await ReplicaAuditReader.ReadVerifiedAsync(Conn, AuditKeys, AuditCheckpoints);
        Assert.Equal(genuineHead, state.VerifiedThroughSeq);
        Assert.True(state.UnverifiedRemainder);                                                                    // alerted (audit_unverified), never used
        Assert.NotNull(state.CompletedPairSeq("nas", RunA, Hash));                                                 // genuine facts below the forgery still count
        Assert.Null(state.CompletedPairSeq("nas", RunB, Hash));                                                    // the forged pair makes nothing "not pending"
        Assert.False(state.HasStart("nas", RunB, Hash));

        var raw = await ReplicaAuditReader.ReadAsync(Conn);
        Assert.NotNull(raw.CompletedPairSeq("nas", RunB, Hash));                                                   // what the informational read would have shown
        Assert.False(raw.Authenticated);
    }

    [PostgresFact]
    public async Task A_sequence_gap_ends_the_prefix_and_nothing_above_it_is_evidence()
    {
        await StartAccess();
        await Write(BackupAuditEvents.DeletionStarted, Started("nas", RunA));
        await Write(BackupAuditEvents.Deleted, Deleted("nas", RunA));
        await Write(BackupAuditEvents.DeletionStarted, Started("nas", RunB));
        await Write(BackupAuditEvents.Deleted, Deleted("nas", RunB));
        var events = await Events(AuditStream.System);
        var gapAt = events.Single(e => e.EventType == BackupAuditEvents.DeletionStarted && e.Detail!.Contains(RunB)).Seq;
        await Tamper($"DELETE FROM {Ev.Table} WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = @q", ("s", AuditStream.SystemKey), ("q", gapAt));

        var state = await ReplicaAuditReader.ReadVerifiedAsync(Conn, AuditKeys, AuditCheckpoints);
        Assert.Equal(gapAt - 1, state.VerifiedThroughSeq);
        Assert.True(state.UnverifiedRemainder);
        Assert.NotNull(state.CompletedPairSeq("nas", RunA, Hash));
        Assert.Null(state.CompletedPairSeq("nas", RunB, Hash));                                                    // its 2016 lies above the gap
    }

    [PostgresFact]
    public async Task A_missing_audit_key_or_an_unreachable_database_fails_closed_as_audit_unreadable()
    {
        await StartAccess();
        await Write(BackupAuditEvents.DeletionStarted, Started("nas", RunA));
        var other = new InMemoryAuditKeyRing("other");                                                             // the genuine key id is not in this ring: no record can be authenticated
        var state = await ReplicaAuditReader.ReadVerifiedAsync(Conn, other, AuditCheckpoints);
        Assert.Equal(0L, state.VerifiedThroughSeq);                                                                // nothing verifies, so no fact is evidence
        Assert.False(state.HasStart("nas", RunA, Hash));
        Assert.True(state.UnverifiedRemainder);
        var ex = await Assert.ThrowsAsync<BackupException>(() => ReplicaAuditReader.ReadVerifiedAsync(new Npgsql.NpgsqlConnectionStringBuilder(Conn) { Port = 1, Timeout = 2, Pooling = false }.ConnectionString, AuditKeys, AuditCheckpoints));
        Assert.Equal("audit_unreadable", ex.Code);
    }
}
