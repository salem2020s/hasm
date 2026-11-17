import type { StockAdjustment, StockWarehouseRef, Stocktake, StocktakeCapabilities, StocktakeSummary } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, renderApp, signedIn } from "./test-utils";

// Phase 3.8B-2: the adjustment of an approved stocktake on the shared stocktake screen (Web and Desktop), and follow-up M2 (reloaded lines offer every unit of their item).
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const PERMS = [...ALL_PERMISSIONS, "company.stocktake.view", "company.stocktake.create"];
const w1: StockWarehouseRef = { id: "w1", branchId: "b", code: "W1", nameAr: "المستودع الأول", nameEn: "First", isActive: true };
const caps = (over: Partial<StocktakeCapabilities> = {}): StocktakeCapabilities => ({ view: ["w1"], create: ["w1"], update: ["w1"], submit: ["w1"], approve: ["w1"], cancel: ["w1"], adjust: ["w1"], post: ["w1"], reverse: ["w1"], ...over });
const stamp = "2026-03-11T10:00:00Z";
const summary: StocktakeSummary = { id: "s1", status: 3, number: "STK-2026-000001", countDate: "2026-03-11", branchId: "b", warehouseId: "w1", reference: "R1", lineCount: 2, countedLines: 2, varianceLines: 2, revision: 4, createdAtUtc: stamp, updatedAtUtc: stamp };
const units = [{ id: "u-pcs", code: "PCS", nameAr: "حبة", nameEn: "Piece", decimals: 0, factor: 1, isBase: true }, { id: "u-box", code: "BOX", nameAr: "صندوق", nameEn: "Box", decimals: 2, factor: 12, isBase: false }];
const take = (over: Partial<Stocktake> = {}): Stocktake => ({
  id: "s1", status: 3, branchId: "b", warehouseId: "w1", countDate: "2026-03-11", reference: "R1", notes: null, number: "STK-2026-000001", fiscalYearId: "y", snapshotAtUtc: stamp, stale: false,
  createdBy: "u1", createdAtUtc: stamp, submittedBy: "u2", submittedAtUtc: stamp, decidedBy: "u3", decidedAtUtc: stamp, decisionNote: null, cancelledBy: null, cancelledAtUtc: null, cancelReason: null, revision: 4, updatedAtUtc: stamp,
  lines: [
    { lineNo: 1, itemId: "i1", itemCode: "APPLE", itemNameAr: "تفاح", itemNameEn: "Apple", unitId: "u-box", unitCode: "BOX", countedQuantity: 2, factor: 12, countedBaseQuantity: 24, bookQuantity: 30, variance: -6, baseUnitCode: "PCS", reason: "broken", currentBookQuantity: 30, stale: false, units },
    { lineNo: 2, itemId: "i2", itemCode: "PEAR", itemNameAr: "كمثرى", itemNameEn: "Pear", unitId: "u-pcs", unitCode: "PCS", countedQuantity: 5, factor: 1, countedBaseQuantity: 5, bookQuantity: 3, variance: 2, baseUnitCode: "PCS", reason: "found", currentBookQuantity: 3, stale: false, units: [units[0]!] },
  ],
  events: [], adjustmentId: null, adjustmentNumber: null, ...over,
});
const adjustment = (over: Partial<StockAdjustment> = {}): StockAdjustment => ({
  id: "a1", stocktakeId: "s1", stocktakeNumber: "STK-2026-000001", branchId: "b", warehouseId: "w1", adjustmentDate: "2026-03-11", number: "SA-2026-000001", fiscalYearId: "y", createdBy: "u1", createdAtUtc: stamp, replayed: false,
  lines: [
    { lineNo: 1, itemId: "i1", itemCode: "APPLE", itemNameAr: "تفاح", itemNameEn: "Apple", unitId: "u-pcs", unitCode: "PCS", direction: -1, quantity: 6, countedQuantity: 24, bookQuantity: 30, reason: "broken", amount: null },
    { lineNo: 2, itemId: "i2", itemCode: "PEAR", itemNameAr: "كمثرى", itemNameEn: "Pear", unitId: "u-pcs", unitCode: "PCS", direction: 1, quantity: 2, countedQuantity: 5, bookQuantity: 3, reason: "found", amount: null },
  ],
  posting: null, ...over,
});
const posted = adjustment({
  lines: adjustment().lines.map((l) => ({ ...l, amount: l.direction < 0 ? 60 : 21 })),
  posting: { id: "p1", effectiveDate: "2026-03-11", postedAtUtc: stamp, postedBy: "u1", issueOperationId: "o1", receiptOperationId: "o2", accountingVersionId: "v2", journalEntryId: "j1", journalStatus: 1, totalValue: 81, accountingState: 1, reversal: null },
});

function setup(over: Record<string, unknown> = {}, c = caps(), current: Stocktake = take()) {
  const client = signedIn({
    getStocktakeCapabilities: vi.fn().mockResolvedValue(c),
    listStocktakeWarehouses: vi.fn().mockResolvedValue({ warehouses: [w1] }),
    listStocktakes: vi.fn().mockResolvedValue({ stocktakes: [summary], total: 1 }),
    getStocktake: vi.fn().mockResolvedValue(current),
    searchStocktakeItems: vi.fn().mockResolvedValue({ items: [] }),
    updateStocktake: vi.fn().mockResolvedValue(current),
    getStockAdjustment: vi.fn().mockResolvedValue(adjustment()),
    ...over,
  }, PERMS);
  renderApp(client);
  return client;
}
const tab = (name: string) => screen.getByRole("tab", { name: new RegExp(`^${name}`) });
const open = async () => { await go("#/inventory/stocktakes"); await userEvent.click(await screen.findByRole("button", { name: "عرض" })); await screen.findByTestId("stk-editor"); };

