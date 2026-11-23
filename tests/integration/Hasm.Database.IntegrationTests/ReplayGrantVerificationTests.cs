using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Npgsql;
using CP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlan;
using CA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CompensationPlanApproval;
using RK = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplaySignerKey;
using RG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayExecutionGrant;
using RU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayGrantUse;
using RF = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayGrant;
using RA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayAuthorization;
using SD = Hasm.Infrastructure.CompanyDatabase.CompanySchema.StockDocument;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// T2 slice A2 (company migration 0038): the insert-time verification of an execution grant. The message of design 4.4 (hasm-replay-v5) is rebuilt in SQL from the rows and checked against the design's known-answer vectors and
/// against an independent .NET twin that reads the same rows; a REAL 3072-bit key signs real messages in the tests, and every rule of 4.3 is shown to refuse in turn (each refusal carries its own reason). INERT: the grant tables stay
/// SELECT-only for the runtime role, so only the database owner writes them here, and nothing activates.
/// </summary>
public class ReplayGrantVerificationTests : PlanExecutionTestBase
{
    // ------------------------------------------------------------------------------------------------------------ the independent .NET twin of the builders
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static Guid U(char d) => Guid.Parse($"{new string(d, 8)}-{new string(d, 4)}-4{new string(d, 3)}-8{new string(d, 3)}-{new string(d, 12)}");

    private static string Head(Guid company, Guid plan, long revision, Guid approval, Guid approver, Guid creator, Guid document, long documentRevision, Guid item, Guid warehouse, string payloadHash, string fingerprints,
        string closure, string linesHash, string epoch, Guid executor) =>
        string.Join('\n', "hasm-replay-v5", "grant", $"company={company}", $"plan={plan}", $"revision={revision}", $"approval={approval}", $"approver={approver}", $"creator={creator}", $"document={document}@{documentRevision}",
            $"item={item}", $"warehouse={warehouse}", $"payload={payloadHash}", $"fingerprints={fingerprints}", $"closure={closure}", $"lines={linesHash}", $"epoch={epoch}", $"executor={executor}");

    private static string Message(string head, string attestation, Guid grant, Guid signer, string key, long notBefore, long notAfter) =>
        head + "\n" + string.Join('\n', $"attestation={Sha(attestation)}", $"grant={grant}", $"signer={signer}", $"key={key}", $"not-before={notBefore}", $"not-after={notAfter}");

    private static string Digest16(string head) => Sha(head)[..16];

    private static string Attestation(Guid creator, string creatorMethod, long creatorAt, Guid approver, string approverMethod, long approverAt, string request, string participants = "3", string note = "plan, document and totals confirmed") =>
        string.Join('\n', "hasm-replay-attestation-v1", $"creator={creator}|{creatorMethod}|{creatorAt}", $"approver={approver}|{approverMethod}|{approverAt}", $"request={request}", $"participants={participants}", $"note={note}");

    private static string Lit(byte[] b) => "'\\x" + Convert.ToHexStringLower(b) + "'::bytea";

