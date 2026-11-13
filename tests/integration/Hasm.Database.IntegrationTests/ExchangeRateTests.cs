using System.Net;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Ledger;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Cc = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompanyCurrency;
using Lh = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LedgerHistory;
using Xr = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ExchangeRate;

namespace Hasm.Database.IntegrationTests;

/// <summary>Exchange rates (company migration 0008 slice S2, Phase 3.2B) against real PostgreSQL; runs only on the verified disposable cluster.</summary>
public abstract class ExchangeRateTestBase : ChartAccessTestBase
{
    protected ExchangeRateService Rates { get; private set; } = null!;
    protected ExchangeRateManagementService XMgmt { get; private set; } = null!;
    protected long Baseline;

    protected void BuildRateFacade()
    {
        Rates = new ExchangeRateService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        XMgmt = new ExchangeRateManagementService(Rbac, Rates);
    }

    /// <summary>JOD as base; USD and EUR enabled; SAR present but disabled.</summary>
    protected async Task PrepareRates(bool currencies = true)
    {
        await PrepareRbac();
        BuildRateFacade();
        if (currencies)
        {
            await InCompany(Company.PhysicalDatabaseName, SeedCurrency("JOD", true, true));
            foreach (var (code, enabled) in new[] { ("USD", true), ("EUR", true), ("SAR", false) }) await InCompany(Company.PhysicalDatabaseName, SeedCurrency(code, false, enabled));
        }
        Baseline = await Count(Company, Audit_.Table);
    }

    private static string SeedCurrency(string code, bool isBase, bool enabled) =>
        $"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, {(enabled ? "true" : "false")}, 1, now())";

    protected async Task<long> LatestId(string currency) => Ok(await XMgmt.OverviewAsync(Admin.Id, Company.Id)).Currencies.Single(c => c.CurrencyCode == currency).LatestId;

    protected async Task<ExchangeRateView> Enter(string currency, string rate, string from, string? note = null)
    {
        var id = await LatestId(currency);
        return Ok(await Rates.EnterAsync(Admin.Id, Company.Id, id, new ExchangeRateInput(currency, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(from), note)));
    }

    protected Task<string?> RawFails(string sql) => InTx(sql);
}

public class ExchangeRateTests : ExchangeRateTestBase
{
    [PostgresFact]
    public async Task Without_a_base_currency_nothing_can_be_entered_and_with_one_the_foreign_currencies_start_without_rates()
    {
        await PrepareRates(currencies: false);
        Assert.Equal("base_currency_not_selected", (await XMgmt.OverviewAsync(Admin.Id, Company.Id)).Code);
        Assert.Equal("base_currency_not_selected", (await Rates.EnterAsync(Admin.Id, Company.Id, 0, new ExchangeRateInput("USD", 0.71m, new DateOnly(2026, 1, 1), null))).Code);
        await PrepareRatesCurrenciesOnly();
        var o = Ok(await XMgmt.OverviewAsync(Admin.Id, Company.Id));
        Assert.Equal("JOD", o.BaseCurrency);
        Assert.Equal(3, o.BaseMinorUnits);
        Assert.Equal(["EUR", "USD"], o.Currencies.Select(c => c.CurrencyCode).Order());   // SAR is disabled: not offered
        Assert.All(o.Currencies, c => { Assert.Null(c.Latest); Assert.Equal(0, c.LatestId); });
        Assert.Equal(0L, await Count(Company, Xr.Table));
        Assert.Equal(0L, await Count(Company, Audit_.Table) - Baseline);
    }

    private async Task PrepareRatesCurrenciesOnly()
    {
        foreach (var (code, isBase, enabled) in new[] { ("JOD", true, true), ("USD", false, true), ("EUR", false, true), ("SAR", false, false) })
            await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('{code}', {(isBase ? "true" : "false")}, {(enabled ? "true" : "false")}, 1, now())");
    }

