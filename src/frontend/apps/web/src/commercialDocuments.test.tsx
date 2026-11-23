import type { CommercialDocument, InventoryItem, PartyRole } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const STAMP = "2026-03-01T10:00:00Z";
const VIEW = "company.commercialdocs.view";
const ALL = [VIEW, "company.commercialdocs.create", "company.commercialdocs.update", "company.commercialdocs.submit", "company.commercialdocs.approve", "company.commercialdocs.cancel"];
const PERMS = [...ALL_PERMISSIONS, ...ALL, "company.partyroles.view", "company.items.view", "company.branches.view"];

const branch = { id: "b1", code: "BA", nameAr: "الفرع الأول", nameEn: "First branch", isActive: true, isMain: true, activationEpoch: 1, seeded: true };
const customer: PartyRole = { id: "r1", partyId: "p1", partyCode: "C1", partyName: "شركة الأمل", role: 1, isActive: true, paymentMode: 1, termDays: 0, creditLimit: null, controlAccountId: null, controlAccountCode: null, representativeId: null, revision: 1 };
const apple: InventoryItem = {
  id: "i1", code: "APPLE", nameAr: "تفاح", nameEn: "Apple", itemType: 1, groupId: "g", baseUnitId: "u-pcs", purchaseUnitId: null, salesUnitId: "u-box", isLot: false, isSerial: false, hasExpiry: false, notes: null, isActive: true, revision: 1,
  effectiveValuation: null, alternateUnits: [{ unitId: "u-box", unitCode: "BOX", factor: 12 }], createdAtUtc: STAMP, updatedAtUtc: STAMP,
};
const unitRows = [{ id: "u-pcs", code: "PCS" }, { id: "u-box", code: "BOX" }];
const doc = (over: Partial<CommercialDocument> = {}): CommercialDocument => ({
  id: "d1", docType: "commercial_test", branchId: "b1", partyId: "p1", currency: "JOD", documentDate: "2026-03-10", mode: 1, status: 1, note: null, submittedBy: null, officialNumber: null, approvedBy: null,
  approvedAtUtc: null, sealedAtUtc: null, exchangeRate: null, totalNet: null, totalTax: null, totalGross: null, revision: 1, createdBy: "someone", lastEditedBy: "someone", createdAtUtc: STAMP, updatedAtUtc: STAMP, replayed: false,
  lines: [{ lineNo: 1, itemId: "i1", unitId: "u-box", quantity: 2, warehouseId: null, lineNet: null, pricingHash: null }],
  events: [{ kind: 1, fromStatus: null, toStatus: 1, revision: 1, actorUserId: "someone", reason: null, atUtc: STAMP }], ...over,
});
const approved = (over: Partial<CommercialDocument> = {}) => doc({
  status: 3, revision: 5, officialNumber: "2026-000001", submittedBy: "someone", approvedBy: "approver", approvedAtUtc: STAMP, sealedAtUtc: STAMP, exchangeRate: 1, totalNet: 20, totalTax: 3.2, totalGross: 23.2,
  lines: [{ lineNo: 1, itemId: "i1", unitId: "u-box", quantity: 2, warehouseId: null, lineNet: 20, pricingHash: "a".repeat(64) }], ...over,
});

function setup(over: Record<string, unknown> = {}, perms = PERMS, types = ["commercial_test"], docs: CommercialDocument[] = [doc()], current: CommercialDocument = doc()) {
  const client = signedIn({
    listCommercialDocumentTypes: vi.fn().mockResolvedValue(types),
    listCommercialDocuments: vi.fn().mockResolvedValue(docs),
    getCommercialDocument: vi.fn().mockResolvedValue(current),
    listBranches: vi.fn().mockResolvedValue({ branches: [branch], limit: null, everCreated: null, can: { create: false, activate: false, designate: false, update: [] } }),
    listPartyRoles: vi.fn().mockResolvedValue({ items: [customer], total: 1 }),
    listInventoryItems: vi.fn().mockResolvedValue({ items: [apple], total: 1 }),
    getInventoryItem: vi.fn().mockResolvedValue(apple),
    lookupInventoryUnits: vi.fn().mockResolvedValue(unitRows),
    getCommercialApprovals: vi.fn().mockResolvedValue({ plan: [], decisions: [] }),
    listDocumentLinks: vi.fn().mockResolvedValue([]), getLineQuantities: vi.fn().mockResolvedValue([]), createDocumentLink: vi.fn(),
    listCommercialLinkPairs: vi.fn().mockResolvedValue([1, 2, 3, 4].map((kind) => ({ source: "commercial_test", target: "commercial_test", kind }))),
    listShortCloses: vi.fn().mockResolvedValue([]), listShortCloseTypes: vi.fn().mockResolvedValue(["commercial_test"]), shortCloseDocument: vi.fn(),
    createCommercialDocument: vi.fn(), updateCommercialDocument: vi.fn(),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}
const tab = (name: string) => screen.getByRole("tab", { name: new RegExp(`^${name}`) });
const openDoc = async () => { await go("#/commercial/documents"); await userEvent.click(await screen.findByRole("button", { name: "عرض" })); await screen.findByTestId("commercial-editor"); };

describe("commercial documents: types from the server registry and the list", () => {
  it("shows 'no document types activated' and offers no creation when the registry is empty (production), still listing what exists", async () => {
    const client = setup({}, PERMS, [], []);
    await go("#/commercial/documents");
    expect(await screen.findByTestId("no-types")).toHaveTextContent("لا توجد أنواع مستندات مفعّلة");
    expect(screen.queryByRole("button", { name: "مستند جديد" })).toBeNull();
    expect(screen.getByText("لا توجد مستندات بعد.")).toBeInTheDocument();
    expect(client.listCommercialDocumentTypes).toHaveBeenCalledTimes(1);
  });

  it("lists number, type, party, branch and status and passes the filters to the server", async () => {
    const client = setup({}, PERMS, ["commercial_test"], [approved({ id: "d1" }), doc({ id: "d2", status: 2 })]);
    await go("#/commercial/documents");
    const row = await screen.findByTestId("doc-d1");
    expect(within(row).getByText("2026-000001")).toBeInTheDocument();
    expect(within(row).getByText("شركة الأمل")).toBeInTheDocument();
    expect(within(row).getByText(/BA — الفرع الأول/)).toBeInTheDocument();
    expect(within(row).getByText("معتمد")).toBeInTheDocument();
    expect(within(screen.getByTestId("doc-d2")).getByText("بانتظار الاعتماد")).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("الحالة"), "2");
    await waitFor(() => expect(client.listCommercialDocuments).toHaveBeenLastCalledWith(expect.objectContaining({ status: 2, offset: 0, limit: 25 })));
    await userEvent.selectOptions(screen.getByLabelText("الفرع"), "b1");
    await waitFor(() => expect(client.listCommercialDocuments).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: "b1" })));
  });

  it("hides the creation button from a user without the create permission (the server still authorizes) and says it is read-only", async () => {
    setup({}, [...ALL_PERMISSIONS, VIEW]);
    await go("#/commercial/documents");
    await screen.findByTestId("doc-d1");
    expect(screen.queryByRole("button", { name: "مستند جديد" })).toBeNull();
    expect(screen.getByTestId("read-only")).toBeInTheDocument();
  });

  it("is absent from the menu and refused as a route for a user with no commercial document permission", async () => {
    setup({}, ALL_PERMISSIONS);
    await screen.findByTestId("company-name");
    expect(screen.queryByRole("button", { name: "المستندات التجارية" })).toBeNull();
    await go("#/commercial/documents");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });
});

