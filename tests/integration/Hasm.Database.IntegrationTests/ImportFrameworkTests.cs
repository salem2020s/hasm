using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Imports;
using Hasm.Infrastructure.Security;
using Hasm.Infrastructure.Tests;
using Microsoft.Extensions.DependencyInjection;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using Ib = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportBatch;
using Iev = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportEvent;
using Ir = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportRow;
using Ist = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ImportSettings;
using Ph = Hasm.Infrastructure.CompanyDatabase.CompanySchema.PolicyHistory;

namespace Hasm.Database.IntegrationTests;

/// <summary>A framework-independence adapter: it knows no business data, only the descriptor the framework needs.</summary>
public sealed class TestImportAdapter(string code = "test-adapter", string upload = "company.accounts.create", string view = "company.accounts.view") : IImportAdapter
{
    public string Code => code; public int Version => 1; public string NameAr => "محوّل اختبار"; public string NameEn => "Test adapter";
    public string UploadPermission => upload; public string ViewPermission => view;
}

/// <summary>The generic import framework (Phase 3.2D S1) against real PostgreSQL; disposable cluster only.</summary>
public abstract class ImportTestBase : HttpApiTestBase
{
    protected CompanyAuditedTransactions Tx { get; private set; } = null!;
    protected InMemoryKekRing Ring { get; private set; } = null!;
    protected EnvelopeEncryptor Enc { get; private set; } = null!;
    protected EncryptedFileStore Store { get; private set; } = null!;
    protected ImportOptions ImportOpts { get; private set; } = null!;
    protected ImportService Imports { get; private set; } = null!;
    protected ImportManagementService IMg { get; private set; } = null!;
    protected ImportAdapterRegistry Registry { get; private set; } = null!;
    protected string StorageDir { get; } = Path.Combine(Path.GetTempPath(), "hasm-import-" + Guid.NewGuid().ToString("N"));
    protected static readonly EncryptionOptions TestEncryption = new() { AllowInsecureFilePermissions = true };

    protected async Task PrepareImports(ImportOptions? options = null, IEnumerable<IImportAdapter>? adapters = null)
    {
        await PrepareRbac();
        BuildImportFacade(options, adapters);
    }

    protected void BuildImportFacade(ImportOptions? options = null, IEnumerable<IImportAdapter>? adapters = null)
    {
        Tx = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Ring = new InMemoryKekRing("k1");
        Enc = new EnvelopeEncryptor(Ring);
        var o = options ?? new ImportOptions();
        ImportOpts = new ImportOptions
        {
            StorageDirectory = o.StorageDirectory ?? StorageDir, MaxFileBytes = o.MaxFileBytes, MaxRows = o.MaxRows, MaxColumns = o.MaxColumns, MaxWorksheets = o.MaxWorksheets,
            MaxCellChars = o.MaxCellChars, MaxExpandedBytes = o.MaxExpandedBytes, MaxZipEntries = o.MaxZipEntries, MaxProcessingSeconds = o.MaxProcessingSeconds, MaxRetentionDays = o.MaxRetentionDays,
        };
        Store = new EncryptedFileStore(ImportOpts, TestEncryption);
        Registry = new ImportAdapterRegistry(adapters ?? [new TestImportAdapter()]);
        Imports = new ImportService(Resolver, Tx, Enc, Store, ImportOpts, Registry, Clock);
        IMg = new ImportManagementService(Rbac, Imports, Registry);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { if (Directory.Exists(StorageDir)) Directory.Delete(StorageDir, true); } catch (IOException) { }
    }

    protected static T Ok<T>(ConfigResult<T> r) { Assert.True(r.IsOk, $"{r.Status} {r.Code} {string.Join(",", r.Fields?.Select(f => f.Field + ":" + f.Code) ?? [])}"); return r.Value!; }
    protected static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    protected const string Csv2 = "code,name\n0101,Cash\n0102,Bank\n";
    protected string CompanyDir => Path.Combine(StorageDir, Company.Id.ToString("N"));
    protected string[] StoredFiles() => Directory.Exists(CompanyDir) ? Directory.GetFiles(CompanyDir) : [];
    protected static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    protected async Task<ImportBatchView> Upload(string text = Csv2, string name = "chart.csv", Guid? by = null, string? key = null) =>
        Ok(await Imports.UploadAsync(by ?? Admin.Id, Company.Id, "test-adapter", name, Bytes(text), key));
    protected async Task<ImportBatchView> Staged(string text = Csv2, string name = "chart.csv", StageInput? input = null)
    {
        var b = await Upload(text, name);
        return Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, input ?? new StageInput(0, 1, "utf-8", null)));
    }
    protected async Task<ImportBatchView> Reload(Guid id) => (await Imports.GetAsync(Company.Id, id))!;
    protected Task<long> Rows(Guid id) => Sql<long>($"SELECT count(*) FROM {Ir.Table} WHERE {Ir.BatchId} = '{id}'");

    /// <summary>Pushes a batch along allowed states with direct SQL (what a later slice does through its services), for life-cycle tests.</summary>
    protected async Task Force(Guid id, params ImportStatus[] path)
    {
        foreach (var s in path)
        {
            var terminal = ImportStatusRules.IsTerminal(s);
            // the gate into Approved / Rejected needs a REAL independent decision of the current revision (the database refuses it otherwise), so the helper records one first
            if (s is ImportStatus.Approved or ImportStatus.Rejected)
            {
                var cur = await Reload(id);
                await Tx.ExecuteAsync(Company.Id, async w => await Hasm.Infrastructure.Ledger.DocumentApprovals.RecordAsync(w,
                    new Hasm.Infrastructure.Ledger.ApprovalDecisionRequest(ImportService.DocumentType, id, cur.Revision, Guid.NewGuid(), Guid.Empty, Guid.Empty, s == ImportStatus.Approved, s == ImportStatus.Approved ? null : "no", 1), DateTime.UtcNow, default));
            }
            var approval = s == ImportStatus.Approved ? $", {Ib.ApprovalRevision} = {Ib.Revision}" : s is ImportStatus.Mapped or ImportStatus.Validated or ImportStatus.Staged ? $", {Ib.ApprovalRevision} = NULL" : "";
            var result = s == ImportStatus.Applied ? $", {Ib.Result} = COALESCE({Ib.Result}, '{{}}'::jsonb)" : "";
            await Sql($"UPDATE {Ib.Table} SET {Ib.Status} = {(short)s}, {Ib.Revision} = {Ib.Revision} + 1, {Ib.UpdatedAtUtc} = now(), {Ib.Mapping} = COALESCE({Ib.Mapping}, '{{}}'::jsonb), {Ib.PlanHash} = COALESCE({Ib.PlanHash}, repeat('a', 64)), {Ib.Plan} = COALESCE({Ib.Plan}, '{{}}'::jsonb), " +
                $"{Ib.SubmittedBy} = COALESCE({Ib.SubmittedBy}, {Ib.CreatedBy}), {Ib.SubmittedAtUtc} = COALESCE({Ib.SubmittedAtUtc}, now()){(terminal ? $", {Ib.TerminalAtUtc} = now()" : "")}{approval}{result} WHERE {Ib.Id} = '{id}'");
        }
    }

    protected async Task AssertAuditIntact()
    {
        var report = await new AuditVerifier(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName), AuditKeys, AuditCheckpoints).VerifyAsync(AuditStream.ForBusiness(Company.Id));
        Assert.True(report.Verdict == AuditVerdict.Verified || report.Findings.All(f => f.Severity == FindingSeverity.Note));
        Assert.Equal(await Count(Company, Audit_.Table), report.Events);
    }
}