    // ------------------------------------------------------------------------------------------------------------ vectors (pure builders, no plan)
    [PostgresRoleFact]
    public async Task The_builders_reproduce_the_known_answer_vectors_of_the_design_byte_for_byte_whatever_the_session_settings()
    {
        await PrepareRuntime();
        var fingerprints = string.Join(',', Enumerable.Range(1, 7).Select(i => new string((char)('0' + i), 64)));
        // the lines text: two lines of the design, hash b5fd4e1d...
        var lines = await Sql<string>($"SELECT string_agg(public.{RF.FnLineText}(v.n, v.posting, v.d, v.period, v.branch, v.account, v.debit, v.credit), chr(10) ORDER BY v.n) FROM (VALUES " +
            "(1, '9f6c1d2e-0000-4000-8000-000000000001'::uuid, DATE '2026-03-11', '1a2b3c4d-0000-4000-8000-0000000000aa'::uuid, '2b3c4d5e-0000-4000-8000-0000000000bb'::uuid, '3c4d5e6f-0000-4000-8000-0000000000cc'::uuid, 12.5::numeric, 0::numeric), " +
            "(2, NULL::uuid, DATE '2026-03-12', '1a2b3c4d-0000-4000-8000-0000000000aa'::uuid, '2b3c4d5e-0000-4000-8000-0000000000bb'::uuid, '4d5e6f70-0000-4000-8000-0000000000dd'::uuid, 0::numeric, 12.5::numeric)) v(n, posting, d, period, branch, account, debit, credit)");
        Assert.Equal("1|9f6c1d2e-0000-4000-8000-000000000001|2026-03-11|1a2b3c4d-0000-4000-8000-0000000000aa|2b3c4d5e-0000-4000-8000-0000000000bb|3c4d5e6f-0000-4000-8000-0000000000cc|12.500000|0.000000\n" +
                     "2|-|2026-03-12|1a2b3c4d-0000-4000-8000-0000000000aa|2b3c4d5e-0000-4000-8000-0000000000bb|4d5e6f70-0000-4000-8000-0000000000dd|0.000000|12.500000", lines);
        Assert.Equal("b5fd4e1d6a1b3265a0c351234a1ffc54b0fe850693aecdb67f1163c38facda49", Sha(lines));
        // the head, the request digest, the attestation and the grant message of the design (version 5)
        var head = $"SELECT public.{RF.FnHead}('{U('1')}', '{U('2')}', 2, '{U('3')}', '{U('4')}', '{U('5')}', '{U('6')}', 3, '{U('7')}', '{U('8')}', 'e5f0ae53bfb61148c98966f48120e58e83c035a397ad09ca30e879cd3417b18f', '{fingerprints}', '{new string('c', 64)}', 'b5fd4e1d6a1b3265a0c351234a1ffc54b0fe850693aecdb67f1163c38facda49', '4329df10c3039a9f9e66941607a58ebd1474826d1c4ac2cc7a82a11284b7aa42', '{U('9')}')";
        var sqlHead = await Sql<string>(head);
        Assert.Equal(Head(U('1'), U('2'), 2, U('3'), U('4'), U('5'), U('6'), 3, U('7'), U('8'), "e5f0ae53bfb61148c98966f48120e58e83c035a397ad09ca30e879cd3417b18f", fingerprints, new string('c', 64),
            "b5fd4e1d6a1b3265a0c351234a1ffc54b0fe850693aecdb67f1163c38facda49", "4329df10c3039a9f9e66941607a58ebd1474826d1c4ac2cc7a82a11284b7aa42", U('9')), sqlHead);
        Assert.Equal("b0953249b1f326dd", await Sql<string>($"SELECT public.{RF.FnRequestDigest}(({head}))"));
        var attestation = Attestation(U('5'), "phone", 1795650600, U('4'), "in-person", 1795650900, "b0953249b1f326dd");
        Assert.Equal(236, Encoding.UTF8.GetByteCount(attestation));
        Assert.Equal("6e403bf40283b64f96df716b55dadb52dec9250764c47e26a6f09e846e1e9e3a", Sha(attestation));
        var sqlMessage = await Sql<string>($"SELECT public.{RF.FnGrantMessage}(({head}), $att${attestation}$att$, '{U('a')}', '{U('b')}', 'k1', 1795651200, 1795658400)");
        Assert.Equal(ReplayRsaVectors.Message, sqlMessage);                                     // the same 1408 bytes that A1's signature vector signs
        Assert.Equal(1408, Encoding.UTF8.GetByteCount(sqlMessage));
        Assert.Equal("ef7615cb4c06e2fcd5422e2bbd901334439f1f58285c51c3ae9b3412a7715e1b", Sha(sqlMessage));
        Assert.Equal(Message(sqlHead, attestation, U('a'), U('b'), "k1", 1795651200, 1795658400), sqlMessage);
        // the session settings that change text and numbers do not change a byte
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        foreach (var setting in new[] { "SET TimeZone = 'Asia/Riyadh'", "SET DateStyle = 'German, DMY'", "SET IntervalStyle = 'iso_8601'", "SET bytea_output = 'escape'", "SET extra_float_digits = -3", "SET lc_numeric = 'C'" })
        {
            await using (var s = new NpgsqlCommand(setting, conn)) await s.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand($"SELECT public.{RF.FnGrantMessage}(({head}), $att${attestation}$att$, '{U('a')}', '{U('b')}', 'k1', 1795651200, 1795658400)", conn);
            Assert.Equal(ReplayRsaVectors.Message, (string)(await cmd.ExecuteScalarAsync())!);
            await using var cmd2 = new NpgsqlCommand($"SELECT public.{RF.FnLineText}(1, NULL, DATE '2026-03-11', '{U('1')}', '{U('2')}', '{U('3')}', 12.5, 0)", conn);
            Assert.Equal($"1|-|2026-03-11|{U('1')}|{U('2')}|{U('3')}|12.500000|0.000000", (string)(await cmd2.ExecuteScalarAsync())!);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ a real plan, a real key, real signatures
    private sealed record Grant(Guid Id, string Key, Guid Plan, Guid Executor, Guid Signer, long NotBefore, long NotAfter, string Epoch, string Attestation, byte[] Signature);

    private sealed class Bed
    {
        public Plan Plan = null!; public RSA Rsa = RSA.Create(3072); public Guid Signer = Guid.NewGuid(); public string KeyId = "k1"; public string Epoch = new('a', 64);
        public Guid Company;
    }

    private async Task<string?> AsOwner(params string[] statements)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            return null;
        }
        catch (PostgresException ex) { return Describe(ex); }
    }