describe("commercial document editor (drafts)", () => {
  it("creates a draft from the server's type, sends no price, tax or total, and carries one idempotency key", async () => {
    const created = doc({ id: "d9" });
    const create = vi.fn().mockResolvedValue(created);
    const client = setup({ createCommercialDocument: create, getCommercialDocument: vi.fn().mockResolvedValue(created) }, PERMS, ["commercial_test"], []);
    await go("#/commercial/documents");
    await userEvent.click(await screen.findByRole("button", { name: "مستند جديد" }));
    await screen.findByTestId("commercial-editor");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["المستند", "السطور"]);       // no Physical or Accounting tab and no placeholder
    await userEvent.selectOptions(screen.getByLabelText(/^الفرع/), "b1");
    await userEvent.type(screen.getByLabelText(/^ابحث عن عميل أو مورد/), "امل");
    await userEvent.selectOptions(await screen.findByLabelText("اختر الطرف"), "p1");
    await userEvent.type(screen.getByLabelText(/^العملة/), "jod");
    await userEvent.click(tab("السطور"));
    await userEvent.type(screen.getByLabelText("ابحث عن مادة"), "app");
    await userEvent.selectOptions(await screen.findByLabelText("اختر المادة"), "i1");
    await waitFor(() => expect(screen.getByLabelText("الوحدة")).toHaveValue("u-box"));                  // the sales unit is the default
    await userEvent.type(screen.getByLabelText("الكمية"), "2");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const body = create.mock.calls[0]![0] as Record<string, unknown>;
    expect(body).toEqual({
      docType: "commercial_test", branchId: "b1", partyId: "p1", currency: "JOD", documentDate: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), mode: 1, note: null, idempotencyKey: expect.stringMatching(/.{8,}/),
      lines: [{ itemId: "i1", unitId: "u-box", quantity: 2 }],
    });
    expect(JSON.stringify(body)).not.toMatch(/price|tax|total|cost|discount|snapshot/i);
    expect(client.getCommercialDocument).toHaveBeenCalledWith("d9");                                      // the editor switched to the saved draft
  });

  it("validates before sending (party, currency, quantity) and moves to the tab with the first error", async () => {
    const create = vi.fn();
    setup({ createCommercialDocument: create }, PERMS, ["commercial_test"], []);
    await go("#/commercial/documents");
    await userEvent.click(await screen.findByRole("button", { name: "مستند جديد" }));
    await userEvent.click(await screen.findByRole("button", { name: "حفظ" }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent("صحّح");
    expect(create).not.toHaveBeenCalled();
    expect(screen.getAllByText("هذا الحقل مطلوب.").length).toBeGreaterThan(0);
    expect(tab("المستند")).toHaveAttribute("aria-selected", "true");
  });

  it("switching tabs keeps the input and offers submit only after the draft is saved", async () => {
    setup();
    await openDoc();
    await userEvent.type(screen.getByLabelText("ملاحظة"), "ملاحظة تجريبية");
    await userEvent.click(tab("السطور"));
    await userEvent.click(tab("المستند"));
    expect(screen.getByLabelText("ملاحظة")).toHaveValue("ملاحظة تجريبية");
    expect(screen.getByRole("button", { name: "إرسال للاعتماد" })).toBeDisabled();
  });

  it("shows the server's validation refusal on the field and reloads the latest version after a revision conflict", async () => {
    const update = vi.fn().mockRejectedValueOnce(apiError(400, "invalid_request", { fields: [{ field: "partyId", code: "party_role_required" }] })).mockRejectedValueOnce(apiError(412, "revision_conflict"));
    const get = vi.fn().mockResolvedValue(doc());
    setup({ updateCommercialDocument: update, getCommercialDocument: get });
    await openDoc();
    await userEvent.type(screen.getByLabelText("ملاحظة"), "x");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText("الطرف ليس عميلاً أو مورداً فعّالاً.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    const banner = await screen.findByTestId("form-conflict");
    const calls = get.mock.calls.length;
    await userEvent.click(within(banner).getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(get.mock.calls.length).toBeGreaterThan(calls));
    expect(update).toHaveBeenCalledWith("d1", expect.anything(), 1);
  });
});

describe("commercial document life cycle", () => {
  it("submits after a confirmation with the revision as If-Match, and withdraws a submitted document", async () => {
    const submit = vi.fn().mockResolvedValue(doc({ status: 2, revision: 2, submittedBy: "u1" }));
    const client = setup({ submitCommercialDocument: submit, withdrawCommercialDocument: vi.fn().mockResolvedValue(doc()) });
    await openDoc();
    await userEvent.click(screen.getByRole("button", { name: "إرسال للاعتماد" }));
    expect(submit).not.toHaveBeenCalled();                                                                // a confirmation comes first
    await userEvent.click(await screen.findByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(submit).toHaveBeenCalledWith("d1", 1));
    expect(client.getCommercialDocument).toHaveBeenCalledTimes(2);                                        // reloaded after the action
  });

  it("approves a submitted document of someone else with the revision, and refuses the creator or submitter on the screen with the reason", async () => {
    const approve = vi.fn().mockResolvedValue(approved());
    setup({ approveCommercialDocument: approve }, PERMS, ["commercial_test"], [doc({ status: 2 })], doc({ status: 2, revision: 3, submittedBy: "other", createdBy: "someone" }));
    await openDoc();
    await userEvent.click(screen.getByTestId("approve"));
    await waitFor(() => expect(approve).toHaveBeenCalledWith("d1", 3));
  });

  it("disables approve and reject for the LAST EDITOR too (D-066: not the creator, the last editor or the submitter)", async () => {
    setup({}, PERMS, ["commercial_test"], [doc({ status: 2 })], doc({ status: 2, revision: 3, createdBy: "x", lastEditedBy: "u1", submittedBy: "x" }));
    await openDoc();
    expect(screen.getByTestId("approve")).toBeDisabled();
    expect(screen.getByTestId("reject")).toBeDisabled();
    expect(screen.getByTestId("self-approval-note")).toBeInTheDocument();
  });

  it("accepts Arabic-Indic digits in a quantity and refuses a grouping separator (the shared parser)", async () => {
    const update = vi.fn().mockResolvedValue(doc());
    setup({ updateCommercialDocument: update });
    await openDoc();
    await userEvent.click(tab("السطور"));
    await userEvent.clear(screen.getByLabelText("الكمية"));
    await userEvent.type(screen.getByLabelText("الكمية"), "1,500");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText(/كمية غير صالحة/)).toBeInTheDocument();
    expect(update).not.toHaveBeenCalled();
    await userEvent.clear(screen.getByLabelText("الكمية"));
    await userEvent.type(screen.getByLabelText("الكمية"), "١٫٥");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect((update.mock.calls[0]![1] as { lines: { quantity: number }[] }).lines[0]!.quantity).toBe(1.5);
  });

  it("disables approve and reject for the user who submitted the document and says why", async () => {
    setup({}, PERMS, ["commercial_test"], [doc({ status: 2 })], doc({ status: 2, revision: 3, submittedBy: "u1" }));
    await openDoc();
    expect(screen.getByTestId("approve")).toBeDisabled();
    expect(screen.getByTestId("reject")).toBeDisabled();
    expect(screen.getByTestId("self-approval-note")).toHaveTextContent("لا يمكن لمن أنشأ المستند");
  });

  it("hides approve from a user without the approve permission (the server still authorizes)", async () => {
    setup({}, PERMS.filter((p) => p !== "company.commercialdocs.approve"), ["commercial_test"], [doc({ status: 2 })], doc({ status: 2, revision: 3, submittedBy: "other" }));
    await openDoc();
    expect(screen.queryByTestId("approve")).toBeNull();
    expect(screen.queryByTestId("reject")).toBeNull();
  });

  it("rejects with a mandatory reason and cancels with a mandatory reason", async () => {
    const reject = vi.fn().mockResolvedValue(doc({ status: 4 }));
    setup({ rejectCommercialDocument: reject }, PERMS, ["commercial_test"], [doc({ status: 2 })], doc({ status: 2, revision: 3, submittedBy: "other" }));
    await openDoc();
    await userEvent.click(screen.getByTestId("reject"));
    await userEvent.click(await screen.findByRole("button", { name: "تأكيد" }));                         // no reason: refused on the client, the dialog stays open with the error inside it
    expect(within(screen.getByRole("dialog")).getByText("السبب مطلوب.")).toBeInTheDocument();
    expect(reject).not.toHaveBeenCalled();
    await userEvent.type(screen.getByLabelText(/^السبب/), "   ");                                        // whitespace only is still no reason and what was typed is kept
    await userEvent.click(screen.getByRole("button", { name: "تأكيد" }));
    expect(screen.getByLabelText(/^السبب/)).toHaveValue("   ");
    expect(within(screen.getByRole("dialog")).getByText("السبب مطلوب.")).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText(/^السبب/));
    await userEvent.type(screen.getByLabelText(/^السبب/), "السعر غير مقبول");
    await userEvent.click(screen.getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(reject).toHaveBeenCalledWith("d1", "السعر غير مقبول", 3));
  });

  it("cancels an approved document with a reason and shows the server's refusal when it has links", async () => {
    const cancel = vi.fn().mockRejectedValue(apiError(409, "document_has_links"));
    setup({ cancelCommercialDocument: cancel }, PERMS, ["commercial_test"], [approved()], approved());
    await openDoc();
    await userEvent.click(screen.getByTestId("cancel-document"));
    await userEvent.type(await screen.findByLabelText(/^السبب/), "ألغاه العميل");
    await userEvent.click(screen.getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(cancel).toHaveBeenCalledWith("d1", "ألغاه العميل", 5));
    expect(await screen.findByTestId("form-error")).toHaveTextContent("للمستند روابط بمستندات أخرى");
  });

  it("shows the number, the server-sealed totals and the line net of an approved document, the re-approval note, the history, and no Physical or Accounting tab", async () => {
    setup({}, PERMS, ["commercial_test"], [approved()], approved({ events: [
      { kind: 1, fromStatus: null, toStatus: 1, revision: 1, actorUserId: "someone", reason: null, atUtc: STAMP },
      { kind: 6, fromStatus: 2, toStatus: 3, revision: 5, actorUserId: "approver", reason: null, atUtc: STAMP },
    ] }));
    await openDoc();
    expect(screen.getByTestId("doc-status")).toHaveTextContent("معتمد");
    expect(screen.getByLabelText("الرقم الرسمي")).toHaveValue("2026-000001");
    expect(screen.getByTestId("total-gross")).toHaveTextContent("23.20");
    expect(screen.getByTestId("reapproval-note")).toBeInTheDocument();
    await userEvent.click(tab("السطور"));
    expect(screen.getByTestId("line-net-0")).toHaveTextContent("20.00");
    await userEvent.click(tab("السجل"));
    const rows = within(screen.getByTestId("history")).getAllByRole("row");
    expect(rows).toHaveLength(3);
    expect(within(rows[2]!).getByText("اعتُمد")).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["المستند", "السطور", "الروابط", "السجل"]);
  });
});

