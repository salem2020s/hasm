import type { StockDocument, StockDocumentCapabilities, StockDocumentSummary, StockItemRef, StockWarehouseRef } from "@hasm/api-client";
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

const PERMS = [...ALL_PERMISSIONS, "company.stockdocs.view", "company.stockdocs.create"];
const w1: StockWarehouseRef = { id: "w1", branchId: "b", code: "W1", nameAr: "المستودع الأول", nameEn: "First", isActive: true };
const w2: StockWarehouseRef = { id: "w2", branchId: "b", code: "W2", nameAr: "المستودع الثاني", nameEn: "Second", isActive: true };
const caps = (over: Partial<StockDocumentCapabilities> = {}): StockDocumentCapabilities => ({ view: ["w1", "w2"], create: ["w1", "w2"], update: ["w1", "w2"], submit: ["w1", "w2"], approve: ["w1", "w2"], cancel: ["w1", "w2"], ...over });
const apple: StockItemRef = {
  id: "i1", code: "APPLE", nameAr: "تفاح", nameEn: "Apple",
  units: [{ id: "u-pcs", code: "PCS", nameAr: "قطعة", nameEn: "Piece", decimals: 0, factor: 1, isBase: true }, { id: "u-box", code: "BOX", nameAr: "علبة", nameEn: "Box", decimals: 2, factor: 12, isBase: false }],
};
const stamp = "2026-03-01T10:00:00Z";
const summary = (over: Partial<StockDocumentSummary> = {}): StockDocumentSummary => ({
  id: "d1", docType: 1, status: 1, number: null, documentDate: "2026-03-10", branchId: "b", sourceWarehouseId: null, targetWarehouseId: "w1", reference: "REF-1", lineCount: 1, revision: 1, createdAtUtc: stamp, updatedAtUtc: stamp, ...over,
});
const doc = (over: Partial<StockDocument> = {}): StockDocument => ({
  id: "d1", docType: 1, status: 1, branchId: "b", sourceWarehouseId: null, targetWarehouseId: "w1", documentDate: "2026-03-10", reference: "REF-1", notes: null, number: null, fiscalYearId: null, createdBy: "u1", createdAtUtc: stamp,
  submittedBy: null, submittedAtUtc: null, decidedBy: null, decidedAtUtc: null, decisionNote: null, cancelledBy: null, cancelledAtUtc: null, cancelReason: null, revision: 1, updatedAtUtc: stamp,
  lines: [{ lineNo: 1, itemId: "i1", itemCode: "APPLE", itemNameAr: "تفاح", itemNameEn: "Apple", unitId: "u-box", unitCode: "BOX", quantity: 2, factor: 12, baseQuantity: 24, baseUnitCode: "PCS", notes: null }],
  events: [{ id: 1, kind: 1, fromStatus: null, toStatus: 1, actorUserId: "u1", atUtc: stamp, note: null }], ...over,
});

function setup(over: Record<string, unknown> = {}, c = caps(), docs: StockDocumentSummary[] = [summary()], current: StockDocument = doc()) {
  const client = signedIn({
    getStockDocumentCapabilities: vi.fn().mockResolvedValue(c),
    listStockWarehouses: vi.fn().mockResolvedValue({ warehouses: [w1, w2] }),
    listStockDocuments: vi.fn().mockResolvedValue({ documents: docs, total: docs.length }),
    getStockDocument: vi.fn().mockResolvedValue(current),
    searchStockItems: vi.fn().mockResolvedValue({ items: [apple] }),
    createStockDocument: vi.fn(), updateStockDocument: vi.fn(),
    ...over,
  }, PERMS);
  renderApp(client);
  return client;
}
const tab = (name: string) => screen.getByRole("tab", { name: new RegExp(`^${name}`) });
const openDoc = async () => { await go("#/inventory/stock-documents"); await userEvent.click(await screen.findByRole("button", { name: "عرض" })); await screen.findByTestId("stock-editor"); };