    private async Task<Bed> NewBed(bool registerKey = true, Guid? keyOwner = null, ItemView? item = null)
    {
        await PrepareRuntime();
        var bed = new Bed { Plan = await ApprovedPlan(item ?? Avg, "2026-03-11"), Company = await Sql<Guid>($"SELECT {CompanySchema.Identity.CompanyId} FROM {CompanySchema.Identity.Table}") };
        if (registerKey) Assert.Null(await RegisterKey(bed, bed.KeyId, keyOwner));
        return bed;
    }

    private static BigInteger Modulus(RSA rsa) => new(rsa.ExportParameters(false).Modulus!, isUnsigned: true, isBigEndian: true);

    /// <summary>Registers the bed's key. Every key names the person it belongs to (M1 of the review of PR 236): by default the bed's signer; <paramref name="unbound"/> registers a key with no signer.</summary>
    private Task<string?> RegisterKey(Bed bed, string id, Guid? owner = null, bool unbound = false) =>
        AsOwner($"INSERT INTO {RK.Table} ({RK.KeyId}, {RK.Modulus}, {RK.SignerUserId}) VALUES ('{id}', '{Modulus(bed.Rsa)}'::numeric, {(unbound ? "NULL" : $"'{owner ?? bed.Signer}'")})");

    /// <summary>The 17 lines of the grant message, rebuilt by the .NET twin from the rows of the plan (never from SQL builders).</summary>
    private async Task<string> HeadFromRows(Bed bed, Guid plan, string epoch, Guid executor)
    {
        async Task<object?> One(string sql) { await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)); await conn.OpenAsync(); await using var cmd = new NpgsqlCommand(sql, conn); return await cmd.ExecuteScalarAsync(); }
        async Task<string> Col(string table, string column, string idColumn, Guid id) => Convert.ToString(await One($"SELECT {column}::text FROM {table} WHERE {idColumn} = '{id}'"), System.Globalization.CultureInfo.InvariantCulture)!;
        var approval = Guid.Parse(await Col(CP.Table, CP.ApprovalId, CP.Id, plan));
        var fingerprints = string.Join(',', await Task.WhenAll(PlanFingerprints.Select(c => Col(CP.Table, c, CP.Id, plan))));
        var lines = await Sql<string>($"SELECT coalesce(string_agg(x.t, chr(10) ORDER BY x.n), '') FROM (SELECT l.{CompanySchema.CompensationPlanLine.LineNo} n, l.{CompanySchema.CompensationPlanLine.LineNo}::text || '|' || coalesce(l.{CompanySchema.CompensationPlanLine.PostingId}::text, '-') || '|' || to_char(l.{CompanySchema.CompensationPlanLine.EntryDate}, 'YYYY-MM-DD') || '|' || l.{CompanySchema.CompensationPlanLine.PeriodId}::text || '|' || l.{CompanySchema.CompensationPlanLine.BranchId}::text || '|' || l.{CompanySchema.CompensationPlanLine.AccountId}::text || '|' || l.{CompanySchema.CompensationPlanLine.Debit}::text || '|' || l.{CompanySchema.CompensationPlanLine.Credit}::text t FROM {CompanySchema.CompensationPlanLine.Table} l WHERE l.{CompanySchema.CompensationPlanLine.PlanId} = '{plan}') x");
        return Head(bed.Company, plan, long.Parse(await Col(CA.Table, CA.PlanRevision, CA.Id, approval)), approval, Guid.Parse(await Col(CA.Table, CA.Approver, CA.Id, approval)), Guid.Parse(await Col(CP.Table, CP.CreatedBy, CP.Id, plan)),
            Guid.Parse(await Col(CP.Table, CP.DocumentId, CP.Id, plan)), long.Parse(await Col(CP.Table, CP.DocumentRevision, CP.Id, plan)), Guid.Parse(await Col(CP.Table, CP.ItemId, CP.Id, plan)), Guid.Parse(await Col(CP.Table, CP.WarehouseId, CP.Id, plan)),
            Sha(await Col(CP.Table, CP.Payload, CP.Id, plan)), fingerprints, await Col(CP.Table, CP.ClosureHash, CP.Id, plan), Sha(lines), epoch, executor);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>A grant for the plan, signed with the bed's key over the message the .NET twin builds from the rows. The parameters falsify one thing each.</summary>
    private async Task<Grant> Make(Bed bed, Plan? plan = null, Guid? signer = null, Guid? executor = null, long? notBefore = null, long? notAfter = null, string? epoch = null, string? attestation = null, string? keyId = null,
        Func<string, string>? attestationEdit = null, string? creatorMethod = null, long? creatorAt = null, string? requestOverride = null, RSA? signWith = null, bool signStaleHead = false)
    {
        plan ??= bed.Plan; signer ??= bed.Signer; executor ??= Executor; epoch ??= bed.Epoch; keyId ??= bed.KeyId;
        var nb = notBefore ?? Now() - 300; var na = notAfter ?? nb + 7200;
        var head = await HeadFromRows(bed, plan.Id, epoch, executor.Value);
        var request = requestOverride ?? Digest16(head);
        var att = attestation ?? Attestation(Planner, creatorMethod ?? "phone", creatorAt ?? nb - 600, PlanApprover, "in-person", nb - 300, request);
        if (attestationEdit is not null) att = attestationEdit(att);
        var id = Guid.NewGuid();
        var message = Message(signStaleHead ? head.Replace("hasm-replay-v5", "hasm-replay-v4") : head, att, id, signer.Value, keyId, nb, na);
        var signature = (signWith ?? bed.Rsa).SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new Grant(id, keyId, plan.Id, executor.Value, signer.Value, nb, na, epoch, att, signature);
    }

    private static string Insert(Grant g) =>
        $"INSERT INTO {RG.Table} ({RG.Id}, {RG.KeyId}, {RG.PlanId}, {RG.ExecutorUserId}, {RG.SignerUserId}, {RG.NotBefore}, {RG.NotAfter}, {RG.EpochDigest}, {RG.Attestation}, {RG.Signature}) " +
        $"VALUES ('{g.Id}', '{g.Key}', '{g.Plan}', '{g.Executor}', '{g.Signer}', {g.NotBefore}, {g.NotAfter}, '{g.Epoch}', $att${g.Attestation}$att$, {Lit(g.Signature)})";

    [PostgresRoleFact]
    public async Task A_correctly_signed_grant_of_an_approved_plan_is_accepted_and_the_sql_builders_equal_the_dotnet_twin_on_the_real_rows()
    {
        var bed = await NewBed();
        var g = await Make(bed);
        // the SQL readers build the same head, request digest and message as the twin that reads the same rows
        var head = await HeadFromRows(bed, bed.Plan.Id, bed.Epoch, Executor);
        Assert.Equal(head, await Sql<string>($"SELECT public.{RF.FnPlanHead}('{bed.Plan.Id}', '{bed.Epoch}', '{Executor}')"));
        Assert.Equal(Digest16(head), await Sql<string>($"SELECT public.{RF.FnRequestDigest}(public.{RF.FnPlanHead}('{bed.Plan.Id}', '{bed.Epoch}', '{Executor}'))"));
        Assert.Equal(Sha(""), head.Split('\n').Single(l => l.StartsWith("lines=", StringComparison.Ordinal))[6..]);                 // a plan without lines: the hash of the empty string
        Assert.Equal(Message(head, g.Attestation, g.Id, g.Signer, g.Key, g.NotBefore, g.NotAfter),
            await Sql<string>($"SELECT public.{RF.FnGrantMessage}(public.{RF.FnPlanHead}('{bed.Plan.Id}', '{bed.Epoch}', '{Executor}'), $att${g.Attestation}$att$, '{g.Id}', '{g.Signer}', '{g.Key}', {g.NotBefore}, {g.NotAfter})"));
        Assert.Null(await AsOwner(Insert(g)));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {RG.Table} WHERE {RG.Id} = '{g.Id}'"));
        Assert.Equal("ok", await Sql<string>($"SELECT coalesce(public.{RF.FnAttestationProblem}($att${g.Attestation}$att$, '{bed.Plan.Id}', '{bed.Epoch}', '{Executor}', {g.NotBefore}), 'ok')"));
    }

    [PostgresRoleFact]
    public async Task A_plan_with_lines_hashes_its_lines_the_same_way_in_sql_and_in_the_twin()
    {
        await PrepareRuntime();
        var p = new Plan { Doc = await Approved(Receipt(W1, "2026-03-11", L(Avg, Pcs, 10m))), Item = Avg.Id, Warehouse = W1.Id };
        var branch = await Sql<Guid>($"SELECT {CompanySchema.Branch.Id} FROM {CompanySchema.Branch.Table} LIMIT 1");
        var period = await Sql<Guid>($"SELECT {CompanySchema.FiscalPeriod.Id} FROM {CompanySchema.FiscalPeriod.Table} LIMIT 1");
        var accounts = await Sql<string>($"SELECT string_agg({CompanySchema.Account.Id}::text, ',') FROM (SELECT {CompanySchema.Account.Id} FROM {CompanySchema.Account.Table} ORDER BY {CompanySchema.Account.Id} LIMIT 2) a");
        var (a1, a2) = (accounts.Split(',')[0], accounts.Split(',')[1]);
        string Line(int n, string debit, string credit, string account) =>
            $"INSERT INTO {CompanySchema.CompensationPlanLine.Table} ({CompanySchema.CompensationPlanLine.PlanId}, {CompanySchema.CompensationPlanLine.LineNo}, {CompanySchema.CompensationPlanLine.PostingId}, {CompanySchema.CompensationPlanLine.EntryDate}, " +
            $"{CompanySchema.CompensationPlanLine.PeriodId}, {CompanySchema.CompensationPlanLine.BranchId}, {CompanySchema.CompensationPlanLine.AccountId}, {CompanySchema.CompensationPlanLine.Debit}, {CompanySchema.CompensationPlanLine.Credit}) " +
            $"VALUES ('{p.Id}', {n}, NULL, DATE '2026-03-11', '{period}', '{branch}', '{account}', {debit}, {credit})";
        Assert.Null(await Rt([PlanInsert(p), Closure(p, Avg.Id, W1.Id), Line(2, "0", "12.5", a2), Line(1, "12.500001", "0", a1)]));
        var sqlLines = await Sql<string>($"SELECT public.{RF.FnLinesText}('{p.Id}')");
        var expected = $"1|-|2026-03-11|{period}|{branch}|{a1}|12.500001|0.000000\n2|-|2026-03-11|{period}|{branch}|{a2}|0.000000|12.500000";
        Assert.Equal(expected, sqlLines);                                                                                       // ordered by line number, not by insertion order
        Assert.Equal(Sha(expected), await Sql<string>($"SELECT encode(sha256(convert_to(public.{RF.FnLinesText}('{p.Id}'), 'UTF8')), 'hex')"));
        Assert.Equal("", await Sql<string>($"SELECT public.{RF.FnLinesText}('{Guid.NewGuid()}')"));                          // a plan with no lines: the empty string
    }

    // ------------------------------------------------------------------------------------------------------------ every rule refuses, each with its own reason
    private static void Refused(string reason, string? outcome) { Assert.NotNull(outcome); Assert.StartsWith(Refusal, outcome); Assert.Contains(reason, outcome); }

    [PostgresRoleFact]
    public async Task Every_insert_time_rule_of_4_3_refuses_with_its_own_reason()
    {
        var bed = await NewBed();
        async Task Case(string reason, Func<Task<Grant>> make) => Refused(reason, await AsOwner(Insert(await make())));

        // the signature: another key, another content, another version, another signer in the message, a signature of another grant
        var other = RSA.Create(3072);
        await Case("grant_signature_invalid", () => Make(bed, signWith: other));
        await Case("grant_signature_invalid", () => Make(bed, signStaleHead: true));
        var swapped = await Make(bed);
        var forged = swapped with { Executor = Guid.NewGuid() };                                                                  // the row says another executor than the one that was signed
        Refused("grant_attestation_invalid", await AsOwner(Insert(forged)));                                                      // (the attestation's request digest covers the executor too)
        var epochChanged = swapped with { Epoch = new string('b', 64) };
        Refused("grant_attestation_invalid", await AsOwner(Insert(epochChanged)));
        var other2 = await Make(bed);
        Refused("grant_signature_invalid", await AsOwner(Insert(swapped with { Signature = other2.Signature })));                // a signature of another grant id
        Refused("grant_signature_invalid", await AsOwner(Insert(swapped with { Id = Guid.NewGuid() })));
        Refused("grant_signature_invalid", await AsOwner(Insert(swapped with { NotAfter = swapped.NotAfter - 1 })));

        // the key
        await Case("grant_key_unknown", () => Make(bed, keyId: "unregistered"));
        Assert.Null(await RegisterKey(bed, "k-owned", Guid.NewGuid()));
        await Case("grant_signer_is_not_the_key_owner", () => Make(bed, keyId: "k-owned"));
        Assert.Null(await RegisterKey(bed, "k-unbound", unbound: true));
        await Case("grant_key_has_no_signer", () => Make(bed, keyId: "k-unbound"));                                             // a key that names no person is refused until resolution (ii) exists
        Assert.Null(await RegisterKey(bed, "k-revoked"));
        Assert.Null(await AsOwner($"UPDATE {RK.Table} SET {RK.RevokeFrom} = clock_timestamp() - interval '1 second' WHERE {RK.KeyId} = 'k-revoked'"));
        await Case("grant_key_revoked", () => Make(bed, keyId: "k-revoked"));
        Assert.Null(await RegisterKey(bed, "k-future"));
        Assert.Null(await AsOwner($"UPDATE {RK.Table} SET {RK.RevokeFrom} = clock_timestamp() + interval '1 hour' WHERE {RK.KeyId} = 'k-future'"));
        Refused("grant_signature_invalid", await AsOwner(Insert(await Make(bed, keyId: "k-future", signWith: other))));          // a key revoked only LATER still counts: the signature is what fails

        // the window
        await Case("grant_outside_its_window", () => Make(bed, notBefore: Now() + 3600));
        await Case("grant_outside_its_window", () => Make(bed, notBefore: Now() - 7000, notAfter: Now() - 60));
        await Case("grant_window_longer_than_2_hours", () => Make(bed, notBefore: Now() - 300, notAfter: Now() - 300 + 7201));
        Assert.Null(await AsOwner(Insert(await Make(bed, notBefore: Now() - 7000, notAfter: Now() - 7000 + 7200))));            // a window of exactly 2 hours that still holds is accepted

        // independence: the signer is not the creator, the approver, the executor, the document's creator
        await Case("grant_signer_not_independent", () => Make(bed, signer: Planner));
        await Case("grant_signer_not_independent", () => Make(bed, signer: PlanApprover));
        await Case("grant_signer_not_independent", () => Make(bed, signer: Executor));
        await Case("grant_signer_not_independent", () => Make(bed, signer: Admin.Id));                                           // the creator, submitter and approver of the event document
        var creatorOfDocument = await Sql<Guid>($"SELECT {SD.CreatedBy} FROM {SD.Table} WHERE {SD.Id} = '{bed.Plan.Doc.Id}'");
        await Case("grant_signer_not_independent", () => Make(bed, signer: creatorOfDocument));
        await Case("grant_signer_not_independent", () => Make(bed, executor: bed.Signer));                                      // the executor is the signer

        // the plan's approval must still be sound (0036): a plan whose stored fingerprint no longer matches its approval is refused (the test switches the plan's own guards off as the database owner to forge that state)
        Assert.Null(await AsOwner($"ALTER TABLE {CP.Table} DISABLE TRIGGER USER", $"UPDATE {CP.Table} SET {CP.PlanFingerprint} = '{new string('f', 64)}' WHERE {CP.Id} = '{bed.Plan.Id}'", $"ALTER TABLE {CP.Table} ENABLE TRIGGER USER"));
        await Case("grant_plan_approval_unsound", () => Make(bed));
    }

    [PostgresRoleFact]
    public async Task The_attestation_is_checked_field_by_field_and_a_grant_of_an_unapproved_plan_or_a_missing_plan_is_refused()
    {
        var bed = await NewBed();
        async Task Case(string reason, Func<Task<Grant>> make) => Refused(reason, await AsOwner(Insert(await make())));
        // well formed, wrong, or altered
        await Case("attestation_creator_mismatch", () => Make(bed, attestationEdit: a => a.Replace($"creator={Planner}", $"creator={Guid.NewGuid()}")));
        await Case("attestation_approver_mismatch", () => Make(bed, attestationEdit: a => a.Replace($"approver={PlanApprover}", $"approver={Guid.NewGuid()}")));
        await Case("attestation_creator_line", () => Make(bed, creatorMethod: "telepathy"));
        await Case("attestation_creator_time", () => Make(bed, creatorAt: Now() - 300 + 301));                                  // later than not-before + 300
        await Case("attestation_creator_time", () => Make(bed, creatorAt: Now() - 300 - 86401));                                // earlier than not-before - 86400
        await Case("attestation_approver_time", () => Make(bed, attestationEdit: a => a.Replace("|in-person|", "|in-person|9")));
        await Case("attestation_request_mismatch", () => Make(bed, requestOverride: "0123456789abcdef"));
        await Case("attestation_creator_line", () => Make(bed, attestationEdit: a => System.Text.RegularExpressions.Regex.Replace(a, @"(creator=[^|]+\|phone\|)", "${1}0")));                  // no leading zero in an integer (4.4)
        await Case("attestation_approver_line", () => Make(bed, attestationEdit: a => System.Text.RegularExpressions.Regex.Replace(a, @"(approver=[^|]+\|in-person\|)", "${1}0")));
        await Case("attestation_request_line", () => Make(bed, attestationEdit: a => a.Replace("request=", "request=X")));
        await Case("attestation_participants_line", () => Make(bed, attestationEdit: a => a.Replace("participants=3", "participants=03")));
        await Case("attestation_note", () => Make(bed, attestationEdit: a => a[..a.IndexOf("note=", StringComparison.Ordinal)] + "note=" + new string('x', 501)));
        await Case("attestation_malformed", () => Make(bed, attestationEdit: a => a + "\nextra=1"));
        await Case("attestation_malformed", () => Make(bed, attestationEdit: a => a.Replace("hasm-replay-attestation-v1", "hasm-replay-attestation-v2")));
        await Case("attestation_malformed", () => Make(bed, attestationEdit: a => string.Join('\n', a.Split('\n').Take(5))));
        Assert.Null(await AsOwner(Insert(await Make(bed, attestationEdit: a => a[..a.IndexOf("note=", StringComparison.Ordinal)] + "note=" + new string('x', 500)))));                  // exactly 500 characters
        // the plan: not approved, absent
        var proposed = await ProposedPlan(Fifo, "2026-03-11");
        await Case("grant_plan_not_approved", async () => await MakeUnchecked(bed, proposed));
        await Case("grant_plan_not_found", async () => await MakeUnchecked(bed, new Plan { Id = Guid.NewGuid() }));
    }

    /// <summary>A grant whose message cannot be built from rows (the plan is not approved or missing): the fields are fixed values, the signature is irrelevant because the plan rule refuses first.</summary>
    private Task<Grant> MakeUnchecked(Bed bed, Plan plan) =>
        Task.FromResult(new Grant(Guid.NewGuid(), bed.KeyId, plan.Id, Executor, bed.Signer, Now() - 300, Now() + 6900, bed.Epoch, "x", new byte[384]));

    [PostgresRoleFact]
    public async Task A_grant_use_must_name_the_plan_and_the_executor_of_its_execution_inside_the_window()
    {
        var bed = await NewBed();
        var g = await Make(bed);
        Assert.Null(await AsOwner(Insert(g)));
        // the execution of ANOTHER plan with this grant
        var other = await ApprovedPlan(Fifo, "2026-03-11");
        async Task<string?> WithUse(Plan p, Guid? by, Guid grant)
        {
            var script = new Script { AuditSubject = p.Id, AuditTarget = p.Execution };
            script.Add(("audit", []));
            script.Add(("execution", [ExecutionInsert(p, by)]));
            script.Add(("use", [$"INSERT INTO {RU.Table} ({RU.ExecutionId}, {RU.GrantId}) VALUES ('{p.Execution}', '{grant}')"]));
            return await RunAsOwner(script);
        }
        Refused("grant_use_wrong_plan", await WithUse(other, Executor, g.Id));
        Refused("grant_use_wrong_executor", await WithUse(bed.Plan, Guid.NewGuid(), g.Id));
        // an unknown grant (a grant that does not exist)
        Refused("grant_use_grant_not_found", await WithUse(bed.Plan, Executor, Guid.NewGuid()));
        // a window that ended before the use: a grant valid for a few seconds is accepted at insert and refused when it is used afterwards
        var shortLived = await Make(bed, notBefore: Now() - 300, notAfter: Now() + 3);
        Assert.Null(await AsOwner(Insert(shortLived)));
        await Task.Delay(TimeSpan.FromSeconds(5));
        Refused("grant_use_outside_its_window", await WithUse(bed.Plan, Executor, shortLived.Id));
        // the right plan and executor pass the use rule: the execution is then refused LATER by the 0036 guards of an incomplete epoch, never by this rule
        var outcome = await WithUse(bed.Plan, Executor, g.Id);
        Assert.NotNull(outcome);
        Assert.DoesNotContain("grant_use_", outcome);
    }

    // ------------------------------------------------------------------------------------------------------------ inertness, privileges and the decision on EXECUTE (review L1 of PR 233)
    [PostgresRoleFact]
    public async Task The_functions_are_invoker_rights_readers_executable_by_public_the_runtime_role_still_cannot_write_a_grant_and_only_the_two_triggers_call_them()
    {
        var bed = await NewBed();
        var functions = new[] { RF.FnLineText, RF.FnLinesText, RF.FnHead, RF.FnRequestDigest, RF.FnGrantMessage, RF.FnPlanHead, RF.FnAttestationProblem, RF.FnGrantInsert, RF.FnUseInsert };
        foreach (var f in functions)
        {
            Assert.False(await Sql<bool>($"SELECT prosecdef FROM pg_proc WHERE proname = '{f}'"), f);
            Assert.True(await Sql<bool>($"SELECT proconfig IS NULL FROM pg_proc WHERE proname = '{f}'"), f + " has no settings (no search_path)");
            Assert.Equal("plpgsql", await Sql<string>($"SELECT CASE WHEN l.lanname = 'sql' THEN 'plpgsql' ELSE l.lanname END FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang WHERE p.proname = '{f}'"));
            Assert.True(await Sql<bool>($"SELECT has_function_privilege('public', p.oid, 'EXECUTE') FROM pg_proc p WHERE p.proname = '{f}'"), f + ": EXECUTE stays with PUBLIC (decision of slice A2)");
        }
        Assert.True(await Sql<bool>($"SELECT has_function_privilege('public', p.oid, 'EXECUTE') FROM pg_proc p WHERE p.proname = '{RA.FnRsaVerify}'"));
        // the runtime role can call the readers (they read what it may read) but cannot write a grant or a use
        await using var conn = await AsRuntime();
        await using (var cmd = new NpgsqlCommand($"SELECT public.{RF.FnPlanHead}('{bed.Plan.Id}', '{bed.Epoch}', '{Executor}')", conn)) Assert.NotNull(await cmd.ExecuteScalarAsync());
        foreach (var table in new[] { RG.Table, RU.Table })
        {
            Assert.False((bool)(await Scalar(conn, $"SELECT has_table_privilege(current_user, '{table}', 'INSERT')"))!);
            await using var tx = await conn.BeginTransactionAsync();
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand($"INSERT INTO {table} DEFAULT VALUES", conn, tx); await cmd.ExecuteNonQueryAsync(); });
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            await tx.RollbackAsync();
        }
        // nothing but the grant insert trigger function calls the verifier; the builders are called only by the readers and the two trigger functions
        Assert.Equal(new[] { RF.FnGrantInsert }, await StringsAsync($"SELECT proname FROM pg_proc WHERE prosrc LIKE '%{RA.FnRsaVerify}%' AND proname <> '{RA.FnRsaVerify}' ORDER BY proname"));
        Assert.Equal(new[] { RF.FnAttestationProblem }, await StringsAsync($"SELECT proname FROM pg_proc WHERE prosrc LIKE '%{RF.FnRequestDigest}%' AND proname <> '{RF.FnRequestDigest}' ORDER BY proname"));
        // the verifier returns FALSE (never NULL) on NULL input, so an IF NOT on its result cannot pass on NULL (I1 of the review of PR 233)
        Assert.False(await Sql<bool>($"SELECT public.{RA.FnRsaVerify}(NULL, NULL, NULL)"));
    }

    private static async Task<object?> Scalar(NpgsqlConnection conn, string sql) { await using var cmd = new NpgsqlCommand(sql, conn); return await cmd.ExecuteScalarAsync(); }

    private async Task<string[]> StringsAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return [.. list];
    }
}
