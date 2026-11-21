using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.Pricing;
using Hasm.Infrastructure.Taxes;
using CD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocument;
using CDA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentApproval;
using CDS = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentSnapshot;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CommercialDocumentLine;
using XR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ExchangeRate;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.13 slice S1b, snapshots (SRS 6.3, 8.4, 19.8; Q19 default, D-057): the price, tax and rate of a document are PRODUCED BY THE SERVER at submit (the client sends none), re-produced at approval and refused with
/// <c>pricing_changed</c> on any difference, and sealed at approval: later price, tax or rate changes never alter them. No cost and no margin in this slice (S3). Disposable cluster only.
/// </summary>
public class CommercialSnapshotTests : CommercialTestBase
{
    private Task<Guid> Approver() => NewApprover();
    private static readonly DateOnly Jan = new(2026, 1, 1);

    [PostgresFact]
    public async Task The_server_produces_the_price_tax_and_rate_at_submit_and_the_snapshots_match_an_independent_quote()
    {
        await Prepare();
        var vat = Ok(await Tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput("VAT", "ضريبة", "VAT")));
        Ok(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(Jan, TaxTreatment.Standard, 16m)));
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, new TaxRuleInput(CommercialTestType.Name, CommercialDefaults.TaxOperation, null, null, vat.Id, 10, [], Jan, null)));
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("SN1");
        var draft = await NewDraft(branch, party, item, unit, "snap-key-00001", qty: 3m);
        Assert.All(draft.Lines, l => { Assert.Null(l.LineNet); Assert.Null(l.PricingHash); });                                  // a draft carries no price or tax
        Assert.Null(draft.TotalGross);

        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, draft.Id, draft.Revision));
        var line = Assert.Single(s.Lines);
        Assert.Equal((30m, 30m, 4.8m, 34.8m, 1m), (line.LineNet!.Value, s.TotalNet!.Value, s.TotalTax!.Value, s.TotalGross!.Value, s.ExchangeRate!.Value));   // 3 x 10, 16 % tax, base currency rate 1
        Assert.Matches("^[0-9a-f]{64}$", line.PricingHash!);
        Assert.Matches("^[0-9a-f]{64}$", s.Fingerprint!);
        var independent = Ok(await Pricing.QuoteAsync(Company.Id, new PricingRequest(Day, "JOD", item, null, unit, 3m, party), null));
        Assert.Equal(independent.ContentHash, line.PricingHash);                                                                // the stored snapshot IS the server's quote of the same inputs
        Assert.Equal(PricingSnapshots.CurrentVersion, independent.Version.StartsWith("pricing-snapshot-v1") ? PricingSnapshots.CurrentVersion : independent.Version);

        // the stored values are the server's: the snapshot JSON in PostgreSQL carries the same hash and the engine's results
        var json = await Sql<string>($"SELECT {CDS.PricingSnapshot} FROM {CL.Table} WHERE {CL.DocumentId} = '{draft.Id}'");
        Assert.Contains(independent.ContentHash, json);
        Assert.Contains("34.8", await Sql<string>($"SELECT {CDA.TaxSnapshot} FROM {CD.Table} WHERE {CD.Id} = '{draft.Id}'"));
        Assert.Equal("base", await Sql<string>($"SELECT {CDA.ExchangeRateRef} FROM {CD.Table} WHERE {CD.Id} = '{draft.Id}'"));   // a base-currency document names no rate record

        // a foreign-currency document takes the dated rate of ITS date; no rate is a refusal, never a guess
        var usdList = Ok(await Pricing.CreatePriceListAsync(Admin.Id, Company.Id, new PriceListInput("PL-USD", "قائمة", "USD list", 10, "USD", Jan, null, true, null)));
        Ok(await Pricing.CreateEntryAsync(Admin.Id, Company.Id, usdList.Id, new PriceEntryInput(item, null, unit, 0m, 14m, null)));
        var usd = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "snap-key-00002", qty: 1m) with { Currency = "usd" }));
        var su = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, usd.Id, usd.Revision));
        Assert.Equal(0.71m, su.ExchangeRate);
        var usdRate = await Sql<long>($"SELECT {XR.Id} FROM {XR.Table} WHERE {XR.CurrencyCode} = 'USD' AND {XR.ValidFrom} <= '{Day:yyyy-MM-dd}' ORDER BY {XR.ValidFrom} DESC, {XR.Id} DESC LIMIT 1");   // the rate record effective on the document date
        Assert.Equal($"rate:{usdRate}", await Sql<string>($"SELECT {CDA.ExchangeRateRef} FROM {CD.Table} WHERE {CD.Id} = '{usd.Id}'"));   // the rate is traceable to the exact dated rate record it came from
        Assert.Equal(14m, su.Lines[0].LineNet);
        var eur = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "snap-key-00003") with { Currency = "eur" }));
        Invalid(await Docs.SubmitAsync(Admin.Id, Company.Id, eur.Id, eur.Revision), "currency", "exchange_rate_missing");
        // an item nobody can price is refused with the engine's code on its line (never an implicit zero)
        var (unpriced, unpricedUnit) = await NewItemAndUnit("SN2");
        await Sql($"UPDATE {Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceEntry.Table} SET {Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceEntry.IsActive} = false, {Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceEntry.Revision} = {Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceEntry.Revision} + 1 WHERE {Hasm.Infrastructure.CompanyDatabase.CompanySchema.PriceEntry.Id} = '{Prices[unpriced].Entry}'");
        var noPrice = await NewDraft(branch, party, unpriced, unpricedUnit, "snap-key-00004");
        Invalid(await Docs.SubmitAsync(Admin.Id, Company.Id, noPrice.Id, noPrice.Revision), "lines[0]", "no_price");
        Assert.Equal(CommercialStatus.Draft, (await Docs.GetAsync(Company.Id, noPrice.Id))!.Status);                              // nothing changed
    }

    [PostgresFact]
    public async Task The_approval_re_quotes_refuses_a_changed_price_and_seals_the_snapshot_so_later_changes_never_alter_it()
    {
        await Prepare();
        var approver = await Approver();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("SN3");
        var draft = await NewDraft(branch, party, item, unit, "snap-key-00011", qty: 2m);
        var s = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, draft.Id, draft.Revision));
        Assert.Equal(20m, s.TotalGross);
        var fingerprint = s.Fingerprint;

        // the price list changes between the submit and the approval: the server prices again and refuses the difference
        Ok(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, Prices[item].Entry, 1, new PriceEntryInput(null, null, null, null, 12m, null)));
        Fails(await Docs.ApproveAsync(approver, Company.Id, draft.Id, s.Revision), ConfigStatus.Conflict, "pricing_changed");
        var unchanged = (await Docs.GetAsync(Company.Id, draft.Id))!;
        Assert.Equal((CommercialStatus.Submitted, s.Revision, fingerprint), (unchanged.Status, unchanged.Revision, unchanged.Fingerprint));   // nothing changed
        Assert.Null(unchanged.OfficialNumber);
        // withdrawal drops the quote (a draft carries none); the resubmit prices at the NEW price
        var back = Ok(await Docs.WithdrawAsync(Admin.Id, Company.Id, draft.Id, s.Revision));
        Assert.Null(back.Fingerprint); Assert.Null(back.TotalNet); Assert.Null(back.ExchangeRate);
        Assert.All(back.Lines, l => { Assert.Null(l.LineNet); Assert.Null(l.PricingHash); });
        var s2 = Ok(await Docs.SubmitAsync(Admin.Id, Company.Id, draft.Id, back.Revision));
        Assert.Equal(24m, s2.TotalGross);
        Assert.NotEqual(fingerprint, s2.Fingerprint);
        // PostgreSQL: an approval whose sealed fingerprint is not the submitted one is refused for a direct statement
        string Raw(string fp) => $"UPDATE {CD.Table} SET {CD.Status} = 3, {CDA.ApprovedBy} = '{approver}', {CDA.ApprovedAtUtc} = now(), {CDA.SealedAtUtc} = now(), {CDA.OfficialNumber} = 'RAW-1', {CDA.FiscalYearId} = (SELECT {Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear.Id} FROM {Hasm.Infrastructure.CompanyDatabase.CompanySchema.FiscalYear.Table} LIMIT 1), {CDA.Fingerprint} = '{fp}', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{draft.Id}'";
        Assert.Equal("P0001", await Fails(Raw(new string('c', 64))));
        var approved = Ok(await Docs.ApproveAsync(approver, Company.Id, draft.Id, s2.Revision));
        Assert.Equal(s2.Fingerprint, approved.Fingerprint);
        Assert.NotNull(approved.SealedAtUtc);
        var sealedNet = approved.Lines[0].LineNet; var sealedHash = approved.Lines[0].PricingHash; var sealedGross = approved.TotalGross;

        // sealed: later price, tax and rate changes never alter or invalidate the document's values
        Ok(await Pricing.UpdateEntryAsync(Admin.Id, Company.Id, Prices[item].Entry, 2, new PriceEntryInput(null, null, null, null, 99m, null)));
        var vat = Ok(await Tax.CreateTaxAsync(Admin.Id, Company.Id, new TaxInput("VAT2", "ضريبة", "VAT2")));
        Ok(await Tax.AddRateAsync(Admin.Id, Company.Id, vat.Id, new TaxRateInput(Jan, TaxTreatment.Standard, 50m)));
        Ok(await Tax.CreateRuleAsync(Admin.Id, Company.Id, new TaxRuleInput(CommercialTestType.Name, CommercialDefaults.TaxOperation, null, null, vat.Id, 10, [], Jan, null)));
        var after = (await Docs.GetAsync(Company.Id, draft.Id))!;
        Assert.Equal((sealedNet, sealedHash, sealedGross), (after.Lines[0].LineNet, after.Lines[0].PricingHash, after.TotalGross));
        // the sealed values are immutable in PostgreSQL (lines are locked, the header changes only its note)
        Assert.Equal("P0001", await Fails($"UPDATE {CL.Table} SET {CDS.LineNet} = 1, {CDS.PricingHash} = '{new string('d', 64)}', {CDS.PricingSnapshot} = 'x' WHERE {CL.DocumentId} = '{draft.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CDA.TotalGross} = 1, {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{draft.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CDA.TaxHash} = '{new string('e', 64)}', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{draft.Id}'"));
        // a draft carries no snapshot: a direct statement cannot plant one (PostgreSQL)
        var plain = await NewDraft(branch, party, item, unit, "snap-key-00012");
        Assert.Equal("P0001", await Fails($"UPDATE {CD.Table} SET {CDA.Fingerprint} = '{new string('f', 64)}', {CD.Revision} = {CD.Revision} + 1 WHERE {CD.Id} = '{plain.Id}'"));
    }
}