public class ImportDatabaseGuardTests : ImportTestBase
{
    [PostgresFact]
    public async Task The_life_cycle_is_enforced_for_every_writer_transitions_evidence_revisions_terminal_states_and_deletion()
    {
        await PrepareImports();
        var b = await Staged();
        var t = Ib.Table;
        // identity and evidence are immutable
        foreach (var col in new[] { $"{Ib.AdapterCode} = 'other'", $"{Ib.FileName} = 'x.csv'", $"{Ib.FileSize} = 1", $"{Ib.FileSha256} = '{new string('a', 64)}'", $"{Ib.StorageName} = '{new string('b', 32)}.enc'", $"{Ib.CreatedBy} = gen_random_uuid()", $"{Ib.FileKind} = 2"})
            Assert.Equal("P0001", await Fails($"UPDATE {t} SET {col}, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));
        // the revision advances by exactly one on every change
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.UpdatedAtUtc} = now() WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.Revision} = {Ib.Revision} + 2 WHERE {Ib.Id} = '{b.Id}'"));
        // only the defined transitions exist (an approval or an application cannot be skipped to)
        foreach (var bad in new[] { ImportStatus.Applied, ImportStatus.Applying, ImportStatus.Approved, ImportStatus.Submitted, ImportStatus.Validated, ImportStatus.Failed, ImportStatus.Rejected })
            Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.Status} = {(short)bad}, {Ib.Revision} = {Ib.Revision} + 1, {Ib.Mapping} = '{{}}', {Ib.SubmittedBy} = gen_random_uuid(), {Ib.SubmittedAtUtc} = now(), {Ib.TerminalAtUtc} = now() WHERE {Ib.Id} = '{b.Id}'"));
        // check constraints tie states to their evidence: no mapped state without a mapping, no submission without its submitter, terminal states carry their time
        Assert.Equal("23514", await Fails($"UPDATE {t} SET {Ib.Status} = 3, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));
        await Force(b.Id, ImportStatus.Mapped, ImportStatus.Validated);
        Assert.Equal("23514", await Fails($"UPDATE {t} SET {Ib.Status} = 5, {Ib.Revision} = {Ib.Revision} + 1, {Ib.SubmittedBy} = NULL WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Equal("23514", await Fails($"UPDATE {t} SET {Ib.Status} = 11, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));                         // terminal without its time
        await Force(b.Id, ImportStatus.Submitted, ImportStatus.Approved, ImportStatus.Applying, ImportStatus.Applied);
        // a finished batch is frozen: no state change, no content change, no deletion; only the retention bookkeeping and the key id may change
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.Status} = 3, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.Mapping} = '{{\"x\":1}}', {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.PlanHash} = '{new string('c', 64)}', {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {t} WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {t} CASCADE"));
        Assert.Null(await Fails($"UPDATE {t} SET {Ib.KeyId} = 'k9', {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Null(await Fails($"UPDATE {t} SET {Ib.OriginalPurgedAtUtc} = now(), {Ib.RowsPurgedAtUtc} = now(), {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.OriginalPurgedAtUtc} = NULL, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{b.Id}'"));                  // a purge record is final
        // an unfinished batch can never be marked purged
        var other = await Staged();
        Assert.Equal("P0001", await Fails($"UPDATE {Ib.Table} SET {Ib.RowsPurgedAtUtc} = now(), {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{other.Id}'"));
        // pending states cannot skip validation, recoverable failure has its own exits, applying never expires
        await Force(other.Id, ImportStatus.Mapped, ImportStatus.Validated, ImportStatus.Submitted, ImportStatus.Approved, ImportStatus.Applying);
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.Status} = 12, {Ib.TerminalAtUtc} = now(), {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{other.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.Status} = 11, {Ib.TerminalAtUtc} = now(), {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{other.Id}'"));
        await Force(other.Id, ImportStatus.Failed);
        Assert.Equal("P0001", await Fails($"UPDATE {t} SET {Ib.Status} = 12, {Ib.TerminalAtUtc} = now(), {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{other.Id}'"));   // failed work never expires by itself
        Assert.Null(await Fails($"UPDATE {t} SET {Ib.Status} = 7, {Ib.Revision} = {Ib.Revision} + 1 WHERE {Ib.Id} = '{other.Id}'"));                                           // retry application is allowed
    }

    [PostgresFact]
    public async Task Staged_rows_are_frozen_outside_staging_the_history_is_append_only_and_settings_obey_their_guards()
    {
        await PrepareImports();
        var b = await Staged();
        var rawRow = $"INSERT INTO {Ir.Table} ({Ir.BatchId}, {Ir.RowNo}, {Ir.Raw}, {Ir.RowHash}) VALUES ('{b.Id}', 99, '[\"x\"]', '{new string('d', 64)}')";
        // rows can only be inserted while the batch is being staged (state 2) ...
        Assert.Null(await Fails(rawRow));
        // ... the content of a staged row is immutable, and nothing is validated while staging
        Assert.Equal("P0001", await Fails($"UPDATE {Ir.Table} SET {Ir.Raw} = '[\"y\"]' WHERE {Ir.BatchId} = '{b.Id}' AND {Ir.RowNo} = 99"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ir.Table} SET {Ir.Status} = 2 WHERE {Ir.BatchId} = '{b.Id}' AND {Ir.RowNo} = 99"));
        await Force(b.Id, ImportStatus.Mapped);
        Assert.Equal("P0001", await Fails(rawRow.Replace(", 99,", ", 98,")));                                                        // no new rows once mapped
        Assert.Null(await Fails($"UPDATE {Ir.Table} SET {Ir.Status} = 2, {Ir.Normalized} = '{{}}' WHERE {Ir.BatchId} = '{b.Id}' AND {Ir.RowNo} = 99"));
        Assert.Equal("P0001", await Fails($"UPDATE {Ir.Table} SET {Ir.RowHash} = '{new string('e', 64)}' WHERE {Ir.BatchId} = '{b.Id}' AND {Ir.RowNo} = 99"));
        Assert.Equal("23514", await Fails($"UPDATE {Ir.Table} SET {Ir.Status} = 42 WHERE {Ir.BatchId} = '{b.Id}' AND {Ir.RowNo} = 99"));      // a constraint on the value
        await Force(b.Id, ImportStatus.Validated, ImportStatus.Submitted);
        Assert.Equal("P0001", await Fails($"UPDATE {Ir.Table} SET {Ir.Status} = 3 WHERE {Ir.BatchId} = '{b.Id}' AND {Ir.RowNo} = 99"));        // frozen from submission on
        Assert.Equal("P0001", await Fails($"DELETE FROM {Ir.Table} WHERE {Ir.BatchId} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Ir.Table}"));
        // the history is append-only and bound to a real audit event
        Assert.Equal("P0001", await Fails($"UPDATE {Iev.Table} SET {Iev.Kind} = 2"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Iev.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Iev.Table}"));
        Assert.Equal("23503", await Fails($"INSERT INTO {Iev.Table} ({Iev.BatchId}, {Iev.Kind}, {Iev.Revision}, {Iev.AtUtc}, {Iev.Detail}, {Iev.AuditStream}, {Iev.AuditSeq}) VALUES ('{b.Id}', 1, 1, now(), '{{}}', 'business:x', 999999)"));
        // settings: bounded values, revision +1, never deleted
        Assert.Equal("23514", await Fails($"INSERT INTO {Ist.Table} ({Ist.RetentionDays}, {Ist.PendingExpiryDays}, {Ist.Revision}, {Ist.UpdatedAtUtc}) VALUES (0, 30, 1, now())"));
        Ok(await Imports.PutSettingsAsync(Admin.Id, Company.Id, 0, new ImportSettingsInput(10, 20)));
        Assert.Equal("P0001", await Fails($"UPDATE {Ist.Table} SET {Ist.RetentionDays} = 11, {Ist.Revision} = {Ist.Revision} + 5"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {Ist.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {Ist.Table}"));
    }

    [PostgresFact]
    public async Task Value_constraints_reject_malformed_evidence_and_duplicate_idempotency_keys_and_storage_names()
    {
        await PrepareImports();
        var b = await Upload(key: "k-1");
        string Insert(string overrides, string sha = "", string storage = "", string key = "NULL", string adapter = "test-adapter", string kind = "1", string status = "1") =>
            $"INSERT INTO {Ib.Table} ({Ib.AdapterCode}, {Ib.AdapterVersion}, {Ib.Status}, {Ib.FileName}, {Ib.FileKind}, {Ib.FileSize}, {Ib.FileSha256}, {Ib.StorageName}, {Ib.KeyId}, {Ib.IdempotencyKey}, {Ib.CreatedBy}, {Ib.CreatedAtUtc}, {Ib.UpdatedAtUtc}, {Ib.ExpiresAtUtc}) " +
            $"VALUES ('{adapter}', 1, {status}, 'f.csv', {kind}, 10, '{(sha.Length == 0 ? new string('a', 64) : sha)}', '{(storage.Length == 0 ? Guid.NewGuid().ToString("N") + ".enc" : storage)}', 'k1', {key}, gen_random_uuid(), now(), now(), now())";
        Assert.Null(await Fails(Insert("")));
        Assert.Equal("23514", await Fails(Insert("", sha: "xyz")));
        Assert.Equal("23514", await Fails(Insert("", sha: new string('A', 64))));                    // lowercase hex only
        Assert.Equal("23514", await Fails(Insert("", storage: "../evil.enc")));
        Assert.Equal("23514", await Fails(Insert("", adapter: "Bad Adapter")));
        Assert.Equal("23514", await Fails(Insert("", kind: "3")));
        Assert.Equal("23514", await Fails(Insert("", status: "13")));
        Assert.Equal("23505", await Fails(Insert("", key: "'k-1'")));                                // idempotency key is unique
        Assert.Equal("23505", await Fails(Insert("", storage: b.Id.ToString("N") + ".enc")));       // storage name is unique
    }
}

public class ImportServiceTests : ImportTestBase
{
    [PostgresFact]
    public async Task An_upload_is_validated_encrypted_before_storage_recorded_with_its_evidence_and_staged_reproducibly_without_touching_anything_else()
    {
        await PrepareImports();
        var text = "code,name\n0101,Cash السري\n0102,Bank\n";
        var data = Bytes(text);
        var b = await Upload(text);
        Assert.Equal((ImportStatus.Uploaded, ImportFileKind.Csv, (long)data.Length, 1L, "k1"), (b.Status, b.FileKind, b.FileSize, b.Revision, b.KeyId));
        Assert.Equal(Hex(data), b.FileSha256);
        Assert.True(b.OriginalAvailable);
        var file = Assert.Single(StoredFiles());
        Assert.Equal(b.Id.ToString("N") + ".enc", Path.GetFileName(file));
        var stored = File.ReadAllBytes(file);
        Assert.True(stored.AsSpan().IndexOf(Bytes("Bank")) < 0 && stored.AsSpan().IndexOf(Bytes("السري")) < 0);   // ciphertext only: no persistent plaintext
        Assert.Equal(data, Enc.Decrypt(stored, $"hasm-import:{Company.Id:N}:{b.Id:N}"));
        Assert.Equal(0L, await Rows(b.Id));
        // staging
        var s = Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, new StageInput(0, 1, "utf-8", null)));
        Assert.Equal((ImportStatus.Staged, 2), (s.Status, s.RowCount));
        Assert.NotNull(s.RowsHash);
        var page = (await Imports.RowsAsync(Company.Id, b.Id, 0, 100, null))!;
        Assert.Equal(new[] { "code", "name" }, page.Headers.ToArray());
        Assert.Equal(2, page.Total);
        Assert.Equal(new[] { "0101", "Cash السري" }, page.Rows[0].Cells);                                 // leading zero and Arabic preserved
        Assert.Equal(new[] { 2, 3 }, page.Rows.Select(r => r.RowNo));
        // re-staging the same file with the same options reproduces the same hash; other options replace the rows and the hash
        var again = Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, s.Revision, new StageInput(0, 1, "utf-8", null)));
        Assert.Equal(s.RowsHash, again.RowsHash);
        var other = Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, again.Revision, new StageInput(0, 2, "utf-8", null)));
        Assert.NotEqual(s.RowsHash, other.RowsHash);
        Assert.Equal((1, 1L), (other.RowCount, await Rows(b.Id)));
        // nothing outside the import tables exists: the framework has no business tables to touch and the history records every step
        var events = await Imports.EventsAsync(Company.Id, b.Id);
        Assert.Equal([ImportEventKind.Uploaded, ImportEventKind.Staged, ImportEventKind.Staged, ImportEventKind.Staged], events.Select(e => e.Kind));
        Assert.Equal(4L, await Sql<long>($"SELECT count(*) FROM {Iev.Table} WHERE {Iev.BatchId} = '{b.Id}'"));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Xlsx_files_stage_with_sheet_selection_and_identifier_formats_and_hostile_or_invalid_uploads_leave_nothing_behind()
    {
        await PrepareImports();
        var xlsx = new XlsxBuilder().Sheet("Notes", [["ignore me"]]).Sheet("Accounts", [["code", "name"], [new XlsxBuilder.Cell("A2", 101, 1), "Cash"]]).Build();
        var b = Ok(await Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "chart.xlsx", xlsx, null));
        Assert.Equal((ImportFileKind.Xlsx, 2), (b.FileKind, b.Sheets.Count));
        Assert.Equal(["Notes", "Accounts"], b.Sheets.Select(s => s.Name));
        var s = Ok(await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, new StageInput(1, 1, null, null)));
        Assert.Equal(new[] { "0101", "Cash" }, (await Imports.RowsAsync(Company.Id, b.Id, 0, 10, null))!.Rows[0].Cells);
        Assert.Equal("invalid_worksheet", Assert.Single((await Imports.StageAsync(Admin.Id, Company.Id, b.Id, s.Revision, new StageInput(5, 1, null, null))).Fields!).Code);
        // every refusal records nothing and stores nothing
        var batches = await Sql<long>($"SELECT count(*) FROM {Ib.Table}"); var audits = await Count(Company, Audit_.Table); var files = StoredFiles().Length;
        var formula = new XlsxBuilder().Sheet("S", [["a"], [new XlsxBuilder.Cell("A2", 1, 0, "1+1")]]).Build();
        var bad = new (string Name, byte[] Data, string Code)[]
        {
            ("f.xlsx", formula, "formula_not_allowed"), ("f.xls", xlsx, "unsupported_format"), ("f.xlsm", xlsx, "unsupported_format"), ("f.csv", xlsx, "unsupported_format"), ("f.xlsx", Bytes("a,b"), "unsupported_format"),
            ("f.csv", [], "empty_file"), ("f.xlsx", xlsx[..(xlsx.Length / 2)], "malformed_file"),
        };
        foreach (var (name, data, code) in bad)
        {
            var r = await Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", name, data, null);
            Assert.Equal((ConfigStatus.Invalid, "file", code), (r.Status, r.Fields!.Single().Field, r.Fields!.Single().Code));
        }
        Assert.Equal("unknown_adapter", (await Imports.UploadAsync(Admin.Id, Company.Id, "nope", "f.csv", Bytes(Csv2), null)).Fields!.Single().Code);
        Assert.Equal((batches, audits, files), (await Sql<long>($"SELECT count(*) FROM {Ib.Table}"), await Count(Company, Audit_.Table), StoredFiles().Length));
        // a file that is accepted by the upload but is hostile when read (a CSV with an invalid character) is refused at staging and the batch stays as it was
        var c = await Upload("a,b\n1\u0007,2\n");
        var refused = await Imports.StageAsync(Admin.Id, Company.Id, c.Id, c.Revision, new StageInput(0, 1, "utf-8", null));
        Assert.Equal("invalid_character", refused.Fields!.Single().Code);
        Assert.Equal((ImportStatus.Uploaded, c.Revision), ((await Reload(c.Id)).Status, (await Reload(c.Id)).Revision));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Upload_limits_are_server_enforced_and_configurable()
    {
        await PrepareImports(new ImportOptions { MaxFileBytes = 40, MaxRows = 2, MaxColumns = 2, MaxCellChars = 8 });
        Assert.Equal("file_too_large", (await Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "a.csv", Bytes(new string('x', 41)), null)).Fields!.Single().Code);
        var rows = await Upload("a,b\n1,2\n3,4\n5,6\n");
        Assert.Equal("too_many_rows", (await Imports.StageAsync(Admin.Id, Company.Id, rows.Id, rows.Revision, new StageInput(0, 1, null, null))).Fields!.Single().Code);
        var cols = await Upload("a,b,c\n1,2,3\n");
        Assert.Equal("too_many_columns", (await Imports.StageAsync(Admin.Id, Company.Id, cols.Id, cols.Revision, new StageInput(0, 1, null, null))).Fields!.Single().Code);
        var cell = await Upload("a\n123456789\n");
        Assert.Equal("cell_too_long", (await Imports.StageAsync(Admin.Id, Company.Id, cell.Id, cell.Revision, new StageInput(0, 1, null, null))).Fields!.Single().Code);
        Assert.Equal(ImportStatus.Uploaded, (await Reload(rows.Id)).Status);                          // a refused staging changes nothing
        var defaults = new ImportOptions();
        Assert.Equal((10L * 1024 * 1024, 50_000), (defaults.MaxFileBytes, defaults.MaxRows));
    }

    [PostgresFact]
    public async Task A_repeated_upload_with_the_same_idempotency_key_returns_the_first_batch_and_races_create_exactly_one()
    {
        await PrepareImports();
        var first = await Upload(key: "client-key-1");
        var again = await Upload(key: "client-key-1");
        Assert.Equal(first.Id, again.Id);
        Assert.Single(StoredFiles());
        Assert.Equal("idempotency_key_reused", (await Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "chart.csv", Bytes("a\n1\n"), "client-key-1")).Code);           // other content
        var (other, _) = await Member([PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.AccountsCreate], ScopeKind.Company, null);
        Assert.Equal("idempotency_key_reused", (await Imports.UploadAsync(other.Id, Company.Id, "test-adapter", "chart.csv", Bytes(Csv2), "client-key-1")).Code);                // other user
        Assert.Equal("invalid", (await Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "chart.csv", Bytes(Csv2), new string('k', 101))).Fields!.Single().Code);
        var race = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "chart.csv", Bytes("race\n1\n"), "race-key")));
        Assert.True(race.Count(r => r.IsOk) >= 1);
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ib.Table} WHERE {Ib.IdempotencyKey} = 'race-key'"));
        Assert.Equal(2, StoredFiles().Length);                                                          // the loser's ciphertext was removed again: no orphan
        Assert.All(race.Where(r => !r.IsOk), r => Assert.Equal("idempotency_key_reused", r.Code));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_tampered_swapped_missing_or_unreadable_original_is_never_staged_and_never_decrypted()
    {
        await PrepareImports();
        var a = await Upload(); var b = await Upload("x,y\n1,2\n", "b.csv");
        var fileA = Path.Combine(CompanyDir, a.Id.ToString("N") + ".enc"); var fileB = Path.Combine(CompanyDir, b.Id.ToString("N") + ".enc");
        // one flipped byte
        var bytes = File.ReadAllBytes(fileA); bytes[^20] ^= 1; File.WriteAllBytes(fileA, bytes);
        Assert.Equal("original_corrupted", (await Imports.StageAsync(Admin.Id, Company.Id, a.Id, a.Revision, new StageInput(0, 1, null, null))).Code);
        // another batch's ciphertext swapped in (it is bound to its own batch)
        File.Copy(fileB, fileA, overwrite: true);
        Assert.Equal("original_corrupted", (await Imports.StageAsync(Admin.Id, Company.Id, a.Id, a.Revision, new StageInput(0, 1, null, null))).Code);
        // missing
        File.Delete(fileA);
        Assert.Equal("original_unavailable", (await Imports.StageAsync(Admin.Id, Company.Id, a.Id, a.Revision, new StageInput(0, 1, null, null))).Code);
        Assert.Equal((ImportStatus.Uploaded, a.Revision), ((await Reload(a.Id)).Status, (await Reload(a.Id)).Revision));
        // key store unavailable: fail closed (an exception, never plaintext, nothing changed)
        Ring.Unavailable = true;
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, new StageInput(0, 1, null, null)));
        Assert.Equal(ImportStatus.Uploaded, (await Reload(b.Id)).Status);
        Ring.Unavailable = false;
        Assert.True((await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision, new StageInput(0, 1, null, null))).IsOk);
    }

    [PostgresFact]
    public async Task Missing_encryption_or_storage_configuration_fails_closed_and_records_nothing()
    {
        await PrepareImports();
        Ring.Unavailable = true;
        var ex = await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "a.csv", Bytes(Csv2), null));
        Assert.Equal("encryption_key_unavailable", ex.Code);
        Ring.Unavailable = false;
        var unconfigured = new ImportService(Resolver, Tx, Enc, new EncryptedFileStore(new ImportOptions { StorageDirectory = null }, TestEncryption), ImportOpts, Registry, Clock);
        Assert.Equal("import_storage_not_configured", (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => unconfigured.UploadAsync(Admin.Id, Company.Id, "test-adapter", "a.csv", Bytes(Csv2), null))).Code);
        var relative = new ImportService(Resolver, Tx, Enc, new EncryptedFileStore(new ImportOptions { StorageDirectory = "relative/dir" }, TestEncryption), ImportOpts, Registry, Clock);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => relative.UploadAsync(Admin.Id, Company.Id, "test-adapter", "a.csv", Bytes(Csv2), null));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ib.Table}"));
        Assert.Empty(StoredFiles());
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Revisions_cancellation_and_renewal_follow_the_rules_and_every_change_is_audited()
    {
        await PrepareImports();
        var b = await Staged();
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Imports.StageAsync(Admin.Id, Company.Id, b.Id, null, new StageInput(0, 1, null, null))).Status);
        var stale = await Imports.StageAsync(Admin.Id, Company.Id, b.Id, b.Revision - 1, new StageInput(0, 1, null, null));
        Assert.Equal((ConfigStatus.PreconditionFailed, b.Revision), (stale.Status, stale.CurrentRevision));
        Assert.Equal("revision_conflict", (await Imports.CancelAsync(Admin.Id, Company.Id, b.Id, b.Revision - 1)).Code);
        // renewal extends the expiry; a validated batch must be validated again
        Clock.Advance(TimeSpan.FromDays(10));
        var renewed = Ok(await Imports.RenewAsync(Admin.Id, Company.Id, b.Id, b.Revision));
        Assert.Equal(Clock.GetUtcNow().AddDays(30).UtcDateTime, renewed.ExpiresAtUtc.UtcDateTime, TimeSpan.FromSeconds(2));
        Assert.Equal(ImportStatus.Staged, renewed.Status);
        await Force(b.Id, ImportStatus.Mapped, ImportStatus.Validated, ImportStatus.Submitted);
        var current = await Reload(b.Id);
        var revalidate = Ok(await Imports.RenewAsync(Admin.Id, Company.Id, b.Id, current.Revision));
        Assert.Equal((ImportStatus.Mapped, null, null), (revalidate.Status, revalidate.SubmittedBy, revalidate.PlanHash));    // the approval chain restarts
        // cancellation: terminal, final, and impossible while applying
        var cancelled = Ok(await Imports.CancelAsync(Admin.Id, Company.Id, b.Id, revalidate.Revision));
        Assert.Equal(ImportStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.TerminalAtUtc);
        Assert.Equal("batch_finished", (await Imports.CancelAsync(Admin.Id, Company.Id, b.Id, cancelled.Revision)).Code);
        Assert.Equal("batch_not_stageable", (await Imports.StageAsync(Admin.Id, Company.Id, b.Id, cancelled.Revision, new StageInput(0, 1, null, null))).Code);
        Assert.Equal("batch_not_renewable", (await Imports.RenewAsync(Admin.Id, Company.Id, b.Id, cancelled.Revision)).Code);
        var running = await Staged();
        await Force(running.Id, ImportStatus.Mapped, ImportStatus.Validated, ImportStatus.Submitted, ImportStatus.Approved, ImportStatus.Applying);
        Assert.Equal("batch_applying", (await Imports.CancelAsync(Admin.Id, Company.Id, running.Id, (await Reload(running.Id)).Revision)).Code);
        Assert.Equal("import_not_found", (await Imports.CancelAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1)).Code);
        Assert.Contains(ImportEventKind.Cancelled, (await Imports.EventsAsync(Company.Id, b.Id)).Select(e => e.Kind));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Concurrent_settings_writes_with_the_same_if_match_have_one_winner_and_412_for_the_others()
    {
        // C2 matrix part 2: no settings row yet, seven parallel writers with If-Match 0 used to give 503 (an unmapped unique violation on the singleton)
        await PrepareImports();
        var before = await Count(Company, Audit_.Table);
        // deterministic (#165 G L1): every writer is held between its revision read and its write until all seven have read, so all of them race for the same first write
        var gate = new StartBarrier(7, TimeSpan.FromSeconds(60));
        Imports.AfterSettingsReadForTests = gate.WaitAsync;
        var first = await Task.WhenAll(Enumerable.Range(0, 7).Select(_ => Task.Run(() => Imports.PutSettingsAsync(Admin.Id, Company.Id, 0, new ImportSettingsInput(30, 14)))));
        Assert.Equal(7, gate.Arrived);
        Assert.Single(first, r => r.IsOk);
        Assert.All(first.Where(r => !r.IsOk), r => Assert.Equal((ConfigStatus.PreconditionFailed, "revision_conflict", (long?)1), (r.Status, r.Code, r.CurrentRevision)));
        // and the same for an update at a known revision (the update applies only at the expected revision, so no write is lost)
        gate = new StartBarrier(7, TimeSpan.FromSeconds(60));
        Imports.AfterSettingsReadForTests = gate.WaitAsync;
        var second = await Task.WhenAll(Enumerable.Range(0, 7).Select(i => Task.Run(() => Imports.PutSettingsAsync(Admin.Id, Company.Id, 1, new ImportSettingsInput(40 + i, 14)))));
        Assert.Equal(7, gate.Arrived);
        Imports.AfterSettingsReadForTests = null;
        var winner = Assert.Single(second, r => r.IsOk);
        Assert.All(second.Where(r => !r.IsOk), r => Assert.Equal((ConfigStatus.PreconditionFailed, "revision_conflict", (long?)2), (r.Status, r.Code, r.CurrentRevision)));
        var now = await Imports.GetSettingsAsync(Company.Id);
        Assert.Equal((winner.Value!.RetentionDays, 2L), (now.RetentionDays, now.Revision));
        Assert.Equal(2L, await Sql<long>($"SELECT count(*) FROM {Ph.Table} WHERE {Ph.Entity} = 6"));            // one history row and one audit event per successful write
        Assert.Equal(before + 2, await Count(Company, Audit_.Table));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Concurrent_uploads_with_the_same_key_and_file_replay_one_batch_and_a_different_file_is_refused()
    {
        // C2 matrix part 2: seven parallel identical uploads with one Idempotency-Key used to give some 409 idempotency_key_reused instead of the documented replay
        await PrepareImports();
        var data = Bytes(Csv2);
        var results = await Task.WhenAll(Enumerable.Range(0, 7).Select(_ => Task.Run(() => Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "chart.csv", data, "same-key"))));
        Assert.All(results, r => Assert.True(r.IsOk, $"{r.Status} {r.Code}"));
        var id = results[0].Value!.Id;
        Assert.All(results, r => Assert.Equal(id, r.Value!.Id));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ib.Table}"));
        Assert.Single(StoredFiles());                                                                               // the losers' ciphertext is not left behind
        // the same key with a different file, or by another user, is still refused
        var other = await Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", "chart.csv", Bytes(Csv2 + "0103,Other\n"), "same-key");
        Assert.Equal((ConfigStatus.Conflict, "idempotency_key_reused"), (other.Status, other.Code));
        Assert.Single(StoredFiles());
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Company_settings_are_revisioned_validated_audited_and_recorded_in_the_policy_history()
    {
        await PrepareImports();
        var def = await Imports.GetSettingsAsync(Company.Id);
        Assert.Equal((30, 30, 0L, false), (def.RetentionDays, def.PendingExpiryDays, def.Revision, def.Configured));
        var before = await Count(Company, Audit_.Table);
        var set = Ok(await Imports.PutSettingsAsync(Admin.Id, Company.Id, 0, new ImportSettingsInput(14, 45)));
        Assert.Equal((14, 45, 1L, true), (set.RetentionDays, set.PendingExpiryDays, set.Revision, set.Configured));
        Assert.Equal(before + 1, await Count(Company, Audit_.Table));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {Ph.Table} WHERE {Ph.Entity} = 6"));
        Assert.Equal(1L, Ok(await Imports.PutSettingsAsync(Admin.Id, Company.Id, 1, new ImportSettingsInput(14, 45))).Revision);          // unchanged: no revision, no event
        Assert.Equal(before + 1, await Count(Company, Audit_.Table));
        Assert.Equal(ConfigStatus.PreconditionFailed, (await Imports.PutSettingsAsync(Admin.Id, Company.Id, 0, new ImportSettingsInput(7, 7))).Status);
        Assert.Equal(ConfigStatus.PreconditionRequired, (await Imports.PutSettingsAsync(Admin.Id, Company.Id, null, new ImportSettingsInput(7, 7))).Status);
        foreach (var bad in new[] { new ImportSettingsInput(0, 30), new ImportSettingsInput(30, 0), new ImportSettingsInput(4000, 30), new ImportSettingsInput(null, 30), null })
            Assert.Equal(ConfigStatus.Invalid, (await Imports.PutSettingsAsync(Admin.Id, Company.Id, 1, bad)).Status);
        // a new batch uses the configured pending-expiry period
        var b = await Upload();
        Assert.Equal(Clock.GetUtcNow().AddDays(45).UtcDateTime, b.ExpiresAtUtc.UtcDateTime, TimeSpan.FromSeconds(2));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task A_rewrap_that_cannot_move_every_original_reports_failures_and_that_the_old_keys_must_be_kept()
    {
        // D-180 (register item K follow-up): failures are counted, never skipped silently, and the outcome says whether the old keys may be retired
        await PrepareImports();
        var moved = await Upload(); var lost = await Upload("x,y\n1,2\n", "lost.csv");
        Ring.Add("k2", RandomNumberGenerator.GetBytes(32)); Ring.Activate("k2");
        var stranded = await Upload("p,q\n3,4\n", "stranded.csv");                                                         // under k2
        Ring.Add("k3", RandomNumberGenerator.GetBytes(32)); Ring.Activate("k3");
        File.Delete(Path.Combine(CompanyDir, lost.Id.ToString("N") + ".enc"));                                              // the original is missing
        Ring.Remove("k2");                                                                                                    // the stranded original's key is unavailable
        var r = await Imports.RewrapAsync(Admin.Id, Company.Id);
        Assert.Equal((1, 2), (r.Rewrapped, r.Failed));
        Assert.False(r.OldKeysMayBeRetired);
        Assert.Equal(2, r.Usage.UnderOtherKeys);                                                                              // the lost row still names k1, the stranded file is under k2
        Assert.Equal(["k1", "k2", "k3"], r.Usage.KeyIdsInUse);
        Assert.Equal("k3", (await Reload(moved.Id)).KeyId);
        Assert.Equal("k2", (await Reload(stranded.Id)).KeyId);                                                                // untouched
        await AssertAuditIntact();
    }

    private EncryptionKeyRetirementService RetirementReport() => new(Options,
        new Hasm.Infrastructure.Attachments.AttachmentOptions { StorageDirectory = Path.Combine(StorageDir, "attachments") }, ImportOpts, TestEncryption);
    private static KeyRetirementEntry KeyOf(KeyRetirementReport r, string id) => r.Keys.Single(k => k.KeyId == id);

    [PostgresFact]
    public async Task The_key_retirement_report_keeps_every_key_a_live_import_original_needs_and_is_incomplete_without_a_real_backup_root()
    {
        // D-181: a key that only an import original still needs is NOT retirable; after a complete rewrap no import original needs it (backups do not contain import originals, D-179).
        // Review H1 of PR 182: an EMPTY folder is not a backup root (it proves nothing about the retained runs), so the report stays incomplete and nothing is retirable; the complete
        // report over a real, verified run is BackupRetentionTests.The_key_retirement_report_is_complete_only_over_a_real_complete_verified_run_and_with_the_scope_attested.
        await PrepareImports();
        var a = await Upload();
        Ring.Add("k2", RandomNumberGenerator.GetBytes(32)); Ring.Activate("k2");
        var backups = Path.Combine(StorageDir, "..", "hasm-keyreport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backups);
        try
        {
            var service = RetirementReport();
            var before = await service.ReportAsync(["k1", "k2"], "k2", [backups], null, backupScopeAttested: true);
            Assert.Equal(["backup_inventory_incomplete:backup_root_no_complete_verified_run"], before.Blockers);              // the empty folder is the ONLY blocker
            Assert.False(before.Complete);
            Assert.All(before.Keys, k => Assert.False(k.Retirable));
            Assert.Equal(["needed_by_import_originals", "report_incomplete"], KeyOf(before, "k1").Reasons);
            Assert.Equal(["active_key", "report_incomplete"], KeyOf(before, "k2").Reasons);
            Assert.True((await Imports.RewrapAsync(Admin.Id, Company.Id)).OldKeysMayBeRetired);
            var after = await service.ReportAsync(["k1", "k2"], "k2", [backups], null, backupScopeAttested: true);
            Assert.Equal(["report_incomplete"], KeyOf(after, "k1").Reasons);                                                    // no import original needs k1 any more
            Assert.All(after.Keys, k => Assert.False(k.Retirable));                                                             // still nothing retirable: no real backup root
            // a root that does not exist (a typo, an unmounted share) is a blocker of its own, and so is giving no root at all
            var typo = await service.ReportAsync(["k1", "k2"], "k2", [backups + "-typo"], null, backupScopeAttested: true);
            Assert.Equal(["backup_inventory_incomplete:backup_root_missing"], typo.Blockers);
            Assert.All(typo.Keys, k => Assert.False(k.Retirable));
            var noRoot = await service.ReportAsync(["k1", "k2"], "k2", [], null, backupScopeAttested: true);
            Assert.Equal(["no_backup_root"], noRoot.Blockers);
            Assert.All(noRoot.Keys, k => Assert.False(k.Retirable));
            Assert.Empty(Directory.GetFileSystemEntries(backups));                                                              // read-only: nothing was written into the root
            Assert.False(Directory.Exists(Path.Combine(StorageDir, "attachments")));                                            // and no storage folder was created (review L2)
        }
        finally { Directory.Delete(backups, true); }
    }

    [PostgresFact]
    public async Task A_corrupt_import_original_or_a_missing_storage_folder_makes_the_key_retirement_report_incomplete()
    {
        // review M1 of PR 182: the wiring of the envelope read (ImportService.KeyUsageAsync through the report service), not only the builder's rule
        await PrepareImports();
        var good = await Upload(); var bad = await Upload("x,y\n1,2\n", "bad.csv");
        Ring.Add("k2", RandomNumberGenerator.GetBytes(32)); Ring.Activate("k2");
        var service = RetirementReport();
        var blocker = $"import_original_key_unknown:{Company.Id:N}:1";
        Assert.DoesNotContain(blocker, (await service.ReportAsync(["k1", "k2"], "k2", [], null, backupScopeAttested: true)).Blockers);
        File.WriteAllBytes(Path.Combine(CompanyDir, bad.Id.ToString("N") + ".enc"), [1, 2, 3]);                               // not an envelope: its key cannot be determined
        var r = await service.ReportAsync(["k1", "k2"], "k2", [], null, backupScopeAttested: true);
        Assert.False(r.Complete);
        Assert.Contains(blocker, r.Blockers);
        Assert.All(r.Keys, k => Assert.False(k.Retirable));
        Assert.Contains("needed_by_import_originals", KeyOf(r, "k1").Reasons);                                                // the rows still name k1
        Assert.Equal(1, (await Imports.RewrapAsync(Admin.Id, Company.Id)).Usage.Unknown);                                      // the rewrap signal sees the same file
        Assert.Equal("k2", (await Reload(good.Id)).KeyId);

        // the whole company folder gone (an unmounted or misconfigured storage) is NOT "no files": every original is unknown, and the read-only scan does not create the folder
        var moved = CompanyDir + ".moved";
        Directory.Move(CompanyDir, moved);
        try
        {
            var gone = await service.ReportAsync(["k1", "k2"], "k2", [], null, backupScopeAttested: true);
            Assert.Contains($"import_original_key_unknown:{Company.Id:N}:2", gone.Blockers);
            Assert.False(Directory.Exists(CompanyDir));
        }
        finally { Directory.Move(moved, CompanyDir); }
    }

    [PostgresFact]
    public async Task An_import_original_whose_envelope_names_another_key_than_its_row_keeps_both_keys_non_retirable()
    {
        // review M1 of PR 182: a rewrap that crashed between the file replacement and the row update leaves the file under the new key and the row naming the old one; BOTH stay needed
        await PrepareImports();
        var a = await Upload();
        Ring.Add("k2", RandomNumberGenerator.GetBytes(32)); Ring.Activate("k2");
        Ring.Add("k3", RandomNumberGenerator.GetBytes(32)); Ring.Activate("k3");
        var service = RetirementReport();
        var before = await service.ReportAsync(["k1", "k2", "k3"], "k3", [], null, backupScopeAttested: true);
        Assert.Contains("needed_by_import_originals", KeyOf(before, "k1").Reasons);
        Assert.DoesNotContain("needed_by_import_originals", KeyOf(before, "k2").Reasons);
        var underK2 = new EnvelopeEncryptor(new InMemoryKekRing("k2")).Encrypt(Bytes(Csv2), "any");                            // only the header is read: the key id is k2
        File.WriteAllBytes(Path.Combine(CompanyDir, a.Id.ToString("N") + ".enc"), underK2);
        Assert.Equal("k1", (await Reload(a.Id)).KeyId);                                                                        // the row still names k1
        var r = await service.ReportAsync(["k1", "k2", "k3"], "k3", [], null, backupScopeAttested: true);
        Assert.Contains("needed_by_import_originals", KeyOf(r, "k1").Reasons);                                                 // the row's key
        Assert.Contains("needed_by_import_originals", KeyOf(r, "k2").Reasons);                                                 // the file's key
        Assert.DoesNotContain(r.Blockers, b => b.StartsWith("import_original_key_unknown", StringComparison.Ordinal));
        Assert.All(r.Keys, k => Assert.False(k.Retirable));
    }

    [PostgresFact]
    public async Task Key_rotation_rewraps_every_stored_original_under_the_active_key_atomically_audited_and_repeatably()
    {
        await PrepareImports();
        var a = await Staged(); var b = await Staged("x,y\n1,2\n", "b.csv");
        Ring.Add("k2", RandomNumberGenerator.GetBytes(32)); Ring.Activate("k2");
        Assert.True((await Imports.StageAsync(Admin.Id, Company.Id, a.Id, a.Revision, new StageInput(0, 1, null, null))).IsOk);       // old files stay readable during rotation
        var result = await Imports.RewrapAsync(Admin.Id, Company.Id);
        Assert.Equal(2, result.Rewrapped);
        Assert.Equal((0, 0, 0), (result.Failed, result.Usage.UnderOtherKeys, result.Usage.Unknown));                          // D-180: the retirability signal
        Assert.True(result.OldKeysMayBeRetired);
        Assert.Equal(["k2"], result.Usage.KeyIdsInUse);
        foreach (var id in new[] { a.Id, b.Id })
        {
            Assert.Equal("k2", (await Reload(id)).KeyId);
            Assert.Equal("k2", EnvelopeEncryptor.KeyIdOf(File.ReadAllBytes(Path.Combine(CompanyDir, id.ToString("N") + ".enc"))));
            Assert.Contains(ImportEventKind.Rewrapped, (await Imports.EventsAsync(Company.Id, id)).Select(e => e.Kind));
        }
        Assert.Equal(0, (await Imports.RewrapAsync(Admin.Id, Company.Id)).Rewrapped);                                            // idempotent
        Ring.Remove("k1");                                                                                                          // the retired key is no longer needed
        var bNow = await Reload(b.Id);
        Assert.True((await Imports.StageAsync(Admin.Id, Company.Id, b.Id, bNow.Revision, new StageInput(0, 1, null, null))).IsOk);
        Assert.Equal(Hex(Bytes(Csv2)), (await Reload(a.Id)).FileSha256);                                                        // the evidence hash never changes
        Assert.Empty(Directory.GetFiles(CompanyDir, "*.tmp-*"));                                                                  // no temporary leftovers
        await AssertAuditIntact();
    }
}

public class ImportRetentionTests : ImportTestBase
{
    [PostgresFact]
    public async Task Pending_batches_expire_finished_batches_are_purged_after_retention_and_hashes_history_and_audit_survive()
    {
        await PrepareImports();
        var cancelled = await Staged();
        Ok(await Imports.CancelAsync(Admin.Id, Company.Id, cancelled.Id, cancelled.Revision));
        var pending = await Upload("p\n1\n", "p.csv");
        Clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(new ImportMaintenanceResult(0, 0, 0), await Imports.MaintainAsync(Admin.Id, Company.Id));                   // nothing is due yet
        Assert.Equal(2, StoredFiles().Length);
        Clock.Advance(TimeSpan.FromDays(2));                                                                                     // day 31: the pending batch expired (uploaded at day 0); the cancelled one is 31 days past its end
        var first = await Imports.MaintainAsync(Admin.Id, Company.Id);
        Assert.Equal(1, first.Expired);
        Assert.Equal(1, first.Purged);                                                                                           // the cancelled batch (terminal 31 days ago) was purged
        var c = await Reload(cancelled.Id);
        Assert.False(c.OriginalAvailable); Assert.False(c.RowsAvailable);
        Assert.Equal((0L, c.FileSha256, c.RowsHash), (await Rows(cancelled.Id), Hex(Bytes(Csv2)), c.RowsHash));                    // rows gone, evidence kept
        Assert.NotNull(c.RowsHash);
        var e = await Reload(pending.Id);
        Assert.Equal(ImportStatus.Expired, e.Status);
        Assert.True(e.OriginalAvailable);                                                                                        // expired, retained for another retention period
        Assert.Single(StoredFiles());
        Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(new ImportMaintenanceResult(0, 1, 0), await Imports.MaintainAsync(Admin.Id, Company.Id));
        Assert.Empty(StoredFiles());
        Assert.Equal(new ImportMaintenanceResult(0, 0, 0), await Imports.MaintainAsync(Admin.Id, Company.Id));                   // repeatable
        var kinds = (await Imports.EventsAsync(Company.Id, cancelled.Id)).Select(x => x.Kind).ToArray();
        Assert.Equal([ImportEventKind.Uploaded, ImportEventKind.Staged, ImportEventKind.Cancelled, ImportEventKind.Purged], kinds);
        Assert.Equal(new[] { ImportEventKind.Uploaded, ImportEventKind.Expired, ImportEventKind.Purged }, (await Imports.EventsAsync(Company.Id, pending.Id)).Select(x => x.Kind));
        Assert.Equal((c.FileSha256, c.RowsHash), ((await Reload(cancelled.Id)).FileSha256, (await Reload(cancelled.Id)).RowsHash));
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Renewal_and_the_company_retention_setting_change_when_things_expire_and_are_purged()
    {
        await PrepareImports();
        Ok(await Imports.PutSettingsAsync(Admin.Id, Company.Id, 0, new ImportSettingsInput(7, 20)));
        var b = await Staged();
        Clock.Advance(TimeSpan.FromDays(15));
        var renewed = Ok(await Imports.RenewAsync(Admin.Id, Company.Id, b.Id, b.Revision));                                     // expiry now day 35
        Clock.Advance(TimeSpan.FromDays(15));                                                                                    // day 30: renewed batch still pending
        Assert.Equal(0, (await Imports.MaintainAsync(Admin.Id, Company.Id)).Expired);
        Clock.Advance(TimeSpan.FromDays(6));                                                                                     // day 36
        Assert.Equal(1, (await Imports.MaintainAsync(Admin.Id, Company.Id)).Expired);
        Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(0, (await Imports.MaintainAsync(Admin.Id, Company.Id)).Purged);                                             // retention is 7 days after the end, not yet
        Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await Imports.MaintainAsync(Admin.Id, Company.Id)).Purged);
        Assert.Equal(renewed.FileSha256, (await Reload(b.Id)).FileSha256);
    }

    [PostgresFact]
    public async Task A_batch_that_is_applying_or_failed_is_never_expired_cancelled_by_force_or_purged_automatically()
    {
        await PrepareImports();
        var applying = await Staged(); var failed = await Staged("x,y\n1,2\n", "b.csv"); var approved = await Staged("z\n1\n", "c.csv");
        await Force(applying.Id, ImportStatus.Mapped, ImportStatus.Validated, ImportStatus.Submitted, ImportStatus.Approved, ImportStatus.Applying);
        await Force(failed.Id, ImportStatus.Mapped, ImportStatus.Validated, ImportStatus.Submitted, ImportStatus.Approved, ImportStatus.Applying, ImportStatus.Failed);
        await Force(approved.Id, ImportStatus.Mapped, ImportStatus.Validated, ImportStatus.Submitted, ImportStatus.Approved);
        Clock.Advance(TimeSpan.FromDays(400));
        var result = await Imports.MaintainAsync(Admin.Id, Company.Id);
        Assert.Equal((1, 0), (result.Expired, result.Purged));                                                                   // only the not-yet-applied approved batch expires
        Assert.Equal((ImportStatus.Applying, ImportStatus.Failed, ImportStatus.Expired), ((await Reload(applying.Id)).Status, (await Reload(failed.Id)).Status, (await Reload(approved.Id)).Status));
        Assert.True((await Reload(applying.Id)).OriginalAvailable && (await Reload(failed.Id)).OriginalAvailable);
        Assert.Equal(2L, await Rows(applying.Id)); Assert.Equal(1L, await Rows(failed.Id));
        // the failed batch can still be cancelled by a person; after retention it is then purged
        var cancelled = Ok(await Imports.CancelAsync(Admin.Id, Company.Id, failed.Id, (await Reload(failed.Id)).Revision));
        Assert.Equal(ImportStatus.Cancelled, cancelled.Status);
        Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(2, (await Imports.MaintainAsync(Admin.Id, Company.Id)).Purged);                                             // the cancelled and the expired one; the applying batch stays
        Assert.True((await Reload(applying.Id)).OriginalAvailable);
        await AssertAuditIntact();
    }
}

