import type { LocalParty, LocalPartySummary, PartyCapabilities, PartyProposal } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, fakeClient, renderApp, USER } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const PERMS = [...ALL_PERMISSIONS, "company.parties.view", "company.parties.create", "company.parties.update", "company.parties.submit", "company.parties.configure"];
const caps = (over: Partial<PartyCapabilities> = {}): PartyCapabilities => ({ view: true, create: true, update: true, submit: true, configure: true, ...over });
const stamp = "2026-11-17T10:00:00Z";
const summary: LocalPartySummary = { id: "p1", code: "NR-1", displayName: "النور", isActive: true, linkState: 2, legalNameAr: "شركة النور", legalNameEn: null, hasOpenRequest: false, revision: 3, updatedAtUtc: stamp };
const party = (over: Partial<LocalParty> = {}): LocalParty => ({
  id: "p1", code: "NR-1", displayName: "النور", notes: null, isActive: true, linkState: 2, globalPartyId: "g1", revision: 3, createdBy: "u1", createdAtUtc: stamp, updatedAtUtc: stamp,
  identity: { globalPartyId: "g1", kind: 1, legalNameAr: "شركة النور", legalNameEn: "Al Noor", taxNumber: "300-111", registrationNumber: null, status: 1, revision: 1 },
  requests: [{ id: "r1", kind: 1, status: 3, outcome: 1, partyKind: 1, legalNameAr: "شركة النور", legalNameEn: null, taxNumber: "300-111", registrationNumber: null, proposedStatus: 1, note: null, attempts: 1, lastAttemptAtUtc: stamp, lastErrorCode: null, createdBy: "u1", createdAtUtc: stamp, deliveredAtUtc: stamp, completedAtUtc: stamp }],
  ...over,
});

function setup(over: Record<string, unknown> = {}, c = caps(), user = USER) {
  const client = fakeClient({
    status: vi.fn().mockResolvedValue({ authenticated: true, user }),
    getPartyCapabilities: vi.fn().mockResolvedValue(c),
    listParties: vi.fn().mockResolvedValue({ parties: [summary], total: 1 }),
    getParty: vi.fn().mockResolvedValue(party()),
    createParty: vi.fn().mockResolvedValue(party({ id: "p9", linkState: 1, identity: null })),
    updateParty: vi.fn().mockResolvedValue(party()),
    proposeParty: vi.fn().mockResolvedValue(party()),
    syncParties: vi.fn().mockResolvedValue({ delivered: 1, applied: 2, failed: 0, waiting: 0 }),
    reconcileParties: vi.fn().mockResolvedValue({ repairRequested: false, findings: [{ code: "request_not_delivered", localPartyId: "p1", requestId: "r1", globalPartyId: null, repairable: true, repaired: false }] }),
    listPartyProposals: vi.fn().mockResolvedValue({ proposals: [], total: 0 }),
    ...over,
  }, PERMS);
  renderApp(client);
  return client;
}
const tab = (name: string) => screen.getByRole("tab", { name: new RegExp(`^${name}`) });

