import type { NegativeException, NegativeItemFlag, NegativeObligation, StockDocument, StockDocumentCapabilities, StockDocumentSummary, StockItemRef, StockWarehouseRef } from "@hasm/api-client";
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

const PERMS = [...ALL_PERMISSIONS, "company.stockdocs.view", "company.inventorysettings.view", "company.inventorysettings.configure", "company.inventoryaccounting.view"];
const settings = (over = {}) => ({ settings: { versionNo: 0, enabled: false, requireSecondApprover: false, note: null, createdBy: null, createdAtUtc: null, ...over }, canConfigure: true });
const flag = (over: Partial<NegativeItemFlag> = {}): NegativeItemFlag => ({
  itemId: "i1", code: "AVG1", nameAr: "مادة متوسط", nameEn: "Average item", eligible: false, effectiveValuation: 2, tracked: false, isActive: true, hasOpenObligation: false, note: null, changedBy: null, changedAtUtc: null, ...over,
});
const obligation = (over: Partial<NegativeObligation> = {}): NegativeObligation => ({
  id: "o1", seq: 1, itemId: "i1", itemCode: "AVG1", itemNameAr: "مادة متوسط", itemNameEn: "Average item", warehouseId: "w1", documentId: "d1", documentNumber: "SI-2026-000001", lineNo: 1, effectiveDate: "2026-03-12",
  originalQuantity: 7, provisionalTotal: 35, openQuantity: 3, openProvisional: 15, source: 1, justification: null, status: 1, ageDays: 5, journalEntryId: "j1", journalStatus: 1, obligationAccountId: "a", varianceAccountId: "v",
  settlements: [{ id: "s1", receiptPostingId: "p2", receiptDocumentId: "d2", receiptNumber: "SR-2026-000002", receiptLineNo: 1, effectiveDate: "2026-03-13", matchedQuantity: 4, provisionalAmount: 20, actualAmount: 24, difference: 4, journalEntryId: "j2", reversesSettlementId: null, reversed: false }],
  ...over,
});

function setupPage(over: Record<string, unknown> = {}, perms = PERMS) {
  const client = signedIn({
    getNegativeStockSettings: vi.fn().mockResolvedValue(settings()),
    putNegativeStockSettings: vi.fn().mockResolvedValue(settings({ versionNo: 1, enabled: true }).settings),
    listNegativeItemFlags: vi.fn().mockResolvedValue({ items: [flag(), flag({ itemId: "i2", code: "FIFO1", effectiveValuation: 1 }), flag({ itemId: "i3", code: "AVG2", eligible: true, hasOpenObligation: true })] }),
    setNegativeItemFlag: vi.fn().mockResolvedValue(flag({ eligible: true })),
    listNegativeObligations: vi.fn().mockResolvedValue({ obligations: [obligation()] }),
    getNegativeCloseReport: vi.fn().mockResolvedValue({ periodEnd: "2026-03-31", open: [obligation()], expectedObligationBalance: 15, ledgerObligationBalance: 15, settlementDifferences: 4, ledgerVariance: 4, failedSettlementJournals: 0, reconciled: true, blocksFinalClose: true }),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}
const tab = (n: string) => screen.getByRole("tab", { name: new RegExp(`^${n}`) });

describe("negative-stock exception screen", () => {
  it("is organised in four tabs by business meaning and saves the enablement as a new version with the version as If-Match", async () => {
    const client = setupPage();
    await go("#/inventory/negative-stock");
    expect(await screen.findByRole("heading", { name: "استثناء المخزون السالب", level: 1 })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["التفعيل", "المواد المؤهلة", "الالتزامات", "تقرير الإقفال"]);
    expect(screen.getByTestId("neg-note")).toHaveTextContent("ممنوع افتراضياً");
    expect(await screen.findByTestId("neg-version")).toHaveTextContent("غير مفعّل");
    const enabled = screen.getByLabelText("تفعيل الاستثناء للشركة");
    expect(enabled).not.toBeChecked();
    await userEvent.click(enabled);
    await userEvent.click(tab("المواد المؤهلة"));
    await userEvent.click(tab("التفعيل"));
    expect(screen.getByLabelText("تفعيل الاستثناء للشركة")).toBeChecked();                                      // switching tabs keeps the input
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.putNegativeStockSettings).toHaveBeenCalledWith({ enabled: true, requireSecondApprover: false, note: null }, 0));
  });

  it("offers eligibility only to active untracked moving-average items and never removes it while an obligation is open", async () => {
    const client = setupPage();
    await go("#/inventory/negative-stock");
    await userEvent.click(await screen.findByRole("tab", { name: /^المواد المؤهلة/ }));
    const avg = await screen.findByTestId("neg-item-AVG1");
    expect(within(screen.getByTestId("neg-item-FIFO1")).queryByRole("button")).toBeNull();
    expect(within(screen.getByTestId("neg-item-AVG2")).getByRole("button", { name: "إلغاء التأهيل" })).toBeDisabled();
    await userEvent.click(within(avg).getByRole("button", { name: "تأهيل" }));
    await waitFor(() => expect(client.setNegativeItemFlag).toHaveBeenCalledWith("i1", true, null));
    expect(await screen.findByTestId("neg-eligible-AVG1")).toHaveTextContent("✓");
  });

  it("lists obligations with their open figures and settlements, and the close report says what blocks the Final Close", async () => {
    const client = setupPage();
    await go("#/inventory/negative-stock");
    await userEvent.click(await screen.findByRole("tab", { name: /^الالتزامات/ }));
    const row = await screen.findByTestId("neg-obligation-SI-2026-000001-1");
    expect(within(row).getByTestId("neg-open-qty")).toHaveTextContent("3");
    expect(row).toHaveTextContent("SR-2026-000002");
    await userEvent.selectOptions(screen.getByLabelText("عرض"), "all");
    await waitFor(() => expect(client.listNegativeObligations).toHaveBeenLastCalledWith("all"));
    await userEvent.click(tab("تقرير الإقفال"));
    await userEvent.clear(screen.getByLabelText("نهاية الفترة"));
    await userEvent.type(screen.getByLabelText("نهاية الفترة"), "2026-03-31");
    await userEvent.click(screen.getByRole("button", { name: "عرض التقرير" }));
    await waitFor(() => expect(client.getNegativeCloseReport).toHaveBeenCalledWith("2026-03-31"));
    expect(await screen.findByTestId("neg-blocks")).toHaveTextContent("يمنع الإقفال النهائي");
    expect(screen.getByTestId("neg-reconciled")).toHaveTextContent("متطابق");
    expect(screen.getByTestId("neg-soft-warning")).toBeInTheDocument();
  });

  it("shows only the obligations to a warehouse user without the settings or report permissions, and works in English", async () => {
    localStorage.setItem("hasm.locale", "en");
    setupPage({}, [...ALL_PERMISSIONS, "company.stockdocs.view"]);
    await go("#/inventory/negative-stock");
    expect(await screen.findByRole("heading", { name: "Negative-stock exception", level: 1 })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["Obligations"]);
    expect(await screen.findByTestId("neg-obligation-SI-2026-000001-1")).toHaveTextContent("Last average");
  });
});

