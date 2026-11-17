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