describe("parties", () => {
  it("lists the local parties with their link state and states that the shared identity carries no balance", async () => {
    setup();
    await go("#/company/parties");
    const row = await screen.findByTestId("pty-p1");
    expect(within(row).getByText("شركة النور")).toBeInTheDocument();
    expect(within(row).getByText("مرتبط")).toBeInTheDocument();
    expect(screen.getByTestId("pty-isolation")).toHaveTextContent("لا تتضمن أي رصيد");
  });

  it("creates a local party with its proposed shared identity, keeping input across tabs", async () => {
    const client = setup();
    await go("#/company/parties");
    await screen.findByTestId("pty-p1");
    await userEvent.click(screen.getByRole("button", { name: "طرف جديد" }));
    await userEvent.type(screen.getByLabelText(/^الرمز/), "nr-2");
    await userEvent.type(screen.getByLabelText(/^الاسم لدى الشركة/), "عميل");
    await userEvent.click(tab("الهوية المشتركة"));
    await userEvent.type(screen.getByLabelText(/^الاسم القانوني بالعربية/), "شركة جديدة");
    await userEvent.type(screen.getByLabelText(/^الرقم الضريبي/), "123");
    await userEvent.click(tab("البيانات المحلية"));
    expect(screen.getByLabelText(/^الرمز/)).toHaveValue("nr-2");                                            // switching tabs keeps the input
    await userEvent.click(screen.getByRole("button", { name: "إنشاء واقتراح" }));
    await waitFor(() => expect(client.createParty).toHaveBeenCalledWith({
      code: "nr-2", displayName: "عميل", notes: null, isActive: true,
      identity: { kind: 1, legalNameAr: "شركة جديدة", legalNameEn: null, taxNumber: "123", registrationNumber: null, status: null, note: null },
    }));
  });

  it("proposes a change of the shared data of a linked party with the party revision", async () => {
    const client = setup();
    await go("#/company/parties");
    await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
    await screen.findByTestId("pty-editor");
    await userEvent.click(tab("الهوية المشتركة"));
    const name = screen.getByLabelText(/^الاسم القانوني بالعربية/);
    await userEvent.clear(name);
    await userEvent.type(name, "شركة النور الحديثة");
    await userEvent.click(screen.getByTestId("pty-propose"));
    await waitFor(() => expect(client.proposeParty).toHaveBeenCalledWith("p1", expect.objectContaining({ legalNameAr: "شركة النور الحديثة", taxNumber: "300-111", status: 1 }), 3));
    await userEvent.click(tab("الطلبات"));
    expect(screen.getByTestId("pty-requests")).toHaveTextContent("معتمد");
  });

  it("carries the shared identity revision the user viewed when it proposes a change", async () => {
    const client = setup();
    await go("#/company/parties");
    await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
    await screen.findByTestId("pty-editor");
    await userEvent.click(tab("الهوية المشتركة"));
    await userEvent.type(screen.getByLabelText(/^مبرر الاقتراح/), "تحديث");
    await userEvent.click(screen.getByTestId("pty-propose"));
    await waitFor(() => expect(client.proposeParty).toHaveBeenCalledWith("p1", expect.objectContaining({ viewedSharedRevision: 1 }), 3));
  });

  it("never lets a local Save adopt a newer shared revision: the proposal still names the revision the user viewed and a notice appears", async () => {
    const newer = party({ identity: { globalPartyId: "g1", kind: 1, legalNameAr: "شركة النور", legalNameEn: "Al Noor", taxNumber: "300-111", registrationNumber: null, status: 1, revision: 2 } });
    const client = setup({ getParty: vi.fn().mockResolvedValueOnce(party()).mockResolvedValue(newer) });
    await go("#/company/parties");
    await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
    await screen.findByTestId("pty-editor");
    await userEvent.click(tab("الهوية المشتركة"));
    await userEvent.type(screen.getByLabelText(/^مبرر الاقتراح/), "تحديث");
    await userEvent.click(tab("البيانات المحلية"));
    await userEvent.type(screen.getByLabelText(/^ملاحظات/), "ملاحظة");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));                                     // reloads r2 behind the user's back
    expect(await screen.findByTestId("pty-identity-stale")).toBeInTheDocument();
    await userEvent.click(tab("الهوية المشتركة"));
    await userEvent.click(screen.getByTestId("pty-propose"));
    await waitFor(() => expect(client.proposeParty).toHaveBeenCalledWith("p1", expect.objectContaining({ viewedSharedRevision: 1 }), expect.any(Number)));
  });

  it("keeps unrelated edits and shows a per-tab unsaved marker: Save refreshes only the local data, Propose only the identity", async () => {
    const client = setup();
    await go("#/company/parties");
    await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
    await screen.findByTestId("pty-editor");
    const marker = (name: string) => tab(name).querySelector(".tab-dot");
    expect(marker("البيانات المحلية")).toBeNull(); expect(marker("الهوية المشتركة")).toBeNull();
    // an identity edit AND a local edit
    await userEvent.click(tab("الهوية المشتركة"));
    await userEvent.type(screen.getByLabelText(/^الاسم القانوني بالإنجليزية/), " Ltd");
    expect(marker("الهوية المشتركة")).not.toBeNull(); expect(marker("البيانات المحلية")).toBeNull();
    await userEvent.click(tab("البيانات المحلية"));
    await userEvent.type(screen.getByLabelText(/^ملاحظات/), "ملاحظة محلية");
    expect(marker("البيانات المحلية")).not.toBeNull();
    // Save the local data: the identity edit is still there and still marked
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.updateParty).toHaveBeenCalled());
    await waitFor(() => expect(marker("البيانات المحلية")).toBeNull());
    expect(marker("الهوية المشتركة")).not.toBeNull();
    await userEvent.click(tab("الهوية المشتركة"));
    expect(screen.getByLabelText(/^الاسم القانوني بالإنجليزية/)).toHaveValue("Al Noor Ltd");
    // a new local edit survives the proposal of the identity
    await userEvent.click(tab("البيانات المحلية"));
    await userEvent.type(screen.getByLabelText(/^ملاحظات/), "تعديل ثان");
    await userEvent.click(tab("الهوية المشتركة"));
    await userEvent.click(screen.getByTestId("pty-propose"));
    await waitFor(() => expect(client.proposeParty).toHaveBeenCalled());
    await waitFor(() => expect(marker("الهوية المشتركة")).toBeNull());
    expect(marker("البيانات المحلية")).not.toBeNull();
    await userEvent.click(tab("البيانات المحلية"));
    expect(screen.getByLabelText(/^ملاحظات/)).toHaveValue("تعديل ثان");
  });

  it("synchronizes the request log and shows the reconciliation findings", async () => {
    const client = setup();
    await go("#/company/parties");
    await screen.findByTestId("pty-p1");
    await userEvent.click(screen.getByTestId("pty-sync"));
    expect(await screen.findByTestId("saved")).toHaveTextContent("طُبّق 2");
    await userEvent.click(screen.getByTestId("pty-reconcile"));
    await waitFor(() => expect(client.reconcileParties).toHaveBeenCalledWith(false));
    expect(await screen.findByTestId("pty-findings")).toHaveTextContent("طلب لم يصل إلى السجل المركزي");
  });

  it("hides creation, synchronization and reconciliation from a view-only user", async () => {
    setup({}, caps({ create: false, update: false, submit: false, configure: false }));
    await go("#/company/parties");
    await screen.findByTestId("pty-p1");
    expect(screen.queryByRole("button", { name: "طرف جديد" })).toBeNull();
    expect(screen.queryByTestId("pty-sync")).toBeNull();
    expect(screen.queryByTestId("pty-reconcile")).toBeNull();
  });
});

