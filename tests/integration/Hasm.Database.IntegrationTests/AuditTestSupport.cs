using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Au = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Shared helpers for the audit-integrity suites (real PostgreSQL, same safety gate as every database test). The table owner is the
/// test role, so tests can switch the append-only trigger off to play an attacker with direct database access: exactly the
/// threat the keyed chain and the external checkpoints are meant to detect.
/// </summary>
public abstract class AuditTestBase : AccessTestBase
{
    protected const short FutureEvent = 1000;

    protected AuditVerifier Verifier => new(Options.BuildSystemConnectionString(admin: true), AuditKeys, AuditCheckpoints);
    protected Task<AuditVerificationReport> Verify(AuditStream stream) => Verifier.VerifyAsync(stream);

    protected static string[] Codes(AuditVerificationReport r, FindingSeverity? severity = null) =>
        [.. r.Findings.Where(f => severity is null || f.Severity == severity).Select(f => f.Code).Distinct()];

    protected static void AssertVerified(AuditVerificationReport r, long events)
    {
        Assert.True(r.Verdict == AuditVerdict.Verified, $"expected Verified but was {r.Verdict}: {string.Join(", ", r.Findings.Select(f => $"{f.Code}@{f.Seq}"))}");
        Assert.Equal(events, r.Events);
        Assert.Equal(events, r.VerifiedThroughSeq);
        Assert.Equal(0, r.UnanchoredEvents);
    }

    protected Task Tamper(string sql, params (string, object)[] ps) =>
        Exec($"ALTER TABLE {Ev.Table} DISABLE TRIGGER {Ev.GuardTrigger}; ALTER TABLE {Ev.Table} DISABLE TRIGGER {SystemSchema.AuditHeadLink.TrSystemLink}; {sql}; ALTER TABLE {Ev.Table} ENABLE TRIGGER {SystemSchema.AuditHeadLink.TrSystemLink}; ALTER TABLE {Ev.Table} ENABLE TRIGGER {Ev.GuardTrigger};", ps);   // tampering as the OWNER: the append-only guard AND the link guard of the audit head are off around the statement (a DBA who disables a trigger is outside the database's own protection; the verifier detects it)

    protected Task TamperLegacy(string sql, params (string, object)[] ps) =>
        Exec($"ALTER TABLE {Au.Table} DISABLE TRIGGER {Au.GuardTrigger}; {sql}; ALTER TABLE {Au.Table} ENABLE TRIGGER {Au.GuardTrigger};", ps);

    protected async Task<List<AuditRecord>> Events(AuditStream stream)
    {
        await using var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {AuditStorage.Columns} FROM {Ev.Table} WHERE {Ev.StreamKey} = @s ORDER BY {Ev.Seq}", conn);
        cmd.Parameters.AddWithValue("s", stream.Key);
        var list = new List<AuditRecord>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(AuditStorage.Read(r));
        return list;
    }

    protected Task<long> EventCount(AuditStream? stream = null) =>
        Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE (@s::text IS NULL OR {Ev.StreamKey} = @s)", ("s", stream is { } s ? s.Key : DBNull.Value));

    /// <summary>Inserts a raw row (an attacker with database access who does NOT hold the audit key).</summary>
    protected Task InsertRaw(AuditRecord r) => Tamper(
        $"INSERT INTO {Ev.Table} ({AuditStorage.Columns}) VALUES (@s,@q,@id,@t,@e,@c,@a,@tu,@su,@l,@d,@v,@k,@p,@h,@m)",
        ("s", r.StreamKey), ("q", r.Seq), ("id", r.EventId), ("t", r.OccurredAtUtc), ("e", r.EventType), ("c", (object?)r.CompanyId ?? DBNull.Value),
        ("a", (object?)r.ActorUserId ?? DBNull.Value), ("tu", (object?)r.TargetUserId ?? DBNull.Value), ("su", (object?)r.SubjectId ?? DBNull.Value),
        ("l", (object?)r.LegacyRef ?? DBNull.Value), ("d", (object?)r.Detail ?? DBNull.Value), ("v", r.FormatVersion), ("k", r.KeyId),
        ("p", r.PrevHash), ("h", r.Hash), ("m", r.Mac));

    /// <summary>Inserts a raw row with every trigger ENABLED (what the owner or the runtime role can do without tampering): the link guard of the audit head judges it.</summary>
    protected Task InsertRawEnforced(AuditRecord r) => Exec(
        $"INSERT INTO {Ev.Table} ({AuditStorage.Columns}) VALUES (@s,@q,@id,@t,@e,@c,@a,@tu,@su,@l,@d,@v,@k,@p,@h,@m)",
        ("s", r.StreamKey), ("q", r.Seq), ("id", r.EventId), ("t", r.OccurredAtUtc), ("e", r.EventType), ("c", (object?)r.CompanyId ?? DBNull.Value),
        ("a", (object?)r.ActorUserId ?? DBNull.Value), ("tu", (object?)r.TargetUserId ?? DBNull.Value), ("su", (object?)r.SubjectId ?? DBNull.Value),
        ("l", (object?)r.LegacyRef ?? DBNull.Value), ("d", (object?)r.Detail ?? DBNull.Value), ("v", r.FormatVersion), ("k", r.KeyId),
        ("p", r.PrevHash), ("h", r.Hash), ("m", r.Mac));

    /// <summary>A company with an administrator and <paramref name="roles"/> custom roles: 2 + roles events in the company stream.</summary>
    protected async Task<(Guid Company, UserRecord Admin, AuditStream Stream)> Scenario(int roles = 4)
    {
        await StartAccess();
        var company = await NewCompany();
        var admin = await NewAdmin(company);
        for (var i = 0; i < roles; i++)
            Ok(await Access.CreateRoleAsync(admin.Id, company, $"Role {i}", [PermissionCatalog.Codes.MembersView]));
        var stream = AuditStream.ForCompany(company);
        Assert.Equal(2 + roles, await EventCount(stream));
        return (company, admin, stream);
    }

    protected async Task<byte[][]> Hashes(AuditStream stream) => [.. (await Events(stream)).Select(e => e.Hash)];
}