public class ImportAccessTests : ImportTestBase
{
    private static string[] P(params string[] p) => p;

    [PostgresFact]
    public async Task Every_operation_needs_the_import_permission_and_the_adapters_own_business_permission_and_revocation_applies_at_once()
    {
        await PrepareImports();
        var csv = Bytes(Csv2);
        // generic permission only, business permission only: both refused
        var (importOnly, _) = await Member(P(PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsView), ScopeKind.Company, null);
        var (businessOnly, _) = await Member(P(PermissionCatalog.Codes.AccountsCreate, PermissionCatalog.Codes.AccountsView), ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.UploadAsync(importOnly.Id, Company.Id, "test-adapter", "a.csv", csv, null)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.UploadAsync(businessOnly.Id, Company.Id, "test-adapter", "a.csv", csv, null)).Status);
        var (both, roleId) = await Member(P(PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.ImportsCancel, PermissionCatalog.Codes.AccountsCreate, PermissionCatalog.Codes.AccountsView), ScopeKind.Company, null);
        var b = Ok(await IMg.UploadAsync(both.Id, Company.Id, "test-adapter", "a.csv", csv, null));
        Assert.Equal(ConfigStatus.Invalid, (await IMg.UploadAsync(both.Id, Company.Id, "nope", "a.csv", csv, null)).Status);
        // viewing needs the import view permission AND the adapter's view permission
        Assert.Equal(b.Id, Ok(await IMg.GetAsync(both.Id, Company.Id, b.Id)).Id);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.GetAsync(importOnly.Id, Company.Id, b.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.GetAsync(businessOnly.Id, Company.Id, b.Id)).Status);
        // staging needs the upload permission of both kinds; cancelling needs its own permission
        var st = new StageInput(0, 1, null, null);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.StageAsync(businessOnly.Id, Company.Id, b.Id, b.Revision, st)).Status);
        var staged = Ok(await IMg.StageAsync(both.Id, Company.Id, b.Id, b.Revision, st));
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.CancelAsync(importOnly.Id, Company.Id, b.Id, staged.Revision)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.RowsAsync(importOnly.Id, Company.Id, b.Id, 0, 10, null)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.EventsAsync(businessOnly.Id, Company.Id, b.Id)).Status);
        Assert.Equal(2, Ok(await IMg.RowsAsync(both.Id, Company.Id, b.Id, 0, 10, null)).Total);
        // revocation takes effect before the next call
        var assignment = (await Rbac.GetEffectivePermissionsAsync(both.Id, Company.Id)).Grants.Single(g => g.RoleId == roleId).AssignmentId;
        Assert.True((await Rbac.RevokeAssignmentAsync(Admin.Id, Company.Id, assignment)).IsOk);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.CancelAsync(both.Id, Company.Id, b.Id, staged.Revision)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.GetAsync(both.Id, Company.Id, b.Id)).Status);
        Assert.Equal(ImportStatus.Staged, (await Reload(b.Id)).Status);
        // configuration, maintenance and key rotation need the configure permission; the administrator preset can do everything except approve
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.PutSettingsAsync(importOnly.Id, Company.Id, 0, new ImportSettingsInput(5, 5))).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.MaintainAsync(importOnly.Id, Company.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.RewrapAsync(importOnly.Id, Company.Id)).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.GetSettingsAsync(businessOnly.Id, Company.Id)).Status);
        Assert.True((await IMg.PutSettingsAsync(Admin.Id, Company.Id, 0, new ImportSettingsInput(5, 5))).IsOk);
        Assert.True((await IMg.MaintainAsync(Admin.Id, Company.Id)).IsOk && (await IMg.RewrapAsync(Admin.Id, Company.Id)).IsOk);
        var eff = await Rbac.GetEffectivePermissionsAsync(Admin.Id, Company.Id);
        Assert.False(eff.Has(PermissionCatalog.Codes.ImportsApprove, ScopeTarget.Company));                                     // approval is a separate duty
        foreach (var code in new[] { PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.ImportsUpload, PermissionCatalog.Codes.ImportsConfigure, PermissionCatalog.Codes.ImportsSubmit, PermissionCatalog.Codes.ImportsApply, PermissionCatalog.Codes.ImportsCancel })
            Assert.True(eff.Has(code, ScopeTarget.Company), code);
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Listings_are_restricted_inside_the_query_to_the_adapters_the_caller_may_see_and_nobody_learns_whether_a_hidden_batch_exists()
    {
        await PrepareImports(adapters: [new TestImportAdapter(), new TestImportAdapter("journal-adapter", PermissionCatalog.Codes.JournalCreate, PermissionCatalog.Codes.JournalView)]);
        var accounts = new List<Guid>(); var journal = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            accounts.Add(Ok(await Imports.UploadAsync(Admin.Id, Company.Id, "test-adapter", $"a{i}.csv", Bytes($"a\n{i}\n"), null)).Id);
            journal.Add(Ok(await Imports.UploadAsync(Admin.Id, Company.Id, "journal-adapter", $"j{i}.csv", Bytes($"j\n{i}\n"), null)).Id);
        }
        var (viewer, _) = await Member(P(PermissionCatalog.Codes.ImportsView, PermissionCatalog.Codes.AccountsView), ScopeKind.Company, null);
        var seen = new List<Guid>();
        for (var off = 0; off < 12; off += 3) seen.AddRange(Ok(await IMg.ListAsync(viewer.Id, Company.Id, new ImportListFilter(null, null, 3, off))).Select(x => x.Id));      // paging walks only visible batches
        Assert.Equal(accounts.Order(), seen.Order());
        Assert.Empty(Ok(await IMg.ListAsync(viewer.Id, Company.Id, new ImportListFilter(null, "journal-adapter"))));
        Assert.Equal(10, Ok(await IMg.ListAsync(Admin.Id, Company.Id, new ImportListFilter(null, null, 50, 0))).Count);
        Assert.Equal(5, Ok(await IMg.ListAsync(Admin.Id, Company.Id, new ImportListFilter(ImportStatus.Uploaded, "journal-adapter"))).Count);
        // a hidden batch looks exactly like a forbidden one; only someone who may view imports learns that an unknown id does not exist
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.GetAsync(viewer.Id, Company.Id, journal[0])).Status);
        Assert.Equal(ConfigStatus.NotFound, (await IMg.GetAsync(viewer.Id, Company.Id, Guid.NewGuid())).Status);
        var (nobody, _) = await Member(P(PermissionCatalog.Codes.AccountsView), ScopeKind.Company, null);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.GetAsync(nobody.Id, Company.Id, Guid.NewGuid())).Status);
        Assert.Equal(ConfigStatus.Forbidden, (await IMg.ListAsync(nobody.Id, Company.Id, new ImportListFilter(null, null))).Status);
        // the adapters offered to a user are those whose data they may see, with what they may do
        var offered = Ok(await IMg.AdaptersAsync(viewer.Id, Company.Id));
        Assert.Equal([("test-adapter", false)], offered.Select(a => (a.Adapter.Code, a.CanUpload)));
        Assert.Equal(2, Ok(await IMg.AdaptersAsync(Admin.Id, Company.Id)).Count);
    }
}

