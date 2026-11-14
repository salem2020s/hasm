using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using NpgsqlTypes;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;
using XR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ExchangeRate;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using CU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Currency;

namespace Hasm.Infrastructure.Ledger;

/// <summary>
/// Exchange-rate maintenance (Phase 3.2B, slice S2). <b>No authorization here by design:</b> <see cref="ExchangeRateManagementService"/> wraps it.
/// Rates are MANUAL (no external provider is assumed), append-only and effective-dated: the effective rate for a date is the entry with the latest
/// <c>valid_from</c> not after the date, and among entries with the same <c>valid_from</c> the latest entered one, chosen by the SQL function that the posting
/// engine will use too. A missing rate is reported as such (<see cref="ResolveAsync"/> returns null); there is never an implicit 1. Entering a rate runs in
/// <see cref="CompanyAuditedTransactions"/> (rate, history row and audit event commit together) after a company-wide advisory lock, guarded by the currency's
/// latest entry id (a concurrent entry makes the second one fail with a revision conflict instead of silently overriding it).
/// </summary>
public sealed class ExchangeRateService(CompanyDatabaseResolver resolver, CompanyAuditedTransactions transactions, TimeProvider clock)
{
    internal const int LockKey1 = 0x48435852;   // "HCXR": advisory-lock namespace of exchange-rate entry
    internal const int LockKey2 = 1;

    private sealed class Abort(ConfigStatus status, string code, long? currentRevision = null) : Exception(code)
    {
        public ConfigResult<T> To<T>() => new(status, code, default, null, currentRevision);
    }

    private static readonly string Select =
        $"SELECT x.{XR.Id}, x.{XR.CurrencyCode}, x.{XR.Rate}, x.{XR.ValidFrom}, x.{XR.EnteredAtUtc}, x.{XR.EnteredBy}, x.{XR.Note} FROM {XR.Table} x";