    [PostgresFact]
    public async Task The_effective_rate_is_the_latest_entry_not_after_the_date_and_a_missing_rate_is_reported_never_assumed()
    {
        await PrepareRates();
        Assert.Null(await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2026, 6, 1)));           // nothing entered: missing
        var jan = await Enter("USD", "0.7100000000", "2026-01-01");
        var mar = await Enter("USD", "0.7200000000", "2026-03-01");
        Assert.Null(await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2025, 12, 31)));         // before the first rate: missing
        Assert.Equal(jan.Id, (await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2026, 1, 1)))!.RateId);
        Assert.Equal(jan.Id, (await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2026, 2, 28)))!.RateId);
        var onMar = (await Rates.ResolveAsync(Company.Id, "usd", new DateOnly(2026, 3, 1)))!;
        Assert.Equal((mar.Id, 0.72m, new DateOnly(2026, 3, 1)), (onMar.RateId, onMar.Rate, onMar.ValidFrom));
        Assert.Equal(mar.Id, (await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2030, 1, 1)))!.RateId);
        Assert.Null(await Rates.ResolveAsync(Company.Id, "EUR", new DateOnly(2026, 6, 1)));           // another currency has its own rates
        Assert.Equal(Ok(await XMgmt.ResolveAsync(Admin.Id, Company.Id, "USD", new DateOnly(2026, 2, 1))).RateId, jan.Id);
        Assert.Equal("rate_missing", (await XMgmt.ResolveAsync(Admin.Id, Company.Id, "EUR", new DateOnly(2026, 2, 1))).Code);
    }

    [PostgresFact]
    public async Task A_correction_for_the_same_date_is_a_new_entry_that_wins_while_the_old_one_stays_in_history()
    {
        await PrepareRates();
        var first = await Enter("USD", "0.7100000000", "2026-01-01");
        var fix = await Enter("USD", "0.7150000000", "2026-01-01", "typo");
        Assert.True(fix.Id > first.Id);
        Assert.Equal(fix.Id, (await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2026, 1, 15)))!.RateId);
        var history = await Rates.ListAsync(Company.Id, "USD", null, null, 100);
        Assert.Equal([fix.Id, first.Id], history.Select(h => h.Id));                                   // nothing was replaced or removed
        Assert.Equal(0.71m, history[1].Rate);
        // Re-entering the effective value for that date is refused (no ambiguous duplicates).
        var dup = await Rates.EnterAsync(Admin.Id, Company.Id, await LatestId("USD"), new ExchangeRateInput("USD", 0.715m, new DateOnly(2026, 1, 1), null));
        Assert.Equal((ConfigStatus.Conflict, "rate_unchanged"), (dup.Status, dup.Code));
        Assert.Equal(2L, await Count(Company, Xr.Table));
    }

    [PostgresFact]
    public async Task A_posted_snapshot_is_unaffected_by_later_rate_maintenance()
    {
        await PrepareRates();
        await Enter("USD", "0.7100000000", "2026-01-01");
        var applied = (await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2026, 1, 10)))!;     // what a posting would store on its own lines
        var baseAmount = CurrencyMath.ConvertToBase(1000m, applied.Rate, 3);
        await Enter("USD", "0.8000000000", "2026-01-01", "later correction");
        await Enter("USD", "0.9000000000", "2026-01-05");
        // The stored snapshot still refers to its own immutable rate row and reproduces the same amount.
        var row = (await Rates.ListAsync(Company.Id, "USD", null, null, 100)).Single(r => r.Id == applied.RateId);
        Assert.Equal(applied.Rate, row.Rate);
        Assert.Equal(baseAmount, CurrencyMath.ConvertToBase(1000m, row.Rate, 3));
        Assert.Equal(710.000m, baseAmount);
    }

    [PostgresFact]
    public async Task Entering_a_rate_is_audited_with_one_history_row_and_one_event_and_invalid_input_writes_nothing()
    {
        await PrepareRates();
        var r = await Enter("USD", "0.7100000000", "2026-01-01", "central bank");
        Assert.Equal(1L, await Count(Company, Xr.Table));
        Assert.Equal(1L, await Count(Company, Audit_.Table) - Baseline);
        Assert.Equal(1L, await Count(Company, Lh.Table));
        Assert.Equal(r.Id, await Sql<long>($"SELECT {Lh.Revision} FROM {Lh.Table}"));
        Assert.Contains("0.7100000000", await Sql<string>($"SELECT {Lh.Changes}::text FROM {Lh.Table}"));
        Assert.Equal(Admin.Id, r.EnteredBy);
        Assert.Equal("central bank", r.Note);

        async Task<ConfigResult<ExchangeRateView>> Try(string? cur, decimal? rate, string date, long? expected = null) =>
            await Rates.EnterAsync(Admin.Id, Company.Id, expected ?? await LatestId("USD"), new ExchangeRateInput(cur, rate, DateOnly.Parse(date), null));
        Assert.Equal(ConfigStatus.Invalid, (await Try("USD", 0m, "2026-02-01")).Status);
        Assert.Equal(ConfigStatus.Invalid, (await Try("USD", 0.12345678901m, "2026-02-01")).Status);
        Assert.Equal("currency_is_base", (await Try("JOD", 1m, "2026-02-01", 0)).Code);              // the base currency is implicitly 1 and never stored
        Assert.Equal("currency_not_enabled", (await Try("SAR", 3.75m, "2026-02-01", 0)).Code);       // present but disabled
        Assert.Equal("currency_not_enabled", (await Try("XXX", 3.75m, "2026-02-01", 0)).Code);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Rates.EnterAsync(Admin.Id, Company.Id, null, new ExchangeRateInput("USD", 0.8m, new DateOnly(2026, 2, 1), null))).Status);
        Assert.Equal(1L, await Count(Company, Xr.Table));
        Assert.Equal(1L, await Count(Company, Audit_.Table) - Baseline);
    }

    [PostgresFact]
    public async Task Concurrent_entry_for_one_currency_lets_exactly_one_win_and_the_others_must_refresh()
    {
        await PrepareRates();
        await Enter("USD", "0.7100000000", "2026-01-01");
        var seen = await LatestId("USD");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Rates.EnterAsync(Admin.Id, Company.Id, seen, new ExchangeRateInput("USD", 0.72m + i / 100m, new DateOnly(2026, 2, 1), null))));
        Assert.Single(results, r => r.IsOk);
        Assert.Equal(5, results.Count(r => r.Status == ConfigStatus.PreconditionFailed));
        Assert.Equal(2L, await Count(Company, Xr.Table));
        // A stale guard is also refused sequentially, and reports the current guard value.
        var stale = await Rates.EnterAsync(Admin.Id, Company.Id, seen, new ExchangeRateInput("USD", 0.9m, new DateOnly(2026, 3, 1), null));
        Assert.Equal(ConfigStatus.PreconditionFailed, stale.Status);
        Assert.Equal(await LatestId("USD"), stale.CurrentRevision);
        // Another currency is independent.
        Ok(await Rates.EnterAsync(Admin.Id, Company.Id, 0, new ExchangeRateInput("EUR", 0.8m, new DateOnly(2026, 2, 1), null)));
    }
}