const proposal = (over: Partial<PartyProposal> = {}): PartyProposal => ({
  id: "pp1", companyId: "c2", companyNameAr: "شركة ب", companyNameEn: "B Co", kind: 1, legalNameAr: "شركة النور", legalNameEn: null, taxNumber: "300-111", registrationNumber: null, proposedAtUtc: stamp, status: 1, revision: 1,
  localPartyId: "l2", targetGlobalPartyId: null, partyKind: 1, proposedStatus: 1, note: null, proposedBy: "someone", proposedByName: "Someone", resolution: null, outcomeGlobalPartyId: null,
  decidedBy: null, decidedAtUtc: null, decisionNote: null, target: null,
  matches: [{ globalPartyId: "g1", kind: 1, legalNameAr: "شركة النور القائمة", legalNameEn: null, taxNumber: "300-111", registrationNumber: null, status: 1, revision: 1 }],
  ...over,
});

describe("party approvals", () => {
  const ADMIN = { ...USER, isIdentityAdministrator: true };
  const queue = { proposals: [proposal()], total: 1 };

  it("is reserved to identity administrators (screen and menu)", async () => {
    setup();
    await go("#/admin/party-proposals");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });

  it("pages the central queue instead of showing only the first page", async () => {
    const many = { proposals: [proposal()], total: 60 };
    const listPartyProposals = vi.fn().mockResolvedValue(many);
    setup({ listPartyProposals }, caps(), ADMIN);
    await go("#/admin/party-proposals");
    await screen.findByTestId("ppa-pp1");
    expect(listPartyProposals).toHaveBeenLastCalledWith(1, 0, 25);
    expect(screen.getByTestId("ppa-pager")).toHaveTextContent("1–1 من 60");
    expect(within(screen.getByTestId("ppa-pager")).getByRole("button", { name: "السابق" })).toBeDisabled();
    await userEvent.click(within(screen.getByTestId("ppa-pager")).getByRole("button", { name: "التالي" }));
    await waitFor(() => expect(listPartyProposals).toHaveBeenLastCalledWith(1, 25, 25));
    // changing the status filter returns to the first page
    await userEvent.selectOptions(screen.getByLabelText(/^الحالة/), "");
    await waitFor(() => expect(listPartyProposals).toHaveBeenLastCalledWith(undefined, 0, 25));
  });

  it("steps back when the last row of a later page was decided and the page emptied", async () => {
    let emptied = false;                                                                                      // the queue shrinks to 25 once the later page was served empty
    const listPartyProposals = vi.fn().mockImplementation(async (_status: unknown, offset: number) => {
      if (offset === 25) { emptied = true; return { proposals: [], total: 25 }; }
      return { proposals: [proposal()], total: emptied ? 25 : 60 };
    });
    setup({ listPartyProposals }, caps(), ADMIN);
    await go("#/admin/party-proposals");
    await screen.findByTestId("ppa-pp1");
    await userEvent.click(within(screen.getByTestId("ppa-pager")).getByRole("button", { name: "التالي" }));
    await waitFor(() => expect(listPartyProposals).toHaveBeenCalledWith(1, 25, 25));
    await waitFor(() => expect(listPartyProposals).toHaveBeenLastCalledWith(1, 0, 25));                    // clamped back to the last existing page
    expect(await screen.findByTestId("ppa-pp1")).toBeInTheDocument();
    expect(screen.getByTestId("ppa-pager")).toHaveTextContent("1–1 من 25");
  });

  it("links a proposal to an existing identity or approves it as new", async () => {
    const client = setup({ listPartyProposals: vi.fn().mockResolvedValue(queue), getPartyProposal: vi.fn().mockResolvedValue(proposal()), approvePartyProposal: vi.fn().mockResolvedValue(proposal({ status: 2, resolution: 2 })) }, caps(), ADMIN);
    await go("#/admin/party-proposals");
    await userEvent.click(within(await screen.findByTestId("ppa-pp1")).getByRole("button", { name: "فتح" }));
    await screen.findByTestId("ppa-proposal");
    await userEvent.click(within(screen.getByTestId("ppa-matches")).getByRole("button", { name: "ربط بهذه الهوية" }));
    await waitFor(() => expect(client.approvePartyProposal).toHaveBeenCalledWith("pp1", "g1", 1));
    expect(await screen.findByTestId("ppa-status")).toHaveTextContent("معتمد");
  });

  it("blocks a decision on one's own proposal and rejects with a note", async () => {
    const mine = proposal({ proposedBy: USER.id });
    setup({ listPartyProposals: vi.fn().mockResolvedValue({ proposals: [mine], total: 1 }), getPartyProposal: vi.fn().mockResolvedValue(mine) }, caps(), ADMIN);
    await go("#/admin/party-proposals");
    await userEvent.click(within(await screen.findByTestId("ppa-pp1")).getByRole("button", { name: "فتح" }));
    expect(await screen.findByTestId("ppa-self")).toBeInTheDocument();
    expect(screen.getByTestId("ppa-approve")).toBeDisabled();
    expect(screen.getByTestId("ppa-reject")).toBeDisabled();
  });

  it("rejects a proposal of someone else with the mandatory reason", async () => {
    const client = setup({ listPartyProposals: vi.fn().mockResolvedValue(queue), getPartyProposal: vi.fn().mockResolvedValue(proposal()), rejectPartyProposal: vi.fn().mockResolvedValue(proposal({ status: 3, decisionNote: "dup" })) }, caps(), ADMIN);
    await go("#/admin/party-proposals");
    await userEvent.click(within(await screen.findByTestId("ppa-pp1")).getByRole("button", { name: "فتح" }));
    await userEvent.click(await screen.findByTestId("ppa-reject"));
    await userEvent.type(screen.getByLabelText(/^سبب الرفض/), "dup");
    await userEvent.click(screen.getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(client.rejectPartyProposal).toHaveBeenCalledWith("pp1", "dup", 1));
  });
});

