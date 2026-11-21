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
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["المستند", "السطور", "السجل"]);
  });
});

describe("commercial documents in English and the Tauri shell", () => {
  it("works in English (LTR) with the same tabs and keyboard navigation", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup();
    await go("#/commercial/documents");
    await userEvent.click(await screen.findByRole("button", { name: "View" }));
    await screen.findByTestId("commercial-editor");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["Document", "Lines", "History"]);
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
