using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Taxes;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Release 1 acceptance C (invariants under concurrency): N writers holding the SAME revision of a master entity race; exactly one
/// wins and every other writer gets the stale-revision refusal, nothing is lost silently (the stored row is the winner's, one revision
/// higher), and exactly one audit row is written. Sequential stale updates are covered by each entity's own tests; parallel
/// posting and import application by JournalTests and ImportApprovalTests.
/// </summary>
internal static class Race
{
    public const int Writers = 8;

    /// <summary>Runs the writers at the same time and returns the winner's index; fails unless exactly one succeeded and all others were refused as stale.</summary>
    public static async Task<int> OneWinner<T>(Func<int, Task<ConfigResult<T>>> write)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, Writers).Select(i => Task.Run(async () => { await gate.Task; return await write(i); })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        var winners = results.Select((r, i) => (r, i)).Where(x => x.r.IsOk).Select(x => x.i).ToList();
        Assert.True(winners.Count == 1, "winners: " + string.Join(", ", results.Select(r => $"{r.Status}/{r.Code}")));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ConfigStatus.PreconditionFailed, r.Status));
        return winners[0];
    }
}

public class AccountLostUpdateTests : ChartTestBase
{
    [PostgresFact]
    public async Task Parallel_updates_from_one_revision_keep_exactly_the_winners_change()
    {
        await PrepareChart();
        var a = await NewChild(await NewRoot("1"), "101");
        var audit = await Count(Company, Audit_.Table);
        var winner = await Race.OneWinner(i => Chart().UpdateDetailsAsync(Admin.Id, Company.Id, a.Id, 1, new AccountDetailsInput("حساب", $"Writer {i}", null, null, null, null)));
        var stored = (await Chart().GetAsync(Company.Id, a.Id))!;
        Assert.Equal((2L, $"Writer {winner}"), (stored.Revision, stored.NameEn));
        Assert.Equal(audit + 1, await Count(Company, Audit_.Table));
    }
}

public class BranchLostUpdateTests : BranchTestBase
{
    [PostgresFact]
    public async Task Parallel_updates_from_one_revision_keep_exactly_the_winners_change()
    {
        await Prepare();
        var b = await Make("LU");
        var audit = await Count(Company, Audit_.Table);
        var winner = await Race.OneWinner(i => Branches().UpdateAsync(Admin.Id, Company.Id, b.Id, 1, NewBranch("LU", en: $"Writer {i}")));
        var stored = (await Branches().GetAsync(Company.Id, b.Id))!;
        Assert.Equal((2L, $"Writer {winner}"), (stored.Revision, stored.Data.NameEn));
        Assert.Equal(audit + 1, await Count(Company, Audit_.Table));
    }
}

public class WarehouseLostUpdateTests : WarehouseTestBase
{
    [PostgresFact]
    public async Task Parallel_updates_from_one_revision_keep_exactly_the_winners_change()
    {
        await Prepare();
        var w = await MakeWarehouse("LU");
        var audit = await Count(Company, Audit_.Table);
        var winner = await Race.OneWinner(i => Warehouses().UpdateAsync(Admin.Id, Company.Id, w.Id, 1, NewWarehouse("LU", w.BranchId, en: $"Writer {i}")));
        var stored = (await Warehouses().GetAsync(Company.Id, w.Id))!;
        Assert.Equal((2L, $"Writer {winner}"), (stored.Revision, stored.Data.NameEn));
        Assert.Equal(audit + 1, await Count(Company, Audit_.Table));
    }
}

public class PartyLostUpdateTests : PartyTestBase
{
    [PostgresFact]
    public async Task Parallel_local_updates_from_one_revision_keep_exactly_the_winners_change()
    {
        await PrepareParties();
        var p = await NewParty("LU-1");
        var winner = await Race.OneWinner(i => PMgmt.UpdateAsync(Admin.Id, Company.Id, p.Id, p.Revision, new LocalPartyInput("LU-1", $"Writer {i}", null, true)));
        var stored = Ok(await PMgmt.GetAsync(Admin.Id, Company.Id, p.Id));
        Assert.Equal((p.Revision + 1, $"Writer {winner}"), (stored.Revision, stored.DisplayName));
    }
}

public class PriceListLostUpdateTests : InventoryTestBase
{
    [PostgresFact]
    public async Task Parallel_updates_from_one_revision_keep_exactly_the_winners_change()
    {
        await PrepareInventory();
        var pricing = new PricingConfigService(Resolver, new Hasm.Infrastructure.CompanyDatabase.CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        PriceListInput List(int priority) => new("RETAIL", "تجزئة", "Retail", priority, "JOD", new DateOnly(2026, 1, 1), null, true, null);
        var list = Ok(await pricing.CreatePriceListAsync(Admin.Id, Company.Id, List(1)));
        var winner = await Race.OneWinner(i => pricing.UpdatePriceListAsync(Admin.Id, Company.Id, list.Id, 1, List(10 + i)));
        var stored = (await pricing.GetPriceListAsync(Company.Id, list.Id))!;
        Assert.Equal((2L, 10 + winner), (stored.Revision, stored.Priority));
    }
}

public class TaxLostUpdateTests : InventoryTestBase
{
    [PostgresFact]
    public async Task Parallel_updates_from_one_revision_keep_exactly_the_winners_change()
    {
        await PrepareInventory();
        var tax = new TaxConfigService(Resolver, new Hasm.Infrastructure.CompanyDatabase.CompanyAuditedTransactions(Resolver, Audit, Clock), Clock);
        var vat = Ok(await tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput("VAT", "ضريبة", "Tax")));
        var winner = await Race.OneWinner(i => tax.UpdateTaxAsync(Admin.Id, Company.Id, vat.Id, 1, new TaxInput(null, "ضريبة", $"Writer {i}")));
        var stored = (await tax.GetTaxAsync(Company.Id, vat.Id))!;
        Assert.Equal((2L, $"Writer {winner}"), (stored.Revision, stored.NameEn));
    }
}