describe("party registry review follow-ups (PR #46)", () => {
  const ADMIN = { ...USER, isIdentityAdministrator: true };

  it("shows the list without shared names and a banner when the central registry is unavailable", async () => {
    setup({ listParties: vi.fn().mockResolvedValue({ parties: [{ ...summary, legalNameAr: null }], total: 1, registryUnavailable: true }) });
    await go("#/company/parties");
    await screen.findByTestId("pty-p1");
    expect(screen.getByTestId("pty-registry-down")).toHaveTextContent("السجل المركزي غير متاح");
  });

  it("shows the company the central reason of its rejected proposal", async () => {
    const rejected = party({ linkState: 3, globalPartyId: null, identity: null, requests: [{ ...party().requests[0]!, outcome: 2, decisionNote: "مكرر" }] });
    setup({ getParty: vi.fn().mockResolvedValue(rejected) });
    await go("#/company/parties");
    await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
    await screen.findByTestId("pty-editor");
    await userEvent.click(tab("الطلبات"));
    expect(screen.getByTestId("pty-decision-note")).toHaveTextContent("مكرر");
  });

  it("refuses one-character registry searches and explains a change based on an outdated identity", async () => {
    const stale = proposal({ kind: 2, targetGlobalPartyId: "g1", basedOnRevision: 1, matches: [], target: { globalPartyId: "g1", kind: 1, legalNameAr: "حالية", legalNameEn: null, taxNumber: null, registrationNumber: null, status: 1, revision: 2 } });
    const { ApiError } = await import("@hasm/api-client");
    setup({ listPartyProposals: vi.fn().mockResolvedValue({ proposals: [stale], total: 1 }), getPartyProposal: vi.fn().mockResolvedValue(stale), approvePartyProposal: vi.fn().mockRejectedValue(new ApiError(409, "identity_changed", {})) }, caps(), ADMIN);
    await go("#/admin/party-proposals");
    await userEvent.click(within(await screen.findByTestId("ppa-pp1")).getByRole("button", { name: "فتح" }));
    expect(await screen.findByTestId("ppa-stale")).toBeInTheDocument();
    await userEvent.click(screen.getByTestId("ppa-approve"));
    expect(await screen.findByTestId("form-error")).toHaveTextContent("تغيّرت الهوية المشتركة");
  });

  it("disables the registry search below two characters", async () => {
    setup({ listPartyProposals: vi.fn().mockResolvedValue({ proposals: [proposal()], total: 1 }), getPartyProposal: vi.fn().mockResolvedValue(proposal()) }, caps(), ADMIN);
    await go("#/admin/party-proposals");
    await userEvent.click(within(await screen.findByTestId("ppa-pp1")).getByRole("button", { name: "فتح" }));
    await userEvent.type(await screen.findByLabelText(/^بحث في السجل/), "ا");
    expect(screen.getByRole("button", { name: "بحث في السجل" })).toBeDisabled();
  });
});