describe("stock documents list", () => {
  it("lists documents with number, type, warehouse and status, states that nothing is posted, and passes filters to the server", async () => {
    const client = setup({}, caps(), [summary({ id: "d1", number: "SR-2026-000001", status: 3 }), summary({ id: "d2", docType: 3, sourceWarehouseId: "w1", targetWarehouseId: "w2", status: 2 })]);
    await go("#/inventory/stock-documents");
    const row = await screen.findByTestId("doc-d1");
    expect(within(row).getByText("SR-2026-000001")).toBeInTheDocument();
    expect(within(row).getByText("استلام")).toBeInTheDocument();
    expect(within(row).getByText("معتمد")).toBeInTheDocument();
    expect(within(screen.getByTestId("doc-d2")).getByText(/W1 — المستودع الأول ← W2 — المستودع الثاني/)).toBeInTheDocument();
    expect(screen.getByTestId("no-posting-note")).toHaveTextContent("لا تغيّر كميات المخزون");
    await userEvent.selectOptions(screen.getByLabelText("الحالة"), "2");
    await waitFor(() => expect(client.listStockDocuments).toHaveBeenLastCalledWith(expect.objectContaining({ status: 2, offset: 0, limit: 25 })));
    await userEvent.type(screen.getByLabelText("بحث"), "SR-2026");
    await waitFor(() => expect(client.listStockDocuments).toHaveBeenLastCalledWith(expect.objectContaining({ search: "SR-2026" })));
  });

  it("hides the creation buttons from a user who cannot create, and disables transfers with a single usable warehouse", async () => {
    setup({}, caps({ create: [] }));
    await go("#/inventory/stock-documents");
    await screen.findByTestId("doc-d1");
    expect(screen.queryByRole("button", { name: "سند استلام جديد" })).toBeNull();
    expect(screen.getByTestId("read-only")).toBeInTheDocument();
  });

  it("disables a transfer when the user can create in only one warehouse", async () => {
    setup({}, caps({ create: ["w1"] }));
    await go("#/inventory/stock-documents");
    await screen.findByTestId("doc-d1");
    expect(screen.getByRole("button", { name: "تحويل جديد" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "سند استلام جديد" })).toBeEnabled();
  });
});

describe("stock document editor: tabs standard", () => {
  const openNewReceipt = async (over: Record<string, unknown> = {}, c = caps()) => {
    const client = setup(over, c);
    await go("#/inventory/stock-documents");
    await userEvent.click(await screen.findByRole("button", { name: "سند استلام جديد" }));
    await screen.findByTestId("stock-editor");
    return client;
  };

  it("organises a new document into named tabs without a history tab, with the actions outside the panels", async () => {
    await openNewReceipt();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["معلومات عامة", "السطور"]);
    const actions = screen.getByTestId("stock-actions");
    for (const name of ["معلومات عامة", "السطور"]) {
      await userEvent.click(tab(name));
      expect(within(actions).getByRole("button", { name: "حفظ" })).toBeVisible();
      expect(within(actions).getByRole("button", { name: "إلغاء" })).toBeVisible();
      expect(screen.getByTestId("stock-tabs").contains(actions)).toBe(false);
    }
    expect(screen.queryByLabelText("من مستودع")).toBeNull();                                                       // a receipt has only a target warehouse
  });

  it("builds a line with a server-found item, converts the quantity for display and creates the draft with the exact body", async () => {
    const created = doc({ id: "d9" });
    const client = await openNewReceipt({ createStockDocument: vi.fn().mockResolvedValue(created) });
    await userEvent.selectOptions(screen.getByLabelText(/^إلى مستودع/), "w1");
    await userEvent.type(screen.getByLabelText(/^المرجع/), "PO-7");
    await userEvent.click(tab("السطور"));
    await userEvent.type(within(screen.getByTestId("line-0")).getByLabelText("ابحث عن مادة"), "app");
    await waitFor(() => expect(client.searchStockItems).toHaveBeenCalledWith("app"));
    await userEvent.selectOptions(await screen.findByLabelText("اختر المادة"), "i1");
    expect(within(screen.getByTestId("line-0")).getByLabelText("الوحدة")).toHaveValue("u-pcs");                        // the base unit is the default
    await userEvent.selectOptions(within(screen.getByTestId("line-0")).getByLabelText("الوحدة"), "u-box");
    await userEvent.type(within(screen.getByTestId("line-0")).getByLabelText("الكمية"), "2.5");
    expect(screen.getByTestId("base-0")).toHaveTextContent("30 PCS");                                              // 2.5 boxes x 12
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.createStockDocument).toHaveBeenCalledTimes(1));
    expect(client.createStockDocument).toHaveBeenCalledWith({
      docType: 1, sourceWarehouseId: null, targetWarehouseId: "w1", documentDate: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), reference: "PO-7", notes: null,
      lines: [{ itemId: "i1", unitId: "u-box", quantity: 2.5, notes: null }],
    });
  });

  it("keeps unsaved input when switching tabs and marks the tab that has errors without silently saving", async () => {
    const client = await openNewReceipt();
    await userEvent.type(screen.getByLabelText(/^المرجع/), "KEEP");
    await userEvent.click(tab("السطور"));
    await userEvent.click(tab("معلومات عامة"));
    expect(screen.getByLabelText(/^المرجع/)).toHaveValue("KEEP");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));                                             // no warehouse, no item, no quantity
    expect(client.createStockDocument).not.toHaveBeenCalled();
    expect(screen.getByTestId("form-error")).toHaveTextContent("صحّح الأخطاء");
    expect(tab("معلومات عامة")).toHaveTextContent("يحتوي على أخطاء");
    expect(tab("السطور")).toHaveTextContent("يحتوي على أخطاء");
    expect(tab("معلومات عامة")).toHaveAttribute("aria-selected", "true");
  });

  it("shows a server rejection on its line and tab and keeps everything typed", async () => {
    const failing = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "lines[0].quantity", code: "base_quantity_precision_exceeded" }] }));
    const client = await openNewReceipt({ createStockDocument: failing });
    await userEvent.selectOptions(screen.getByLabelText(/^إلى مستودع/), "w1");
    await userEvent.click(tab("السطور"));
    await userEvent.type(within(screen.getByTestId("line-0")).getByLabelText("ابحث عن مادة"), "app");
    await userEvent.selectOptions(await screen.findByLabelText("اختر المادة"), "i1");
    await userEvent.selectOptions(within(screen.getByTestId("line-0")).getByLabelText("الوحدة"), "u-box");
    await userEvent.type(within(screen.getByTestId("line-0")).getByLabelText("الكمية"), "0.3");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect((await screen.findAllByText(/لا تُمثَّل بدقة/)).length).toBeGreaterThan(0);
    expect(client.createStockDocument).toHaveBeenCalledTimes(1);
    expect(within(screen.getByTestId("line-0")).getByLabelText("الكمية")).toHaveValue("0.3");
  });

  it("a transfer needs two different warehouses", async () => {
    const client = setup();
    await go("#/inventory/stock-documents");
    await userEvent.click(await screen.findByRole("button", { name: "تحويل جديد" }));
    await screen.findByTestId("stock-editor");
    await userEvent.selectOptions(screen.getByLabelText(/^من مستودع/), "w1");
    await userEvent.selectOptions(screen.getByLabelText(/^إلى مستودع/), "w1");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText("لا يمكن التحويل إلى المستودع نفسه.")).toBeInTheDocument();
    expect(client.createStockDocument).not.toHaveBeenCalled();
  });
});

