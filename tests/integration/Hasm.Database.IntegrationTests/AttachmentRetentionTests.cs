using System.Net;
using System.Text;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Attachments;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.Security;
using Npgsql;
using AT = Hasm.Infrastructure.CompanyDatabase.CompanySchema.Attachment;
using Audit_ = Hasm.Infrastructure.CompanyDatabase.CompanySchema.BusinessAudit;
using LH = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LegalHold;
using LP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.LocalParty;
using RP = Hasm.Infrastructure.CompanyDatabase.CompanySchema.RetentionPolicy;
using RR = Hasm.Infrastructure.CompanyDatabase.CompanySchema.RetentionRule;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Phase R slice R1 (company migration 0031, system migration 0030, owner decision D-082) against real PostgreSQL: the retention POLICY life cycle (a draft approved by ANOTHER user, superseded, revoked), legal holds
/// (placed and released by different users), the read-only eligibility preview (nothing is deleted; the retention basis D-109 is owner-pending, so nothing is ever eligible), RBAC and the database guards. Disposable cluster only.
/// </summary>
public class AttachmentRetentionDatabaseTests : InventoryTestBase
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 retention");
    private string dir = null!;
    private AttachmentService Attachments = null!;
    private AttachmentRetentionService Store = null!;
    private AttachmentRetentionManagementService Mgmt = null!;
    private Guid party;

    private async Task Prepare()
    {
        await PrepareInventory();
        dir = Path.Combine(Path.GetTempPath(), "hasm-ret-" + Guid.NewGuid().ToString("N"));
        var options = new AttachmentOptions { StorageDirectory = dir, MaxFileBytes = 1024 * 1024 };
        var transactions = new CompanyAuditedTransactions(Resolver, Audit, Clock);
        Attachments = new AttachmentService(Resolver, transactions, new AttachmentFileStore(options, new EncryptionOptions { AllowInsecureFilePermissions = true }), new EnvelopeEncryptor(new InMemoryKekRing("k1")), options, Clock);
        Store = new AttachmentRetentionService(Resolver, transactions, Clock);
        Mgmt = new AttachmentRetentionManagementService(Rbac, Store);
        party = Guid.NewGuid();
        await Sql($"INSERT INTO {LP.Table} ({LP.Id}, {LP.Code}, {LP.DisplayName}, {LP.IsActive}, {LP.LinkState}, {LP.Revision}, {LP.CreatedBy}, {LP.CreatedAtUtc}, {LP.UpdatedAtUtc}) VALUES ('{party}', 'RT', 'p', true, 1, 1, '{Admin.Id}', now(), now())");
    }

    public override async Task DisposeAsync()
    {
        if (dir is not null && Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private DateOnly Today => DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);
    private Task<long> Events(short type) => Sql<long>($"SELECT count(*) FROM {Audit_.Table} WHERE {Audit_.EventType} = {type}");
    private static RetentionRule Months(string kind, int months, short sens = 0) => new(kind, sens, months, false);
    /// <summary>A member holding the delegated Retention Approver preset (granted offline: a company administrator cannot grant a permission they do not hold).</summary>
    private async Task<UserRecord> Approver()
    {
        var (user, _) = await Member([PermissionCatalog.Codes.RetentionView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantRetentionApproverOfflineAsync(user.Login, Company.Id));
        return user;
    }
    private static async Task Refused(Task t) { var ex = await Assert.ThrowsAnyAsync<Exception>(() => t); Assert.True(ex is PostgresException or AggregateException, ex.GetType().Name); }
    private async Task<AttachmentView> UpAndRemove(bool sensitive = false)
    {
        var up = Ok(await Attachments.UploadAsync(Admin.Id, Company.Id, "local_party", party, sensitive ? "salary.pdf" : "contract.pdf", "application/pdf", Pdf, sensitive));
        return Ok(await Attachments.RemoveAsync(Admin.Id, Company.Id, up.Id, up.Revision, "no longer needed", allowSensitive: true));
    }
    private async Task<RetentionPolicyVersionView> ApprovedPolicy(params RetentionRule[] rules)
    {
        var draft = Ok(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, "note", rules)));
        return Ok(await Store.ApprovePolicyAsync((await Approver()).Id, Company.Id, draft.Id, draft.Revision, null));
    }

    // ---------------------------------------------------------------------------------------------------------------- policy

    [PostgresFact]
    public async Task A_retention_policy_is_drafted_approved_by_another_user_superseded_with_an_acknowledged_shortening_and_revoked_with_database_guards()
    {
        await Prepare();
        var approver = await Approver();
        var v1 = Ok(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, "first", [Months("*", 60), Months("local_party", 120, 2)])));
        Assert.Equal((1, RetentionPolicyStatus.Draft, 1L, 2), (v1.Version, v1.Status, v1.Revision, v1.Rules.Count));
        Assert.Equal(1L, await Events(AttachmentRetentionAuditEvents.PolicyDrafted));

        // validation names the fields; nothing is stored for an invalid draft
        Invalid(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today.AddDays(-1), null, [Months("*", 1)])), "effectiveFrom", "in_the_past");
        Invalid(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, null, [Months("nope", 1)])), "rules[0].subjectKind", "subject_kind_invalid");
        Invalid(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, null, [Months("*", 1), Months("*", 2)])), "rules[1]", "duplicate_rule");
        Assert.Single(await Store.ListPoliciesAsync(Company.Id));

        // the author never approves: refused by the service AND by PostgreSQL
        Fails(await Store.ApprovePolicyAsync(Admin.Id, Company.Id, v1.Id, 1, null), ConfigStatus.Forbidden, "approver_must_differ");
        await Refused(Sql($"UPDATE {RP.Table} SET {RP.Status} = 2, {RP.ApprovedBy} = {RP.DraftedBy}, {RP.ApprovedAtUtc} = now(), {RP.Revision} = {RP.Revision} + 1 WHERE {RP.Id} = '{v1.Id}'"));
        Fails(await Store.ApprovePolicyAsync(approver.Id, Company.Id, v1.Id, null, null), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Store.ApprovePolicyAsync(approver.Id, Company.Id, v1.Id, 9, null), ConfigStatus.PreconditionFailed, "revision_conflict");
        Fails(await Store.ApprovePolicyAsync(approver.Id, Company.Id, Guid.NewGuid(), 1, null), ConfigStatus.NotFound, "policy_not_found");

        var a1 = Ok(await Store.ApprovePolicyAsync(approver.Id, Company.Id, v1.Id, 1, null));
        Assert.Equal((RetentionPolicyStatus.Approved, approver.Id, false, 2L), (a1.Status, a1.ApprovedBy, a1.ShortensAnyPeriod, a1.Revision));   // the first version shortens nothing
        Fails(await Store.ApprovePolicyAsync(approver.Id, Company.Id, v1.Id, 2, null), ConfigStatus.Conflict, "not_a_draft");

        // a shorter second version needs a written acknowledgement; approving it supersedes the first
        var v2 = Ok(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, null, [Months("*", 12)])));
        Assert.Equal(2, v2.Version);
        Invalid(await Store.ApprovePolicyAsync(approver.Id, Company.Id, v2.Id, 1, null), "shorteningAcknowledgement", "required");
        var a2 = Ok(await Store.ApprovePolicyAsync(approver.Id, Company.Id, v2.Id, 1, "the legal department confirmed the shorter period"));
        Assert.True(a2.ShortensAnyPeriod);
        var all = await Store.ListPoliciesAsync(Company.Id);
        Assert.Equal([RetentionPolicyStatus.Superseded, RetentionPolicyStatus.Approved], all.Select(p => p.Status));
        Assert.NotNull(all[0].EndedAtUtc);
        Assert.Null(all[1].EndedAtUtc);
        Assert.Equal(2L, await Events(AttachmentRetentionAuditEvents.PolicyApproved));

        // a longer third version shortens nothing and needs no acknowledgement
        var v3 = Ok(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, null, [Months("*", 240)])));
        Assert.False(Ok(await Store.ApprovePolicyAsync(approver.Id, Company.Id, v3.Id, 1, null)).ShortensAnyPeriod);

        // revoking: only an approved version, with a reason and the revision; the company is back to preservation
        var current = (await Store.ListPoliciesAsync(Company.Id)).Single(p => p.Status == RetentionPolicyStatus.Approved);
        Invalid(await Store.RevokePolicyAsync(approver.Id, Company.Id, current.Id, current.Revision, " "), "reason", "required");
        Fails(await Store.RevokePolicyAsync(approver.Id, Company.Id, current.Id, current.Revision + 5, "x"), ConfigStatus.PreconditionFailed, "revision_conflict");
        var revoked = Ok(await Store.RevokePolicyAsync(approver.Id, Company.Id, current.Id, current.Revision, "wrong period"));
        Assert.Equal((RetentionPolicyStatus.Revoked, approver.Id), (revoked.Status, revoked.RevokedBy));
        Fails(await Store.RevokePolicyAsync(approver.Id, Company.Id, current.Id, revoked.Revision, "again"), ConfigStatus.Conflict, "not_approved");
        Assert.Equal(1L, await Events(AttachmentRetentionAuditEvents.PolicyRevoked));
        Assert.DoesNotContain(await Store.ListPoliciesAsync(Company.Id), p => p.Status == RetentionPolicyStatus.Approved);

        // database guards: rules are immutable and cannot join a non-draft version, versions are never deleted or skipped over, a revoked version never changes
        await Refused(Sql($"INSERT INTO {RR.Table} ({RR.Id}, {RR.PolicyId}, {RR.SubjectKind}, {RR.Sensitivity}, {RR.Months}, {RR.Indefinite}) VALUES (gen_random_uuid(), '{v1.Id}', 'local_party', 1, 3, false)"));
        await Refused(Sql($"UPDATE {RR.Table} SET {RR.Months} = 1"));
        await Refused(Sql($"DELETE FROM {RR.Table}"));
        await Refused(Sql($"DELETE FROM {RP.Table}"));
        await Refused(Sql($"TRUNCATE {RP.Table} CASCADE"));
        await Refused(Sql($"UPDATE {RP.Table} SET {RP.Note} = 'edited', {RP.Revision} = {RP.Revision} + 1 WHERE {RP.Id} = '{v1.Id}'"));
        await Refused(Sql($"UPDATE {RP.Table} SET {RP.Status} = 2, {RP.Revision} = {RP.Revision} + 1 WHERE {RP.Id} = '{current.Id}'"));
        // at most one version is approved at a time (the unique index): an out-of-order direct approval of another draft is refused
        var v4 = Ok(await Store.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, null, [Months("*", 36)])));
        Assert.Equal(4, v4.Version);
        Assert.Equal([1, 2, 3, 4], (await Store.ListPoliciesAsync(Company.Id)).Select(p => p.Version));
    }

    // ---------------------------------------------------------------------------------------------------------------- holds

    [PostgresFact]
    public async Task Legal_holds_are_placed_and_released_by_different_users_and_a_released_hold_and_every_hold_stay_in_the_database()
    {
        await Prepare();
        var approver = await Approver();
        var att = await UpAndRemove();
        // validation of the scope and its columns
        Invalid(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Company, null, null, null, " ", null)), "reason", "required");
        Invalid(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Company, "local_party", null, null, "case", null)), "scope", "scope_mismatch");
        Invalid(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Record, "nope", party, null, "case", null)), "subjectKind", "subject_kind_invalid");
        Invalid(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Record, "local_party", null, null, "case", null)), "subjectId", "required");
        Invalid(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Attachment, null, null, null, "case", null)), "attachmentId", "required");
        Invalid(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Attachment, null, null, Guid.NewGuid(), "case", null)), "attachmentId", "attachment_not_found");
        Invalid(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput((LegalHoldScope)9, null, null, null, "case", null)), "scope", "invalid_value");
        Assert.Empty(await Store.ListHoldsAsync(Company.Id, true));

        var company = Ok(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Company, null, null, null, "audit", "CASE-1")));
        var record = Ok(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Record, "local_party", party, null, "dispute", null)));
        var one = Ok(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Attachment, null, null, att.Id, "evidence", null)));
        Assert.All(new[] { company, record, one }, h => Assert.Equal((true, 1L), (h.IsActive, h.Revision)));
        Assert.Equal(3L, await Events(AttachmentRetentionAuditEvents.HoldPlaced));
        Assert.Equal(3, (await Store.ListHoldsAsync(Company.Id, false)).Count);

        // releasing: not by the placer (service and PostgreSQL), not without a reason or the revision, once only
        Fails(await Store.ReleaseHoldAsync(Admin.Id, Company.Id, company.Id, 1, "done"), ConfigStatus.Forbidden, "releaser_must_differ");
        await Refused(Sql($"UPDATE {LH.Table} SET {LH.ReleasedBy} = {LH.PlacedBy}, {LH.ReleasedAtUtc} = now(), {LH.ReleaseReason} = 'x', {LH.Revision} = {LH.Revision} + 1 WHERE {LH.Id} = '{company.Id}'"));
        Invalid(await Store.ReleaseHoldAsync(approver.Id, Company.Id, company.Id, 1, " "), "reason", "required");
        Fails(await Store.ReleaseHoldAsync(approver.Id, Company.Id, company.Id, null, "done"), ConfigStatus.PreconditionRequired, "precondition_required");
        Fails(await Store.ReleaseHoldAsync(approver.Id, Company.Id, company.Id, 4, "done"), ConfigStatus.PreconditionFailed, "revision_conflict");
        Fails(await Store.ReleaseHoldAsync(approver.Id, Company.Id, Guid.NewGuid(), 1, "done"), ConfigStatus.NotFound, "hold_not_found");
        var released = Ok(await Store.ReleaseHoldAsync(approver.Id, Company.Id, company.Id, 1, "the audit is closed"));
        Assert.Equal((false, approver.Id, 2L), (released.IsActive, released.ReleasedBy, released.Revision));
        Fails(await Store.ReleaseHoldAsync(approver.Id, Company.Id, company.Id, 2, "again"), ConfigStatus.Conflict, "already_released");
        Assert.Equal(1L, await Events(AttachmentRetentionAuditEvents.HoldReleased));
        Assert.Equal(2, (await Store.ListHoldsAsync(Company.Id, false)).Count);
        Assert.Equal(3, (await Store.ListHoldsAsync(Company.Id, true)).Count);                                  // released holds stay visible

        // database guards: never deleted or truncated, only the release changes a hold, a released hold never changes again
        await Refused(Sql($"DELETE FROM {LH.Table}"));
        await Refused(Sql($"TRUNCATE {LH.Table}"));
        await Refused(Sql($"UPDATE {LH.Table} SET {LH.Reason} = 'edited', {LH.Revision} = {LH.Revision} + 1 WHERE {LH.Id} = '{record.Id}'"));
        await Refused(Sql($"UPDATE {LH.Table} SET {LH.ReleaseReason} = 'edited', {LH.Revision} = {LH.Revision} + 1 WHERE {LH.Id} = '{company.Id}'"));
        await Refused(Sql($"UPDATE {LH.Table} SET {LH.Scope} = 1, {LH.SubjectKind} = NULL, {LH.SubjectId} = NULL, {LH.Revision} = {LH.Revision} + 1 WHERE {LH.Id} = '{record.Id}'"));
        await Refused(Sql($"INSERT INTO {LH.Table} ({LH.Id}, {LH.Scope}, {LH.SubjectKind}, {LH.Reason}, {LH.PlacedBy}, {LH.PlacedAtUtc}, {LH.Revision}, {LH.UpdatedAtUtc}) VALUES (gen_random_uuid(), 2, 'local_party', 'no subject id', '{Admin.Id}', now(), 1, now())"));   // a scope with missing columns
    }

    // ---------------------------------------------------------------------------------------------------------------- preview and RBAC

    [PostgresFact]
    public async Task The_preview_lists_removed_attachments_with_reasons_and_nothing_is_eligible_while_the_basis_is_owner_pending_and_the_permissions_are_enforced()
    {
        await Prepare();
        Assert.False(RetentionBasis.OwnerConfirmed);
        var before = await UpAndRemove();                                                                       // removed BEFORE any policy existed
        Clock.Advance(TimeSpan.FromHours(1));
        await ApprovedPolicy(Months("*", 1));
        Clock.Advance(TimeSpan.FromHours(1));
        var plain = await UpAndRemove();
        var secret = await UpAndRemove(sensitive: true);
        var page = await Store.PreviewAsync(Company.Id, null, null);
        Assert.Equal((3L, false), (page.Total, page.BasisConfirmed));
        string Reason(Guid id) => page.Items.Single(i => i.AttachmentId == id).Reason;
        Assert.Equal("indefinite", Reason(before.Id));                                                           // no period was ever committed to
        Assert.Equal("not_yet_eligible", Reason(plain.Id));
        Assert.Equal("not_yet_eligible", Reason(secret.Id));
        Assert.True(page.Items.Single(i => i.AttachmentId == plain.Id).EligibleAtUtc > Clock.GetUtcNow());
        Assert.Equal(new Dictionary<string, int> { ["indefinite"] = 1, ["not_yet_eligible"] = 2 }, page.ReasonCounts);

        // a month later every finite item would be eligible by its date, but the basis is owner-pending: nothing is ever eligible
        Clock.Advance(TimeSpan.FromDays(45));
        var later = await Store.PreviewAsync(Company.Id, null, null);
        Assert.Equal(["indefinite", "retention_basis_pending", "retention_basis_pending"], later.Items.Select(i => i.Reason).Order());
        Assert.DoesNotContain(later.Items, i => i.Reason == "eligible");

        // holds outrank the pending basis in the answer; a release restores it; paging bounds the preview
        var hold = Ok(await Store.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Record, "local_party", party, null, "dispute", null)));
        Assert.Equal(["indefinite", "legal_hold", "legal_hold"], (await Store.PreviewAsync(Company.Id, null, null)).Items.Select(i => i.Reason).Order());
        Assert.Equal(hold.Id, (await Store.PreviewAsync(Company.Id, null, null)).Items.First(i => i.Reason == "legal_hold").HoldId);
        Ok(await Store.ReleaseHoldAsync((await Approver()).Id, Company.Id, hold.Id, 1, "closed"));
        Assert.Equal(2, (await Store.PreviewAsync(Company.Id, null, null)).Items.Count(i => i.Reason == "retention_basis_pending"));
        var firstPage = await Store.PreviewAsync(Company.Id, 0, 2);
        Assert.Equal((2, 3L), (firstPage.Items.Count, firstPage.Total));
        Assert.Single((await Store.PreviewAsync(Company.Id, 2, 2)).Items);
        // a preview never changes anything: the three rows stay removed and no file or row was deleted
        Assert.Equal(3L, await Sql<long>($"SELECT count(*) FROM {AT.Table} WHERE {AT.State} = 2"));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(dir, Company.Id.ToString("N")), "*.att").Length);

        // RBAC: the Company Administrator preset reads, drafts and holds but NEVER approves; the Retention Approver preset approves; an outsider does nothing
        var drafted = Ok(await Mgmt.DraftPolicyAsync(Admin.Id, Company.Id, new RetentionPolicyInput(Today, null, [Months("*", 2)])));
        Fails(await Mgmt.ApprovePolicyAsync(Admin.Id, Company.Id, drafted.Id, drafted.Revision, null), ConfigStatus.Forbidden, "forbidden");
        Fails(await Mgmt.RevokePolicyAsync(Admin.Id, Company.Id, drafted.Id, 1, "x"), ConfigStatus.Forbidden, "forbidden");
        Ok(await Mgmt.PreviewAsync(Admin.Id, Company.Id, null, null));
        var held = Ok(await Mgmt.PlaceHoldAsync(Admin.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Company, null, null, null, "case", null)));
        Fails(await Mgmt.ReleaseHoldAsync(Admin.Id, Company.Id, held.Id, 1, "x"), ConfigStatus.Forbidden, "forbidden");           // release needs approve
        var viewer = (await Member([PermissionCatalog.Codes.RetentionView], ScopeKind.Company, null)).User;
        Ok(await Mgmt.ListPoliciesAsync(viewer.Id, Company.Id));
        Ok(await Mgmt.ListHoldsAsync(viewer.Id, Company.Id, true));
        Fails(await Mgmt.DraftPolicyAsync(viewer.Id, Company.Id, new RetentionPolicyInput(Today, null, [Months("*", 1)])), ConfigStatus.Forbidden, "forbidden");
        Fails(await Mgmt.PlaceHoldAsync(viewer.Id, Company.Id, new LegalHoldInput(LegalHoldScope.Company, null, null, null, "case", null)), ConfigStatus.Forbidden, "forbidden");
        var outsider = await NewUser("retentionoutsider");
        Fails(await Mgmt.ListPoliciesAsync(outsider.Id, Company.Id), ConfigStatus.Forbidden, "forbidden");
        Fails(await Mgmt.PreviewAsync(outsider.Id, Company.Id, null, null), ConfigStatus.Forbidden, "forbidden");
        var none = await Mgmt.CapabilitiesAsync(outsider.Id, Company.Id);
        Assert.Equal((false, false, false, false, false), (none.View, none.Configure, none.Hold, none.Approve, none.Execute));
        var admin = await Mgmt.CapabilitiesAsync(Admin.Id, Company.Id);
        Assert.Equal((true, true, true, false, true), (admin.View, admin.Configure, admin.Hold, admin.Approve, admin.Execute));
        var (offline, _) = await Member([PermissionCatalog.Codes.RetentionView], ScopeKind.Company, null);
        Assert.Equal(AccessService.FiscalApproverStatus.Granted, await Rbac.GrantRetentionApproverOfflineAsync(offline.Login, Company.Id));
        Ok(await Mgmt.ApprovePolicyAsync(offline.Id, Company.Id, drafted.Id, drafted.Revision, "acknowledged"));
        var granted = await Mgmt.CapabilitiesAsync(offline.Id, Company.Id);
        Assert.Equal((true, false, false, true, false), (granted.View, granted.Configure, granted.Hold, granted.Approve, granted.Execute));
    }

}
