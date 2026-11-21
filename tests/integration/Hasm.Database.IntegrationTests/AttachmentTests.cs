using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Security;
using AT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Attachment;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase 3.12 (company migration 0024, system migration 0026) against real PostgreSQL and a real temporary directory: metadata in the company database, bytes on the filesystem, plaintext hash verified on every
/// read, application-level encryption of sensitive files, tamper and swap detection, removal, key rotation drill, authorization and traversal safety. Disposable cluster only.
/// </summary>
public class AttachmentTests : InventoryTestBase
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 hello attachment");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private string dir = null!;
    private InMemoryKekRing ring = null!;
    private AttachmentFileStore Store = null!;
    private AttachmentService Svc = null!;
    private AttachmentManagementService Mgmt = null!;
    private Guid party;

    private async Task Prepare(AttachmentOptions? opts = null)
    {
        await PrepareInventory();
        dir = Path.Combine(Path.GetTempPath(), "hasm-att-" + Guid.NewGuid().ToString("N"));
        ring = new InMemoryKekRing("k1");
        var options = opts ?? new AttachmentOptions { StorageDirectory = dir, MaxFileBytes = 1024 * 1024 };
        Store = new AttachmentFileStore(options, new EncryptionOptions { AllowInsecureFilePermissions = true });
        Svc = new AttachmentService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Store, new EnvelopeEncryptor(ring), options, Clock);
        Mgmt = new AttachmentManagementService(Rbac, Svc);
        party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'AP', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
    }

    public override async Task DisposeAsync()
    {
        if (dir is not null && Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private Task<ConfigResult<AttachmentView>> Up(byte[]? bytes = null, string type = "application/pdf", bool sensitive = false, string name = "contract.pdf") =>
        Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, name, type, bytes ?? Pdf, sensitive);
    private string StoredPath(Guid id) => Directory.GetFiles(Path.Combine(dir, Company.Id.ToString("N")), "*.att").Select(p => (p, n: Path.GetFileName(p))).Single(x => x.n == Sql<string>($"SELECT {AT.StorageName} FROM {AT.Table} WHERE {AT.Id} = '{id}'").GetAwaiter().GetResult()).p;
    private Task<long> Events(short type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");

    [PostgresFact]
    public async Task A_normal_file_is_stored_as_is_with_its_hash_and_a_sensitive_file_is_encrypted_at_rest()
    {
        await Prepare();
        var normal = Ok(await Up());
        var sensitive = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" SECRET")).ToArray(), sensitive: true, name: "salary.pdf"));
        Assert.Equal((Pdf.Length, false, 1L), ((int)normal.SizeBytes, normal.IsSensitive, normal.Revision));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Pdf)), normal.Sha256);
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(StoredPath(normal.Id)));                                             // normal: plain on disk (backend-only access)
        var onDisk = await File.ReadAllBytesAsync(StoredPath(sensitive.Id));
        Assert.DoesNotContain("SECRET", Encoding.ASCII.GetString(onDisk));                                                   // sensitive: application-level encryption
        Assert.Equal("k1", await Sql<string>($"SELECT {AT.KeyId} FROM {AT.Table} WHERE {AT.Id} = '{sensitive.Id}'"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {AT.Table} WHERE {AT.Id} = '{normal.Id}' AND {AT.KeyId} IS NULL"));
        Assert.Equal(2L, await Events(AttachmentAuditEvents.Uploaded));
        var opened = Ok(await Svc.OpenAsync(Admin.Id, Company.Id, sensitive.Id, allowSensitive: true));
        Assert.Contains("SECRET", Encoding.ASCII.GetString(opened.Bytes));
        Assert.Equal(1L, await Events(AttachmentAuditEvents.SensitiveDownloaded));                                           // a sensitive read is audited
        Ok(await Svc.OpenAsync(Admin.Id, Company.Id, normal.Id, allowSensitive: false));
        Assert.Equal(1L, await Events(AttachmentAuditEvents.SensitiveDownloaded));                                           // a normal read is not
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, sensitive.Id, allowSensitive: false), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(2, (await Svc.ListAsync(Company.Id, "local_party", party, true, false)).Count);
        Assert.Single(await Svc.ListAsync(Company.Id, "local_party", party, false, false));                                  // sensitive entries are hidden from a caller without the permission
    }

    [PostgresFact]
    public async Task Uploads_are_validated_for_kind_record_name_size_type_and_signature_and_a_refusal_leaves_no_file()
    {
        await Prepare();
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "journal_entry", party, "a.pdf", "application/pdf", Pdf, false), "subjectKind", "subject_kind_invalid");
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", Guid.NewGuid(), "a.pdf", "application/pdf", Pdf, false), "subjectId", "subject_not_found");
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", null, "a.pdf", "application/pdf", Pdf, false), "subjectId", "required");
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "  ", "application/pdf", Pdf, false), "fileName", "file_name_invalid");
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "a.exe", "application/x-msdownload", Pdf, false), "contentType", "content_type_not_allowed");
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "a.pdf", "application/pdf", [], false), "file", "file_empty");
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "a.pdf", "application/pdf", new byte[1024 * 1024 + 1], false), "file", "file_too_large");
        Invalid(await Up(Png, "application/pdf"), "file", "content_type_mismatch");                                          // a PNG declared as a PDF
        Invalid(await Up(Pdf, "image/png"), "file", "content_type_mismatch");
        Invalid(await Up([0x00, 0x01, 0x02], "text/plain"), "file", "content_type_mismatch");                               // NUL bytes are not text
        Invalid(await Up([0xFF, 0xFE, 0xFD], "text/csv"), "file", "content_type_mismatch");                                 // invalid UTF-8
        Ok(await Up(Encoding.UTF8.GetBytes("a,b\n١,٢"), "text/csv", name: "data.csv"));
        Ok(await Up(Png, "image/png", name: "scan.png"));
        Assert.Equal(2L, await Count(Company, AT.Table));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, Company.Id.ToString("N")), "*.att").Length);                     // refusals wrote nothing

        // a hostile name is reduced to its last safe segment; it is only ever a label, never a path
        var sneaky = Ok(await Up(name: "../../etc/pass\0wd.pdf"));
        Assert.Equal("pass_wd.pdf", sneaky.FileName);
        Assert.Equal("evil.pdf", AttachmentService.SanitizeFileName(@"C:\Users\x\evil.pdf"));
        Assert.Null(AttachmentService.SanitizeFileName("..."));
        Assert.Null(AttachmentService.SanitizeFileName("../"));
        Assert.Equal(200, AttachmentService.SanitizeFileName(new string('a', 500))!.Length);
    }

    [PostgresFact]
    public async Task Storage_names_cannot_escape_the_company_directory_and_companies_are_isolated()
    {
        await Prepare();
        foreach (var bad in new[] { "../x.att", "..\\x.att", "a/b.att", "x.att/..", "ZZ.att", new string('a', 32) + ".txt", "/etc/passwd", "" })
        {
            Assert.Throws<ArgumentException>(() => Store.Create(Company.Id, bad, [1]));
            Assert.Throws<ArgumentException>(() => Store.Read(Company.Id, bad));
            Assert.Throws<ArgumentException>(() => Store.Delete(Company.Id, bad));
        }
        var name = AttachmentFileStore.NewName();
        Store.Create(Company.Id, name, [1, 2, 3]);
        Assert.Throws<IOException>(() => Store.Create(Company.Id, name, [9]));                                              // never overwrites
        Assert.Equal([1, 2, 3], Store.Read(Company.Id, name));
        Assert.Null(Store.Read(Guid.NewGuid(), name));                                                                      // another company's directory does not have it
        var unconfigured = new AttachmentFileStore(new AttachmentOptions { StorageDirectory = null }, new EncryptionOptions());
        Assert.Equal("attachment_storage_not_configured", Assert.Throws<EncryptionUnavailableException>(() => unconfigured.Read(Company.Id, name)).Code);
        Assert.Throws<EncryptionUnavailableException>(() => new AttachmentFileStore(new AttachmentOptions { StorageDirectory = "relative" }, new EncryptionOptions()).Read(Company.Id, name));
        // an unconfigured service refuses the upload and leaves nothing behind
        var svc = new AttachmentService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), unconfigured, new EnvelopeEncryptor(ring), new AttachmentOptions(), Clock);
        Fails(await svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "a.pdf", "application/pdf", Pdf, false), ConfigStatus.Conflict, "attachment_storage_not_configured");
        Assert.Equal(0L, await Count(Company, AT.Table));
    }

    [PostgresFact]
    public async Task Tampering_a_missing_file_and_a_swapped_ciphertext_are_detected_audited_and_nothing_is_returned()
    {
        await Prepare();
        var plain = Ok(await Up());
        var s1 = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" ONE")).ToArray(), sensitive: true, name: "one.pdf"));
        var s2 = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" TWO")).ToArray(), sensitive: true, name: "two.pdf"));
        Ok(await Svc.OpenAsync(Admin.Id, Company.Id, plain.Id, true));

        var p = StoredPath(plain.Id);
        var original = await File.ReadAllBytesAsync(p);
        var tampered = (byte[])original.Clone(); tampered[^1] ^= 1;
        await File.WriteAllBytesAsync(p, tampered);
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, plain.Id, true), ConfigStatus.Conflict, "integrity_failure");          // plaintext hash mismatch
        await File.WriteAllBytesAsync(p, original);
        Ok(await Svc.OpenAsync(Admin.Id, Company.Id, plain.Id, true));

        var f1 = StoredPath(s1.Id); var bytes1 = await File.ReadAllBytesAsync(f1);
        var flipped = (byte[])bytes1.Clone(); flipped[flipped.Length / 2] ^= 1;
        await File.WriteAllBytesAsync(f1, flipped);
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, s1.Id, true), ConfigStatus.Conflict, "integrity_failure");             // ciphertext tampering
        await File.WriteAllBytesAsync(f1, await File.ReadAllBytesAsync(StoredPath(s2.Id)));
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, s1.Id, true), ConfigStatus.Conflict, "integrity_failure");             // another attachment's valid ciphertext: the context binds it to its own id
        await File.WriteAllBytesAsync(f1, bytes1);
        Ok(await Svc.OpenAsync(Admin.Id, Company.Id, s1.Id, true));

        File.Delete(p);
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, plain.Id, true), ConfigStatus.Conflict, "integrity_failure");          // the file went missing
        Assert.Equal(4L, await Events(AttachmentAuditEvents.IntegrityFailure));
        ring.Unavailable = true;
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, s2.Id, true), ConfigStatus.Conflict, "encryption_key_unavailable");     // a missing key fails closed and is not an integrity accusation
        Assert.Equal(4L, await Events(AttachmentAuditEvents.IntegrityFailure));
    }

    [PostgresFact]
    public async Task Removal_needs_a_reason_and_the_revision_keeps_the_row_and_bytes_and_PostgreSQL_guards_the_table()
    {
        await Prepare();
        var a = Ok(await Up());
        var path = StoredPath(a.Id);
        Fails(await Svc.RemoveAsync(Admin.Id, Company.Id, a.Id, null, "why", true), ConfigStatus.PreconditionRequired, "precondition_required");
        Invalid(await Svc.RemoveAsync(Admin.Id, Company.Id, a.Id, 1, "  ", true), "reason", "required");
        Invalid(await Svc.RemoveAsync(Admin.Id, Company.Id, a.Id, 1, new string('x', 501), true), "reason", "too_long");
        Fails(await Svc.RemoveAsync(Admin.Id, Company.Id, a.Id, 9, "why", true), ConfigStatus.PreconditionFailed, "revision_conflict");
        Fails(await Svc.RemoveAsync(Admin.Id, Company.Id, Guid.NewGuid(), 1, "why", true), ConfigStatus.NotFound, "attachment_not_found");
        var removed = Ok(await Svc.RemoveAsync(Admin.Id, Company.Id, a.Id, 1, "uploaded by mistake", true));
        Assert.Equal((true, 2L, "uploaded by mistake"), (removed.IsRemoved, removed.Revision, removed.RemovalReason));
        Fails(await Svc.RemoveAsync(Admin.Id, Company.Id, a.Id, 2, "again", true), ConfigStatus.Conflict, "already_removed");
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, a.Id, true), ConfigStatus.NotFound, "attachment_not_found");
        Assert.Empty(await Svc.ListAsync(Company.Id, "local_party", party, true, false));
        Assert.Single(await Svc.ListAsync(Company.Id, "local_party", party, true, true));
        Assert.True(File.Exists(path));                                                                                     // bytes retained (retention policy is an owner decision)
        Assert.Equal(1L, await Events(AttachmentAuditEvents.Removed));

        var b = Ok(await Up(name: "b.pdf"));
        Assert.Equal("P0001", await Fails($"DELETE FROM {AT.Table}"));
        // Since the retention tables (company 0031) reference the attachment, a plain TRUNCATE is refused earlier by PostgreSQL itself (0A000, referenced by a foreign key); CASCADE reaches the guard trigger.
        Assert.Equal("0A000", await Fails($"TRUNCATE {AT.Table}"));
        Assert.Equal("P0001", await Fails($"TRUNCATE {AT.Table} CASCADE"));
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.FileName} = 'x', {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));          // identity data is immutable
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.Sha256} = '{new string('a', 64)}', {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.UpdatedAtUtc} = now() WHERE {AT.Id} = '{b.Id}'"));                                          // revision must advance
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.UpdatedAtUtc} = now(), {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{a.Id}'"));      // a removed row never changes again
        Assert.Equal("23514", await Fails($"UPDATE {AT.Table} SET {AT.State} = 2, {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));                // removal needs who, when and why
        Assert.Equal("23514", await Fails($"UPDATE {AT.Table} SET {AT.KeyId} = 'k9', {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));            // a normal file has no key
    }

    private Hasm.Infrastructure.Backup.EncryptionKeyRetirementService RetirementReport() => new(Options,
        new AttachmentOptions { StorageDirectory = dir }, new Hasm.Infrastructure.Imports.ImportOptions { StorageDirectory = Path.Combine(dir, "imports") }, new EncryptionOptions { AllowInsecureFilePermissions = true });
    private static KeyRetirementEntry KeyOf(KeyRetirementReport r, string id) => r.Keys.Single(k => k.KeyId == id);

    [PostgresFact]
    public async Task A_corrupt_sensitive_attachment_or_a_missing_storage_folder_makes_the_key_retirement_report_incomplete()
    {
        // review M1 of PR 182: the wiring of the envelope read (AttachmentService.KeyUsageAsync through the report service), not only the builder's rule
        await Prepare();
        Ok(await Up());                                                                                                        // a normal file is stored as is and is not scanned
        var bad = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" BAD")).ToArray(), sensitive: true, name: "bad.pdf"));
        ring.Add("k2", RandomNumberGenerator.GetBytes(32)); ring.Activate("k2");
        var service = RetirementReport();
        var blocker = $"attachment_key_unknown:{Company.Id:N}:1";
        var clean = await service.ReportAsync(["k1", "k2"], "k2", [], null, backupScopeAttested: true);
        Assert.DoesNotContain(blocker, clean.Blockers);
        Assert.Contains("needed_by_attachments", KeyOf(clean, "k1").Reasons);
        await File.WriteAllBytesAsync(StoredPath(bad.Id), [1, 2, 3]);                                                          // not an envelope: its key cannot be determined
        var r = await service.ReportAsync(["k1", "k2"], "k2", [], null, backupScopeAttested: true);
        Assert.False(r.Complete);
        Assert.Contains(blocker, r.Blockers);
        Assert.All(r.Keys, k => Assert.False(k.Retirable));
        Assert.Contains("needed_by_attachments", KeyOf(r, "k1").Reasons);                                                      // the row still names k1

        // the whole company folder gone (an unmounted or misconfigured storage) is NOT "no files", and the read-only scan does not create it (review L2)
        var companyDir = Path.Combine(dir, Company.Id.ToString("N"));
        Directory.Move(companyDir, companyDir + ".moved");
        try
        {
            var gone = await service.ReportAsync(["k1", "k2"], "k2", [], null, backupScopeAttested: true);
            Assert.Contains(blocker, gone.Blockers);
            Assert.False(Directory.Exists(companyDir));
            Assert.False(Directory.Exists(Path.Combine(dir, "imports")));
        }
        finally { Directory.Move(companyDir + ".moved", companyDir); }
    }

    [PostgresFact]
    public async Task A_sensitive_attachment_whose_envelope_names_another_key_than_its_row_keeps_both_keys_non_retirable()
    {
        // review M1 of PR 182: a rewrap that crashed between the file replacement and the row update leaves the file under the new key and the row naming the old one; BOTH stay needed
        await Prepare();
        var a = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" A")).ToArray(), sensitive: true, name: "a.pdf"));
        ring.Add("k2", RandomNumberGenerator.GetBytes(32)); ring.Activate("k2");
        ring.Add("k3", RandomNumberGenerator.GetBytes(32)); ring.Activate("k3");
        var service = RetirementReport();
        var before = await service.ReportAsync(["k1", "k2", "k3"], "k3", [], null, backupScopeAttested: true);
        Assert.Contains("needed_by_attachments", KeyOf(before, "k1").Reasons);
        Assert.DoesNotContain("needed_by_attachments", KeyOf(before, "k2").Reasons);
        await File.WriteAllBytesAsync(StoredPath(a.Id), new EnvelopeEncryptor(new InMemoryKekRing("k2")).Encrypt(Pdf, "any"));   // only the header is read: the key id is k2
        Assert.Equal("k1", await Sql<string>($"SELECT {AT.KeyId} FROM {AT.Table} WHERE {AT.Id} = '{a.Id}'"));                  // the row still names k1
        var r = await service.ReportAsync(["k1", "k2", "k3"], "k3", [], null, backupScopeAttested: true);
        Assert.Contains("needed_by_attachments", KeyOf(r, "k1").Reasons);                                                      // the row's key
        Assert.Contains("needed_by_attachments", KeyOf(r, "k2").Reasons);                                                      // the file's key
        Assert.DoesNotContain(r.Blockers, b => b.StartsWith("attachment_key_unknown", StringComparison.Ordinal));
        Assert.All(r.Keys, k => Assert.False(k.Retirable));
    }

    [PostgresFact]
    public async Task A_removed_sensitive_file_keeps_its_key_in_use_so_the_old_key_is_never_reported_as_retirable()
    {
        await Prepare();
        var kept = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" KEPT")).ToArray(), sensitive: true, name: "kept.pdf"));
        var gone = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" GONE")).ToArray(), sensitive: true, name: "gone.pdf"));
        Ok(await Svc.RemoveAsync(Admin.Id, Company.Id, gone.Id, gone.Revision, "obsolete", true));                          // removed: the bytes stay, under k1, and the row never changes again
        Assert.True((await Svc.RewrapAsync(Admin.Id, Company.Id)).OldKeysMayBeRetired);                                    // k1 is still the active key: nothing is under another key

        ring.Add("k2", RandomNumberGenerator.GetBytes(32));
        ring.Activate("k2");
        var outcome = await Svc.RewrapAsync(Admin.Id, Company.Id);
        Assert.Equal((1, 0, 0, 1), (outcome.Rewrapped, outcome.Skipped, outcome.Failed, outcome.RetainedUnderOtherKeys));   // the active file moved to k2; the removed one cannot
        Assert.False(outcome.OldKeysMayBeRetired);                                                                          // so k1 must be kept
        Assert.Equal("k1", await Sql<string>($"SELECT {AT.KeyId} FROM {AT.Table} WHERE {AT.Id} = '{gone.Id}'"));
        var report = await Svc.VerifyAsync(Admin.Id, Company.Id);
        Assert.Contains("k1", report.KeyIdsInUse);                                                                               // the verification report names the key that is still needed
        Assert.True(report.IsClean);

        Assert.True(ring.Remove("k1"));                                                                                     // retiring it anyway fails closed and visibly
        var after = await Svc.VerifyAsync(Admin.Id, Company.Id);
        Assert.False(after.IsClean);
        Assert.Equal([gone.Id], after.KeyUnavailable);
        Assert.Contains(" KEPT", Encoding.ASCII.GetString(Ok(await Svc.OpenAsync(Admin.Id, Company.Id, kept.Id, true)).Bytes));   // the rewrapped file is unaffected
    }

    [PostgresFact]
    public async Task Key_rotation_drill_rewraps_every_sensitive_file_under_the_new_key_and_the_old_key_can_then_be_retired()
    {
        await Prepare();
        var plain = Ok(await Up());
        var secrets = new List<AttachmentView>();
        for (var i = 0; i < 3; i++) secrets.Add(Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes($" S{i}")).ToArray(), sensitive: true, name: $"s{i}.pdf")));
        var before = await File.ReadAllBytesAsync(StoredPath(secrets[0].Id));
        Assert.Equal(new RewrapOutcome(0, 3, 0), await Svc.RewrapAsync(Admin.Id, Company.Id));                              // nothing to do while k1 is active

        ring.Add("k2", RandomNumberGenerator.GetBytes(32));
        ring.Activate("k2");
        Assert.Equal(new RewrapOutcome(3, 0, 0), await Svc.RewrapAsync(Admin.Id, Company.Id));
        Assert.Equal(3L, await Events(AttachmentAuditEvents.Rewrapped));
        Assert.NotEqual(before, await File.ReadAllBytesAsync(StoredPath(secrets[0].Id)));
        foreach (var s in secrets) Assert.Equal("k2", await Sql<string>($"SELECT {AT.KeyId} FROM {AT.Table} WHERE {AT.Id} = '{s.Id}'"));
        Assert.Equal(new RewrapOutcome(0, 3, 0), await Svc.RewrapAsync(Admin.Id, Company.Id));                              // idempotent

        Assert.True(ring.Remove("k1"));                                                                                     // the old key is retired
        for (var i = 0; i < 3; i++) Assert.Contains($" S{i}", Encoding.ASCII.GetString(Ok(await Svc.OpenAsync(Admin.Id, Company.Id, secrets[i].Id, true)).Bytes));
        Ok(await Svc.OpenAsync(Admin.Id, Company.Id, plain.Id, true));
        var newer = Ok(await Up(sensitive: true, name: "new.pdf"));
        Assert.Equal("k2", await Sql<string>($"SELECT {AT.KeyId} FROM {AT.Table} WHERE {AT.Id} = '{newer.Id}'"));          // new files use the active key

        // recovery: with the active key lost the files stay unreadable (fail closed) and are readable again when the key is restored
        var k2 = RandomNumberGenerator.GetBytes(32);
        var recovery = new InMemoryKekRing("k3", k2);
        var lostSvc = new AttachmentService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), Store, new EnvelopeEncryptor(recovery), new AttachmentOptions { StorageDirectory = dir }, Clock);
        Fails(await lostSvc.OpenAsync(Admin.Id, Company.Id, secrets[0].Id, true), ConfigStatus.Conflict, "encryption_key_unavailable");
    }

    [PostgresFact]
    public async Task Without_the_permissions_nothing_is_read_or_written_and_the_sensitive_permission_is_separate()
    {
        await Prepare();
        var normal = Ok(await Up());
        var secret = Ok(await Up(sensitive: true, name: "s.pdf"));
        var outsider = await NewUser("attoutsider");
        var before = await Count(Company, AT.Table);
        Fails(await Mgmt.ListAsync(outsider.Id, Company.Id, "local_party", party), ConfigStatus.Forbidden, "forbidden");
        Fails(await Mgmt.OpenAsync(outsider.Id, Company.Id, normal.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await Mgmt.UploadAsync(outsider.Id, Company.Id, "local_party", party, "a.pdf", "application/pdf", Pdf, false), ConfigStatus.Forbidden, "forbidden");
        Fails(await Mgmt.RemoveAsync(outsider.Id, Company.Id, normal.Id, 1, "x"), ConfigStatus.Forbidden, "forbidden");
        Assert.Equal(new AttachmentCapabilities(false, false, false, false, false), await Mgmt.CapabilitiesAsync(outsider.Id, Company.Id));
        Assert.Equal(before, await Count(Company, AT.Table));

        var (editor, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.AttachmentsUpload, PermissionCatalog.Codes.AttachmentsDelete, PermissionCatalog.Codes.PartiesView], ScopeKind.Company, null);
        Assert.Single(Ok(await Mgmt.ListAsync(editor.Id, Company.Id, "local_party", party)));                               // the sensitive one is invisible
        Ok(await Mgmt.OpenAsync(editor.Id, Company.Id, normal.Id));
        Fails(await Mgmt.OpenAsync(editor.Id, Company.Id, secret.Id), ConfigStatus.NotFound, "attachment_not_found");         // looks exactly like a missing file
        Fails(await Mgmt.RemoveAsync(editor.Id, Company.Id, secret.Id, 1, "x"), ConfigStatus.NotFound, "attachment_not_found");
        Fails(await Mgmt.UploadAsync(editor.Id, Company.Id, "local_party", party, "s.pdf", "application/pdf", Pdf, true), ConfigStatus.Forbidden, "forbidden");
        Ok(await Mgmt.UploadAsync(editor.Id, Company.Id, "local_party", party, "n.pdf", "application/pdf", Pdf, false));
        Assert.Equal(new AttachmentCapabilities(true, true, true, false, false), await Mgmt.CapabilitiesAsync(editor.Id, Company.Id));

        var (officer, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.AttachmentsSensitiveView, PermissionCatalog.Codes.AttachmentsSensitiveUpload, PermissionCatalog.Codes.AttachmentsUpload, PermissionCatalog.Codes.PartiesView], ScopeKind.Company, null);
        Assert.Equal(3, Ok(await Mgmt.ListAsync(officer.Id, Company.Id, "local_party", party)).Count);
        Ok(await Mgmt.OpenAsync(officer.Id, Company.Id, secret.Id));
        Ok(await Mgmt.UploadAsync(officer.Id, Company.Id, "local_party", party, "s2.pdf", "application/pdf", Pdf, true));
        Fails(await Mgmt.RemoveAsync(officer.Id, Company.Id, secret.Id, 1, "x"), ConfigStatus.Forbidden, "forbidden");           // removal is its own permission
        Fails(await Mgmt.RewrapAsync(editor.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");                               // key rotation handles sensitive files
        Assert.Equal(new RewrapOutcome(0, 2, 0), Ok(await Mgmt.RewrapAsync(officer.Id, Company.Id)));
    }

    [PostgresFact]
    public async Task Review_hardening_names_types_truncation_orphans_hash_exposure_and_the_owning_module_permission()
    {
        await Prepare();
        // bidirectional overrides, zero-width and separators never survive in a label; control characters become underscores
        Assert.Equal("invoicefdp.exe", AttachmentService.SanitizeFileName("invoice\u202Efdp.exe"));
        Assert.Equal("ab.pdf", AttachmentService.SanitizeFileName("a\u200Bb\u2028.pdf"));
        Assert.Null(AttachmentService.SanitizeFileName("\u200B\u200B"));
        Assert.Equal("a_b.pdf", AttachmentService.SanitizeFileName("a\u0085b.pdf"));
        // truncation never splits a surrogate pair (a lone surrogate would break the insert)
        var emoji = new string('a', 199) + "\U0001F600";
        var cut = AttachmentService.SanitizeFileName(emoji)!;
        Assert.True(cut.Length <= 200 && !char.IsHighSurrogate(cut[^1]));
        Ok(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, emoji + ".pdf", "application/pdf", Pdf, false));
        // a content type may carry parameters; Windows labels a .csv as application/vnd.ms-excel
        Ok(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "t.csv", "text/csv; charset=utf-8", Encoding.UTF8.GetBytes("a,b"), false));
        var csv = Ok(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "x.csv", "application/vnd.ms-excel", Encoding.UTF8.GetBytes("a,b"), false));
        Assert.Equal("text/csv", csv.ContentType);
        Invalid(await Svc.UploadAsync(Admin.Id, Company.Id, "local_party", party, "x.xls", "application/vnd.ms-excel", Encoding.UTF8.GetBytes("a,b"), false), "contentType", "content_type_not_allowed");

        // the plaintext digest of a sensitive file is not copied into the audit trail
        var secret = Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes(" DIGEST")).ToArray(), sensitive: true, name: "d.pdf"));
        var detail = await Sql<string>($"SELECT {Audit_.Detail} FROM {Audit_.Table} WHERE {Audit_.EventType} = {AttachmentAuditEvents.Uploaded} ORDER BY {Audit_.Seq} DESC LIMIT 1");
        Assert.Contains("sha256=withheld", detail);
        Assert.DoesNotContain(secret.Sha256[..16], detail);

        // a truncated or malformed ciphertext is an integrity failure like any other tampering
        var path = StoredPath(secret.Id);
        var full = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, full[..(full.Length / 2)]);
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, secret.Id, true), ConfigStatus.Conflict, "integrity_failure");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        Fails(await Svc.OpenAsync(Admin.Id, Company.Id, secret.Id, true), ConfigStatus.Conflict, "integrity_failure");
        Assert.Equal(2L, await Events(AttachmentAuditEvents.IntegrityFailure));
        await File.WriteAllBytesAsync(path, full);

        // a failed rewrap is audited and counted, never silent
        ring.Add("k2", RandomNumberGenerator.GetBytes(32)); ring.Activate("k2");
        File.Delete(path);
        var outcome = await Svc.RewrapAsync(Admin.Id, Company.Id);
        Assert.True(outcome.Failed >= 1);
        Assert.True(await Events(AttachmentAuditEvents.RewrapFailed) >= 1);

        // orphans: a file no row names is swept once it is old enough; referenced and fresh files stay
        var companyDir = Path.Combine(dir, Company.Id.ToString("N"));
        var oldOrphan = AttachmentFileStore.NewName(); var freshOrphan = AttachmentFileStore.NewName();
        Store.Create(Company.Id, oldOrphan, [1]); Store.Create(Company.Id, freshOrphan, [2]);
        File.SetLastWriteTimeUtc(Path.Combine(companyDir, oldOrphan), DateTime.UtcNow.AddHours(-3));
        var kept = Directory.GetFiles(companyDir, "*.att").Length - 2;
        // a crashed write leaves "<name>.att.tmp-<guid>": the stale one is swept with the orphans, a fresh one (a live write) and a foreign-named file stay
        var staleTemp = Path.Combine(companyDir, AttachmentFileStore.NewName() + ".tmp-" + Guid.NewGuid().ToString("N"));
        var freshTemp = Path.Combine(companyDir, AttachmentFileStore.NewName() + ".tmp-" + Guid.NewGuid().ToString("N"));
        var foreign = Path.Combine(companyDir, "notes.att.tmp-keep");
        File.WriteAllBytes(staleTemp, [3]); File.WriteAllBytes(freshTemp, [4]); File.WriteAllBytes(foreign, [5]);
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddHours(-3)); File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddHours(-3));
        Assert.Equal(2, await Svc.SweepOrphansAsync(Admin.Id, Company.Id, TimeSpan.FromHours(1)));
        Assert.False(File.Exists(staleTemp)); Assert.True(File.Exists(freshTemp)); Assert.True(File.Exists(foreign));
        Assert.False(Store.Exists(Company.Id, oldOrphan));
        Assert.True(Store.Exists(Company.Id, freshOrphan));
        Assert.Equal(kept + 1, Directory.GetFiles(companyDir, "*.att").Length);
        Assert.Equal(1L, await Events(AttachmentAuditEvents.OrphansSwept));

        // an attachment is never reachable by someone who may not see the record it belongs to
        var normal = Ok(await Up(name: "o.pdf"));
        var (blind, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.AttachmentsUpload, PermissionCatalog.Codes.AttachmentsDelete], ScopeKind.Company, null);
        Fails(await Mgmt.ListAsync(blind.Id, Company.Id, "local_party", party), ConfigStatus.Forbidden, "forbidden");
        // a record the caller may not see looks exactly like a missing one (no 403-vs-404 existence oracle by id)
        Fails(await Mgmt.OpenAsync(blind.Id, Company.Id, normal.Id), ConfigStatus.NotFound, "attachment_not_found");
        Fails(await Mgmt.OpenAsync(blind.Id, Company.Id, Guid.NewGuid()), ConfigStatus.NotFound, "attachment_not_found");
        Fails(await Mgmt.UploadAsync(blind.Id, Company.Id, "local_party", party, "a.pdf", "application/pdf", Pdf, false), ConfigStatus.Forbidden, "forbidden");
        Fails(await Mgmt.RemoveAsync(blind.Id, Company.Id, normal.Id, 1, "x"), ConfigStatus.NotFound, "attachment_not_found");
        Fails(await Mgmt.RemoveAsync(blind.Id, Company.Id, Guid.NewGuid(), 1, "x"), ConfigStatus.NotFound, "attachment_not_found");
        Fails(await Mgmt.SweepOrphansAsync(blind.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
    }

    [PostgresFact]
    public async Task Backup_and_restore_drill_the_verification_proves_a_restored_set_and_names_every_problem_and_the_keys_it_needs()
    {
        await Prepare();
        var plain = Ok(await Up());
        var removedPlain = Ok(await Up(name: "gone.pdf"));
        var secrets = new List<AttachmentView>();
        for (var i = 0; i < 2; i++) secrets.Add(Ok(await Up(Pdf.Concat(Encoding.ASCII.GetBytes($" S{i}")).ToArray(), sensitive: true, name: $"s{i}.pdf")));
        Ok(await Svc.RemoveAsync(Admin.Id, Company.Id, removedPlain.Id, 1, "mistake", true));                          // removed bytes are retained and must also survive a restore
        var clean = await Svc.VerifyAsync(Admin.Id, Company.Id);
        Assert.True(clean.IsClean);
        Assert.Equal((4, 4, 2), (clean.Checked, clean.Ok, clean.TotalSensitive));
        Assert.Equal(["k1"], clean.KeyIdsInUse);
        Assert.Equal(1L, await Events(AttachmentAuditEvents.Verified));

        // BACKUP: the database rows, the attachment folder and (separately protected) the key. RESTORE into a fresh folder and prove it.
        var restored = Path.Combine(Path.GetTempPath(), "hasm-att-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Path.Combine(dir, Company.Id.ToString("N")), Path.Combine(restored, Company.Id.ToString("N")));
            var options = new AttachmentOptions { StorageDirectory = restored, MaxFileBytes = 1024 * 1024 };
            var restoredSvc = new AttachmentService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new AttachmentFileStore(options, new EncryptionOptions { AllowInsecureFilePermissions = true }), new EnvelopeEncryptor(ring), options, Clock);
            Assert.True((await restoredSvc.VerifyAsync(Admin.Id, Company.Id)).IsClean);                                 // the stored hashes prove the restored files
            var files = Directory.GetFiles(Path.Combine(restored, Company.Id.ToString("N")), "*.att");

            // a restore that lost a file, corrupted one, lost the key and carries a stray file is described exactly
            var byName = new Dictionary<Guid, string>();
            foreach (var a in new[] { plain, removedPlain, secrets[0], secrets[1] }) byName[a.Id] = Path.Combine(restored, Company.Id.ToString("N"), await Sql<string>($"SELECT {AT.StorageName} FROM {AT.Table} WHERE {AT.Id} = '{a.Id}'"));
            File.Delete(byName[removedPlain.Id]);
            var bytes = await File.ReadAllBytesAsync(byName[plain.Id]); bytes[^1] ^= 1; await File.WriteAllBytesAsync(byName[plain.Id], bytes);
            File.WriteAllBytes(Path.Combine(restored, Company.Id.ToString("N"), AttachmentFileStore.NewName()), [9, 9]);
            var withoutKey = new AttachmentService(Resolver, new CompanyAuditedTransactions(Resolver, Audit, Clock), new AttachmentFileStore(options, new EncryptionOptions { AllowInsecureFilePermissions = true }), new EnvelopeEncryptor(new InMemoryKekRing("k9")), options, Clock);
            var bad = await withoutKey.VerifyAsync(Admin.Id, Company.Id);
            Assert.False(bad.IsClean);
            Assert.Equal([removedPlain.Id], bad.Missing);
            Assert.Equal([plain.Id], bad.HashMismatch);
            Assert.Equal(secrets.Select(s => s.Id).Order(), bad.KeyUnavailable.Order());                               // without the key backup the sensitive files cannot be proven
            Assert.Equal((1, 0), (bad.OrphanFiles, bad.Unreadable.Count));
            Assert.Equal(["k1"], bad.KeyIdsInUse);                                                                        // the operator learns which key the key backup must contain
        }
        finally { if (Directory.Exists(restored)) try { Directory.Delete(restored, true); } catch (IOException) { } }

        var (editor, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.PartiesView], ScopeKind.Company, null);
        Fails(await Mgmt.VerifyAsync(editor.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Assert.True(Ok(await Mgmt.VerifyAsync(Admin.Id, Company.Id)).IsClean);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }
}