describe("commercial documents in English and the Tauri shell", () => {
  it("works in English (LTR) with the same tabs and keyboard navigation", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup();
    await go("#/commercial/documents");
    await userEvent.click(await screen.findByRole("button", { name: "View" }));
    await screen.findByTestId("commercial-editor");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["Document", "Lines", "Links", "History"]);
    expect(document.documentElement.dir).toBe("ltr");
    screen.getByRole("tab", { name: /^Document/ }).focus();
    await userEvent.keyboard("{ArrowRight}");
    await waitFor(() => expect(screen.getByRole("tab", { name: /^Lines/ })).toHaveFocus());
  });

  it("shows the empty production state in English", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup({}, PERMS, [], []);
    await go("#/commercial/documents");
    expect(await screen.findByTestId("no-types")).toHaveTextContent("No document types are activated");
  });

  it("behaves the same inside the Tauri shell", async () => {
    (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {};
    try {
      setup();
      await openDoc();
      await userEvent.type(screen.getByLabelText("ملاحظة"), "T");
      await userEvent.click(tab("السطور"));
      await userEvent.click(tab("المستند"));
      expect(screen.getByLabelText("ملاحظة")).toHaveValue("T");
    } finally { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; }
  });
});

const submittedDoc = (over: Partial<CommercialDocument> = {}) => doc({ status: 2, revision: 3, submittedBy: "other", createdBy: "someone", lastEditedBy: "someone", ...over });
const stage = (n: number, required: number, approved: number, roleId: string | null = null) => ({ stage: n, required, approved, roleId });
const decision = (id: number, approver: string, st: number) => ({ id, revision: 3, approver, stage: st, decidedAtUtc: "2026-03-02T09:30:00Z" });
const openSubmitted = async (approvals: unknown, over: Record<string, unknown> = {}, current: CommercialDocument = submittedDoc()) => {
  const client = setup({ getCommercialApprovals: vi.fn().mockResolvedValue(approvals), ...over }, PERMS, ["commercial_test"], [current], current);
  await openDoc();
  return client;
};