public class ExchangeRateDatabaseGuardTests : ExchangeRateTestBase
{
    [PostgresFact]
    public async Task Rates_cannot_be_changed_deleted_or_truncated_and_only_exist_for_enabled_foreign_currencies()
    {
        await PrepareRates();
        await Enter("USD", "0.7100000000", "2026-01-01");
        Assert.Equal("P0001", await RawFails($"UPDATE {Xr.Table} SET {Xr.Rate} = 1"));
        Assert.Equal("P0001", await RawFails($"DELETE FROM {Xr.Table}"));
        Assert.Equal("P0001", await RawFails($"TRUNCATE {Xr.Table}"));
        string Insert(string cur, string rate = "1", string date = "2026-01-01") =>
            $"INSERT INTO {Xr.Table} ({Xr.CurrencyCode}, {Xr.Rate}, {Xr.ValidFrom}, {Xr.EnteredAtUtc}, {Xr.EnteredBy}, {Xr.AuditStream}, {Xr.AuditSeq}) " +
            $"SELECT '{cur}', {rate}, '{date}', now(), gen_random_uuid(), {Xr.AuditStream}, {Xr.AuditSeq} FROM {Xr.Table} LIMIT 1";
        Assert.Equal("23505", await RawFails(Insert("USD")));                                         // a row cannot reuse another entry's audit event
        Assert.Equal("P0001", await RawFails(Insert("JOD")));                                         // base currency
        Assert.Equal("P0001", await RawFails(Insert("SAR")));                                         // disabled
        Assert.Equal("P0001", await RawFails(Insert("QQQ")));                                         // unknown (not a company currency)
        Assert.Equal("23514", await RawFails(Insert("USD", "0")));
        Assert.Equal("23514", await RawFails(Insert("USD", "-1")));
        Assert.Equal("23514", await RawFails(Insert("USD", "1", "1999-12-31")));
        Assert.Equal(1L, await Count(Company, Xr.Table));
    }

    [PostgresFact]
    public async Task The_sql_resolver_agrees_with_the_service_and_the_numeric_precision_is_exact()
    {
        await PrepareRates();
        var a = await Enter("USD", "0.7123456789", "2026-01-01");
        var b = await Enter("USD", "1234.5678901234", "2026-02-01");
        Assert.Equal(a.Id, await Sql<long>($"SELECT {CompanySchema.ExchangeRate.ResolveFunction}('USD', DATE '2026-01-31')"));
        Assert.Equal(b.Id, await Sql<long>($"SELECT {CompanySchema.ExchangeRate.ResolveFunction}('USD', DATE '2026-02-01')"));
        Assert.True(await Sql<bool>($"SELECT {CompanySchema.ExchangeRate.ResolveFunction}('USD', DATE '2025-01-01') IS NULL"));
        Assert.Equal(0.7123456789m, (await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2026, 1, 2)))!.Rate);
        Assert.Equal(1234.5678901234m, (await Rates.ResolveAsync(Company.Id, "USD", new DateOnly(2026, 2, 2)))!.Rate);
    }
}

