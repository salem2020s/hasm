using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Parties;
using Npgsql;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// Party import (Phase 3.9C slice 2): creates company-local parties on the generic import framework. Every created party is a normal pending local party and queues the proposal of its SHARED identity in
/// the same transaction (<see cref="PartyService.CreateInWorkAsync"/>: the same writes, validation and audit events as creating one by hand), so the registry protocol of Phase 3.9A is preserved: the shared
/// identity is created, matched or refused by the central approval, never by the import. Delivery of the queued proposals happens through the ordinary party synchronization after the batch is applied
/// (nothing is delivered inside the import transaction, so the batch stays all-or-nothing).
/// <para><b>What an import can do:</b> CREATE new local parties or explicitly SKIP a row. It never overwrites, merges, re-codes, activates or deactivates an existing party and never links or changes a shared
/// identity (the same owner decision as the other adapters). A code that exists with the same display name and notes is an <i>exact</i> match (nothing to do; the shared identity is not compared, it lives in
/// the registry); a code that exists with differences is a <i>conflict</i> (resolution: skip). Customer/supplier roles, terms, credit limits and representatives are NOT imported here (they are separate
/// audited records with their own permissions).</para>
/// </summary>
public sealed class PartyImportAdapter(PartyService parties) : IImportAdapter
{
    public const string AdapterCode = "parties";
    public string Code => AdapterCode;
    public int Version => 1;
    public string NameAr => "الأطراف (العملاء والموردون)";
    public string NameEn => "Parties (customers and suppliers)";
    public string UploadPermission => PermissionCatalog.Codes.PartiesCreate;
    public string ViewPermission => PermissionCatalog.Codes.PartiesView;
    public bool Atomic => true;
    public IReadOnlyList<string> Resolutions => [ImportResolutions.Skip];

    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("code", "رمز الطرف", "Party code", ImportFieldType.Code, Required: true, MaxLength: 30, IsKey: true, UpperCase: true, Aliases: ["party", "code", "party code", "الرمز", "رمز الطرف", "كود الطرف"]),
        new("display_name", "الاسم المعروض", "Display name", ImportFieldType.Text, Required: true, MaxLength: 200, Aliases: ["name", "display name", "الاسم", "الاسم المعروض"]),
        new("notes", "ملاحظات", "Notes", ImportFieldType.Text, MaxLength: 1000, Aliases: ["note", "remarks", "ملاحظة", "ملاحظات"]),
        new("kind", "نوع الطرف", "Party kind", ImportFieldType.Enum, Required: true, EnumValues: new Dictionary<string, string[]>
        {
            ["organization"] = ["org", "company", "business", "organisation", "منشأة", "شركة", "مؤسسة"], ["individual"] = ["person", "personal", "فرد", "شخص", "شخصي"],
        }, Aliases: ["type", "party type", "النوع", "نوع الطرف"]),
        new("legal_name_ar", "الاسم القانوني بالعربية", "Legal name (Arabic)", ImportFieldType.Text, Required: true, MaxLength: 200, Aliases: ["arabic legal name", "legal name ar", "الاسم القانوني", "الاسم القانوني بالعربية"]),
        new("legal_name_en", "الاسم القانوني بالإنجليزية", "Legal name (English)", ImportFieldType.Text, MaxLength: 200, Aliases: ["english legal name", "legal name en", "الاسم القانوني بالإنجليزية"]),
        new("tax_number", "الرقم الضريبي", "Tax number", ImportFieldType.Text, MaxLength: 50, Aliases: ["tax", "vat", "tax no", "tax id", "الرقم الضريبي"]),
        new("registration_number", "رقم السجل", "Registration number", ImportFieldType.Text, MaxLength: 50, Aliases: ["registration", "commercial register", "cr", "رقم السجل", "السجل التجاري"]),
    ];

    private static (PartyValidation.Local? Local, PartyValidation.Identity? Identity, List<RowIssue> Issues) Validate(NormalizedRow row)
    {
        var kind = row.Get("kind") switch { "organization" => 1, "individual" => 2, _ => (int?)null };
        var input = new LocalPartyInput(row.Get("code"), row.Get("display_name"), row.Get("notes"), true,
            new SharedIdentityInput(kind, row.Get("legal_name_ar"), row.Get("legal_name_en"), row.Get("tax_number"), row.Get("registration_number")));
        var issues = new List<RowIssue>();
        var okLocal = PartyValidation.TryLocal(input, true, out var local, out var localErrors);
        issues.AddRange(localErrors.Select(e => new RowIssue(LocalField(e.Field), e.Code)));
        var okIdentity = PartyValidation.TryIdentity(input.Identity, false, "identity.", out var identity, out var identityErrors);
        issues.AddRange(identityErrors.Select(e => new RowIssue(IdentityField(e.Field), e.Code)));
        return (okLocal ? local : null, okIdentity ? identity : null, issues);
    }

    private static string LocalField(string f) => f switch { "displayName" => "display_name", var x => x };

    private static string IdentityField(string f) => f.Replace("identity.", "", StringComparison.Ordinal) switch
    {
        "legalNameAr" => "legal_name_ar", "legalNameEn" => "legal_name_en", "taxNumber" => "tax_number", "registrationNumber" => "registration_number", var x => x,
    };

    private sealed record Existing(string Code, string DisplayName, string? Notes, bool IsActive);

    private static async Task<Dictionary<string, Existing>> LoadAsync(ImportAnalysisContext c, CancellationToken ct)
    {
        var map = new Dictionary<string, Existing>(StringComparer.Ordinal);
        await using var q = new NpgsqlCommand($"SELECT {LP.Code}, {LP.DisplayName}, {LP.Notes}, {LP.IsActive} FROM {LP.Table} ORDER BY {LP.Code}", c.Connection, c.Transaction);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = new Existing(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3));
        return map;
    }

    public async Task<AnalysisResult> AnalyzeAsync(ImportAnalysisContext context, IReadOnlyList<NormalizedRow> rows, CancellationToken ct)
    {
        var existing = await LoadAsync(context, ct);
        var answers = new List<RowAnalysis>();
        int create = 0, exact = 0;
        foreach (var row in rows.OrderBy(r => r.RowNo))
        {
            var (local, identity, issues) = Validate(row);
            if (local is null || identity is null || issues.Count > 0) { answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Invalid, issues)); continue; }
            if (existing.TryGetValue(local.Code, out var e))
            {
                var diffs = new List<string>();
                if (!string.Equals(local.DisplayName, e.DisplayName, StringComparison.Ordinal)) diffs.Add("display_name");
                if (!string.Equals(local.Notes, e.Notes, StringComparison.Ordinal)) diffs.Add("notes");
                if (diffs.Count == 0) { exact++; answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Exact, Explanation: "exists_identical")); }
                else answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Conflict, [new RowIssue("code", "record_exists_with_differences", string.Join(',', diffs))], [ImportResolutions.Skip], Explanation: "record_exists_with_differences"));
                continue;
            }
            create++;
            answers.Add(new RowAnalysis(row.RowNo, RowOutcome.Valid, Operation: new JsonObject
            {
                ["type"] = "create-party", ["code"] = local.Code, ["displayName"] = local.DisplayName, ["notes"] = local.Notes, ["kind"] = (int)identity.Kind,
                ["legalNameAr"] = identity.LegalNameAr, ["legalNameEn"] = identity.LegalNameEn, ["taxNumber"] = identity.TaxNumber, ["registrationNumber"] = identity.RegistrationNumber,
            }, Explanation: "will_create"));
        }
        var sb = new StringBuilder();
        foreach (var e in existing.Values.OrderBy(e => e.Code, StringComparer.Ordinal)) sb.Append(e.Code).Append('|').Append(e.DisplayName).Append('|').Append(e.Notes).Append('|').Append(e.IsActive ? 1 : 0).Append('\n');
        var baseline = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return new AnalysisResult(answers, baseline,
            [new("parties_to_create", create.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("matched_exactly", exact.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             new("already_existing", existing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
    }

    public async Task<ImportApplyResult> ApplyAsync(ImportApplyContext context, CancellationToken ct)
    {
        var created = 0;
        foreach (var op in context.Operations.OrderBy(o => o.Sequence).ThenBy(o => o.RowNo))
        {
            var o = op.Operation;
            // the plan was produced by this adapter's analysis, but the operation is validated again by the same domain rules (a plan is data until it is applied)
            var (local, identity, issues) = Validate(ToRow(op.RowNo, o));
            if (local is null || identity is null || issues.Count > 0) throw new ImportApplyException("party_invalid", $"row {op.RowNo}: party_invalid ({string.Join(',', issues.Select(i => $"{i.Field}:{i.Code}"))})");
            try { await parties.CreateInWorkAsync(context.Work, context.Actor, local, identity, ct); created++; }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new ImportApplyException("code_taken", $"row {op.RowNo}: code_taken"); }
        }
        return new ImportApplyResult(new JsonObject { ["created"] = created, ["pendingSharedIdentityRequests"] = created });
    }

    private static NormalizedRow ToRow(int rowNo, JsonObject o) => new(rowNo, new Dictionary<string, string?>
    {
        ["code"] = o["code"]?.GetValue<string>(), ["display_name"] = o["displayName"]?.GetValue<string>(), ["notes"] = o["notes"]?.GetValue<string>(),
        ["kind"] = o["kind"]?.GetValue<int>() switch { 1 => "organization", 2 => "individual", _ => null }, ["legal_name_ar"] = o["legalNameAr"]?.GetValue<string>(),
        ["legal_name_en"] = o["legalNameEn"]?.GetValue<string>(), ["tax_number"] = o["taxNumber"]?.GetValue<string>(), ["registration_number"] = o["registrationNumber"]?.GetValue<string>(),
    }, null);
}
