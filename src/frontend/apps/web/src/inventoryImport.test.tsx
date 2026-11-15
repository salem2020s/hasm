import type { ImportBatch, ImportStatus } from "@hasm/api-client";
import { fireEvent, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const PERMS = [...ALL_PERMISSIONS, "company.imports.view", "company.imports.upload", "company.imports.configure", "company.imports.submit", "company.imports.approve", "company.imports.apply", "company.imports.cancel",
  "company.items.create", "company.itemgroups.create", "company.units.create"];
const adapters = {
  adapters: [
    { code: "inventory-units", version: 1, nameAr: "وحدات القياس", nameEn: "Units of measure", canUpload: true },
    { code: "inventory-groups", version: 1, nameAr: "مجموعات المواد", nameEn: "Item groups", canUpload: true },
    { code: "inventory-items", version: 1, nameAr: "المواد والخدمات", nameEn: "Items and services", canUpload: true },
  ],
  limits: { maxFileBytes: 10485760, maxRows: 50000, maxColumns: 50, formats: ["xlsx", "csv"], encodings: ["utf-8"] },
};
const summary = { total: 3, valid: 0, exact: 0, invalid: 3, conflict: 0, resolved: 0, operations: 0, blocking: 3 };
const plan = {
  v: 1, adapter: "inventory-items", adapterVersion: 1, rowsHash: "r", mappingHash: "m", baseline: "b", summary, canApply: false, operationsHash: "o",
  reconciliation: [{ key: "items_to_create", value: "0" }, { key: "barcodes_to_create", value: "0" }, { key: "matched_exactly", value: "0" }],
};
const batch = (status: ImportStatus): ImportBatch => ({
  id: "b1", adapter: "inventory-items", adapterVersion: 1, status, fileName: "items.xlsx", fileKind: "xlsx", fileSize: 120, fileSha256: "abcdef0123456789abcdef", keyId: "k1",
  options: { headers: ["code", "group_code"], headerRow: 1, encoding: "utf-8", delimiter: ",", sheet: 0 }, sheets: [], rowCount: 3, rowsHash: "r", planHash: "0123456789abcdef0123", revision: 3, createdBy: "someone",
  createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", submittedBy: null, submittedAtUtc: null, expiresAtUtc: "2026-02-01T00:00:00Z", terminalAtUtc: null,
  originalAvailable: true, rowsAvailable: true, purgeAfterUtc: null, mapping: null, plan, result: null,
});
const row = (rowNo: number, code: string, detail: string | null, status = 3) => ({ rowNo, cells: ["00" + rowNo, "FOOD"], status, normalized: null, issues: [{ field: "barcodes", code, detail }], resolution: null, operation: null, explanation: null, allowedResolutions: [] });

function render(over: Record<string, unknown> = {}) {
  const current = batch("validated");
  const client = signedIn({
    importAdapters: vi.fn().mockResolvedValue(adapters),
    listImports: vi.fn().mockResolvedValue({ imports: [current] }),
    getImport: vi.fn().mockResolvedValue(current),
    importEvents: vi.fn().mockResolvedValue({ events: [] }),
    importDecisions: vi.fn().mockResolvedValue({ decisions: [] }),
    getImportSettings: vi.fn().mockResolvedValue({ settings: { retentionDays: 30, pendingExpiryDays: 30, revision: 1, configured: true }, can: { configure: true } }),
    importRows: vi.fn().mockResolvedValue({
      headers: ["code", "group_code"], total: 3, offset: 0,
      rows: [row(2, "invalid_check_digit", "4006381333932"), row(3, "barcode_taken", "4006381333931:OLD"), row(4, "record_exists_with_differences", "name_en", 4)],
    }),
    ...over,
  }, PERMS);
  renderApp(client);
  return client;
}

describe("inventory imports on the shared import screen", () => {
  it("offers the three inventory adapters (units, item groups, items) in the upload choice, in Arabic", async () => {
    render();
    await go("#/company/imports");
    const select = await screen.findByLabelText(new RegExp(catalog.ar["imp.adapter"] ?? "imp.adapter"));
    expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual(["وحدات القياس", "مجموعات المواد", "المواد والخدمات"]);
    expect(document.documentElement.dir).toBe("rtl");
  });

  it("shows inventory-specific issues and reconciliation labels translated, never raw codes (Arabic)", async () => {
    render();
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: catalog.ar["imp.open"]! }));
    expect(await screen.findByText(/رقم المراجعة في الباركود غير صحيح \(4006381333932\)/)).toBeInTheDocument();
    expect(screen.getByText(/الباركود مستخدم في مادة أخرى/)).toBeInTheDocument();
    expect(screen.getByText(/السجل موجود بالرمز نفسه ويختلف في: name_en/)).toBeInTheDocument();
    expect(screen.getByText("مواد ستُنشأ")).toBeInTheDocument();
    expect(screen.getByText("باركود سيُنشأ")).toBeInTheDocument();
    expect(screen.queryByText(/invalid_check_digit|barcode_taken|record_exists_with_differences/)).toBeNull();
  });

  it("shows the same screen in English (LTR) with English adapter names and messages", async () => {
    localStorage.setItem("hasm.locale", "en");
    render();
    await go("#/company/imports");
    const select = await screen.findByLabelText(new RegExp(catalog.en["imp.adapter"] ?? "imp.adapter"));
    expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual(["Units of measure", "Item groups", "Items and services"]);
    await userEvent.click(await screen.findByRole("button", { name: catalog.en["imp.open"]! }));
    expect(await screen.findByText(/The barcode check digit is wrong \(4006381333932\)/)).toBeInTheDocument();
    expect(screen.getByText("Items to create")).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
  });

  it("behaves the same inside the Tauri shell", async () => {
    (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {};
    try {
      render();
      await go("#/company/imports");
      await userEvent.click(await screen.findByRole("button", { name: catalog.ar["imp.open"]! }));
      expect(await screen.findByText(/الباركود مستخدم في مادة أخرى/)).toBeInTheDocument();
    } finally { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; }
  });


  it("the units, groups and items screens link to the import screen for users who may upload", async () => {
    const caps = { unitsView: true, unitsCreate: true, groupsView: true, groupsCreate: true, itemsView: true, itemsCreate: true, settingsView: false, settingsConfigure: false };
    const client = signedIn({
      getInventoryCapabilities: vi.fn().mockResolvedValue(caps), listInventoryGroups: vi.fn().mockResolvedValue({ groups: [] }), listInventoryUnits: vi.fn().mockResolvedValue({ units: [] }),
      listInventoryItems: vi.fn().mockResolvedValue({ items: [], total: 0 }),
    }, [...PERMS, "company.items.view", "company.itemgroups.view", "company.units.view"]);
    renderApp(client);
    await go("#/inventory/items");
    await userEvent.click(await screen.findByRole("button", { name: "استيراد من ملف" }));
    expect(window.location.hash).toBe("#/company/imports");
  });

  it("every inventory import message exists in both languages", () => {
    const keys = Object.keys(catalog.ar).filter((k) => /^imp\.(issue|recon)\./.test(k));
    for (const k of ["imp.issue.group_parent_cycle", "imp.issue.valuation_method_unresolved", "imp.issue.barcode_duplicate_in_file", "imp.recon.items_to_create", "imp.recon.units_to_create", "imp.recon.item_groups_to_create"]) {
      expect(keys).toContain(k);
      expect(catalog.en[k]).toBeTruthy();
    }
  });
});
