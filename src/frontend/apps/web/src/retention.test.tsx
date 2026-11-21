import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { retentionAr, retentionEn } from "./messages.retention";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const A = (k: string) => catalog.ar[k]!;
const E = (k: string) => catalog.en[k]!;
const go = async () => {
  await screen.findByTestId("company-name");
  window.location.hash = "#/company/attachment-retention";
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const perms = [...ALL_PERMISSIONS, "company.attachmentretention.view"];
const caps = (over: Record<string, boolean> = {}) => ({ view: true, configure: true, hold: true, approve: true, execute: false, basisConfirmed: false, ...over });
const rule = { subjectKind: "*", sensitivity: 0, months: 84, indefinite: false };
const draft = { id: "v2", version: 2, status: 1, effectiveFrom: "2026-12-01T00:00:00Z", shortensAnyPeriod: false, shorteningAcknowledgement: null, note: null, draftedBy: "u9", draftedAtUtc: "2026-11-01T00:00:00Z", approvedBy: null, approvedAtUtc: null, revokedBy: null, revokedAtUtc: null, revokeReason: null, endedAtUtc: null, revision: 4, rules: [rule] };
const approved = { ...draft, id: "v1", version: 1, status: 2, draftedBy: "u8", approvedBy: "u9", revision: 3 };
const hold = { id: "h1", scope: 1, subjectKind: null, subjectId: null, attachmentId: null, reason: "Court order", reference: "C-1", placedBy: "u9", placedAtUtc: "2026-11-01T00:00:00Z", releasedBy: null, releasedAtUtc: null, releaseReason: null, isActive: true, revision: 2 };
const previewData = { total: 1, basisConfirmed: false, reasonCounts: { retention_basis_pending: 1 }, items: [{ attachmentId: "a1", subjectKind: "local_party", subjectId: "p1", isSensitive: true, removedAtUtc: "2026-01-05T00:00:00Z", reason: "retention_basis_pending", eligibleAtUtc: "2033-01-05T00:00:00Z", holdId: null, policyVersion: 1 }] };

function render(over: Record<string, unknown> = {}, c = caps(), p = perms) {
  const client = signedIn({
    getRetentionCapabilities: vi.fn().mockResolvedValue(c),
    listRetentionPolicies: vi.fn().mockResolvedValue({ policies: [draft, approved] }),
    listLegalHolds: vi.fn().mockResolvedValue({ holds: [hold] }),
    previewRetention: vi.fn().mockResolvedValue(previewData),
    draftRetentionPolicy: vi.fn().mockResolvedValue(draft),
    approveRetentionPolicy: vi.fn().mockResolvedValue({ ...draft, status: 2 }),
    revokeRetentionPolicy: vi.fn().mockResolvedValue({ ...approved, status: 3 }),
    placeLegalHold: vi.fn().mockResolvedValue(hold),
    releaseLegalHold: vi.fn().mockResolvedValue({ ...hold, isActive: false }),
    ...over,
  }, p);
  renderApp(client);
  return client;
}
const tabByName = (n: string) => screen.getByRole("tab", { name: new RegExp(`^${n}`) });

describe("attachment retention screen", () => {
  it("is Arabic and right-to-left by default and says purge is disabled while D-109 is pending", async () => {
    render();
    await go();
    expect(await screen.findByRole("heading", { name: A("ret.title") })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("rtl");
    expect(screen.getByTestId("basis-pending")).toHaveTextContent(A("ret.basis_pending"));
    expect(tabByName(A("ret.tab.policy"))).toBeInTheDocument();
  });

  it("is English and left-to-right when the language is English", async () => {
    localStorage.setItem("hasm.locale", "en");
    render();
    await go();
    expect(await screen.findByRole("heading", { name: E("ret.title") })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(screen.getByTestId("basis-pending")).toHaveTextContent(E("ret.basis_pending"));
  });

  it("does not show the pending banner once the basis is confirmed, and never offers a purge action", async () => {
    localStorage.setItem("hasm.locale", "en");
    render({}, caps({ basisConfirmed: true, execute: true }));
    await go();
    await screen.findByRole("heading", { name: E("ret.title") });
    expect(screen.queryByTestId("basis-pending")).toBeNull();
    expect(screen.queryByRole("button", { name: /purge/i })).toBeNull();
  });

  it("hides every write action from a viewer (the server still authorizes)", async () => {
    localStorage.setItem("hasm.locale", "en");
    render({}, caps({ configure: false, hold: false, approve: false }));
    await go();
    await screen.findByRole("heading", { name: E("ret.title") });
    expect(screen.queryByRole("button", { name: E("ret.policy.approve") })).toBeNull();
    expect(screen.queryByRole("button", { name: E("ret.policy.save_draft") })).toBeNull();
    expect(screen.queryByRole("button", { name: E("ret.hold.release") })).toBeNull();
    expect(screen.queryByRole("button", { name: E("ret.hold.place") })).toBeNull();
  });

  it("approves a draft written by someone else, but offers no approval of the user's own draft", async () => {
    localStorage.setItem("hasm.locale", "en");
    const client = render();
    await go();
    const row = await screen.findByTestId("policy-2");
    await userEvent.click(within(row).getByRole("button", { name: E("ret.policy.approve") }));
    const dialog = screen.getByRole("dialog");
    expect(screen.queryByLabelText(new RegExp(E("ret.policy.ack_label").slice(0, 20)))).toBeNull();
    await userEvent.click(within(dialog).getByRole("button", { name: E("ret.policy.approve") }));
    await waitFor(() => expect(client.approveRetentionPolicy).toHaveBeenCalledWith("v2", 4, null));
  });

  it("offers no approval when the user wrote the draft", async () => {
    localStorage.setItem("hasm.locale", "en");
    render({ listRetentionPolicies: vi.fn().mockResolvedValue({ policies: [{ ...draft, draftedBy: "u1" }] }) });
    await go();
    const row = await screen.findByTestId("policy-2");
    expect(within(row).queryByRole("button", { name: E("ret.policy.approve") })).toBeNull();
    expect(within(row).getByText(E("ret.policy.own_draft"))).toBeInTheDocument();
  });

  it("asks for the written acknowledgement when a draft shortens a period", async () => {
    localStorage.setItem("hasm.locale", "en");
    const client = render({ listRetentionPolicies: vi.fn().mockResolvedValue({ policies: [{ ...draft, shortensAnyPeriod: true }] }) });
    await go();
    await userEvent.click(within(await screen.findByTestId("policy-2")).getByRole("button", { name: E("ret.policy.approve") }));
    await userEvent.type(screen.getByLabelText(new RegExp(E("ret.policy.ack_label").slice(0, 20))), "Counsel agreed");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.policy.approve") }));
    await waitFor(() => expect(client.approveRetentionPolicy).toHaveBeenCalledWith("v2", 4, "Counsel agreed"));
  });

  it("shows a server refusal (the author may not approve) as a translated message", async () => {
    localStorage.setItem("hasm.locale", "en");
    render({ approveRetentionPolicy: vi.fn().mockRejectedValue(apiError(409, "approver_must_differ")) });
    await go();
    await userEvent.click(within(await screen.findByTestId("policy-2")).getByRole("button", { name: E("ret.policy.approve") }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.policy.approve") }));
    expect(await screen.findByText(E("ret.error.approver_must_differ"))).toBeInTheDocument();
  });

  it("saves a draft with the entered rules", async () => {
    localStorage.setItem("hasm.locale", "en");
    const client = render();
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.effective_from")}`)), { target: { value: "2027-01-01" } });
    fireEvent.change(screen.getByLabelText(E("ret.rule.months")), { target: { value: "60" } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    await waitFor(() => expect(client.draftRetentionPolicy).toHaveBeenCalledWith({ effectiveFrom: "2027-01-01", note: null, rules: [{ subjectKind: "*", sensitivity: 0, indefinite: false, months: 60 }] }));
  });

  it.each([["٦٠", "Arabic-Indic"], ["۶۰", "Persian"], ["٦0", "mixed Arabic-Indic and Latin"]])("accepts %s (%s) for the months and sends 60", async (text) => {
    localStorage.setItem("hasm.locale", "en");
    const client = render();
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.effective_from")}`)), { target: { value: "2027-01-01" } });
    fireEvent.change(screen.getByLabelText(E("ret.rule.months")), { target: { value: text } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    await waitFor(() => expect(client.draftRetentionPolicy).toHaveBeenCalledWith({ effectiveFrom: "2027-01-01", note: null, rules: [{ subjectKind: "*", sensitivity: 0, indefinite: false, months: 60 }] }));
  });

  it.each(["", "x", "0", "1201", "٥٫٥", "1,000", "٦x"])("refuses the invalid months value '%s': nothing is sent and the field shows its error", async (text) => {
    localStorage.setItem("hasm.locale", "en");
    const client = render();
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.effective_from")}`)), { target: { value: "2027-01-01" } });
    fireEvent.change(screen.getByLabelText(E("ret.rule.months")), { target: { value: text } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    expect(await screen.findByText(E("field.ret.out_of_range"))).toBeInTheDocument();
    expect(client.draftRetentionPolicy).not.toHaveBeenCalled();
  });

  it("shows a server field error for the months in the scoped wording, inline and in the banner (the value is client-valid, so the server path runs)", async () => {
    localStorage.setItem("hasm.locale", "en");
    const draftRetentionPolicy = vi.fn().mockRejectedValue(apiError(400, "invalid_request", { fields: [{ field: "rules[0].months", code: "duplicate_rule" }] }));
    render({ draftRetentionPolicy });
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.effective_from")}`)), { target: { value: "2027-01-01" } });
    fireEvent.change(screen.getByLabelText(E("ret.rule.months")), { target: { value: "60" } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    await waitFor(() => expect(draftRetentionPolicy).toHaveBeenCalledTimes(1));
    const shown = await screen.findAllByText(E("field.ret.duplicate_rule"));
    expect(shown.length).toBeGreaterThanOrEqual(2);                                                    // the inline field error and the banner list
    expect(shown.some((el) => el.classList.contains("field-error"))).toBe(true);
    expect(shown.some((el) => el.closest(".banner") !== null)).toBe(true);
  });

  it("does not let an emptied client error hide the server's inline error for the same rule", async () => {
    localStorage.setItem("hasm.locale", "en");
    const draftRetentionPolicy = vi.fn().mockRejectedValue(apiError(400, "invalid_request", { fields: [{ field: "rules[0].months", code: "out_of_range" }] }));
    render({ draftRetentionPolicy });
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.effective_from")}`)), { target: { value: "2027-01-01" } });
    const months = () => screen.getByLabelText(E("ret.rule.months"));
    fireEvent.change(months(), { target: { value: "x" } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    expect(await screen.findByText(E("field.ret.out_of_range"))).toBeInTheDocument();                // the client error
    fireEvent.change(months(), { target: { value: "60" } });                                          // fixing the value empties the client error
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    await waitFor(() => expect(draftRetentionPolicy).toHaveBeenCalledTimes(1));
    const shown = await screen.findAllByText(E("field.ret.out_of_range"));
    expect(shown.some((el) => el.classList.contains("field-error"))).toBe(true);                      // the server's inline error is visible
  });

  it("keeps a months error on its own rule when another rule is removed", async () => {
    localStorage.setItem("hasm.locale", "en");
    const draftRetentionPolicy = vi.fn();
    render({ draftRetentionPolicy });
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.effective_from")}`)), { target: { value: "2027-01-01" } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.rule.add") }));
    const inputs = () => screen.getAllByLabelText(E("ret.rule.months"));
    fireEvent.change(inputs()[0]!, { target: { value: "60" } });
    fireEvent.change(inputs()[1]!, { target: { value: "x" } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    expect(await screen.findAllByText(E("field.ret.out_of_range"))).toHaveLength(1);
    expect(inputs()[1]).toHaveAttribute("aria-invalid", "true");
    await userEvent.click(screen.getAllByRole("button", { name: E("ret.rule.remove") })[0]!);          // remove the FIRST rule
    expect(inputs()).toHaveLength(1);
    expect(inputs()[0]).toHaveAttribute("aria-invalid", "true");                                       // the error followed its rule (the invalid one)
    expect(screen.getAllByText(E("field.ret.out_of_range"))).toHaveLength(1);
    expect(draftRetentionPolicy).not.toHaveBeenCalled();
  });

  it.each(["60.0", "6,0", "٦٫٠", "۶٫۰"])("refuses the non-integer months value '%s' (pure digits only)", async (text) => {
    localStorage.setItem("hasm.locale", "en");
    const client = render();
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.effective_from")}`)), { target: { value: "2027-01-01" } });
    fireEvent.change(screen.getByLabelText(E("ret.rule.months")), { target: { value: text } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.policy.save_draft") }));
    expect(await screen.findByText(E("field.ret.out_of_range"))).toBeInTheDocument();
    expect(client.draftRetentionPolicy).not.toHaveBeenCalled();
  });

  it("releases a hold placed by someone else and not the user's own", async () => {
    localStorage.setItem("hasm.locale", "en");
    const client = render({ listLegalHolds: vi.fn().mockResolvedValue({ holds: [hold, { ...hold, id: "h2", placedBy: "u1" }] }) });
    await go();
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.holds")}`) }));
    expect(within(screen.getByTestId("hold-h2")).queryByRole("button", { name: E("ret.hold.release") })).toBeNull();
    await userEvent.click(within(screen.getByTestId("hold-h1")).getByRole("button", { name: E("ret.hold.release") }));
    await userEvent.type(within(screen.getByRole("dialog")).getByLabelText(new RegExp(`^${E("ret.reason")}`)), "Case closed");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.hold.release") }));
    await waitFor(() => expect(client.releaseLegalHold).toHaveBeenCalledWith("h1", 2, "Case closed"));
  });

  it("shows the server refusal when a hold is released by the person who placed it (the UI mirror can be stale)", async () => {
    localStorage.setItem("hasm.locale", "en");
    render({ releaseLegalHold: vi.fn().mockRejectedValue(apiError(409, "releaser_must_differ")) });
    await go();
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.holds")}`) }));
    await userEvent.click(within(screen.getByTestId("hold-h1")).getByRole("button", { name: E("ret.hold.release") }));
    await userEvent.type(within(screen.getByRole("dialog")).getByLabelText(new RegExp(`^${E("ret.reason")}`)), "Closed");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.hold.release") }));
    expect(await screen.findByText(E("ret.error.releaser_must_differ"))).toBeInTheDocument();
  });

  it("applies the same two-person mirror in Arabic (own draft and own hold offer no action)", async () => {
    render({
      listRetentionPolicies: vi.fn().mockResolvedValue({ policies: [{ ...draft, draftedBy: "u1" }] }),
      listLegalHolds: vi.fn().mockResolvedValue({ holds: [{ ...hold, placedBy: "u1" }] }),
    });
    await go();
    const row = await screen.findByTestId("policy-2");
    expect(within(row).queryByRole("button", { name: A("ret.policy.approve") })).toBeNull();
    expect(within(row).getByText(A("ret.policy.own_draft"))).toBeInTheDocument();
    await userEvent.click(screen.getByRole("tab", { name: new RegExp(`^${A("ret.tab.holds")}`) }));
    const h = screen.getByTestId("hold-h1");
    expect(within(h).queryByRole("button", { name: A("ret.hold.release") })).toBeNull();
    expect(within(h).getByText(A("ret.hold.own"))).toBeInTheDocument();
  });

  it("revokes the approved version with a reason, using its revision", async () => {
    localStorage.setItem("hasm.locale", "en");
    const client = render();
    await go();
    await userEvent.click(within(await screen.findByTestId("policy-1")).getByRole("button", { name: E("ret.policy.revoke") }));
    await userEvent.type(within(screen.getByRole("dialog")).getByLabelText(new RegExp(`^${E("ret.reason")}`)), "Superseded by counsel");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.policy.revoke") }));
    await waitFor(() => expect(client.revokeRetentionPolicy).toHaveBeenCalledWith("v1", 3, "Superseded by counsel"));
  });

  it("shows the conflict message when the revision is stale (412)", async () => {
    localStorage.setItem("hasm.locale", "en");
    render({ approveRetentionPolicy: vi.fn().mockRejectedValue(apiError(412, "precondition_failed")) });
    await go();
    await userEvent.click(within(await screen.findByTestId("policy-2")).getByRole("button", { name: E("ret.policy.approve") }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.policy.approve") }));
    expect(await screen.findByText(E("common.conflict"))).toBeInTheDocument();
  });

  it("mirrors the server for hold release: it needs the approve permission, not the hold permission", async () => {
    localStorage.setItem("hasm.locale", "en");
    // hold-only user: may place a hold, but the server refuses a release, so no button
    render({}, caps({ hold: true, approve: false }));
    await go();
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.holds")}`) }));
    expect(within(screen.getByTestId("hold-h1")).queryByRole("button", { name: E("ret.hold.release") })).toBeNull();
    expect(screen.getByRole("button", { name: E("ret.hold.place") })).toBeInTheDocument();
  });

  it("shows hold release to an approver who cannot place holds", async () => {
    localStorage.setItem("hasm.locale", "en");
    render({}, caps({ hold: false, approve: true }));
    await go();
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.holds")}`) }));
    expect(within(screen.getByTestId("hold-h1")).getByRole("button", { name: E("ret.hold.release") })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: E("ret.hold.place") })).toBeNull();
  });

  it("keeps Confirm disabled until the required acknowledgement or reason is entered", async () => {
    localStorage.setItem("hasm.locale", "en");
    const client = render({ listRetentionPolicies: vi.fn().mockResolvedValue({ policies: [{ ...draft, shortensAnyPeriod: true }, approved] }) });
    await go();
    await userEvent.click(within(await screen.findByTestId("policy-2")).getByRole("button", { name: E("ret.policy.approve") }));
    const confirm = () => within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.policy.approve") });
    expect(confirm()).toBeDisabled();
    await userEvent.type(screen.getByLabelText(new RegExp(E("ret.policy.ack_label").slice(0, 20))), "Counsel agreed");
    expect(confirm()).toBeEnabled();
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("cancel") }));
    expect(client.approveRetentionPolicy).not.toHaveBeenCalled();
    // revoke needs a reason
    await userEvent.click(within(screen.getByTestId("policy-1")).getByRole("button", { name: E("ret.policy.revoke") }));
    const revoke = () => within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.policy.revoke") });
    expect(revoke()).toBeDisabled();
    await userEvent.type(within(screen.getByRole("dialog")).getByLabelText(new RegExp(`^${E("ret.reason")}`)), "x");
    expect(revoke()).toBeEnabled();
  });

  it("keeps the release Confirm disabled until a reason is entered", async () => {
    localStorage.setItem("hasm.locale", "en");
    render();
    await go();
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.holds")}`) }));
    await userEvent.click(within(screen.getByTestId("hold-h1")).getByRole("button", { name: E("ret.hold.release") }));
    const confirm = () => within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.hold.release") });
    expect(confirm()).toBeDisabled();
    await userEvent.type(within(screen.getByRole("dialog")).getByLabelText(new RegExp(`^${E("ret.reason")}`)), "Closed");
    expect(confirm()).toBeEnabled();
  });

  it.each(["shortening_acknowledgement_required", "effective_date_passed", "concurrent_change", "attachment_not_found"])("maps the server refusal %s to its own message", async (code) => {
    localStorage.setItem("hasm.locale", "en");
    render({ approveRetentionPolicy: vi.fn().mockRejectedValue(apiError(409, code)) });
    await go();
    await userEvent.click(within(await screen.findByTestId("policy-2")).getByRole("button", { name: E("ret.policy.approve") }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: E("ret.policy.approve") }));
    expect(await screen.findByText(E(`ret.error.${code}`))).toBeInTheDocument();
    expect(E(`ret.error.${code}`)).not.toBe(E("error.generic"));
  });

  it("places a record-level hold", async () => {
    localStorage.setItem("hasm.locale", "en");
    const client = render();
    await go();
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.holds")}`) }));
    fireEvent.change(screen.getByLabelText(E("ret.hold.scope"), { selector: "select" }), { target: { value: "2" } });
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.hold.subject_id")}`)), { target: { value: "p1" } });
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.reason")}`)), { target: { value: "Dispute" } });
    await userEvent.click(screen.getByRole("button", { name: E("ret.hold.place") }));
    await waitFor(() => expect(client.placeLegalHold).toHaveBeenCalledWith({ scope: 2, reason: "Dispute", reference: null, subjectKind: "local_party", subjectId: "p1", attachmentId: null }));
  });

  describe("hold target pickers", () => {
    const party = { id: "p1", code: "P1", displayName: "Acme", isActive: true, linkState: 1, legalNameAr: null, legalNameEn: null, hasOpenRequest: false };
    const item = { id: "i1", code: "APPLE", nameAr: "تفاح", nameEn: "Apple", isActive: true };
    const openHoldForm = async (scopeValue: string) => {
      await go();
      await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.holds")}`) }));
      fireEvent.change(screen.getByLabelText(E("ret.hold.scope"), { selector: "select" }), { target: { value: scopeValue } });
    };
    const reasonAndPlace = async () => {
      fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.reason")}`)), { target: { value: "Dispute" } });
      await userEvent.click(screen.getByRole("button", { name: E("ret.hold.place") }));
    };

    it("picks a party by search when the user may read parties, and sends its id", async () => {
      localStorage.setItem("hasm.locale", "en");
      const listParties = vi.fn().mockResolvedValue({ parties: [party], total: 1 });
      const client = render({ listParties }, caps(), [...perms, "company.parties.view"]);
      await openHoldForm("2");
      expect(screen.queryByLabelText(new RegExp(`^${E("ret.hold.subject_id")}`))).toBeNull();          // no typed id when the picker works
      const box = await screen.findByRole("combobox", { name: E("ret.hold.record") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /P1/ })).toBeInTheDocument());
      expect(listParties).toHaveBeenCalledWith(expect.objectContaining({ limit: 50 }));
      await userEvent.selectOptions(box, "p1");
      await reasonAndPlace();
      await waitFor(() => expect(client.placeLegalHold).toHaveBeenCalledWith({ scope: 2, reason: "Dispute", reference: null, subjectKind: "local_party", subjectId: "p1", attachmentId: null }));
    });

    it("picks an item by search when the user may read items, and clears the choice when the kind changes", async () => {
      localStorage.setItem("hasm.locale", "en");
      const listParties = vi.fn().mockResolvedValue({ parties: [party], total: 1 });
      const listInventoryItems = vi.fn().mockResolvedValue({ items: [item], total: 1 });
      const client = render({ listParties, listInventoryItems }, caps(), [...perms, "company.parties.view", "company.items.view"]);
      await openHoldForm("2");
      const first = await screen.findByRole("combobox", { name: E("ret.hold.record") });
      await waitFor(() => expect(within(first).getByRole("option", { name: /P1/ })).toBeInTheDocument());
      await userEvent.selectOptions(first, "p1");
      fireEvent.change(screen.getByLabelText(E("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "inventory_item" } });
      const box = await screen.findByRole("combobox", { name: E("ret.hold.record") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /APPLE/ })).toBeInTheDocument());
      expect(box).toHaveValue("");                                                                        // the party choice was cleared
      await userEvent.selectOptions(box, "i1");
      await reasonAndPlace();
      await waitFor(() => expect(client.placeLegalHold).toHaveBeenCalledWith({ scope: 2, reason: "Dispute", reference: null, subjectKind: "inventory_item", subjectId: "i1", attachmentId: null }));
    });

    it("falls back to the typed id with a hint when the user may not read that kind", async () => {
      localStorage.setItem("hasm.locale", "en");
      const listParties = vi.fn();
      const client = render({ listParties }, caps(), [...perms, "company.parties.view"]);              // parties yes, items no
      await openHoldForm("2");
      fireEvent.change(screen.getByLabelText(E("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "inventory_item" } });
      const typed = await screen.findByLabelText(new RegExp(`^${E("ret.hold.subject_id")}`));
      expect(screen.getByText(E("ret.hold.record_unavailable"))).toBeInTheDocument();
      expect(screen.queryByRole("combobox", { name: E("ret.hold.record") })).toBeNull();
      fireEvent.change(typed, { target: { value: "i9" } });
      await reasonAndPlace();
      await waitFor(() => expect(client.placeLegalHold).toHaveBeenCalledWith({ scope: 2, reason: "Dispute", reference: null, subjectKind: "inventory_item", subjectId: "i9", attachmentId: null }));
    });

    it("falls back to the typed id when the server answers 403 to the list, never a broken picker", async () => {
      localStorage.setItem("hasm.locale", "en");
      const listParties = vi.fn().mockRejectedValue(apiError(403, "forbidden"));
      render({ listParties }, caps(), [...perms, "company.parties.view"]);
      await openHoldForm("2");
      expect(await screen.findByLabelText(new RegExp(`^${E("ret.hold.subject_id")}`))).toBeInTheDocument();
      expect(screen.getByText(E("ret.hold.record_unavailable"))).toBeInTheDocument();
      expect(screen.queryByRole("combobox", { name: E("ret.hold.record") })).toBeNull();
    });

    const removed = (id: string, over: Record<string, unknown> = {}) => ({ attachmentId: id, subjectKind: "local_party", subjectId: "p1", isSensitive: false, removedAtUtc: "2026-02-03T00:00:00Z", reason: "retention_basis_pending", eligibleAtUtc: null, holdId: null, policyVersion: null, ...over });
    const filtered = (items: unknown[]) => vi.fn().mockImplementation(async (_o?: number, _l?: number, f?: { subjectKind?: string; subjectId?: string }) => ({ ...previewData, items: f?.subjectId ? items : previewData.items, total: f?.subjectId ? items.length : previewData.total }));

    it("keeps the typed attachment id as a fallback, with the hint to copy it from the Eligibility preview", async () => {
      localStorage.setItem("hasm.locale", "en");
      const client = render();
      await openHoldForm("3");
      await userEvent.click(screen.getByRole("button", { name: E("ret.hold.attachment_use_id") }));
      expect(screen.getByText(E("ret.hold.attachment_hint"))).toBeInTheDocument();
      fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.hold.attachment_id")}`)), { target: { value: "a1" } });
      await reasonAndPlace();
      await waitFor(() => expect(client.placeLegalHold).toHaveBeenCalledWith({ scope: 3, reason: "Dispute", reference: null, subjectKind: null, subjectId: null, attachmentId: "a1" }));
    });

    it("picks the record, then one of its removed attachments from the filtered preview (ids and dates only)", async () => {
      localStorage.setItem("hasm.locale", "en");
      const previewRetention = filtered([removed("11111111-aaaa-bbbb-cccc-000000000001"), removed("22222222-aaaa-bbbb-cccc-000000000002", { isSensitive: true })]);
      const listParties = vi.fn().mockResolvedValue({ parties: [party], total: 1 });
      const client = render({ listParties, previewRetention }, caps(), [...perms, "company.parties.view"]);
      await openHoldForm("3");
      expect(screen.getByText(E("ret.hold.attachment_pick_record"))).toBeInTheDocument();                       // no record yet: nothing to list
      expect(screen.queryByRole("combobox", { name: E("ret.hold.attachment") })).toBeNull();
      const record = await screen.findByRole("combobox", { name: E("ret.hold.record") });
      await waitFor(() => expect(within(record).getByRole("option", { name: /P1/ })).toBeInTheDocument());
      await userEvent.selectOptions(record, "p1");
      const box = await screen.findByRole("combobox", { name: E("ret.hold.attachment") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /11111111/ })).toBeInTheDocument());
      expect(previewRetention).toHaveBeenCalledWith(0, 50, { subjectKind: "local_party", subjectId: "p1" });    // the ids-only filter, bounded
      expect(within(box).getByRole("option", { name: new RegExp(E("ret.preview.sensitive")) })).toBeInTheDocument();
      expect(screen.queryByText(/\.pdf/)).toBeNull();                                                          // never a file name
      await userEvent.selectOptions(box, "22222222-aaaa-bbbb-cccc-000000000002");
      await reasonAndPlace();
      await waitFor(() => expect(client.placeLegalHold).toHaveBeenCalledWith({ scope: 3, reason: "Dispute", reference: null, subjectKind: null, subjectId: null, attachmentId: "22222222-aaaa-bbbb-cccc-000000000002" }));
    });

    it("lists the removed attachments of a typed record id when the user cannot search records, and clears the choice when the record changes", async () => {
      localStorage.setItem("hasm.locale", "en");
      const previewRetention = filtered([removed("33333333-aaaa-bbbb-cccc-000000000003")]);
      render({ previewRetention });                                                                              // no parties.view: typed record id
      await openHoldForm("3");
      fireEvent.change(await screen.findByLabelText(new RegExp(`^${E("ret.hold.subject_id")}`)), { target: { value: "p9" } });
      const box = await screen.findByRole("combobox", { name: E("ret.hold.attachment") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /33333333/ })).toBeInTheDocument());
      expect(previewRetention).toHaveBeenCalledWith(0, 50, { subjectKind: "local_party", subjectId: "p9" });
      await userEvent.selectOptions(box, "33333333-aaaa-bbbb-cccc-000000000003");
      fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.hold.subject_id")}`)), { target: { value: "p8" } });
      await waitFor(() => expect(previewRetention).toHaveBeenCalledWith(0, 50, { subjectKind: "local_party", subjectId: "p8" }));
      expect(await screen.findByRole("combobox", { name: E("ret.hold.attachment") })).toHaveValue("");          // the earlier attachment is no longer selected
    });

    it("switches between the picker and the typed id without keeping a stale choice", async () => {
      localStorage.setItem("hasm.locale", "en");
      render();
      await openHoldForm("3");
      await userEvent.click(screen.getByRole("button", { name: E("ret.hold.attachment_use_id") }));
      fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.hold.attachment_id")}`)), { target: { value: "a1" } });
      await userEvent.click(screen.getByRole("button", { name: E("ret.hold.attachment_use_picker") }));
      expect(screen.getByText(E("ret.hold.attachment_pick_record"))).toBeInTheDocument();
      await userEvent.click(screen.getByRole("button", { name: E("ret.hold.attachment_use_id") }));
      expect(screen.getByLabelText(new RegExp(`^${E("ret.hold.attachment_id")}`))).toHaveValue("");           // the typed value was not kept
    });

    it("shows a retryable error, not a broken picker, when the filtered preview cannot be read", async () => {
      localStorage.setItem("hasm.locale", "en");
      const previewRetention = vi.fn().mockImplementation(async (_o?: number, _l?: number, f?: { subjectId?: string }) => { if (f?.subjectId) throw apiError(500, "internal"); return previewData; });
      render({ previewRetention });
      await openHoldForm("3");
      fireEvent.change(await screen.findByLabelText(new RegExp(`^${E("ret.hold.subject_id")}`)), { target: { value: "p1" } });
      expect(await screen.findByText(E("picker.error"))).toBeInTheDocument();
      expect(screen.getByRole("button", { name: E("picker.retry") })).toBeInTheDocument();
    });

    it("shows the Arabic picker, the Arabic fallback hint and the Arabic attachment hint", async () => {
      render({ listParties: vi.fn().mockResolvedValue({ parties: [party], total: 1 }) }, caps(), [...perms, "company.parties.view"]);   // parties yes, items no
      await go();
      await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${A("ret.tab.holds")}`) }));
      fireEvent.change(screen.getByLabelText(A("ret.hold.scope"), { selector: "select" }), { target: { value: "2" } });
      expect(await screen.findByRole("combobox", { name: A("ret.hold.record") })).toBeInTheDocument();                                  // the Arabic picker
      expect(screen.queryByText(A("ret.hold.record_unavailable"))).toBeNull();
      fireEvent.change(screen.getByLabelText(A("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "inventory_item" } });
      expect(await screen.findByText(A("ret.hold.record_unavailable"))).toBeInTheDocument();                                            // the Arabic fallback hint
      expect(screen.getByLabelText(new RegExp(`^${A("ret.hold.subject_id")}`))).toBeInTheDocument();
      fireEvent.change(screen.getByLabelText(A("ret.hold.scope"), { selector: "select" }), { target: { value: "3" } });
      expect(await screen.findByText(A("ret.hold.attachment_pick_record"))).toBeInTheDocument();                                        // the Arabic "choose the record first"
      await userEvent.click(screen.getByRole("button", { name: A("ret.hold.attachment_use_id") }));                                     // the hint belongs to the typed-id fallback
      expect(await screen.findByText(A("ret.hold.attachment_hint"))).toBeInTheDocument();                                               // the Arabic attachment hint
    });

    it("remembers a 403 for a kind: switching to the other kind keeps its picker, and switching back keeps the fallback", async () => {
      localStorage.setItem("hasm.locale", "en");
      const listParties = vi.fn().mockRejectedValue(apiError(403, "forbidden"));
      const listInventoryItems = vi.fn().mockResolvedValue({ items: [item], total: 1 });
      render({ listParties, listInventoryItems }, caps(), [...perms, "company.parties.view", "company.items.view"]);
      await openHoldForm("2");
      expect(await screen.findByText(E("ret.hold.record_unavailable"))).toBeInTheDocument();                                            // parties: 403 -> typed id
      fireEvent.change(screen.getByLabelText(E("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "inventory_item" } });
      const box = await screen.findByRole("combobox", { name: E("ret.hold.record") });                                                   // items: the picker works
      await waitFor(() => expect(within(box).getByRole("option", { name: /APPLE/ })).toBeInTheDocument());
      expect(screen.queryByText(E("ret.hold.record_unavailable"))).toBeNull();
      fireEvent.change(screen.getByLabelText(E("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "local_party" } });
      expect(await screen.findByText(E("ret.hold.record_unavailable"))).toBeInTheDocument();                                            // back to parties: still the fallback
      expect(listParties).toHaveBeenCalledTimes(1);                                                                                      // and the denied list is not called again
    });

    it("a 403 on items does not disable the party picker (denied kinds are remembered separately)", async () => {
      localStorage.setItem("hasm.locale", "en");
      const listParties = vi.fn().mockResolvedValue({ parties: [party], total: 1 });
      const listInventoryItems = vi.fn().mockRejectedValue(apiError(403, "forbidden"));
      render({ listParties, listInventoryItems }, caps(), [...perms, "company.parties.view", "company.items.view"]);
      await openHoldForm("2");
      const box = await screen.findByRole("combobox", { name: E("ret.hold.record") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /P1/ })).toBeInTheDocument());
      fireEvent.change(screen.getByLabelText(E("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "inventory_item" } });
      expect(await screen.findByText(E("ret.hold.record_unavailable"))).toBeInTheDocument();
      fireEvent.change(screen.getByLabelText(E("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "local_party" } });
      expect(await screen.findByRole("combobox", { name: E("ret.hold.record") })).toBeInTheDocument();                                  // parties picker is back
    });

    it("a user holding only the items view permission gets the item picker and the party fallback", async () => {
      localStorage.setItem("hasm.locale", "en");
      const listParties = vi.fn();
      const listInventoryItems = vi.fn().mockResolvedValue({ items: [item], total: 1 });
      render({ listParties, listInventoryItems }, caps(), [...perms, "company.items.view"]);
      await openHoldForm("2");
      expect(await screen.findByText(E("ret.hold.record_unavailable"))).toBeInTheDocument();                                            // default kind is party: no permission
      expect(screen.queryByRole("combobox", { name: E("ret.hold.record") })).toBeNull();
      fireEvent.change(screen.getByLabelText(E("ret.rule.kind"), { selector: "#ret-hkind" }), { target: { value: "inventory_item" } });
      expect(await screen.findByRole("combobox", { name: E("ret.hold.record") })).toBeInTheDocument();
      expect(listParties).not.toHaveBeenCalled();
    });
  });

  it("shows the read-only preview with ids and reasons, never file names", async () => {
    localStorage.setItem("hasm.locale", "en");
    render();
    await go();
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${E("ret.tab.preview")}`) }));
    expect(screen.getByText("a1")).toBeInTheDocument();
    expect(screen.getAllByText(E("ret.reason.retention_basis_pending")).length).toBeGreaterThan(0);
  });

  it("moves between tabs with the keyboard, mirrored in right-to-left", async () => {
    render();
    await go();
    const first = await screen.findByRole("tab", { name: new RegExp(`^${A("ret.tab.policy")}`) });
    first.focus();
    fireEvent.keyDown(first, { key: "ArrowLeft" });   // RTL: left is next
    await waitFor(() => expect(tabByName(A("ret.tab.holds"))).toHaveAttribute("aria-selected", "true"));
  });

  it("keeps entered draft values when switching tabs", async () => {
    localStorage.setItem("hasm.locale", "en");
    render();
    await go();
    await screen.findByTestId("policy-2");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${E("ret.note")}`)), { target: { value: "Keep me" } });
    await userEvent.click(screen.getByRole("tab", { name: new RegExp(`^${E("ret.tab.preview")}`) }));
    await userEvent.click(screen.getByRole("tab", { name: new RegExp(`^${E("ret.tab.policy")}`) }));
    expect(screen.getByLabelText(new RegExp(`^${E("ret.note")}`))).toHaveValue("Keep me");
  });
});

describe("retention catalog", () => {
  it("has the same keys in Arabic and English, none empty", () => {
    expect(Object.keys(retentionAr).sort()).toEqual(Object.keys(retentionEn).sort());
    for (const v of [...Object.values(retentionAr), ...Object.values(retentionEn)]) expect(v.trim()).not.toBe("");
  });
});
