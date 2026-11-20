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
        Assert.Equal("P0001", await Fails($"TRUNCATE {AT.Table}"));
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.FileName} = 'x', {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));          // identity data is immutable
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.Sha256} = '{new string('a', 64)}', {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.UpdatedAtUtc} = now() WHERE {AT.Id} = '{b.Id}'"));                                          // revision must advance
        Assert.Equal("P0001", await Fails($"UPDATE {AT.Table} SET {AT.UpdatedAtUtc} = now(), {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{a.Id}'"));      // a removed row never changes again
        Assert.Equal("23514", await Fails($"UPDATE {AT.Table} SET {AT.State} = 2, {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));                // removal needs who, when and why
        Assert.Equal("23514", await Fails($"UPDATE {AT.Table} SET {AT.KeyId} = 'k9', {AT.Revision} = {AT.Revision} + 1 WHERE {AT.Id} = '{b.Id}'"));            // a normal file has no key
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

        var (editor, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.AttachmentsUpload, PermissionCatalog.Codes.AttachmentsDelete], ScopeKind.Company, null);
        Assert.Single(Ok(await Mgmt.ListAsync(editor.Id, Company.Id, "local_party", party)));                               // the sensitive one is invisible
        Ok(await Mgmt.OpenAsync(editor.Id, Company.Id, normal.Id));
        Fails(await Mgmt.OpenAsync(editor.Id, Company.Id, secret.Id), ConfigStatus.NotFound, "attachment_not_found");         // looks exactly like a missing file
        Fails(await Mgmt.RemoveAsync(editor.Id, Company.Id, secret.Id, 1, "x"), ConfigStatus.NotFound, "attachment_not_found");
        Fails(await Mgmt.UploadAsync(editor.Id, Company.Id, "local_party", party, "s.pdf", "application/pdf", Pdf, true), ConfigStatus.Forbidden, "forbidden");
        Ok(await Mgmt.UploadAsync(editor.Id, Company.Id, "local_party", party, "n.pdf", "application/pdf", Pdf, false));
        Assert.Equal(new AttachmentCapabilities(true, true, true, false, false), await Mgmt.CapabilitiesAsync(editor.Id, Company.Id));

        var (officer, _) = await Member([PermissionCatalog.Codes.AttachmentsView, PermissionCatalog.Codes.AttachmentsSensitiveView, PermissionCatalog.Codes.AttachmentsSensitiveUpload, PermissionCatalog.Codes.AttachmentsUpload], ScopeKind.Company, null);
        Assert.Equal(3, Ok(await Mgmt.ListAsync(officer.Id, Company.Id, "local_party", party)).Count);
        Ok(await Mgmt.OpenAsync(officer.Id, Company.Id, secret.Id));
        Ok(await Mgmt.UploadAsync(officer.Id, Company.Id, "local_party", party, "s2.pdf", "application/pdf", Pdf, true));
        Fails(await Mgmt.RemoveAsync(officer.Id, Company.Id, secret.Id, 1, "x"), ConfigStatus.Forbidden, "forbidden");           // removal is its own permission
        Fails(await Mgmt.RewrapAsync(editor.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");                               // key rotation handles sensitive files
        Assert.Equal(new RewrapOutcome(0, 2, 0), Ok(await Mgmt.RewrapAsync(officer.Id, Company.Id)));
    }
}
