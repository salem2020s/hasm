import type { StockBalancePage, StockBalanceRow, StockLedgerPage, StockReconciliation, StockWarehouseRef } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const PERMS = [...ALL_PERMISSIONS, "company.stockbalance.view"];
const w1: StockWarehouseRef = { id: "w1", branchId: "b", code: "W1", nameAr: "المستودع الأول", nameEn: "First", isActive: true };
const row: StockBalanceRow = { itemId: "i1", itemCode: "APPLE", itemNameAr: "تفاح", itemNameEn: "Apple", groupId: "g", warehouseId: "w1", warehouseCode: "W1", warehouseNameAr: "المستودع الأول", warehouseNameEn: "First", baseUnitCode: "PCS", method: 2, quantity: 8, value: 120, averageUnitCost: 15 };
const page = (over: Partial<StockBalancePage> = {}): StockBalancePage => ({ balances: [row], total: 1, totalValue: 120, asOf: null, ...over });
const ledger: StockLedgerPage = {
  itemId: "i1", warehouseId: "w1", openingQuantity: 0, openingValue: 0, closingQuantity: 8, closingValue: 120, total: 1,
  rows: [{ seq: 1, effectiveDate: "2026-03-10", kind: 1, direction: 1, quantity: 8, amount: 120, quantityAfter: 8, valueAfter: 120, sourceType: "stock_document", sourceId: "d1", sourceNumber: "SR-1" }],
};
const rec = (difference: number): StockReconciliation => ({ asOf: "2026-03-31", costingValue: 120, ledgerValue: 120 - difference, difference, accounts: [{ accountId: "a1", code: "1301", nameAr: "المخزون", nameEn: "Inventory", ledgerBalance: 120 - difference }] });

function setup(over: Record<string, unknown> = {}) {
  const client = signedIn({
    listStockBalanceWarehouses: vi.fn().mockResolvedValue({ warehouses: [w1] }),
    listStockBalances: vi.fn().mockResolvedValue(page()),
    searchStockBalanceItems: vi.fn().mockResolvedValue({ items: [] }),
    getStockLedger: vi.fn().mockResolvedValue(ledger),
    getStockReconciliation: vi.fn().mockResolvedValue(rec(0)),
    ...over,
  }, PERMS);
  renderApp(client);
  return client;
}

describe("stock balances", () => {
  it("lists balances with quantity, value and total, and passes the filters to the server", async () => {
    const client = setup();
    await go("#/inventory/stock-balances");
    const r = await screen.findByTestId("bal-APPLE-W1");
    expect(within(r).getByText(/8 PCS/)).toBeInTheDocument();
    expect(screen.getByTestId("total-value")).toHaveTextContent("120");
    await userEvent.click(screen.getByLabelText("إظهار الأرصدة الصفرية"));
    await waitFor(() => expect(client.listStockBalances).toHaveBeenLastCalledWith(expect.objectContaining({ includeZero: true, offset: 0, limit: 25 })));
  });

  it("opens the item ledger from a balance row with opening, movements and closing", async () => {
    const client = setup();
    await go("#/inventory/stock-balances");
    await userEvent.click(await screen.findByRole("button", { name: "بطاقة المادة" }));
    await waitFor(() => expect(client.getStockLedger).toHaveBeenCalledWith(expect.objectContaining({ itemId: "i1", warehouseId: "w1" })));
    expect(await screen.findByTestId("ledger-row-1")).toHaveTextContent("SR-1");
    expect(screen.getByTestId("ledger-opening")).toBeInTheDocument();
    expect(screen.getByTestId("ledger-closing")).toHaveTextContent("120");
  });

  it("states agreement with the General Ledger, and shows a difference as an error", async () => {
    const client = setup();
    await go("#/inventory/stock-balances");
    await userEvent.click(await screen.findByRole("tab", { name: /المطابقة/ }));
    await userEvent.click(screen.getByRole("button", { name: "تنفيذ المطابقة" }));
    expect(await screen.findByTestId("rec-verdict")).toHaveTextContent("لا فرق");
    client.getStockReconciliation = vi.fn().mockResolvedValue(rec(5));
    await userEvent.click(screen.getByRole("button", { name: "تنفيذ المطابقة" }));
    await waitFor(() => expect(screen.getByTestId("rec-verdict")).toHaveTextContent("يوجد فرق"));
    expect(screen.getByTestId("rec-difference")).toHaveTextContent("5");
  });
});