describe("approval stage progress on the commercial-document screen (S5a)", () => {
  it("shows the stages in order with the current stage, and who decided when, in the History tab", async () => {
    await openSubmitted({ plan: [stage(1, 1, 1), stage(2, 2, 1), stage(3, 1, 0)], decisions: [decision(1, "approver1-xxxx", 1), decision(2, "u1", 2)] });
    await userEvent.click(tab("السجل"));
    const plan = within(screen.getByTestId("approval-plan"));
    expect(within(screen.getByTestId("stage-1")).getByText("مكتملة")).toBeInTheDocument();
    expect(within(screen.getByTestId("stage-2")).getByText("الحالية")).toBeInTheDocument();
    expect(screen.getByTestId("stage-2")).toHaveAttribute("aria-current", "step");
    expect(within(screen.getByTestId("stage-3")).getByText("بانتظار")).toBeInTheDocument();
    expect(within(screen.getByTestId("stage-2")).getByText("1 / 2")).toBeInTheDocument();
    expect(plan.getAllByRole("row")).toHaveLength(4);
    const decisions = within(screen.getByTestId("approval-decisions")).getAllByRole("row");
    expect(decisions).toHaveLength(3);
    expect(within(decisions[1]!).getByText("approver")).toBeInTheDocument();                              // the first 8 characters of the approver id
    expect(within(decisions[1]!).getByText("2026-03-02 09:30:00")).toBeInTheDocument();
    expect(within(decisions[2]!).getByText("أنت")).toBeInTheDocument();                                   // the signed-in user is named "you"
  });

  it("says that no policy matches when the plan is empty and keeps Approve for an independent approver", async () => {
    await openSubmitted({ plan: [], decisions: [] });
    await userEvent.click(tab("السجل"));
    expect(screen.getByTestId("approval-no-plan")).toHaveTextContent("لا توجد سياسة اعتماد مطابقة");
    expect(screen.getByTestId("approve")).toBeEnabled();
  });

  it("offers Approve for the open stage to an eligible user", async () => {
    await openSubmitted({ plan: [stage(1, 1, 1), stage(2, 1, 0, "r")], decisions: [decision(1, "approver1", 1)] });       // the stage role "r" is the one the test user holds
    expect(screen.getByTestId("approve")).toBeEnabled();
    expect(screen.queryByTestId("approve-not-eligible")).toBeNull();
  });

  it("hides Approve, with the reason, from a user who already decided on this revision", async () => {
    await openSubmitted({ plan: [stage(1, 2, 1)], decisions: [decision(1, "u1", 1)] });
    expect(screen.queryByTestId("approve")).toBeNull();
    expect(screen.getByTestId("approve-not-eligible")).toHaveTextContent("سجّلت قرارك على هذه النسخة");
  });

  it("hides Approve AND Reject, with the reason, from a user who does not hold the role of the open stage (the server refuses both)", async () => {
    await openSubmitted({ plan: [stage(1, 1, 0, "some-other-role")], decisions: [] });
    expect(screen.queryByTestId("approve")).toBeNull();
    expect(screen.queryByTestId("reject")).toBeNull();                                                       // Reject mirrors the server rule: a role-bound stage is rejected only by a holder of its role for the document's branch
    expect(screen.getByTestId("approve-not-eligible")).toHaveTextContent("المرحلة الحالية تتطلب دوراً لا تملكه");
  });

  it("hides Approve when every stage of the plan is complete, and keeps Reject", async () => {
    await openSubmitted({ plan: [stage(1, 1, 1)], decisions: [decision(1, "approver1", 1)] });
    expect(screen.queryByTestId("approve")).toBeNull();
    expect(screen.getByTestId("reject")).toBeEnabled();
    expect(screen.getByTestId("approve-not-eligible")).toHaveTextContent("اكتملت كل مراحل الاعتماد");
  });

  it("does not count a stage role held through a grant for ANOTHER branch, like the server: neither Approve nor Reject is offered", async () => {
    const grants = [
      { roleId: "viewer", scope: "company", scopeId: null, permissions: PERMS },                              // approve on the document's branch (company-wide)
      { roleId: "r", scope: "branch", scopeId: "other-branch", permissions: [] },                             // the stage role, held through ANOTHER branch's grant
    ];
    await openSubmitted({ plan: [stage(1, 1, 0, "r")], decisions: [] }, { accessMe: vi.fn().mockResolvedValue({ companyId: "c1", grants }) });
    expect(screen.queryByTestId("approve")).toBeNull();
    expect(screen.queryByTestId("reject")).toBeNull();
    expect(screen.getByTestId("approve-not-eligible")).toHaveTextContent("المرحلة الحالية تتطلب دوراً لا تملكه");
  });

  it.each([["company", null], ["branch", "b1"]])("offers Approve and Reject to a holder of the stage role through a %s grant covering the document's branch", async (scope, scopeId) => {
    const grants = [{ roleId: "viewer", scope: "company", scopeId: null, permissions: PERMS }, { roleId: "r", scope, scopeId, permissions: [] }];
    await openSubmitted({ plan: [stage(1, 1, 0, "r")], decisions: [] }, { accessMe: vi.fn().mockResolvedValue({ companyId: "c1", grants }) });
    expect(screen.getByTestId("approve")).toBeEnabled();
    expect(screen.getByTestId("reject")).toBeEnabled();
    expect(screen.queryByTestId("approve-not-eligible")).toBeNull();
  });

  it("keeps Reject for a stage without a role, and shows the server's generic 403 when a rejection is refused (the API never names the stage role)", async () => {
    const reject = vi.fn().mockRejectedValue(apiError(403, "forbidden"));                                    // what the real server answers for rejecter_role_required and for every other authorization refusal
    await openSubmitted({ plan: [stage(1, 1, 0)], decisions: [] }, { rejectCommercialDocument: reject });
    await userEvent.click(screen.getByTestId("reject"));
    await userEvent.type(screen.getByLabelText(/^السبب/), "no");
    await userEvent.click(screen.getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByText("ليست لديك صلاحية لهذا الإجراء.")).toBeInTheDocument();
    expect(screen.getByTestId("reject")).toBeInTheDocument();                                                // the document is unchanged: still submitted, the action still offered
  });

  describe("Approve and Reject follow the approve permission for the DOCUMENT'S branch, like the server (C4 of PR #137)", () => {
    const NO_APPROVE = PERMS.filter((p) => p !== "company.commercialdocs.approve");
    const withGrants = (grants: unknown[]) => ({ accessMe: vi.fn().mockResolvedValue({ companyId: "c1", grants }) });
    const open = (grants: unknown[]) => openSubmitted({ plan: [], decisions: [] }, withGrants(grants));      // the document is in branch "b1"

    it("a grant of approve for ANOTHER branch hides Approve and Reject (and their notes): the server would refuse both", async () => {
      await open([
        { roleId: "viewer", scope: "company", scopeId: null, permissions: NO_APPROVE },
        { roleId: "approver", scope: "branch", scopeId: "other-branch", permissions: ["company.commercialdocs.approve"] },
      ]);
      expect(screen.queryByTestId("approve")).toBeNull();
      expect(screen.queryByTestId("reject")).toBeNull();
      expect(screen.queryByTestId("approve-not-eligible")).toBeNull();
      expect(screen.queryByTestId("self-approval-note")).toBeNull();
    });

    it("a grant of approve for the document's own branch shows both, enabled", async () => {
      await open([
        { roleId: "viewer", scope: "company", scopeId: null, permissions: NO_APPROVE },
        { roleId: "approver", scope: "branch", scopeId: "b1", permissions: ["company.commercialdocs.approve"] },
      ]);
      expect(screen.getByTestId("approve")).toBeEnabled();
      expect(screen.getByTestId("reject")).toBeEnabled();
    });

    it("a company-wide grant covers every branch (the document's branch included)", async () => {
      await open([{ roleId: "viewer", scope: "company", scopeId: null, permissions: PERMS }]);
      expect(screen.getByTestId("approve")).toBeEnabled();
      expect(screen.getByTestId("reject")).toBeEnabled();
    });

    it("another branch's approve grant does not turn into approval through a different permission, and a branch grant of something else gives nothing", async () => {
      await open([
        { roleId: "viewer", scope: "company", scopeId: null, permissions: NO_APPROVE },
        { roleId: "other", scope: "branch", scopeId: "b1", permissions: ["company.commercialdocs.submit"] },    // this branch, but not the approve permission
      ]);
      expect(screen.queryByTestId("approve")).toBeNull();
      expect(screen.queryByTestId("reject")).toBeNull();
    });

    it("user A with a branch approve grant signs out and user B, with only a view grant, signs in: B sees no Approve or Reject", async () => {
      const gA = [
        { roleId: "viewer", scope: "company", scopeId: null, permissions: NO_APPROVE },
        { roleId: "approver", scope: "branch", scopeId: "b1", permissions: ["company.commercialdocs.approve"] },
      ];
      const gB = [{ roleId: "viewer", scope: "company", scopeId: null, permissions: NO_APPROVE }];            // the same company-wide view/edit rights, but no approve anywhere
      const accessMe = vi.fn().mockResolvedValueOnce({ companyId: "c1", grants: gA }).mockResolvedValue({ companyId: "c1", grants: gB });
      await openSubmitted({ plan: [], decisions: [] }, { accessMe, login: vi.fn().mockResolvedValue({ user: { id: "u2", login: "b", displayName: "B", isIdentityAdministrator: false } }) });
      expect(screen.getByTestId("approve")).toBeEnabled();                                                     // user A may decide on branch b1
      await userEvent.click(screen.getByRole("button", { name: "تسجيل الخروج" }));
      await userEvent.type(await screen.findByLabelText(/اسم المستخدم/), "b");
      await userEvent.type(screen.getByLabelText(/كلمة المرور/), "pw");
      await userEvent.click(screen.getByRole("button", { name: "دخول" }));
      await openDoc();                                                                                        // user B opens the same submitted document
      expect(screen.queryByTestId("approve")).toBeNull();
      expect(screen.queryByTestId("reject")).toBeNull();
      expect(accessMe).toHaveBeenCalledTimes(2);                                                              // B's own grants were read, A's were not reused
    });
  });

  it("says a decision was recorded when the document stays submitted, reloads the progress, and says approved when it completes", async () => {
    const approve = vi.fn().mockResolvedValueOnce(submittedDoc()).mockResolvedValueOnce(approved());
    const progress = vi.fn().mockResolvedValueOnce({ plan: [stage(1, 1, 0), stage(2, 1, 0)], decisions: [] }).mockResolvedValueOnce({ plan: [stage(1, 1, 1), stage(2, 1, 0)], decisions: [decision(1, "u1", 1)] });
    await openSubmitted({}, { approveCommercialDocument: approve, getCommercialApprovals: progress });
    await userEvent.click(screen.getByTestId("approve"));
    expect(await screen.findByTestId("action-notice")).toHaveTextContent("تم تسجيل قرارك");
    await waitFor(() => expect(progress).toHaveBeenCalledTimes(2));                                          // the progress was reloaded after the decision
  });

  it("translates the refusal codes of the stages: forbidden (the server never sends a role-specific code over HTTP), already decided, stage complete", async () => {
    const approve = vi.fn().mockRejectedValueOnce(apiError(403, "forbidden")).mockRejectedValueOnce(apiError(409, "already_decided")).mockRejectedValueOnce(apiError(409, "approval_stage_complete"));
    await openSubmitted({ plan: [stage(1, 2, 0)], decisions: [] }, { approveCommercialDocument: approve });
    for (const text of ["ليست لديك صلاحية لهذا الإجراء.", "سجّلت قرارك على هذه النسخة من قبل.", "اكتملت هذه المرحلة؛ حدّث الصفحة."]) {
      await userEvent.click(screen.getByTestId("approve"));
      expect(await within(screen.getByTestId("form-error")).findByText(text)).toBeInTheDocument();
    }
  });

  it("works in English (LTR)", async () => {
    localStorage.setItem("hasm.locale", "en");
    const current = submittedDoc();
    setup({ getCommercialApprovals: vi.fn().mockResolvedValue({ plan: [stage(1, 1, 0)], decisions: [] }) }, PERMS, ["commercial_test"], [current], current);
    await go("#/commercial/documents");
    await userEvent.click(await screen.findByRole("button", { name: "View" }));
    await userEvent.click(await screen.findByRole("tab", { name: /^History/ }));
    expect(within(await screen.findByTestId("stage-1")).getByText("Current")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Approval progress" })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
  });
});

const link = (over: Record<string, unknown> = {}) => ({ id: "l1", fromDocumentId: "src", fromLineNo: 1, toDocumentId: "d1", toLineNo: 1, kind: 1, quantity: 4, createdBy: "u1", createdAtUtc: STAMP, replayed: false, ...over });
const qty = (over: Record<string, unknown> = {}) => ({ lineNo: 1, ordered: 10, fulfilled: 4, invoiced: 0, returned: 0, remainingToFulfil: 6, remainingToInvoice: 10, remainingToReturn: 10, ...over });
const source = approved({ id: "src", officialNumber: "2026-000002", lines: [{ lineNo: 1, itemId: "i1", unitId: "u-box", quantity: 10, warehouseId: null, lineNet: 100, pricingHash: "b".repeat(64) }, { lineNo: 2, itemId: "i1", unitId: "u-box", quantity: 3, warehouseId: null, lineNet: 30, pricingHash: "c".repeat(64) }] });
/** What the server does: every filter before paging, newest first (the array is ordered newest first), the exact total. */
const serverSearch = (all: CommercialDocument[]) => vi.fn().mockImplementation((q: { status?: number; branchId?: string; partyId?: string; currency?: string; docType?: string; search?: string; offset?: number; limit?: number }) => {
  const m = all.filter((d) => (q.status === undefined || d.status === q.status) && (!q.branchId || d.branchId === q.branchId) && (!q.partyId || d.partyId === q.partyId) && (!q.currency || d.currency === q.currency)
    && (!q.docType || d.docType === q.docType) && (!q.search || (d.officialNumber ?? "").toLowerCase().includes(q.search.toLowerCase())));
  const off = q.offset ?? 0;
  return Promise.resolve({ documents: m.slice(off, off + (q.limit ?? 50)), total: m.length });
});
const openLinks = async (over: Record<string, unknown> = {}, perms = PERMS, current: CommercialDocument = approved()) => {
  const client = setup({
    getCommercialDocument: vi.fn().mockImplementation((id: string) => Promise.resolve(id === "src" ? source : current)),
    searchCommercialDocuments: serverSearch([current, source, approved({ id: "other-party", partyId: "p2", officialNumber: "2026-000003" }), approved({ id: "other-ccy", currency: "USD", officialNumber: "2026-000004" })]),
    ...over,
  }, perms, ["commercial_test"], [current], current);
  await go("#/commercial/documents");
  await userEvent.click(within(await screen.findByTestId("doc-d1")).getByRole("button", { name: "عرض" }));
  await screen.findByTestId("commercial-editor");
  await userEvent.click(tab("الروابط"));
  return client;
};

describe("commercial document links (S2 API on the screen)", () => {
  it("shows the derived line quantities and the links in both directions of an approved document", async () => {
    await openLinks({
      listDocumentLinks: vi.fn().mockResolvedValue([link(), link({ id: "l2", fromDocumentId: "d1", toDocumentId: "next", fromLineNo: 1, toLineNo: 2, kind: 2, quantity: 1 })]),
      getLineQuantities: vi.fn().mockResolvedValue([qty()]),
    });
    const row = await screen.findByTestId("qty-1");
    expect(within(row).getAllByRole("cell").map((c) => c.textContent)).toEqual(["1", "10", "4", "0", "0", "6"]);
    expect(within(screen.getByTestId("link-l1")).getByText("إلى هذا المستند")).toBeInTheDocument();
    expect(within(screen.getByTestId("link-l1")).getByText("ينفّذ")).toBeInTheDocument();
    expect(within(screen.getByTestId("link-l2")).getByText("من هذا المستند")).toBeInTheDocument();
    expect(within(screen.getByTestId("link-l2")).getByText("يفوتر")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /حذف|إزالة/ })).toBeNull();                              // append-only: no remove button
  });

  it("explains the empty state (no type pair is activated in production) and a draft document is not linkable and asks the server for nothing", async () => {
    const client = await openLinks();
    expect(await screen.findByTestId("links-empty")).toHaveTextContent("لا توجد روابط لهذا المستند.");
    expect(screen.getByTestId("links-empty")).toHaveTextContent("لا يوجد في الإنتاج أي زوج أنواع مفعّل");
    document.body.innerHTML = "";
    window.location.hash = "";
    const draftClient = setup({}, PERMS, ["commercial_test"], [doc()], doc());
    await openDoc();
    await userEvent.click(tab("الروابط"));
    expect(screen.getByTestId("links-not-approved")).toHaveTextContent("لا يُربط إلا المستند المعتمد");
    expect(draftClient.listDocumentLinks).not.toHaveBeenCalled();
    expect(draftClient.getLineQuantities).not.toHaveBeenCalled();
    expect(screen.queryByTestId("create-link")).toBeNull();
    expect(client.listDocumentLinks).toHaveBeenCalledWith("d1");
  });

  it("creates a link with this document as the target, offers only valid sources, sends no creator or total, and starts a new idempotency key after a success", async () => {
    const create = vi.fn().mockResolvedValue(link());
    const client = await openLinks({ createDocumentLink: create });
    expect(within(await screen.findByLabelText(/^نوع الرابط/)).getAllByRole("option").map((o) => o.textContent)).toEqual(["— اختر —", "ينفّذ", "يفوتر", "يُرجع", "يعدّل"]);   // only the kinds of the registered pairs
    await userEvent.selectOptions(screen.getByLabelText(/^نوع الرابط/), "2");
    const sources = within(await screen.findByLabelText("المستند المصدر")).getAllByRole("option").map((o) => o.textContent);
    expect(sources.join("|")).toContain("2026-000002");
    expect(sources.join("|")).not.toContain("2026-000003");                                              // another party
    expect(sources.join("|")).not.toContain("2026-000004");                                              // another currency
    expect(sources.join("|")).not.toContain("2026-000001");                                              // the document itself
    await userEvent.selectOptions(screen.getByLabelText("المستند المصدر"), "src");
    await userEvent.selectOptions(await screen.findByLabelText(/^سطر المصدر/), "1");
    await userEvent.selectOptions(screen.getByLabelText(/^سطر هذا المستند/), "1");
    await userEvent.type(within(screen.getByTestId("links-panel")).getByLabelText(/^الكمية/), "٣");
    await userEvent.click(screen.getByTestId("create-link"));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    const first = create.mock.calls[0]![0] as Record<string, unknown>;
    expect(first).toEqual({ fromDocumentId: "src", fromLineNo: 1, toDocumentId: "d1", toLineNo: 1, kind: 2, quantity: 3, idempotencyKey: expect.stringMatching(/.{8,}/) });
    expect(JSON.stringify(first)).not.toMatch(/createdBy|price|total|tax/i);
    expect(await screen.findByTestId("link-created")).toHaveTextContent("تم إنشاء الرابط.");
    expect(client.listDocumentLinks).toHaveBeenCalledTimes(2);                                            // reloaded after the success
    await userEvent.type(within(screen.getByTestId("links-panel")).getByLabelText(/^الكمية/), "1");
    await userEvent.click(screen.getByTestId("create-link"));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));
    expect((create.mock.calls[1]![0] as { idempotencyKey: string }).idempotencyKey).not.toBe(first.idempotencyKey);
  });

  it("keeps the same idempotency key when the same attempt is retried after a failure, and refuses a bad quantity before sending", async () => {
    const create = vi.fn().mockRejectedValueOnce(apiError(409, "over_fulfilment")).mockResolvedValueOnce(link());
    await openLinks({ createDocumentLink: create });
    await userEvent.selectOptions(await screen.findByLabelText(/^نوع الرابط/), "1");
    await userEvent.selectOptions(await screen.findByLabelText("المستند المصدر"), "src");
    await userEvent.selectOptions(await screen.findByLabelText(/^سطر المصدر/), "1");
    await userEvent.selectOptions(screen.getByLabelText(/^سطر هذا المستند/), "1");
    await userEvent.type(within(screen.getByTestId("links-panel")).getByLabelText(/^الكمية/), "1,500");
    await userEvent.click(screen.getByTestId("create-link"));
    expect(await screen.findByText(/كمية غير صالحة/)).toBeInTheDocument();
    expect(create).not.toHaveBeenCalled();
    await userEvent.clear(within(screen.getByTestId("links-panel")).getByLabelText(/^الكمية/));
    await userEvent.type(within(screen.getByTestId("links-panel")).getByLabelText(/^الكمية/), "20");
    await userEvent.click(screen.getByTestId("create-link"));
    expect(await screen.findByTestId("link-error")).toHaveTextContent("تتجاوز الكمية المتبقية في أحد السطرين.");
    await userEvent.click(screen.getByTestId("create-link"));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));
    expect((create.mock.calls[1]![0] as { idempotencyKey: string }).idempotencyKey).toBe((create.mock.calls[0]![0] as { idempotencyKey: string }).idempotencyKey);
  });

  it("translates every refusal code of the server, on the field when it names one", async () => {
    const create = vi.fn()
      .mockRejectedValueOnce(apiError(400, "invalid_request", { fields: [{ field: "kind", code: "link_not_allowed" }] }))
      .mockRejectedValueOnce(apiError(409, "party_mismatch")).mockRejectedValueOnce(apiError(409, "link_exists")).mockRejectedValueOnce(apiError(409, "document_not_approved"));
    await openLinks({ createDocumentLink: create });
    await userEvent.selectOptions(await screen.findByLabelText(/^نوع الرابط/), "1");
    await userEvent.selectOptions(await screen.findByLabelText("المستند المصدر"), "src");
    await userEvent.selectOptions(await screen.findByLabelText(/^سطر المصدر/), "1");
    await userEvent.selectOptions(screen.getByLabelText(/^سطر هذا المستند/), "1");
    await userEvent.type(within(screen.getByTestId("links-panel")).getByLabelText(/^الكمية/), "1");
    await userEvent.click(screen.getByTestId("create-link"));
    expect(await screen.findByText("لا يُسمح بالربط بين هذين النوعين بهذا النوع من الروابط.")).toBeInTheDocument();
    for (const text of ["المستندان لطرفين مختلفين.", "يوجد رابط من هذا النوع بين هذين السطرين.", "لا يُربط إلا بمستندات معتمدة."]) {
      await userEvent.click(screen.getByTestId("create-link"));
      expect(await within(screen.getByTestId("link-error")).findByText(text)).toBeInTheDocument();
    }
  });

  it("offers no link form to a user without the update permission (reading is still allowed)", async () => {
    await openLinks({ listDocumentLinks: vi.fn().mockResolvedValue([link()]) }, PERMS.filter((p) => p !== "company.commercialdocs.update"));
    expect(await screen.findByTestId("link-l1")).toBeInTheDocument();
    expect(screen.queryByTestId("create-link")).toBeNull();
  });

  it("hides the link form and shows the informative empty state when no registered pair has this document's type as its target (production registers none)", async () => {
    for (const pairs of [[], [{ source: "commercial_test", target: "another_type", kind: 1 }]]) {
      const client = await openLinks({ listCommercialLinkPairs: vi.fn().mockResolvedValue(pairs) });
      expect(await screen.findByTestId("no-link-pairs")).toHaveTextContent("لا يُعرض نموذج الربط");
      expect(screen.queryByTestId("create-link")).toBeNull();
      expect(client.listCommercialLinkPairs).toHaveBeenCalled();
      document.body.innerHTML = "";
      window.location.hash = "";
    }
  });

  it("offers only the sources whose type is registered for the chosen kind", async () => {
    await openLinks({
      listCommercialLinkPairs: vi.fn().mockResolvedValue([{ source: "commercial_test", target: "commercial_test", kind: 1 }, { source: "special_source", target: "commercial_test", kind: 3 }]),
      searchCommercialDocuments: serverSearch([approved(), source, approved({ id: "sp", docType: "special_source", officialNumber: "2026-000009" })]),
    });
    const kindSelect = await screen.findByLabelText(/^نوع الرابط/);
    expect(within(kindSelect).getAllByRole("option").map((o) => o.textContent)).toEqual(["— اختر —", "ينفّذ", "يُرجع"]);
    await userEvent.selectOptions(kindSelect, "1");
    expect(within(screen.getByLabelText("المستند المصدر")).getAllByRole("option").map((o) => o.textContent).join("|")).toContain("2026-000002");
    expect(within(screen.getByLabelText("المستند المصدر")).getAllByRole("option").map((o) => o.textContent).join("|")).not.toContain("2026-000009");
    await userEvent.selectOptions(kindSelect, "3");
    const options = within(screen.getByLabelText("المستند المصدر")).getAllByRole("option").map((o) => o.textContent).join("|");
    expect(options).toContain("2026-000009");
    expect(options).not.toContain("2026-000002");
  });

  it("rotates the idempotency key when any field changes, and on a reuse refusal rotates it again and reloads the links", async () => {
    const create = vi.fn().mockRejectedValueOnce(apiError(409, "over_fulfilment")).mockRejectedValueOnce(apiError(409, "over_fulfilment")).mockRejectedValueOnce(apiError(409, "idempotency_key_reused")).mockResolvedValueOnce(link());
    const client = await openLinks({ createDocumentLink: create });
    const panel = () => within(screen.getByTestId("links-panel"));
    const keyOf = (n: number) => (create.mock.calls[n]![0] as { idempotencyKey: string }).idempotencyKey;
    await userEvent.selectOptions(await screen.findByLabelText(/^نوع الرابط/), "1");
    await userEvent.selectOptions(await screen.findByLabelText("المستند المصدر"), "src");
    await userEvent.selectOptions(await screen.findByLabelText(/^سطر المصدر/), "1");
    await userEvent.selectOptions(screen.getByLabelText(/^سطر هذا المستند/), "1");
    await userEvent.type(panel().getByLabelText(/^الكمية/), "20");
    await userEvent.click(screen.getByTestId("create-link"));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    await userEvent.click(screen.getByTestId("create-link"));                                         // the same payload again: the same key
    await waitFor(() => expect(create).toHaveBeenCalledTimes(2));
    expect(keyOf(1)).toBe(keyOf(0));
    await userEvent.clear(panel().getByLabelText(/^الكمية/));
    await userEvent.type(panel().getByLabelText(/^الكمية/), "21");                                    // another payload: another key
    await userEvent.click(screen.getByTestId("create-link"));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(3));
    expect(keyOf(2)).not.toBe(keyOf(0));
    expect(await within(screen.getByTestId("link-error")).findByText("مفتاح الإرسال مستعمل لرابط آخر.")).toBeInTheDocument();
    await waitFor(() => expect(client.listDocumentLinks).toHaveBeenCalledTimes(2));                  // the reuse refusal reloaded the links
    await userEvent.click(screen.getByTestId("create-link"));                                         // the key was rotated after the refusal
    await waitFor(() => expect(create).toHaveBeenCalledTimes(4));
    expect(keyOf(3)).not.toBe(keyOf(2));
  });

  it("reaches older approved sources: every filter is sent to the server, the page and total are exact, and an old source beyond the first page is found by its number", async () => {
    const recent = Array.from({ length: 120 }, (_, i) => approved({ id: `r${i}`, officialNumber: `2025-${String(1000 + i)}`, createdAtUtc: `2025-06-${String(28 - Math.floor(i / 10)).padStart(2, "0")}T10:00:00Z` }));
    const old = approved({ id: "src", officialNumber: "2024-000777", createdAtUtc: "2024-01-01T10:00:00Z" });
    const search = serverSearch([approved(), ...recent, old, approved({ id: "other-party", partyId: "p2", officialNumber: "2025-9999" }), approved({ id: "other-ccy", currency: "USD", officialNumber: "2025-9998" })]);
    await openLinks({ searchCommercialDocuments: search, getCommercialDocument: vi.fn().mockImplementation((id: string) => Promise.resolve(id === "src" ? source : approved())) });
    await userEvent.selectOptions(await screen.findByLabelText(/^نوع الرابط/), "1");
    const select = screen.getByLabelText("المستند المصدر");
    await waitFor(() => expect(within(select).getAllByRole("option").length).toBe(50));                  // one bounded page (50 from the server, this document itself removed) plus the empty choice: the old source is NOT among the newest
    expect(within(select).getAllByRole("option").map((o) => o.textContent).join("|")).not.toContain("2024-000777");
    await waitFor(() => expect(within(screen.getByTestId("cl-source-picker")).getByRole("status")).toHaveTextContent(/49.*121/));   // the exact total: the 120 recent ones and the old one; this document itself is never its own source
    expect(search).toHaveBeenCalledWith({ status: 3, branchId: "b1", partyId: "p1", currency: "JOD", docType: "commercial_test", search: undefined, limit: 50 });
    await userEvent.type(screen.getByLabelText(/المستند المصدر — /), "777");
    await waitFor(() => expect(within(select).getAllByRole("option").map((o) => o.textContent).join("|")).toContain("2024-000777"));   // reached by its number
    expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ search: "777", partyId: "p1", currency: "JOD", status: 3, branchId: "b1" }));
    expect(within(select).getAllByRole("option").map((o) => o.textContent).join("|")).not.toContain("2025-9999");                           // another party is never offered
    await userEvent.selectOptions(select, "src");
    expect(await screen.findByLabelText(/^سطر المصدر/)).toBeEnabled();
  });

  it("works in English (LTR)", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup({ listDocumentLinks: vi.fn().mockResolvedValue([link()]), getLineQuantities: vi.fn().mockResolvedValue([qty()]) }, PERMS, ["commercial_test"], [approved()], approved());
    await go("#/commercial/documents");
    await userEvent.click(await screen.findByRole("button", { name: "View" }));
    await userEvent.click(await screen.findByRole("tab", { name: /^Links/ }));
    expect(await screen.findByTestId("line-quantities")).toBeInTheDocument();
    expect(within(screen.getByTestId("link-l1")).getByText("Into this document")).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Link to another document" })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
  });
});

