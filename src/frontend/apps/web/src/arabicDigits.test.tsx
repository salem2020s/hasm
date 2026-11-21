import type { StockDocument, StockDocumentCapabilities, StockItemRef, StockWarehouseRef } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { parseDecimalInput } from "./numberInput";
import { allPermissions, readRoutes } from "./screenHarness";
import { renderApp, signedIn } from "./test-utils";

/**
 * Release 1 regression (Arabic/English parity of input): a user typing Arabic-Indic digits (and the Arabic decimal separator) into a
 * quantity, money, rate or year field must have them converted before validation and before anything reaches the server, exactly
 * as for Latin digits. Money and rates are covered where they live (ledger.test, taxes.test, accounts.test); this file covers the
 * numeric fields that had no Arabic-digit test: stock quantity, fiscal year, and the shared parsers (item factor, unit decimals
 * and stocktake counts are in inventory.test.tsx and stocktakes.test.tsx).
 */
const PERMS = allPermissions(readRoutes());
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("shared parser", () => {
  it.each([
    ["٢٫٥", 2.5], ["١٢٣", 123], ["۱۲۳٫۵", 123.5], ["٠٫٧١", 0.71], ["2.5", 2.5], ["2,5", 2.5],
  ])("parses %s as %s", (text, n) => expect(parseDecimalInput(text)).toBe(n));
  it.each(["٥٬٠٠٠", "5,000", "1234,567", "١٢٣٤,٥٦٧", "1e3", "٢ ٣", ""])("refuses the ambiguous %s", (text) => expect(parseDecimalInput(text)).toBeNaN());
});

describe("fiscal year: Arabic-Indic start year", () => {
  async function create(yearText: string) {
    const createFiscalYear = vi.fn().mockResolvedValue({ id: "y9", name: "FY 2027" });
    renderApp(signedIn({
      listFiscalYears: vi.fn().mockResolvedValue({ years: [], today: "2026-11-09", closeChecksImplemented: false,
        can: { create: true, closeYear: false, softClose: false, reopen: false, finalClose: false, requestReopen: false, approveReopen: false, viewRequests: false } }),
      createFiscalYear,
    }, PERMS));
    await go("#/company/fiscal-years");
    await userEvent.click(await screen.findByRole("button", { name: "سنة مالية جديدة" }));
    const year = screen.getByLabelText(/سنة البداية/);
    await userEvent.clear(year);
    await userEvent.type(year, yearText);
    await userEvent.click(screen.getByRole("button", { name: "إنشاء السنة" }));
    return createFiscalYear;
  }

  it("Latin digits create the year with a first-of-month date (control)", async () => {
    const c = await create("2027");
    await waitFor(() => expect(c).toHaveBeenCalledWith(expect.objectContaining({ startDate: "2027-01-01" })));
  });

  it("Arabic-Indic digits are accepted and sent as Latin digits (٢٠٢٧ -> 2027-01-01)", async () => {
    const c = await create("٢٠٢٧");
    await waitFor(() => expect(c).toHaveBeenCalledWith(expect.objectContaining({ startDate: "2027-01-01" })));
  });
});

describe("stock document: Arabic-Indic quantity", () => {
  const w1: StockWarehouseRef = { id: "w1", branchId: "b", code: "W1", nameAr: "المستودع الأول", nameEn: "First", isActive: true };
  const apple: StockItemRef = { id: "i1", code: "APPLE", nameAr: "تفاح", nameEn: "Apple",
    units: [{ id: "u-box", code: "BOX", nameAr: "علبة", nameEn: "Box", decimals: 2, factor: 12, isBase: true }] };
  const caps: StockDocumentCapabilities = { view: ["w1"], create: ["w1"], update: ["w1"], submit: ["w1"], approve: ["w1"], cancel: ["w1"], post: ["w1"], reverse: ["w1"] } as unknown as StockDocumentCapabilities;

  async function type(quantity: string, save = true) {
    const createStockDocument = vi.fn().mockResolvedValue({ id: "d9" } as unknown as StockDocument);
    renderApp(signedIn({
      getStockDocumentCapabilities: vi.fn().mockResolvedValue(caps),
      listStockWarehouses: vi.fn().mockResolvedValue({ warehouses: [w1] }),
      listStockDocuments: vi.fn().mockResolvedValue({ documents: [], total: 0 }),
      searchStockItems: vi.fn().mockResolvedValue({ items: [apple] }),
      createStockDocument,
    }, PERMS));
    await go("#/inventory/stock-documents");
    await userEvent.click(await screen.findByRole("button", { name: "سند استلام جديد" }));
    await screen.findByTestId("stock-editor");
    await userEvent.selectOptions(screen.getByLabelText(/^إلى مستودع/), "w1");
    await userEvent.click(screen.getByRole("tab", { name: /^السطور/ }));
    const line = screen.getByTestId("line-0");
    await userEvent.type(within(line).getByLabelText("ابحث عن مادة"), "app");
    await userEvent.selectOptions(await screen.findByLabelText("اختر المادة"), "i1");
    await userEvent.type(within(line).getByLabelText("الكمية"), quantity);
    if (save) await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    return createStockDocument;
  }

  it("Latin digits are sent as a number (control)", async () => {
    const create = await type("2.5");
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create.mock.calls[0]![0].lines[0].quantity).toBe(2.5);
  });

  it.each([["٢٫٥", "30"], ["2,5", "30"], ["2.5", "30"]])("the base-quantity preview works for %s", async (typed, base) => {
    await type(typed, false);
    expect(screen.getByTestId("base-0")).toHaveTextContent(`${base} BOX`);
  });

  it("Arabic-Indic digits and decimal separator are converted like Latin digits (٢٫٥ -> 2.5)", async () => {
    const create = await type("٢٫٥");
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create.mock.calls[0]![0].lines[0].quantity).toBe(2.5);
  });
});
