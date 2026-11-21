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
    listDocumentLinks: vi.fn().mockResolvedValue([]), getLineQuantities: vi.fn().mockResolvedValue([]), createDocumentLink: vi.fn(),
    listCommercialLinkPairs: vi.fn().mockResolvedValue([1, 2, 3, 4].map((kind) => ({ source: "commercial_test", target: "commercial_test", kind }))),
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

const link = (over: Record<string, unknown> = {}) => ({ id: "l1", fromDocumentId: "src", fromLineNo: 1, toDocumentId: "d1", toLineNo: 1, kind: 1, quantity: 4, createdBy: "u1", createdAtUtc: STAMP, replayed: false, ...over });
const qty = (over: Record<string, unknown> = {}) => ({ lineNo: 1, ordered: 10, fulfilled: 4, invoiced: 0, returned: 0, remainingToFulfil: 6, remainingToInvoice: 10, remainingToReturn: 10, ...over });
const source = approved({ id: "src", officialNumber: "2026-000002", lines: [{ lineNo: 1, itemId: "i1", unitId: "u-box", quantity: 10, warehouseId: null, lineNet: 100, pricingHash: "b".repeat(64) }, { lineNo: 2, itemId: "i1", unitId: "u-box", quantity: 3, warehouseId: null, lineNet: 30, pricingHash: "c".repeat(64) }] });
const openLinks = async (over: Record<string, unknown> = {}, perms = PERMS, current: CommercialDocument = approved()) => {
  const client = setup({
    getCommercialDocument: vi.fn().mockImplementation((id: string) => Promise.resolve(id === "src" ? source : current)),
    listCommercialDocuments: vi.fn().mockResolvedValue([current, source, approved({ id: "other-party", partyId: "p2", officialNumber: "2026-000003" }), approved({ id: "other-ccy", currency: "USD", officialNumber: "2026-000004" })]),
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
      listCommercialDocuments: vi.fn().mockResolvedValue([approved(), source, approved({ id: "sp", docType: "special_source", officialNumber: "2026-000009" })]),
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

  it("reaches older approved sources: the picker pages the server list by status and branch and finds a source by its number beyond the first hundred", async () => {
    const filler = (n: number) => Array.from({ length: 100 }, (_, i) => approved({ id: `f${n}-${i}`, partyId: "p2", officialNumber: `2025-${n}${String(i).padStart(4, "0")}` }));
    const old = approved({ id: "src", officialNumber: "2024-000777" });
    const list = vi.fn().mockImplementation((q: { offset?: number }) => Promise.resolve(q.offset === 0 ? [approved(), ...filler(1).slice(1)] : q.offset === 100 ? filler(2) : [old]));
    await openLinks({ listCommercialDocuments: list, getCommercialDocument: vi.fn().mockImplementation((id: string) => Promise.resolve(id === "src" ? source : approved())) });
    await userEvent.selectOptions(await screen.findByLabelText(/^نوع الرابط/), "1");
    const select = screen.getByLabelText("المستند المصدر");
    await waitFor(() => expect(within(select).getAllByRole("option").map((o) => o.textContent).join("|")).toContain("2024-000777"));   // found on the third page
    expect(list).toHaveBeenCalledWith(expect.objectContaining({ status: 3, branchId: "b1", offset: 200, limit: 100 }));
    expect(within(select).getAllByRole("option").map((o) => o.textContent).join("|")).not.toContain("2025-");                              // another party's documents are never offered
    await userEvent.type(screen.getByLabelText(/المستند المصدر — /), "777");
    await waitFor(() => expect(within(select).getAllByRole("option").map((o) => o.textContent).join("|")).toContain("2024-000777"));
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
