using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Parties;
using Hasm.Infrastructure.Tests;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using PR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PartyRequest;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.9C slice 2: the party import adapter end to end against real PostgreSQL: real CSV files, the real adapter, the real party service, independent approval and the atomic application. Every created party
/// is a pending local party with a queued shared-identity proposal (registry protocol of 3.9A preserved; nothing is delivered inside the import). Disposable cluster only.
/// </summary>
public class PartyImportTests : ImportTestBase
{
    private PartyService Parties = null!;
    private FailingAdapter Failing = null!;

    private const string Header = "code,display_name,notes,kind,legal_name_ar,legal_name_en,tax_number,registration_number\n";
    private static JsonObject MapOf(params string[] fields) => new() { ["fields"] = new JsonObject(fields.Select((f, i) => KeyValuePair.Create(f, (JsonNode?)new JsonObject { ["column"] = i }))), ["options"] = new JsonObject { ["decimalSeparator"] = ".", ["thousandsSeparator"] = ",", ["dateFormat"] = "yyyy-MM-dd" } };
    private static readonly JsonObject Map = MapOf("code", "display_name", "notes", "kind", "legal_name_ar", "legal_name_en", "tax_number", "registration_number");

    /// <summary>Wraps the real adapter; when told to, fails AFTER the real adapter has created everything, to prove the whole application rolls back.</summary>
    private sealed class FailingAdapter(IImportAdapter inner) : IImportAdapter
    {
        public bool Fail;
        public string Code => inner.Code; public int Version => inner.Version; public string NameAr => inner.NameAr; public string NameEn => inner.NameEn;
        public string UploadPermission => inner.UploadPermission; public string ViewPermission => inner.ViewPermission;
        public IReadOnlyList<ImportField> Fields => inner.Fields; public IReadOnlyList<string> Resolutions => inner.Resolutions; public bool Atomic => inner.Atomic;
        public Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct) => inner.AnalyzeAsync(context, rows, ct);
        public Task BeginApplyAsync(CompanyAuditedWork work, CancellationToken ct) => inner.BeginApplyAsync(work, ct);
        public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
        {
            var result = await inner.ApplyAsync(context, ct);
            if (Fail) throw new ImportApplyException("simulated_failure", "simulated failure after the parties were created");
            return result;
        }
    }

    private async Task Prepare()
    {
        await PrepareRbac();
        var registry = new PartyRegistryStore(Options.BuildSystemConnectionString(admin: true), Audit, Clock);
        Parties = new PartyService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), registry, Clock);
        Failing = new FailingAdapter(new PartyImportAdapter(Parties));
        BuildImportFacade(adapters: [Failing]);
    }

    private async Task<ImportBatchView> Validate(string rows)
    {
        var u = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, PartyImportAdapter.AdapterCode, "parties.csv", Bytes(Header + rows), null));
        var s = Ok(await Imports.StageAsync(Admin.Id, Company.Id, u.Id, u.Revision, new StageInput(0, 1, "utf-8", null)));
        var m = Ok(await Imports.SetMappingAsync(Admin.Id, Company.Id, s.Id, s.Revision, Map));
        return Ok(await Imports.DryRunAsync(Admin.Id, Company.Id, m.Id, m.Revision));
    }

    private async Task<ImportBatchView> ApproveAndApply(ImportBatchView validated)
    {
        var sub = Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, validated.Id, validated.Revision));
        var (user, _) = await Member([PermissionCatalog.Codes.PartiesView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(user.Login, Company.Id));
        var a = Ok(await Imports.DecideAsync(user.Id, Company.Id, sub.Id, sub.Revision, true, null));
        return Ok(await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision));
    }

    private async Task<Dictionary<int, ImportRowView>> RowMap(Guid id) => (await Imports.RowsAsync(Company.Id, id, 0, 500, null))!.Rows.ToDictionary(r => r.RowNo);
    private static string[] Codes(ImportRowView row) => row.Issues is JsonArray a ? [.. a.Select(i => i!["code"]!.GetValue<string>())] : [];
    private async Task<long> Count(string table) => await Sql<long>($"SELECT count(*) FROM {table}");

    [PostgresFact]
    public async Task Parties_are_created_through_the_party_service_and_each_queues_its_shared_identity_proposal_without_delivering_it()
    {
        await Prepare();
        var v = await Validate(
            "cus-1,شركة الأمل,VIP,منشأة,شركة الأمل التجارية,Al Amal Trading,300-1234,CR/55\n" +
            "SUP-2,أحمد,,individual,أحمد علي,,,\n" +
            "0007,مؤسسة الزهرة,,organization,مؤسسة الزهرة,,,\n");
        Assert.True(v.Plan!["canApply"]!.GetValue<bool>(), v.Plan.ToJsonString());
        Assert.Equal(0L, await Count(LP.Table));                                                                                    // the dry run changed nothing
        var applied = await ApproveAndApply(v);
        Assert.Equal((ImportStatus.Applied, 3), (applied.Status, applied.Result!["summary"]!["created"]!.GetValue<int>()));

        var list = (await Parties.ListAsync(Company.Id, new PartyFilter(null, null, 0, 50))).Items.ToDictionary(x => x.Code);
        Assert.Equal(["0007", "CUS-1", "SUP-2"], list.Keys.Order());                                                                // codes upper-cased, leading zeros kept
        var cus = (await Parties.GetAsync(Company.Id, list["CUS-1"].Id))!;
        Assert.Equal(("شركة الأمل", "VIP", true), (cus.DisplayName, cus.Notes, cus.IsActive));
        Assert.Equal(1, cus.LinkState);                                                                                              // pending: no shared identity is linked by the import
        Assert.Null(cus.GlobalPartyId);
        var open = Assert.IsType<PartyRequestView>(cus.OpenRequest);
        Assert.Equal(((int)PartyRequestStatus.Pending, "شركة الأمل التجارية", "Al Amal Trading", "300-1234", "CR/55", (int)PartyKind.Organization),
            (open.Status, open.LegalNameAr, open.LegalNameEn, open.TaxNumber, open.RegistrationNumber, open.PartyKind));              // identity normalised by the same rules as a manual create
        Assert.Equal(3L, await Count(PR.Table));                                                                                    // one queued proposal per party, none delivered
        Assert.Equal(3L, await Sql<long>($"SELECT count(*) FROM {PR.Table} WHERE {PR.Status} = 1 AND {PR.Attempts} = 0"));
    }

    [PostgresFact]
    public async Task Invalid_rows_block_the_batch_and_existing_codes_are_exact_matches_or_skip_only_conflicts()
    {
        await Prepare();
        Assert.Equal(ImportStatus.Applied, (await ApproveAndApply(await Validate("A1,Alpha,n,organization,ألفا,,,\n"))).Status);

        var v = await Validate(
            "A1,Alpha,n,organization,ألفا,,,\n" +                  // identical local fields: exact, nothing to do
            "A1X,Beta,,organization,بيتا,,,\n" +                    // valid
            "B 2!,Bad code,,organization,س,,,\n" +                  // invalid code
            "C3,Kind,,thing,س,,,\n" +                               // unknown kind
            "D4,NoLegal,,individual,,,,\n" +                        // legal name missing
            "E5,Tax,,organization,ت,,bad tax!,\n");                 // invalid tax identifier
        var rows = (await RowMap(v.Id)).OrderBy(k => k.Key).Select(k => k.Value).ToArray();                                       // in file order
        Assert.Equal(6, rows.Length);
        Assert.Equal((short)RowOutcome.Exact, rows[0].Status);
        Assert.Equal((short)RowOutcome.Valid, rows[1].Status);
        Assert.All(rows.Skip(2), r => Assert.Equal((short)RowOutcome.Invalid, r.Status));
        Assert.Contains("invalid_code", Codes(rows[2]));
        Assert.Contains("invalid_value", Codes(rows[3]));
        Assert.Contains("required", Codes(rows[4]));
        Assert.Contains("invalid_identifier", Codes(rows[5]));
        Assert.False(v.Plan!["canApply"]!.GetValue<bool>());                                                                         // nothing can be applied until the file is corrected
        Assert.Equal(1L, await Count(LP.Table));

        // same code, different display name: a conflict whose only resolution is skip (an import never overwrites a party)
        var c = await Validate("A1,Alpha changed,n,organization,ألفا,,,\n");
        var row = (await RowMap(c.Id)).Values.Single();
        Assert.Equal((short)RowOutcome.Conflict, row.Status);
        Assert.Contains("record_exists_with_differences", Codes(row));
        Assert.False(c.Plan!["canApply"]!.GetValue<bool>());
    }

    [PostgresFact]
    public async Task A_failure_during_the_application_rolls_back_every_party_and_every_queued_request()
    {
        await Prepare();
        Failing.Fail = true;
        var v = await Validate("F1,One,,organization,واحد,,,\nF2,Two,,individual,اثنان,,,\n");
        var sub = Ok(await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, v.Revision));
        var (user, _) = await Member([PermissionCatalog.Codes.PartiesView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantImportApproverOfflineAsync(user.Login, Company.Id));
        var a = Ok(await Imports.DecideAsync(user.Id, Company.Id, sub.Id, sub.Revision, true, null));
        var result = await IMg.ApplyAsync(Admin.Id, Company.Id, a.Id, a.Revision);
        Assert.NotEqual(ImportStatus.Applied, result.IsOk ? result.Value!.Status : (ImportStatus?)null);
        Assert.Equal(0L, await Count(LP.Table));
        Assert.Equal(0L, await Count(PR.Table));
    }

    [PostgresFact]
    public async Task A_code_taken_between_the_dry_run_and_the_application_stops_the_batch_without_a_partial_result()
    {
        await Prepare();
        var v = await Validate("G1,One,,organization,واحد,,,\nG2,Two,,organization,اثنان,,,\n");
        Ok(await Parties.CreateAsync(Admin.Id, Company.Id, new LocalPartyInput("G2", "Taken", null, true, new SharedIdentityInput(1, "مأخوذ", null, null, null))));   // another user creates G2 first
        var submit = await Imports.SubmitAsync(Admin.Id, Company.Id, v.Id, v.Revision);                                            // the baseline (the existing codes) changed: the plan is stale
        Assert.False(submit.IsOk);
        Assert.Equal("plan_stale", submit.Code);
        Assert.Equal(1L, await Count(LP.Table));                                                                                    // only the manually created G2: G1 was not created
    }
}