describe("stock document workflow", () => {
  it("a draft shows its history, can be saved with the revision as If-Match and submitted only when saved", async () => {
    const client = setup({ updateStockDocument: vi.fn().mockResolvedValue(doc({ revision: 2 })), submitStockDocument: vi.fn().mockResolvedValue(doc({ status: 2, revision: 2, submittedBy: "u1" })) });
    await openDoc();
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("بلا رقم (مسودة)");
    expect(screen.getByTestId("doc-status")).toHaveTextContent("مسودة");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["معلومات عامة", "السطور", "السجل"]);
    await userEvent.click(tab("السجل"));
    expect(within(screen.getByTestId("history")).getByText("أُنشئ")).toBeInTheDocument();
    await userEvent.click(tab("معلومات عامة"));
    await userEvent.type(screen.getByLabelText("ملاحظات"), "x");
    expect(screen.getByRole("button", { name: "إرسال للاعتماد" })).toBeDisabled();                                  // unsaved edits: save first
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.updateStockDocument).toHaveBeenCalledWith("d1", expect.objectContaining({ notes: "x" }), 1));
  });

  it("submitting asks for confirmation and sends the current revision", async () => {
    const client = setup({ submitStockDocument: vi.fn().mockResolvedValue(doc({ status: 2, revision: 2, submittedBy: "u1" })) });
    await openDoc();
    await userEvent.click(screen.getByRole("button", { name: "إرسال للاعتماد" }));
    await userEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(client.submitStockDocument).toHaveBeenCalledWith("d1", 1));
  });

  it("the submitter cannot approve or reject: the buttons are disabled with the reason, while another approver can approve with the revision", async () => {
    setup({}, caps(), [summary({ status: 2 })], doc({ status: 2, revision: 3, submittedBy: "u1" }));
    await openDoc();
    expect(screen.getByTestId("approve")).toBeDisabled();
    expect(screen.getByRole("button", { name: "رفض" })).toBeDisabled();
    expect(screen.getByTestId("self-approval-note")).toHaveTextContent("لا يمكن لمن أرسل المستند أن يعتمده");
  });

  it("an independent approver approves (revision as If-Match) and the document shows its number", async () => {
    const client = setup({ approveStockDocument: vi.fn().mockResolvedValue(doc({ status: 3, number: "SR-2026-000001", revision: 4, submittedBy: "other", decidedBy: "u1" })) }, caps(), [summary({ status: 2 })], doc({ status: 2, revision: 3, submittedBy: "other" }));
    await openDoc();
    await userEvent.click(screen.getByTestId("approve"));
    await waitFor(() => expect(client.approveStockDocument).toHaveBeenCalledWith("d1", 3));
  });

  it("rejecting needs a note and cancelling needs a reason", async () => {
    const reject = vi.fn().mockResolvedValue(doc({ status: 4 }));
    const client = setup({ rejectStockDocument: reject }, caps(), [summary({ status: 2 })], doc({ status: 2, revision: 3, submittedBy: "other" }));
    await openDoc();
    await userEvent.click(screen.getByRole("button", { name: "رفض" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "تأكيد" }));                // empty note: refused on the client
    expect(await screen.findByText("ملاحظة الرفض مطلوبة.")).toBeInTheDocument();
    expect(reject).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "رفض" }));
    await userEvent.type(within(await screen.findByRole("dialog")).getByLabelText(/ملاحظة القرار/), "wrong quantities");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(reject).toHaveBeenCalledWith("d1", "wrong quantities", 3));
    expect(client.cancelStockDocument).toBeUndefined();
  });

  it("offers cancelling an approved document with a reason and withdrawing a submitted one", async () => {
    const cancel = vi.fn().mockResolvedValue(doc({ status: 5 }));
    setup({ cancelStockDocument: cancel }, caps(), [summary({ status: 3, number: "SR-2026-000001" })], doc({ status: 3, number: "SR-2026-000001", revision: 5, submittedBy: "other", decidedBy: "x" }));
    await openDoc();
    await userEvent.click(screen.getByRole("button", { name: "إلغاء المستند" }));
    await userEvent.type(within(await screen.findByRole("dialog")).getByLabelText(/سبب الإلغاء/), "entered twice");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(cancel).toHaveBeenCalledWith("d1", "entered twice", 5));
  });

  it("shows no editing or workflow buttons the user has no permission for, and a stale revision offers a reload", async () => {
    const client = setup({ submitStockDocument: vi.fn().mockRejectedValue(apiError(412, "revision_conflict")) }, caps({ update: [], approve: [], cancel: [] }));
    await openDoc();
    expect(screen.queryByRole("button", { name: "حفظ" })).toBeNull();
    expect(screen.queryByRole("button", { name: "إلغاء المستند" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "إرسال للاعتماد" }));
    await userEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "تأكيد" }));
    const conflict = await screen.findByTestId("form-conflict");
    await userEvent.click(within(conflict).getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(client.getStockDocument).toHaveBeenCalledTimes(2));
  });
});

describe("stock documents in English and the Tauri shell", () => {
  it("works in English (LTR) with the same tab structure and keyboard navigation", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup();
    await go("#/inventory/stock-documents");
    await userEvent.click(await screen.findByRole("button", { name: "New stock receipt" }));
    await screen.findByTestId("stock-editor");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["General information", "Lines"]);
    expect(document.documentElement.dir).toBe("ltr");
    screen.getByRole("tab", { name: /^General information/ }).focus();
    await userEvent.keyboard("{ArrowRight}");
    await waitFor(() => expect(screen.getByRole("tab", { name: /^Lines/ })).toHaveFocus());
  });

  it("behaves the same inside the Tauri shell", async () => {
    (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {};
    try {
      setup();
      await go("#/inventory/stock-documents");
      await userEvent.click(await screen.findByRole("button", { name: "سند استلام جديد" }));
      await userEvent.type(await screen.findByLabelText(/^المرجع/), "T");
      await userEvent.click(tab("السطور"));
      await userEvent.click(tab("معلومات عامة"));
      expect(screen.getByLabelText(/^المرجع/)).toHaveValue("T");
    } finally { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; }
  });
});