// ---------------------------------------------------------------------------------------------------------------- the exception on the issue document
const w1: StockWarehouseRef = { id: "w1", branchId: "b", code: "W1", nameAr: "المستودع الأول", nameEn: "First", isActive: true };
const caps: StockDocumentCapabilities = { view: ["w1"], create: ["w1"], update: ["w1"], submit: ["w1"], approve: ["w1"], cancel: ["w1"], post: ["w1"], reverse: ["w1"] };
const item: StockItemRef = { id: "i1", code: "AVG1", nameAr: "مادة متوسط", nameEn: "Average item", units: [{ id: "u", code: "PCS", nameAr: "قطعة", nameEn: "Piece", decimals: 0, factor: 1, isBase: true }] };
const stamp = "2026-03-01T10:00:00Z";
const summary: StockDocumentSummary = { id: "d1", docType: 2, status: 3, number: "SI-2026-000001", documentDate: "2026-03-12", branchId: "b", sourceWarehouseId: "w1", targetWarehouseId: null, reference: null, lineCount: 2, revision: 5, createdAtUtc: stamp, updatedAtUtc: stamp };
const issue: StockDocument = {
  id: "d1", docType: 2, status: 3, branchId: "b", sourceWarehouseId: "w1", targetWarehouseId: null, documentDate: "2026-03-12", reference: null, notes: null, number: "SI-2026-000001", fiscalYearId: null, createdBy: "other", createdAtUtc: stamp,
  submittedBy: "other", submittedAtUtc: stamp, decidedBy: "x", decidedAtUtc: stamp, decisionNote: null, cancelledBy: null, cancelledAtUtc: null, cancelReason: null, revision: 5, updatedAtUtc: stamp,
  lines: [
    { lineNo: 1, itemId: "i1", itemCode: "AVG1", itemNameAr: "مادة متوسط", itemNameEn: "Average item", unitId: "u", unitCode: "PCS", quantity: 10, factor: 1, baseQuantity: 10, baseUnitCode: "PCS", notes: null },
    { lineNo: 2, itemId: "i2", itemCode: "AVG2", itemNameAr: "مادة ثانية", itemNameEn: "Second item", unitId: "u", unitCode: "PCS", quantity: 4, factor: 1, baseQuantity: 4, baseUnitCode: "PCS", notes: null },
  ],
  events: [],
};
const exception = (over: Partial<NegativeException> = {}): NegativeException => ({
  enabled: true, requireSecondApprover: false, approvalsRequired: 1, validApprovals: 0, posted: false, approvals: [],
  plan: {
    documentId: "d1", documentRevision: 5, documentDate: "2026-03-12", needed: true, accountingVersionId: "v", obligationAccountId: "o", varianceAccountId: "k", counterAccountId: "k", problems: ["provisional_cost_unavailable"], hash: "h",
    lines: [
      { lineNo: 1, itemId: "i1", warehouseId: "w1", quantity: 10, onHand: 3, covered: 3, uncovered: 7, source: 1, sourceEntryId: null, sourceQuantity: 3, sourceValue: 15, provisionalTotal: 35, provisionalUnitCost: 5, justification: null, problem: null },
      { lineNo: 2, itemId: "i2", warehouseId: "w1", quantity: 4, onHand: 0, covered: 0, uncovered: 4, source: null, sourceEntryId: null, sourceQuantity: null, sourceValue: null, provisionalTotal: null, provisionalUnitCost: null, justification: null, problem: "provisional_cost_unavailable" },
    ],
  },
  ...over,
});