    private static ExchangeRateView Read(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), DateOnly.FromDateTime(r.GetDateTime(3)), new DateTimeOffset(r.GetDateTime(4), TimeSpan.Zero), r.GetGuid(5), r.IsDBNull(6) ? null : r.GetString(6));

    public async Task<ExchangeRateOverview?> OverviewAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await OverviewAsync(conn, null, ct);
    }

    internal static async Task<ExchangeRateOverview?> OverviewAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var rows = new List<(string Code, string? Name, int? Minor, bool IsBase)>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT cc.{CC.Code}, cu.{CU.NameEn}, cu.{CU.MinorUnits}, cc.{CC.IsBase} FROM {CC.Table} cc JOIN {CU.Table} cu ON cu.{CU.Code} = cc.{CC.Code} WHERE cc.{CC.Enabled} ORDER BY cc.{CC.IsBase} DESC, cc.{CC.Code}", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2), r.GetBoolean(3)));
        var baseRow = rows.FirstOrDefault(x => x.IsBase);
        if (baseRow.Code is null) return null;   // the base currency is not selected yet: nothing can be converted
        var list = new List<ExchangeRateCurrencyView>();
        foreach (var f in rows.Where(x => !x.IsBase))
        {
            ExchangeRateView? latest = null;
            await using var q = new NpgsqlCommand($"{Select} WHERE x.{XR.CurrencyCode} = @c ORDER BY x.{XR.ValidFrom} DESC, x.{XR.Id} DESC LIMIT 1", conn, tx);
            q.Parameters.AddWithValue("c", f.Code);
            await using (var r = await q.ExecuteReaderAsync(ct)) if (await r.ReadAsync(ct)) latest = Read(r);
            await using var m = new NpgsqlCommand($"SELECT COALESCE(max({XR.Id}), 0) FROM {XR.Table} WHERE {XR.CurrencyCode} = @c", conn, tx);
            m.Parameters.AddWithValue("c", f.Code);
            list.Add(new ExchangeRateCurrencyView(f.Code, f.Name, f.Minor, latest, (long)(await m.ExecuteScalarAsync(ct))!));
        }
        return new ExchangeRateOverview(baseRow.Code, baseRow.Minor, list);
    }

    /// <summary>Entered rates, newest first (history is never hidden). Optional currency and date window; at most <paramref name="limit"/> rows.</summary>
    public async Task<IReadOnlyList<ExchangeRateView>> ListAsync(Guid companyId, string? currency, DateOnly? from, DateOnly? to, int limit, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        await using var cmd = new NpgsqlCommand(
            $"{Select} WHERE (@c::varchar IS NULL OR x.{XR.CurrencyCode} = @c) AND (@f::date IS NULL OR x.{XR.ValidFrom} >= @f) AND (@t::date IS NULL OR x.{XR.ValidFrom} <= @t) " +
            $"ORDER BY x.{XR.ValidFrom} DESC, x.{XR.Id} DESC LIMIT @l", conn);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Varchar) { Value = (object?)currency?.Trim().ToUpperInvariant() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Date) { Value = from is null ? DBNull.Value : from.Value.ToDateTime(TimeOnly.MinValue) });
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Date) { Value = to is null ? DBNull.Value : to.Value.ToDateTime(TimeOnly.MinValue) });
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 500));
        var list = new List<ExchangeRateView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<AppliedRate?> ResolveAsync(Guid companyId, string currency, DateOnly date, CancellationToken ct = default)
    {
        await using var conn = await resolver.OpenAsync(companyId, ct);
        return await ResolveAsync(conn, null, currency, date, ct);
    }

    /// <summary>The effective rate for a currency on a date, or null when none exists (the caller must refuse or require an authorized override). Usable inside a posting transaction.</summary>
    public static async Task<AppliedRate?> ResolveAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string currency, DateOnly date, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"{Select} WHERE x.{XR.Id} = {CompanySchema.ExchangeRate.ResolveFunction}(@c, @d)", conn, tx);
        cmd.Parameters.AddWithValue("c", currency.Trim().ToUpperInvariant());
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var v = Read(r);
        return new AppliedRate(v.Id, v.CurrencyCode, v.Rate, v.ValidFrom);
    }

    public async Task<ConfigResult<ExchangeRateView>> EnterAsync(Guid actor, Guid companyId, long? expectedLatestId, ExchangeRateInput? input, CancellationToken ct = default)
    {
        if (expectedLatestId is null) return ConfigResult<ExchangeRateView>.Fail(ConfigStatus.PreconditionRequired, "precondition_required");
        if (!ExchangeRateValidation.TryNormalize(input, out var currency, out var rate, out var validFrom, out var note, out var errors)) return ConfigResult<ExchangeRateView>.Invalid(errors);
        try
        {
            return ConfigResult<ExchangeRateView>.Ok(await transactions.ExecuteAsync(companyId, async work =>
            {
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@a, @b)", work.Connection, work.Transaction))
                {
                    lk.Parameters.AddWithValue("a", LockKey1); lk.Parameters.AddWithValue("b", LockKey2);
                    await lk.ExecuteNonQueryAsync(ct);
                }
                var overview = await OverviewAsync(work.Connection, work.Transaction, ct);
                if (overview is null) throw new Abort(ConfigStatus.Conflict, "base_currency_not_selected");
                if (currency == overview.BaseCurrency) throw new Abort(ConfigStatus.Invalid, "currency_is_base");
                var entry = overview.Currencies.FirstOrDefault(c => c.CurrencyCode == currency) ?? throw new Abort(ConfigStatus.Invalid, "currency_not_enabled");
                if (entry.LatestId != expectedLatestId.Value) throw new Abort(ConfigStatus.PreconditionFailed, "revision_conflict", entry.LatestId);
                var current = await ResolveAsync(work.Connection, work.Transaction, currency, validFrom, ct);
                if (current is not null && current.ValidFrom == validFrom && current.Rate == rate) throw new Abort(ConfigStatus.Conflict, "rate_unchanged");
                var now = clock.GetUtcNow().UtcDateTime;
                var changes = new JsonObject { ["currency"] = currency, ["rate"] = rate.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture), ["validFrom"] = validFrom.ToString("yyyy-MM-dd") };
                if (current is not null && current.ValidFrom == validFrom) changes["replaces"] = current.RateId;   // a correction: the earlier entry stays in history
                var json = changes.ToJsonString();
                var receipt = await work.AppendAuditAsync(new AuditEventData(LedgerAuditEvents.ExchangeRateEntered, ActorUserId: actor,
                    Detail: $"exchange rate {currency} f={string.Join(',', changes.Select(c => c.Key))} h={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant()}"), ct);
                long id;
                await using (var ins = new NpgsqlCommand(
                    $"INSERT INTO {XR.Table} ({XR.CurrencyCode}, {XR.Rate}, {XR.ValidFrom}, {XR.EnteredAtUtc}, {XR.EnteredBy}, {XR.Note}, {XR.AuditStream}, {XR.AuditSeq}) VALUES (@c, @r, @d, @t, @a, @n, @s, @q) RETURNING {XR.Id}",
                    work.Connection, work.Transaction))
                {
                    ins.Parameters.AddWithValue("c", currency); ins.Parameters.AddWithValue("r", rate);
                    ins.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = validFrom.ToDateTime(TimeOnly.MinValue) });
                    ins.Parameters.AddWithValue("t", now); ins.Parameters.AddWithValue("a", actor);
                    ins.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
                    ins.Parameters.AddWithValue("s", receipt.StreamKey); ins.Parameters.AddWithValue("q", receipt.Seq);
                    id = (long)(await ins.ExecuteScalarAsync(ct))!;
                }
                await using var h = new NpgsqlCommand(
                    $"INSERT INTO {LH.Table} ({LH.Entity}, {LH.EntityKey}, {LH.Revision}, {LH.ActorUserId}, {LH.ChangedAtUtc}, {LH.Changes}, {LH.AuditStream}, {LH.AuditSeq}) VALUES (2, @k, @r, @a, @t, @c, @s, @q)",
                    work.Connection, work.Transaction);
                h.Parameters.AddWithValue("k", currency); h.Parameters.AddWithValue("r", id); h.Parameters.AddWithValue("a", actor); h.Parameters.AddWithValue("t", now);
                h.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = json });
                h.Parameters.AddWithValue("s", receipt.StreamKey); h.Parameters.AddWithValue("q", receipt.Seq);
                await h.ExecuteNonQueryAsync(ct);
                return new ExchangeRateView(id, currency, rate, validFrom, new DateTimeOffset(now, TimeSpan.Zero), actor, note);
            }, ct));
        }
        catch (Abort a) { return a.To<ExchangeRateView>(); }
    }
}
