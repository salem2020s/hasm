import type { StockItemRef, StockWarehouseRef, Stocktake, StocktakeCapabilities, StocktakeSummary } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn, USER } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const PERMS = [...ALL_PERMISSIONS, "company.stocktake.view", "company.stocktake.create"];
const w1: StockWarehouseRef = { id: "w1", branchId: "b", code: "W1", nameAr: "المستودع الأول", nameEn: "First", isActive: true };
const caps = (over: Partial<StocktakeCapabilities> = {}): StocktakeCapabilities => ({ view: ["w1"], create: ["w1"], update: ["w1"], submit: ["w1"], approve: ["w1"], cancel: ["w1"], ...over });
const pear: StockItemRef = { id: "i2", code: "PEAR", nameAr: "كمثرى", nameEn: "Pear", units: [{ id: "u-kg", code: "KG", nameAr: "كيلو", nameEn: "Kilogram", decimals: 3, factor: 1, isBase: true }] };
const stamp = "2026-03-11T10:00:00Z";
const summary = (over: Partial<StocktakeSummary> = {}): StocktakeSummary => ({
  id: "s1", status: 1, number: null, countDate: "2026-03-11", branchId: "b", warehouseId: "w1", reference: "R1", lineCount: 2, countedLines: 1, varianceLines: 1, revision: 2, createdAtUtc: stamp, updatedAtUtc: stamp, ...over,
});
const take = (over: Partial<Stocktake> = {}): Stocktake => ({
  id: "s1", status: 1, branchId: "b", warehouseId: "w1", countDate: "2026-03-11", reference: "R1", notes: null, number: null, fiscalYearId: null, snapshotAtUtc: stamp, stale: false,
  createdBy: "u1", createdAtUtc: stamp, submittedBy: null, submittedAtUtc: null, decidedBy: null, decidedAtUtc: null, decisionNote: null, cancelledBy: null, cancelledAtUtc: null, cancelReason: null, revision: 2, updatedAtUtc: stamp,
  lines: [
    { lineNo: 1, itemId: "i1", itemCode: "APPLE", itemNameAr: "تفاح", itemNameEn: "Apple", unitId: "u-box", unitCode: "BOX", countedQuantity: 2, factor: 12, countedBaseQuantity: 24, bookQuantity: 30, variance: -6, baseUnitCode: "PCS", reason: "broken", currentBookQuantity: 30, stale: false },
    { lineNo: 2, itemId: "i2", itemCode: "PEAR", itemNameAr: "كمثرى", itemNameEn: "Pear", unitId: "u-kg", unitCode: "KG", countedQuantity: null, factor: 1, countedBaseQuantity: null, bookQuantity: 2.5, variance: null, baseUnitCode: "KG", reason: null, currentBookQuantity: 2.5, stale: false },
  ],
  events: [{ id: 1, kind: 1, fromStatus: null, toStatus: 1, actorUserId: "u1", atUtc: stamp, note: null }, { id: 2, kind: 8, fromStatus: 1, toStatus: 1, actorUserId: "u1", atUtc: stamp, note: null }], ...over,
});

function setup(over: Record<string, unknown> = {}, c = caps(), current: Stocktake = take()) {
  const client = signedIn({
    getStocktakeCapabilities: vi.fn().mockResolvedValue(c),
    listStocktakeWarehouses: vi.fn().mockResolvedValue({ warehouses: [w1] }),
    listStocktakes: vi.fn().mockResolvedValue({ stocktakes: [summary()], total: 1 }),
    getStocktake: vi.fn().mockResolvedValue(current),
    searchStocktakeItems: vi.fn().mockResolvedValue({ items: [pear] }),
    createStocktake: vi.fn(), updateStocktake: vi.fn().mockResolvedValue(current), generateStocktakeSheet: vi.fn().mockResolvedValue(current),
    submitStocktake: vi.fn().mockResolvedValue(current), approveStocktake: vi.fn().mockResolvedValue(current),
    ...over,
  }, PERMS);
  renderApp(client);
  return client;
}
const tab = (name: string) => screen.getByRole("tab", { name: new RegExp(`^${name}`) });
const open = async () => { await go("#/inventory/stocktakes"); await userEvent.click(await screen.findByRole("button", { name: "عرض" })); await screen.findByTestId("stk-editor"); };