public class ImportApiTests : ImportTestBase
{
    private const string Base = "/api/v1/company/imports";

    private InMemoryKekRing? apiRing;

    private async Task Start(bool keyUnavailable = false)
    {
        await StartApi(configure: b =>
        {
            b.UseSetting("Hasm:Imports:StorageDirectory", StorageDir);
            b.UseSetting("Hasm:Imports:MaxFileBytes", "4000");
            b.ConfigureServices(s =>
            {
                var ring = new InMemoryKekRing("k1") { Unavailable = keyUnavailable };
                apiRing = ring;
                s.AddSingleton<KekRing>(ring);
                s.AddSingleton(TestEncryption);
                s.AddSingleton<IImportAdapter>(new TestImportAdapter());
            });
        });
    }

    private async Task<ApiResponse> SendRaw(string path, byte[] body, string? cookie, string? idempotencyKey = null, bool header = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (header) request.Headers.Add("X-Hasm-Request", "1");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        return new ApiResponse(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Every_import_endpoint_needs_authentication_a_selected_company_and_the_request_header()
    {
        await Start();
        var id = Guid.NewGuid();
        var routes = new (HttpMethod, string)[]
        {
            (HttpMethod.Get, Base), (HttpMethod.Post, Base + "?adapter=test-adapter&fileName=a.csv"), (HttpMethod.Get, $"{Base}/adapters"), (HttpMethod.Get, $"{Base}/settings"), (HttpMethod.Put, $"{Base}/settings"),
            (HttpMethod.Post, $"{Base}/maintenance/run"), (HttpMethod.Post, $"{Base}/maintenance/rewrap"), (HttpMethod.Get, $"{Base}/{id}"), (HttpMethod.Post, $"{Base}/{id}/stage"),
            (HttpMethod.Post, $"{Base}/{id}/cancel"), (HttpMethod.Post, $"{Base}/{id}/renew"), (HttpMethod.Get, $"{Base}/{id}/rows"), (HttpMethod.Get, $"{Base}/{id}/events"),
        };
        foreach (var (method, path) in routes)
        {
            object? body = method == HttpMethod.Get ? null : new { };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(method, path, body, cookie: "forged")).Status);
        }
        var noCompany = await SignIn(Admin, selectCompany: false);
        foreach (var (method, path) in routes)
        {
            var r = await Send(method, path, method == HttpMethod.Get ? null : new { }, noCompany, ifMatch: "\"1\"");
            Assert.Equal((HttpStatusCode.Conflict, "company_not_selected"), (r.Status, r.Error));
        }
        var cookie = await SignIn(Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendRaw(Base + "?adapter=test-adapter&fileName=a.csv", Bytes(Csv2), cookie, header: false)).Status);      // state changes need the request header
    }

    [PostgresFact]
    public async Task The_wizard_flow_works_end_to_end_over_http_with_etags_paging_and_no_download_route()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var adapters = await Send(HttpMethod.Get, $"{Base}/adapters", cookie: cookie);
        Assert.Contains("test-adapter", adapters.Json.GetProperty("adapters").EnumerateArray().Select(a => a.GetProperty("code").GetString()));
        Assert.Contains("chart-of-accounts", adapters.Json.GetProperty("adapters").EnumerateArray().Select(a => a.GetProperty("code").GetString()));   // the real adapter is registered by the API
        Assert.Equal(4000, adapters.Json.GetProperty("limits").GetProperty("maxFileBytes").GetInt32());
        var sb = new StringBuilder("code,name\n");
        for (var i = 1; i <= 120; i++) sb.Append($"{i:0000},Account {i}\n");
        var data = Bytes(sb.ToString());
        var up = await SendRaw($"{Base}?adapter=test-adapter&fileName=..%5C..%5Cchart.csv", data, cookie, "api-key-1");
        Assert.Equal(HttpStatusCode.Created, up.Status);
        Assert.Contains("no-store", up.Headers.CacheControl!.ToString());
        var id = up.Json.GetProperty("id").GetGuid();
        Assert.Equal(("uploaded", "csv", "chart.csv", Hex(data)), (up.Json.GetProperty("status").GetString(), up.Json.GetProperty("fileKind").GetString(), up.Json.GetProperty("fileName").GetString(), up.Json.GetProperty("fileSha256").GetString()));
        Assert.DoesNotContain(StorageDir, up.Body);                                                                      // no server path ever leaves the server
        var retry = await SendRaw($"{Base}?adapter=test-adapter&fileName=chart.csv", data, cookie, "api-key-1");                  // an idempotent retry returns the same batch
        Assert.Equal(id, retry.Json.GetProperty("id").GetGuid());
        Assert.Single(StoredFiles());
        // staging needs If-Match
        Assert.Equal((HttpStatusCode)428, (await Send(HttpMethod.Post, $"{Base}/{id}/stage", new { sheet = 0, headerRow = 1 }, cookie)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(HttpMethod.Post, $"{Base}/{id}/stage", new { sheet = 0, headerRow = 1 }, cookie, ifMatch: "\"99\"")).Status);
        var staged = await Send(HttpMethod.Post, $"{Base}/{id}/stage", new { sheet = 0, headerRow = 1, encoding = "utf-8" }, cookie, ifMatch: Etag(up));
        Assert.Equal(HttpStatusCode.OK, staged.Status);
        Assert.Equal(("staged", 120), (staged.Json.GetProperty("status").GetString(), staged.Json.GetProperty("rowCount").GetInt32()));
        // safe pagination: the page size is capped, offsets walk the rows once, a status filter works
        var big = await Send(HttpMethod.Get, $"{Base}/{id}/rows?limit=100000", cookie: cookie);
        Assert.Equal(120, big.Json.GetProperty("rows").GetArrayLength());
        var walked = new List<int>();
        for (var off = 0; off < 130; off += 50) walked.AddRange((await Send(HttpMethod.Get, $"{Base}/{id}/rows?offset={off}&limit=50", cookie: cookie)).Json.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("rowNo").GetInt32()));
        Assert.Equal(Enumerable.Range(2, 120), walked);
        Assert.Equal("0001", (await Send(HttpMethod.Get, $"{Base}/{id}/rows?limit=1", cookie: cookie)).Json.GetProperty("rows")[0].GetProperty("cells")[0].GetString());
        Assert.Equal(0, (await Send(HttpMethod.Get, $"{Base}/{id}/rows?status=3", cookie: cookie)).Json.GetProperty("rows").GetArrayLength());
        // list, filters, events, settings
        var list = await Send(HttpMethod.Get, $"{Base}?status=staged&adapter=test-adapter&limit=10", cookie: cookie);
        Assert.Equal(1, list.Json.GetProperty("imports").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"{Base}?status=bogus", cookie: cookie)).Status);
        Assert.Equal("staged", (await Send(HttpMethod.Get, $"{Base}/{id}/events", cookie: cookie)).Json.GetProperty("events")[1].GetProperty("kind").GetString());
        var settings = await Send(HttpMethod.Get, $"{Base}/settings", cookie: cookie);
        Assert.Equal(30, settings.Json.GetProperty("settings").GetProperty("retentionDays").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{Base}/settings", new { retentionDays = 10, pendingExpiryDays = 12 }, cookie, ifMatch: Etag(settings))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"{Base}/settings", new { retentionDays = 0, pendingExpiryDays = 12 }, cookie, ifMatch: "\"1\"")).Status);
        // cancel; the original is never downloadable (no such route exists)
        var cancelled = await Send(HttpMethod.Post, $"{Base}/{id}/cancel", new { }, cookie, ifMatch: Etag(staged));
        Assert.Equal("cancelled", cancelled.Json.GetProperty("status").GetString());
        foreach (var path in new[] { "file", "original", "download", "content" }) Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{id}/{path}", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", cookie: cookie)).Status);
        // permissions: a member with neither permission is refused everywhere
        var (nobody, _) = await Member([PermissionCatalog.Codes.AccountsView], ScopeKind.Company, null);
        var nc = await SignIn(nobody);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Base, cookie: nc)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendRaw($"{Base}?adapter=test-adapter&fileName=a.csv", Bytes(Csv2), nc)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"{Base}/{id}", cookie: nc)).Status);
        await AssertAuditIntact();
    }

    [PostgresFact]
    public async Task Hostile_oversized_and_unsupported_uploads_are_refused_with_stable_codes_and_leave_nothing_behind()
    {
        await Start();
        var cookie = await SignIn(Admin);
        var big = await SendRaw($"{Base}?adapter=test-adapter&fileName=a.csv", new byte[4001], cookie);
        Assert.Equal((HttpStatusCode)413, big.Status);
        var formula = new XlsxBuilder().Sheet("S", [["a"], [new XlsxBuilder.Cell("A2", 1, 0, "1+1")]]).Build();
        var f = await SendRaw($"{Base}?adapter=test-adapter&fileName=a.xlsx", formula, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, f.Status);
        Assert.Equal(("file", "formula_not_allowed"), (f.Json.GetProperty("fields")[0].GetProperty("field").GetString(), f.Json.GetProperty("fields")[0].GetProperty("code").GetString()));
        Assert.Equal("unsupported_format", (await SendRaw($"{Base}?adapter=test-adapter&fileName=a.xls", Bytes("x"), cookie)).Json.GetProperty("fields")[0].GetProperty("code").GetString());
        Assert.Equal("unknown_adapter", (await SendRaw($"{Base}?adapter=nope&fileName=a.csv", Bytes(Csv2), cookie)).Json.GetProperty("fields")[0].GetProperty("code").GetString());
        Assert.Equal("empty_file", (await SendRaw($"{Base}?adapter=test-adapter&fileName=a.csv", [], cookie)).Json.GetProperty("fields")[0].GetProperty("code").GetString());
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ib.Table}"));
        Assert.Empty(StoredFiles());
    }

    [PostgresFact]
    public async Task Unavailable_encryption_fails_closed_with_503_and_records_nothing()
    {
        await Start();
        var c2 = await SignIn(Admin);                                                                        // sign-in needs the identity key too (identity integrity S-1), so the key goes away AFTER it
        apiRing!.Unavailable = true;
        var down = await SendRaw($"{Base}?adapter=test-adapter&fileName=a.csv", Bytes(Csv2), c2);
        Assert.Equal((HttpStatusCode)503, down.Status);
        Assert.Equal("encryption_unavailable", down.Error);
        Assert.DoesNotContain("k1", down.Body);
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {Ib.Table}"));
        Assert.Empty(StoredFiles());
    }
}