describe("stock adjustment", () => {
  it("offers every unit of the item on a reloaded line (follow-up M2) and saves a count in another unit", async () => {
    const client = setup({}, caps(), take({ status: 1, number: null, submittedBy: null, decidedBy: null }));
    await open();
    await userEvent.click(tab("السطور"));
    const unit = within(screen.getByTestId("stk-line-0")).getByLabelText("الوحدة");
    expect(within(unit).getAllByRole("option").map((o) => o.getAttribute("value"))).toEqual(["u-pcs", "u-box"]);   // not only the saved BOX
    await userEvent.selectOptions(unit, "u-pcs");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.updateStocktake).toHaveBeenCalledWith("s1", expect.objectContaining({ lines: expect.arrayContaining([expect.objectContaining({ itemId: "i1", unitId: "u-pcs", countedQuantity: 2 })]) }), 4));
  });

  it("generates the adjustment of an approved stocktake once, with the stocktake revision, and opens its tab", async () => {
    const generated = take({ adjustmentId: "a1", adjustmentNumber: "SA-2026-000001" });
    const getStocktake = vi.fn().mockResolvedValueOnce(take()).mockResolvedValue(generated);
    const client = setup({ getStocktake, generateStockAdjustment: vi.fn().mockResolvedValue(adjustment()) });
    await open();
    expect(screen.queryByRole("tab", { name: /^التسوية/ })).toBeNull();                                               // no adjustment yet, no placeholder tab
    await userEvent.click(screen.getByTestId("stk-adjust"));
    await userEvent.click(await screen.findByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(client.generateStockAdjustment).toHaveBeenCalledWith("s1", 4));
    await waitFor(() => expect(tab("التسوية")).toHaveAttribute("aria-selected", "true"));
    expect(await screen.findByDisplayValue("SA-2026-000001")).toBeInTheDocument();
    expect(screen.queryByTestId("stk-adjust")).toBeNull();                                                              // generated once
  });

  it("offers neither generation without the permission nor cancellation once an adjustment exists", async () => {
    setup({}, caps({ adjust: [] }));
    await open();
    expect(screen.queryByTestId("stk-adjust")).toBeNull();
    expect(within(screen.getByTestId("stk-actions")).getByRole("button", { name: "إلغاء الجرد" })).toBeInTheDocument();
  });

  it("posts the adjustment with a value for every surplus line only; shortages are costed by the server", async () => {
    const client = setup({ postStockAdjustment: vi.fn().mockResolvedValue(posted) }, caps(), take({ adjustmentId: "a1", adjustmentNumber: "SA-2026-000001" }));
    await open();
    expect(within(screen.getByTestId("stk-actions")).queryByRole("button", { name: "إلغاء الجرد" })).toBeNull();      // a stocktake with an adjustment cannot be cancelled
    expect(screen.getByTestId("stk-cancel-blocked")).toBeInTheDocument();
    await userEvent.click(tab("التسوية"));
    const panel = await screen.findByTestId("stk-adjustment");
    expect(within(screen.getByTestId("adj-line-1")).queryByLabelText("المبلغ")).toBeNull();                             // the shortage takes no amount
    await userEvent.click(within(panel).getByTestId("adj-post"));
    expect(await screen.findByTestId("adj-error")).toBeInTheDocument();                                                  // the surplus value is required
    expect(client.postStockAdjustment).not.toHaveBeenCalled();
    await userEvent.type(within(screen.getByTestId("adj-line-2")).getByLabelText("المبلغ"), "21");
    await userEvent.click(within(panel).getByTestId("adj-post"));
    await waitFor(() => expect(client.postStockAdjustment).toHaveBeenCalledWith("a1", [{ lineNo: 2, amount: 21 }]));
    expect(await screen.findByTestId("adj-total")).toHaveValue("81");
    expect(screen.getByTestId("adj-amount-1")).toHaveTextContent("60");
  });

  it("reverses a posted adjustment with a date and a reason when allowed, and offers nothing without the permission", async () => {
    const client = setup({ getStockAdjustment: vi.fn().mockResolvedValue(posted), reverseStockAdjustment: vi.fn().mockResolvedValue({ ...posted, posting: { ...posted.posting!, accountingState: 5, reversal: { id: "r1", issueOperationId: "o3", receiptOperationId: "o4", journalAction: 1, journalEntryId: "j1", effectiveDate: "2026-03-20", reversedAtUtc: stamp, reversedBy: "u1", reason: "recount" } } }) },
      caps(), take({ adjustmentId: "a1", adjustmentNumber: "SA-2026-000001" }));
    await open();
    await userEvent.click(tab("التسوية"));
    await screen.findByTestId("adj-reverse");
    expect(screen.queryByTestId("adj-post")).toBeNull();                                                                 // posted once
    const date = screen.getByLabelText(/^تاريخ العكس/);
    fireEvent.change(date, { target: { value: "2026-03-20" } });
    await userEvent.type(screen.getByLabelText(/^سبب العكس/), "recount");
    await userEvent.click(screen.getByTestId("adj-reverse-button"));
    await waitFor(() => expect(client.reverseStockAdjustment).toHaveBeenCalledWith("a1", "2026-03-20", "recount"));
    expect(await screen.findByTestId("adj-reversed")).toHaveTextContent("recount");
  });
});
