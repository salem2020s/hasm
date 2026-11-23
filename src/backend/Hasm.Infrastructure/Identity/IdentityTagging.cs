using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Security;
using Npgsql;
using U = Hasm.Infrastructure.SystemDatabase.SystemSchema.User;
using UT = Hasm.Infrastructure.SystemDatabase.SystemSchema.UserTag;

namespace Hasm.Infrastructure.Identity;

/// <summary>
/// The ONE-TIME owner tagging of the users that exist without a tag (identity integrity S-2a, OD-2/OD-3: there is no compatibility mode, an untagged user cannot sign in until the owner has tagged it). It runs offline
/// under the administrative database account with the key ring, tags only rows that carry NO tag (never re-tags a row that has one, so a tampered tagged row is never blessed), and records the result in the audit
/// stream. WHAT IT TAGS IS WHATEVER THE ROW SAYS NOW: the operator must have reviewed the users (who exists, who is an identity administrator, who requires which second factor) BEFORE running it; the reviewed
/// report of the plan (S-2c) will make that review mechanical.
/// </summary>
public static class IdentityTagging
{
    public sealed record Result(int Tagged, int AlreadyTagged);

    public static async Task<Result> TagUntaggedUsersAsync(string adminConnectionString, IdentityKeys keys, TimeProvider clock, AuditTrail audit, CancellationToken ct = default)
    {
        var key = keys.Active();                                                                                  // fails closed (EncryptionUnavailableException) without a key
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = new List<Guid>(); var already = 0;
        await using (var sel = new NpgsqlCommand($"SELECT {U.Id}, {UT.Tag} IS NULL FROM {U.Table} ORDER BY {U.Id}", conn, tx))
        await using (var r = await sel.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) { if (r.GetBoolean(1)) ids.Add(r.GetGuid(0)); else already++; }
        var tagged = 0;
        foreach (var id in ids)
        {
            var row = await UserRow.LoadAsync(conn, tx, id, forUpdate: true, ct);
            if (row is null || row.Tag is not null) continue;
            var next = row with { KeyId = key.Id, Tag = null };
            await using var set = new NpgsqlCommand($"UPDATE {U.Table} SET {UT.KeyId} = @k, {UT.Tag} = @t WHERE {U.Id} = @id AND {UT.Tag} IS NULL", conn, tx);
            set.Parameters.AddWithValue("k", key.Id);
            set.Parameters.AddWithValue("t", next.ComputeTag(keys, key));
            set.Parameters.AddWithValue("id", id);
            if (await set.ExecuteNonQueryAsync(ct) == 1) tagged++;
        }
        var receipt = await audit.AppendAsync(conn, tx, AuditStream.System, AuditCanonical.FromMicros(AuditCanonical.ToMicros(clock.GetUtcNow())),
            new AuditEventData(MfaAuditEvents.IdentityRecordsTagged, null, null, null, $"identity-tag: {tagged} untagged user(s) tagged under key {key.Id}; {already} already tagged"), ct);
        await audit.CommitAsync(tx, receipt, ct);
        return new Result(tagged, already);
    }
}
