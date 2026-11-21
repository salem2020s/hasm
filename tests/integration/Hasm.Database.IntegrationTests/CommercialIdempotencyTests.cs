using Hasm.Infrastructure.Commercial;
using Hasm.Infrastructure.CompanyConfiguration;

namespace Hasm.Database.IntegrationTests;

/// <summary>Phase 3.13 slice S1a, create idempotency (SRS 19.2): the same key and payload return the original, another payload is refused, the key scope is (type, creator), and simultaneous creates produce one document. Disposable cluster only.</summary>
public class CommercialIdempotencyTests : CommercialTestBase
{
    [PostgresFact]
    public async Task A_repeated_create_returns_the_original_a_different_payload_is_refused_and_the_key_is_scoped_to_the_creator()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("ID1");
        var first = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-key-0001")));
        Assert.False(first.Replayed);
        var again = Ok(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-key-0001")));
        Assert.True(again.Replayed);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1L, await DocCount());
        Assert.Equal(1L, await Events(CommercialAuditEvents.Created));                                                         // the replay wrote nothing
        Fails(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-key-0001", qty: 3m)), ConfigStatus.Conflict, "idempotency_key_reused");
        Fails(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-key-0001", note: "other")), ConfigStatus.Conflict, "idempotency_key_reused");
        Assert.Equal(1L, await DocCount());

        var another = Guid.NewGuid();                                                                                          // another creator may use the same key text: it is a different (type, creator, key)
        var theirs = Ok(await Docs.CreateAsync(another, Company.Id, Doc(branch, party, item, unit, "idem-key-0001")));
        Assert.False(theirs.Replayed);
        Assert.NotEqual(first.Id, theirs.Id);
        Assert.Equal(2L, await DocCount());

        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "short")), "idempotencyKey", "idempotency_key_invalid");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-key-0002") with { DocType = "sales_invoice" }), "docType", "document_type_not_registered");   // production registers no type (Q1)
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, Guid.NewGuid(), item, unit, "idem-key-0003")), "partyId", "party_role_required");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(Guid.NewGuid(), party, item, unit, "idem-key-0004")), "branchId", "branch_not_found");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, Guid.NewGuid(), unit, "idem-key-0005")), "reference", "reference_not_found");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-key-0006") with { Mode = 3 }), "mode", "invalid_value");
        Invalid(await Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-key-0007") with { Mode = null }), "mode", "required");
        Assert.Equal(2L, await DocCount());                                                                                     // refused creates leave nothing
    }

    [PostgresFact]
    public async Task Eight_simultaneous_creates_with_one_key_produce_exactly_one_document()
    {
        await Prepare();
        var branch = await NewBranchId("BA"); var party = await NewCustomerId("C1"); var (item, unit) = await NewItemAndUnit("ID2");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-race-0001"))));
        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Status} {r.Code}"));
        Assert.Single(results.Select(r => r.Value!.Id).Distinct());
        Assert.Equal(1, results.Count(r => !r.Value!.Replayed));                                                               // exactly one created it, the others returned it
        Assert.Equal(1L, await DocCount());
        Assert.Equal(1L, await Events(CommercialAuditEvents.Created));
        var conflict = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Docs.CreateAsync(Admin.Id, Company.Id, Doc(branch, party, item, unit, "idem-race-0002", qty: 1m + i))));
        Assert.Equal(1, conflict.Count(r => r.IsOk));                                                                           // different payloads under one key: one wins, the rest are refused
        Assert.Equal(3, conflict.Count(r => r.Code == "idempotency_key_reused"));
        Assert.Equal(2L, await DocCount());
    }
}