function setupDoc(over: Record<string, unknown> = {}) {
  const client = signedIn({
    getStockDocumentCapabilities: vi.fn().mockResolvedValue(caps),
    listStockWarehouses: vi.fn().mockResolvedValue({ warehouses: [w1] }),
    listStockDocuments: vi.fn().mockResolvedValue({ documents: [summary], total: 1 }),
    getStockDocument: vi.fn().mockResolvedValue(issue),
    searchStockItems: vi.fn().mockResolvedValue({ items: [item] }),
    getStockPosting: vi.fn().mockResolvedValue(null),
    getNegativeException: vi.fn().mockResolvedValue({ exception: exception(), canApprove: true }),
    approveNegativeException: vi.fn().mockResolvedValue(exception({ validApprovals: 1 })),
    ...over,
  }, [...ALL_PERMISSIONS, "company.stockdocs.view", "company.stockdocs.post", "company.stockdocs.negativeexception"]);
  renderApp(client);
  return client;
}
const openIssue = async () => {
  await go("#/inventory/stock-documents");
  await userEvent.click(await screen.findByRole("button", { name: "عرض" }));
  await screen.findByTestId("stock-editor");
  await userEvent.click(screen.getByRole("tab", { name: /^الترحيل/ }));
};

describe("negative-stock exception on an issue document", () => {
  it("shows the server's split, asks a justified manual value for a line without a cost source and approves with the document revision as If-Match", async () => {
    const client = setupDoc();
    await openIssue();
    const panel = await screen.findByTestId("neg-exception");
    expect(within(panel).getByTestId("neg-uncovered-1")).toHaveTextContent("7");
    expect(within(panel).getByTestId("neg-total-1")).toHaveTextContent("35");
    expect(within(panel).getByTestId("neg-problem-2")).toHaveTextContent("أدخل قيمة يدوية مع تبرير");
    expect(within(panel).getByTestId("neg-approvals")).toHaveTextContent("0 من 1");
    await userEvent.click(within(panel).getByRole("button", { name: "الموافقة على الاستثناء" }));
    expect(await within(panel).findByTestId("neg-error")).toBeInTheDocument();                                   // reason and the manual value are required first
    expect(client.approveNegativeException).not.toHaveBeenCalled();
    await userEvent.type(within(panel).getByLabelText("القيمة المؤقتة اليدوية #2"), "20");
    await userEvent.type(within(panel).getByLabelText("تبرير القيمة اليدوية #2"), "supplier quote");
    await userEvent.type(within(panel).getByLabelText("سبب الموافقة"), "urgent dispatch");
    await userEvent.click(within(panel).getByRole("button", { name: "الموافقة على الاستثناء" }));
    await waitFor(() => expect(client.approveNegativeException).toHaveBeenCalledWith("d1", 5, "urgent dispatch", [{ lineNo: 2, amount: 20, justification: "supplier quote" }]));
  });

  it("explains a disabled company and a server refusal in words, and offers no approval without the permission", async () => {
    setupDoc({
      getNegativeException: vi.fn().mockResolvedValue({ exception: exception({ enabled: false }), canApprove: true }),
      approveNegativeException: vi.fn().mockRejectedValue(apiError(409, "negative_exception_self_approval")),
    });
    await openIssue();
    expect(await screen.findByTestId("neg-disabled")).toHaveTextContent("غير مفعّل");
    expect(screen.queryByTestId("neg-approve-form")).toBeNull();
  });

  it("shows a server refusal of the approval in words", async () => {
    setupDoc({ approveNegativeException: vi.fn().mockRejectedValue(apiError(409, "negative_exception_self_approval")), getNegativeException: vi.fn().mockResolvedValue({ exception: exception({ plan: { ...exception().plan, problems: [], lines: [exception().plan.lines[0]] } }), canApprove: true }) });
    await openIssue();
    const panel = await screen.findByTestId("neg-exception");
    await userEvent.type(within(panel).getByLabelText("سبب الموافقة"), "urgent");
    await userEvent.click(within(panel).getByRole("button", { name: "الموافقة على الاستثناء" }));
    expect(await within(panel).findByTestId("neg-error")).toHaveTextContent("لا يوافق على الاستثناء منشئ المستند ولا من يرحّله");
  });
});