public class ExchangeRateAccessTests : ExchangeRateTestBase
{
    private const string Base = "/api/v1/company/exchange-rates";

    [PostgresFact]
    public async Task Viewing_and_entering_rates_are_separate_company_wide_permissions_held_only_by_the_administrator_preset()
    {
        await PrepareRates();
        var roles = (await Rbac.ListRolesAsync(Admin.Id, Company.Id)).Value!;
        var admin = roles.Single(r => r.PresetKey == "company-administrator");
        Assert.Contains("company.exchangerates.view", admin.Permissions);
        Assert.Contains("company.exchangerates.create", admin.Permissions);
        Assert.DoesNotContain(roles.Where(r => r.PresetKey != "company-administrator").SelectMany(r => r.Permissions), p => p.StartsWith("company.exchangerates"));
        var (viewer, _) = await Holder(PermissionCatalog.Codes.ExchangeRatesView);
        var (enterer, _) = await Holder(PermissionCatalog.Codes.ExchangeRatesCreate);
        var input = new ExchangeRateInput("USD", 0.71m, new DateOnly(2026, 1, 1), null);
        Assert.Equal(2, Ok(await XMgmt.OverviewAsync(viewer.Id, Company.Id)).Currencies.Count);
        Assert.Empty(Ok(await XMgmt.ListAsync(viewer.Id, Company.Id, null, null, null, 50)));
        Assert.Equal(ConfigStatus.Forbidden, (await XMgmt.EnterAsync(viewer.Id, Company.Id, 0, input)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await XMgmt.OverviewAsync(enterer.Id, Company.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await XMgmt.ResolveAsync(enterer.Id, Company.Id, "USD", new DateOnly(2026, 1, 1))).Status);
        Assert.Equal("USD", Ok(await XMgmt.EnterAsync(enterer.Id, Company.Id, 0, input)).CurrencyCode);
        Assert.Equal(1L, await Count(Company, Xr.Table));
    }

    [PostgresFact]
    public async Task The_endpoints_need_authentication_use_if_match_and_return_exact_rates_and_clear_missing_rate_errors()
    {
        await StartApi();
        BuildRateFacade();
        await InCompany(Company.PhysicalDatabaseName, $"INSERT INTO {Cc.Table} ({Cc.Code}, {Cc.IsBase}, {Cc.Enabled}, {Cc.Revision}, {Cc.UpdatedAtUtc}) VALUES ('JOD', true, true, 1, now()), ('USD', false, true, 1, now())");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, Base)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, Base, new { })).Status);
        var cookie = await SignIn(Admin);
        var o = await Send(HttpMethod.Get, Base, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, o.Status);
        Assert.Contains("no-store", o.Headers.CacheControl!.ToString());
        Assert.Equal("base_per_foreign_unit", o.Json.GetProperty("direction").GetString());
        Assert.Equal("JOD", o.Json.GetProperty("baseCurrency").GetString());
        Assert.Equal(0, o.Json.GetProperty("currencies")[0].GetProperty("latestId").GetInt64());
        Assert.True(o.Json.GetProperty("can").GetProperty("create").GetBoolean());
        var body = new { currencyCode = "USD", rate = 0.71m, validFrom = "2026-01-01", note = "manual" };
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, Base, body, cookie)).Status);
        var created = await Send(HttpMethod.Post, Base, body, cookie, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("0.7100000000", created.Json.GetProperty("rate").GetString());
        Assert.Equal((HttpStatusCode)412, (await Send(HttpMethod.Post, Base, body with { rate = 0.72m }, cookie, ifMatch: "\"0\"")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, Base, body with { rate = -1m }, cookie, ifMatch: $"\"{created.Json.GetProperty("id").GetInt64()}\"")).Status);
        var resolved = await Send(HttpMethod.Get, $"{Base}/resolve?currency=USD&date=2026-05-01", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, resolved.Status);
        Assert.Equal(created.Json.GetProperty("id").GetInt64(), resolved.Json.GetProperty("rateId").GetInt64());
        var missing = await Send(HttpMethod.Get, $"{Base}/resolve?currency=USD&date=2025-05-01", cookie: cookie);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}/resolve?currency=USD", cookie: cookie)).Status);
        var history = await Send(HttpMethod.Get, $"{Base}/history?currency=USD", cookie: cookie);
        Assert.Equal(1, history.Json.GetProperty("rates").GetArrayLength());
    }
}
