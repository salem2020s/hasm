using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using B = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Audit head integrity, slice 1 on the BUSINESS audit table of a company database (PROVISIONAL company migration number): the same rule as the system table (<see cref="AuditHeadLinkTests"/>), enforced
/// whoever inserts and with every trigger enabled: sequence 1 and the genesis hash on an empty stream, afterwards the head's sequence plus one and the head's hash; a gap, a replay, an unlinked previous
/// hash and a wrong genesis are refused with <c>audit_link_violation</c>. The genuine writer (<c>CompanyAuditedTransactions</c>) is unaffected and its chain still verifies; the existing append-only and
/// identity-binding triggers remain; the SQL genesis expression equals <c>AuditCanonical.GenesisHash</c> for the business stream.
/// </summary>
public class CompanyAuditHeadLinkTests : CompanyRuntimeTestBase
{
    private static string Hex(byte[] b) => Convert.ToHexString(b);

    private static string Insert(string stream, Guid company, long seq, string prevSql) =>
        $"INSERT INTO {B.Table} ({B.StreamKey},{B.Seq},{B.EventId},{B.OccurredAtUtc},{B.EventType},{B.CompanyId},{B.FormatVersion},{B.KeyId},{B.PrevHash},{B.Hash},{B.Mac}) " +
        $"VALUES ('{stream}', {seq}, gen_random_uuid(), now(), 1000, '{company}', 1, 'k1', {prevSql}, decode(repeat('01',32),'hex'), decode(repeat('02',32),'hex'))";

    private static string Bytes(byte[] b) => $"'\\x{Hex(b)}'";

    [PostgresFact]
    public async Task The_business_table_accepts_only_rows_that_continue_the_chain_and_the_genuine_writer_is_unaffected()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        var db = c.PhysicalDatabaseName;
        var stream = AuditStream.ForBusiness(c.Id);
        var genesis = AuditCanonical.GenesisHash(stream.Key);
        async Task Refused(string sql, long expectedCount)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => InCompany(db, sql));
            Assert.Equal("P0001", ex.SqlState);
            Assert.Equal("audit_link_violation", ex.MessageText);
            Assert.Equal(expectedCount, await InCompany<long>(db, $"SELECT count(*) FROM {B.Table}"));
        }

        // empty stream: only sequence 1 with the genesis hash
        await Refused(Insert(stream.Key, c.Id, 1, "decode(repeat('00',32),'hex')"), 0);
        await Refused(Insert(stream.Key, c.Id, 2, Bytes(genesis)), 0);
        await InCompany(db, Insert(stream.Key, c.Id, 1, Bytes(genesis)));
        Assert.Equal(1L, await InCompany<long>(db, $"SELECT count(*) FROM {B.Table}"));
        var head = await InCompany<byte[]>(db, $"SELECT {B.Hash} FROM {B.Table} WHERE {B.Seq} = 1");
        // afterwards: the next sequence and the head's hash, nothing else
        await Refused(Insert(stream.Key, c.Id, 3, Bytes(head)), 1);                                              // a gap
        await Refused(Insert(stream.Key, c.Id, 1, Bytes(head)), 1);                                              // a replay
        await Refused(Insert(stream.Key, c.Id, 2, Bytes(genesis)), 1);                                           // the genesis hash again
        await Refused(Insert(stream.Key, c.Id, 2, "decode(repeat('07',32),'hex')"), 1);                          // a random link
        await InCompany(db, Insert(stream.Key, c.Id, 2, Bytes(head)));
        Assert.Equal(2L, await InCompany<long>(db, $"SELECT count(*) FROM {B.Table}"));

        // the guards that were already there are still there, and the table gained the link guard
        var update = await Assert.ThrowsAsync<PostgresException>(() => InCompany(db, $"UPDATE {B.Table} SET {B.Detail} = 'x'"));
        Assert.Equal("audit events are append-only", update.MessageText);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => InCompany(db, $"DELETE FROM {B.Table}"));
        Assert.Equal("audit events are append-only", delete.MessageText);
        var triggers = new List<string>();
        await using (var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(db)))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT tgname FROM pg_trigger WHERE tgrelid = '{B.Table}'::regclass AND NOT tgisinternal", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) triggers.Add(r.GetString(0));
        }
        Assert.Contains(CompanySchema.AuditHeadLink.TrBusinessLink, triggers);
        Assert.Contains(B.GuardTrigger, triggers);
        Assert.Equal(1, triggers.Count(t => t == CompanySchema.AuditHeadLink.TrBusinessLink));
        // the SQL genesis expression is the canonical one for the business stream
        Assert.Equal(genesis, await InCompany<byte[]>(db, $"SELECT sha256(convert_to('HASM-AUDIT-GENESIS-v1', 'UTF8') || decode('00', 'hex') || convert_to('{stream.Key}', 'UTF8'))"));
    }

    [PostgresFact]
    public async Task The_genuine_business_writer_chains_through_the_guard_and_its_stream_verifies()
    {
        await StartCompanies();
        var c = await ReadyCompany();
        await InCompany(c.PhysicalDatabaseName, "CREATE TABLE zz_ledger (id int PRIMARY KEY, note text)");
        var resolver = SinglePrincipalResolver(c);
        var tx = new CompanyAuditedTransactions(resolver, Audit, Clock);
        for (var i = 1; i <= 3; i++)
        {
            var id = i;
            await tx.ExecuteAsync(c.Id, async w =>
            {
                await using (var cmd = new NpgsqlCommand("INSERT INTO zz_ledger (id, note) VALUES (@i, 'x')", w.Connection, w.Transaction))
                {
                    cmd.Parameters.AddWithValue("i", id);
                    await cmd.ExecuteNonQueryAsync();
                }
                await w.AppendAuditAsync(new AuditEventData(1000, Detail: "ledger " + id));
            });
        }
        Assert.Equal(3L, await InCompany<long>(c.PhysicalDatabaseName, $"SELECT count(*) FROM {B.Table}"));
        var report = await new AuditVerifier(Options.BuildCompanyAdminConnectionString(c.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(c.Id));
        Assert.Equal(3L, report.VerifiedThroughSeq);
        Assert.DoesNotContain(report.Findings, f => f.Severity == FindingSeverity.Failure);
    }
}