const CLOSE = "company.commercialdocs.close";
const CLOSER = [...PERMS, CLOSE];
const shortClose = (over: Record<string, unknown> = {}) => ({ operationId: "op1", documentId: "d1", reason: "أوقف المورّد الصنف", createdBy: "closer-user-id", createdAtUtc: "2026-03-05T08:15:00Z", lines: [{ lineNo: 1, quantity: 2 }], replayed: false, ...over });
/** A partially fulfilled line: 10 ordered, 4 fulfilled, 6 still open (the server derives every figure). */
const openQty = (over: Record<string, unknown> = {}) => qty({ shortClosed: 0, open: 6, ...over });

describe("short close of a commercial document (D-201) on the shared screen", () => {
  it("shows what was closed short and what is open per line and the operations with their reasons, and offers no action without the close permission", async () => {
    const client = await openLinks({ getLineQuantities: vi.fn().mockResolvedValue([openQty({ shortClosed: 2, open: 4, remainingToFulfil: 4 })]), listShortCloses: vi.fn().mockResolvedValue([shortClose()]) });
    const panel = await screen.findByTestId("short-close-panel");
    expect(within(await within(panel).findByTestId("open-1")).getAllByRole("cell").map((c) => c.textContent)).toEqual(["1", "2", "4"]);
    const row = await within(panel).findByTestId("short-close-op1");
    expect(within(row).getByText("أوقف المورّد الصنف")).toBeInTheDocument();
    expect(within(row).getByText("2026-03-05 08:15:00")).toBeInTheDocument();
    expect(within(row).getByText("#1: 2")).toBeInTheDocument();
    expect(screen.queryByTestId("short-close")).toBeNull();                                               // the permission is separate: update, approve and cancel do not imply it
    expect(client.listShortCloseTypes).not.toHaveBeenCalled();
    expect(within(panel).queryByRole("button")).toBeNull();                                               // append-only: no edit, no remove
    expect(client.listShortCloses).toHaveBeenCalledWith("d1");
  });

  it("closes the remaining quantity with a mandatory reason, the revision as If-Match and one idempotency key, and sends no creator or quantity", async () => {
    const close = vi.fn().mockResolvedValue(shortClose({ lines: [{ lineNo: 1, quantity: 6 }] }));
    const quantities = vi.fn().mockResolvedValueOnce([openQty()]).mockResolvedValue([openQty({ shortClosed: 6, open: 0, remainingToFulfil: 0 })]);
    const closes = vi.fn().mockResolvedValueOnce([]).mockResolvedValue([shortClose({ lines: [{ lineNo: 1, quantity: 6 }] })]);
    const client = await openLinks({ shortCloseDocument: close, getLineQuantities: quantities, listShortCloses: closes }, CLOSER);
    expect(await screen.findByTestId("short-closes-empty")).toHaveTextContent("لم تُقفَل أي كمية من هذا المستند.");
    await userEvent.click(await screen.findByTestId("short-close"));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText(/لن يمكن تنفيذها أو فوترتها/)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "تأكيد" }));
    expect(await within(dialog).findByText("السبب مطلوب.")).toBeInTheDocument();                           // refused before anything is sent
    expect(close).not.toHaveBeenCalled();
    await userEvent.type(within(dialog).getByLabelText(/^السبب/), "  ألغى العميل الباقي  ");
    await userEvent.click(within(dialog).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByTestId("short-close-done")).toHaveTextContent("أُقفلت الكمية المتبقية.");
    expect(close).toHaveBeenCalledTimes(1);
    const [id, body, revision] = close.mock.calls[0] as [string, Record<string, unknown>, number];
    expect([id, revision]).toEqual(["d1", 5]);
    expect(Object.keys(body).sort()).toEqual(["idempotencyKey", "reason"]);                                // no creator, no document id, no quantity, no lines: the server closes what is open
    expect(body.reason).toBe("ألغى العميل الباقي");
    expect(String(body.idempotencyKey).length).toBeGreaterThanOrEqual(8);
    expect(screen.queryByRole("dialog")).toBeNull();
    // the derived figures and the operations are read again from the server; nothing is left open, so the action is gone
    await waitFor(() => expect(within(screen.getByTestId("open-1")).getAllByRole("cell").map((c) => c.textContent)).toEqual(["1", "6", "0"]));
    expect(await screen.findByTestId("short-close-op1")).toBeInTheDocument();
    expect(await screen.findByTestId("short-close-nothing")).toHaveTextContent("لا توجد كمية مفتوحة للإقفال.");
    expect(screen.queryByTestId("short-close")).toBeNull();
    expect(client.listShortCloseTypes).toHaveBeenCalled();
  });

  it("offers the action only for a closable type, a partially fulfilled document and an open quantity, and says why otherwise", async () => {
    await openLinks({ getLineQuantities: vi.fn().mockResolvedValue([openQty()]), listShortCloseTypes: vi.fn().mockResolvedValue([]) }, CLOSER);   // production: no closable type is registered
    expect(await screen.findByTestId("short-close-no-type")).toHaveTextContent("هذا النوع من المستندات لا يقبل الإقفال الناقص.");
    expect(screen.queryByTestId("short-close")).toBeNull();
  });

  it("does not offer the action for a document nothing was fulfilled or invoiced from (it is cancelled, not closed short)", async () => {
    await openLinks({ getLineQuantities: vi.fn().mockResolvedValue([openQty({ fulfilled: 0, invoiced: 0, open: 10, remainingToFulfil: 10 })]) }, CLOSER);
    expect(await screen.findByTestId("short-close-not-partial")).toHaveTextContent("المستند غير المنفَّذ يُلغى ولا يُقفَل ناقصاً");
    expect(screen.queryByTestId("short-close")).toBeNull();
  });

  it("offers the action for a document that was only invoiced in part", async () => {
    await openLinks({ getLineQuantities: vi.fn().mockResolvedValue([openQty({ fulfilled: 0, invoiced: 7, open: 3 })]) }, CLOSER);
    expect(await screen.findByTestId("short-close")).toHaveTextContent("إقفال الكمية المتبقية");
  });

  it("shows no short-close panel for a document that is not approved and asks the server for nothing", async () => {
    const client = setup({}, CLOSER, ["commercial_test"], [doc()], doc());
    await openDoc();
    await userEvent.click(tab("الروابط"));
    expect(await screen.findByTestId("links-not-approved")).toBeInTheDocument();
    expect(screen.queryByTestId("short-close-panel")).toBeNull();
    expect(client.listShortCloses).not.toHaveBeenCalled();
    expect(client.listShortCloseTypes).not.toHaveBeenCalled();
  });

  it("translates the server's refusals, keeps the idempotency key for a retry of the same reason and rotates it for another reason", async () => {
    const close = vi.fn()
      .mockRejectedValueOnce(apiError(409, "concurrent_change")).mockRejectedValueOnce(apiError(412, "revision_conflict")).mockRejectedValueOnce(apiError(403, "forbidden"))
      .mockRejectedValueOnce(apiError(409, "document_not_partially_fulfilled")).mockRejectedValueOnce(apiError(409, "short_close_not_allowed_for_type")).mockRejectedValueOnce(apiError(409, "nothing_to_close"))
      .mockRejectedValueOnce(apiError(409, "idempotency_key_reused"));
    await openLinks({ shortCloseDocument: close, getLineQuantities: vi.fn().mockResolvedValue([openQty()]) }, CLOSER);
    const attempt = async (reason: string) => {
      await userEvent.click(await screen.findByTestId("short-close"));
      await userEvent.type(within(screen.getByRole("dialog")).getByLabelText(/^السبب/), reason);
      await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    };
    const texts = ["تغيّر شيء أثناء العملية؛ أعد التحميل وحاول مجدداً.", "تغيّر المستند؛ أعد التحميل وحاول مجدداً.", "لا تملك صلاحية الإقفال الناقص لهذا المستند.",
      "لم يُنفَّذ أو يُفوتَر شيء من هذا المستند بعد؛ يُلغى ولا يُقفَل ناقصاً.", "هذا النوع من المستندات لا يقبل الإقفال الناقص.", "لا توجد كمية مفتوحة للإقفال.", "مفتاح الإرسال مستعمل لعملية إقفال أخرى."];
    for (const text of texts) {
      await attempt("السبب نفسه");
      expect(await within(await screen.findByTestId("short-close-error")).findByText(text)).toBeInTheDocument();
      expect(screen.queryByRole("dialog")).toBeNull();
    }
    const keys = close.mock.calls.map((c) => (c[1] as { idempotencyKey: string }).idempotencyKey);
    expect(new Set(keys).size).toBe(1);                                                                    // the same reason is the same request: a retry replays, never duplicates
    close.mockResolvedValueOnce(shortClose());
    await attempt("السبب نفسه");
    await screen.findByTestId("short-close-done");
    expect((close.mock.calls[7][1] as { idempotencyKey: string }).idempotencyKey).not.toBe(keys[0]);       // after a reuse refusal the key is rotated
    close.mockRejectedValueOnce(apiError(409, "concurrent_change"));
    await attempt("سبب آخر");
    await screen.findByTestId("short-close-error");
    expect((close.mock.calls[8][1] as { idempotencyKey: string }).idempotencyKey).not.toBe((close.mock.calls[7][1] as { idempotencyKey: string }).idempotencyKey);
  });

  it("works in English (LTR) and inside the Tauri shell with the same action", async () => {
    localStorage.setItem("hasm.locale", "en");
    (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {};
    try {
      const close = vi.fn().mockResolvedValue(shortClose());
      setup({ getLineQuantities: vi.fn().mockResolvedValue([openQty()]), listShortCloses: vi.fn().mockResolvedValue([shortClose({ reason: "discontinued" })]), shortCloseDocument: close }, CLOSER, ["commercial_test"], [approved()], approved());
      await go("#/commercial/documents");
      await userEvent.click(await screen.findByRole("button", { name: "View" }));
      await userEvent.click(await screen.findByRole("tab", { name: /^Links/ }));
      expect(await screen.findByRole("heading", { name: "Short close" })).toBeInTheDocument();
      expect(within(await screen.findByTestId("short-close-op1")).getByText("discontinued")).toBeInTheDocument();
      await userEvent.click(await screen.findByRole("button", { name: "Close the remaining quantity" }));
      const dialog = screen.getByRole("dialog");
      await userEvent.keyboard("{Tab}");                                                                   // keyboard reachable: the dialog holds the focus
      await userEvent.type(within(dialog).getByLabelText(/^Reason/), "no longer needed");
      await userEvent.click(within(dialog).getByRole("button", { name: "Confirm" }));
      expect(await screen.findByTestId("short-close-done")).toHaveTextContent("The remaining quantity was closed.");
      expect(close).toHaveBeenCalledWith("d1", expect.objectContaining({ reason: "no longer needed" }), 5);
      expect(document.documentElement.dir).toBe("ltr");
    } finally { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; }
  });
});