describe("stocktake", () => {
  it("lists counts with their progress, states that nothing is posted and passes the filters to the server", async () => {
    const client = setup();
    await go("#/inventory/stocktakes");
    const row = await screen.findByTestId("stk-s1");
    expect(within(row).getByText("1 من 2")).toBeInTheDocument();
    expect(screen.getByTestId("stk-no-posting")).toHaveTextContent("لا يغيّران المخزون");
    await userEvent.selectOptions(screen.getByLabelText("الحالة"), "3");
    await waitFor(() => expect(client.listStocktakes).toHaveBeenLastCalledWith(expect.objectContaining({ status: 3, offset: 0, limit: 25 })));
  });

  it("creates a draft for a chosen warehouse and count date", async () => {
    const client = setup({ createStocktake: vi.fn().mockResolvedValue(take({ id: "s9", lines: [] })) });
    await go("#/inventory/stocktakes");
    await screen.findByTestId("stk-s1");
    await userEvent.click(screen.getByRole("button", { name: "جرد جديد" }));
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["التفاصيل"]);                                   // lines come after the draft exists
    await userEvent.selectOptions(screen.getByLabelText(/^المستودع/), "w1");
    await userEvent.click(screen.getByRole("button", { name: "إنشاء الجرد" }));
    await waitFor(() => expect(client.createStocktake).toHaveBeenCalledWith({ warehouseId: "w1", countDate: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), reference: null, notes: null, lines: [] }));
  });

  it("shows book, counted and variance per line, previews an edited count exactly, keeps edits across tabs and saves the counts", async () => {
    const client = setup();
    await open();
    await userEvent.click(tab("السطور"));
    expect(screen.getByTestId("stk-variance-0")).toHaveTextContent("-6");
    expect(screen.getByTestId("stk-book-1")).toHaveTextContent("2.5 KG");
    await userEvent.type(within(screen.getByTestId("stk-line-1")).getByLabelText("الكمية المعدودة"), "2.505");
    expect(screen.getByTestId("stk-variance-1")).toHaveTextContent("0.005");                                                 // 2.505 - 2.5, no floating-point noise
    expect(screen.getByTestId("generate")).toBeDisabled();                                                                   // save first
    await userEvent.click(tab("التفاصيل"));
    await userEvent.click(tab("السطور"));
    expect(within(screen.getByTestId("stk-line-1")).getByLabelText("الكمية المعدودة")).toHaveValue("2.505");                // switching tabs keeps the input
    await userEvent.type(within(screen.getByTestId("stk-line-1")).getByLabelText("السبب"), "scale");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.updateStocktake).toHaveBeenCalledWith("s1", {
      warehouseId: "w1", countDate: "2026-03-11", reference: "R1", notes: null,
      lines: [{ itemId: "i1", unitId: "u-box", countedQuantity: 2, reason: "broken" }, { itemId: "i2", unitId: "u-kg", countedQuantity: 2.505, reason: "scale" }],
    }, 2));
  });

  it("generates the item list with the revision and shows a server field error on the lines tab", async () => {
    const client = setup({ submitStocktake: vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "lines[1].countedQuantity", code: "required" }] })) });
    await open();
    await userEvent.click(tab("السطور"));
    await userEvent.click(screen.getByTestId("generate"));
    await waitFor(() => expect(client.generateStocktakeSheet).toHaveBeenCalledWith("s1", 2));
    await userEvent.click(within(screen.getByTestId("stk-actions")).getByRole("button", { name: "إرسال للاعتماد" }));
    await userEvent.click(await screen.findByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(client.submitStocktake).toHaveBeenCalledWith("s1", 2));
    expect(await screen.findByTestId("form-error")).toBeInTheDocument();
    expect(tab("السطور")).toHaveAttribute("aria-selected", "true");
  });

  it("marks a stale snapshot, blocks self-approval in the screen, and lets another approver approve", async () => {
    const stale = take({ status: 2, submittedBy: USER.id, stale: true, lines: [{ ...take().lines[0]!, currentBookQuantity: 28, stale: true }] });
    setup({}, caps(), stale);
    await open();
    expect(screen.getByTestId("stk-stale")).toBeInTheDocument();
    expect(screen.getByTestId("stk-approve")).toBeDisabled();
    expect(screen.getByTestId("stk-self-approval")).toBeInTheDocument();
  });

  it("approves a stocktake submitted by someone else", async () => {
    const client = setup({}, caps(), take({ status: 2, submittedBy: "someone-else", revision: 3 }));
    await open();
    await userEvent.click(screen.getByTestId("stk-approve"));
    await waitFor(() => expect(client.approveStocktake).toHaveBeenCalledWith("s1", 3));
  });
});

describe("Arabic-Indic digits in counted quantities", () => {
  it("converts ٢٫٥٠٥ to 2.505 for the preview and for the saved body", async () => {
    const client = setup();
    await open();
    await userEvent.click(tab("السطور"));
    await userEvent.type(within(screen.getByTestId("stk-line-1")).getByLabelText("الكمية المعدودة"), "٢٫٥٠٥");
    expect(screen.getByTestId("stk-variance-1")).toHaveTextContent("0.005");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.updateStocktake).toHaveBeenCalledWith("s1", expect.objectContaining({
      lines: [expect.objectContaining({ countedQuantity: 2 }), expect.objectContaining({ itemId: "i2", countedQuantity: 2.505 })],
    }), 2));
  });
});
