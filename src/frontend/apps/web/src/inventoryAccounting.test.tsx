import type { InventoryAccountingResponse } from "@hasm/api-client";
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

const PERMS = [...ALL_PERMISSIONS, "company.inventoryaccounting.view", "company.inventoryaccounting.configure"];
const accounts = [
  { id: "a1", code: "1301", nameAr: "مخزون البضاعة", nameEn: "Merchandise stock", type: 1 },
  { id: "a2", code: "1302", nameAr: "مخزون المواد الخام", nameEn: "Raw material stock", type: 1 },
  { id: "a3", code: "2101", nameAr: "بضاعة مستلمة غير مفوترة", nameEn: "Goods received not invoiced", type: 2 },
  { id: "a4", code: "5101", nameAr: "تكلفة المبيعات", nameEn: "Cost of sales", type: 5 },
];
const groups = [{ id: "g1", code: "FOOD", nameAr: "أغذية", nameEn: "Food", parentId: null }, { id: "g2", code: "FRUIT", nameAr: "فواكه", nameEn: "Fruit", parentId: "g1" }];
const response = (over: Partial<InventoryAccountingResponse> = {}, canConfigure = true): InventoryAccountingResponse => ({
  configuration: { version: null, versionNo: 0, defaultInventoryAccountId: null, receiptCounterAccountId: null, issueCounterAccountId: null, groups: [] }, canConfigure, accounts, groups, ...over,
});
const configured = response({ configuration: { version: { id: "v1", versionNo: 3, createdAtUtc: "2026-03-01T10:00:00Z", createdBy: "u", note: "n", groupCount: 1, contentHash: "h" }, versionNo: 3, defaultInventoryAccountId: "a1", receiptCounterAccountId: "a3", issueCounterAccountId: "a4", groups: [{ groupId: "g1", accountId: "a2" }] } });

function setup(data: InventoryAccountingResponse, extra: Record<string, unknown> = {}, perms = PERMS) {
  const client = signedIn({
    getInventoryAccounting: vi.fn().mockResolvedValue(data),
    listInventoryAccountingVersions: vi.fn().mockResolvedValue({ versions: data.configuration.version ? [data.configuration.version] : [] }),
    putInventoryAccounting: vi.fn().mockResolvedValue(data.configuration), ...extra,
  }, perms);
  renderApp(client);
  return client;
}
const tab = (n: string) => screen.getByRole("tab", { name: new RegExp(`^${n}`) });

describe("inventory accounting configuration", () => {
  it("shows an unconfigured company with the no-posting note, three tabs and the actions outside the panels", async () => {
    setup(response());
    await go("#/inventory/accounting");
    expect(await screen.findByRole("heading", { name: "حسابات المخزون المحاسبية", level: 1 })).toBeInTheDocument();
    expect(screen.getByTestId("version-chip")).toHaveTextContent("غير مهيّأ");
    expect(screen.getByTestId("no-posting-note")).toHaveTextContent("لا تُرحِّل شيئاً");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["الحسابات الأساسية", "حسابات المجموعات", "النسخ"]);
    expect(screen.getByTestId("accounting-tabs").contains(screen.getByTestId("accounting-actions"))).toBe(false);
    expect(screen.getByRole("button", { name: "حفظ" })).toBeDisabled();                                         // nothing changed yet
  });

  it("offers only asset accounts as inventory accounts and saves a new version with the current version as If-Match", async () => {
    const client = setup(configured);
    await go("#/inventory/accounting");
    await screen.findByTestId("inventory-accounting");
    expect(screen.getByTestId("version-chip")).toHaveTextContent("النسخة 3");
    const def = screen.getByLabelText("حساب المخزون الافتراضي");
    expect(def).toHaveValue("a1");
    expect(within(def).queryByRole("option", { name: /2101/ })).toBeNull();                                      // a liability is never offered as inventory
    expect(within(screen.getByLabelText("الحساب المقابل للاستلام")).getByRole("option", { name: /2101/ })).toBeInTheDocument();
    await userEvent.selectOptions(def, "a2");
    await userEvent.click(tab("حسابات المجموعات"));
    await userEvent.selectOptions(screen.getByLabelText("الحساب FRUIT"), "a1");
    await userEvent.click(tab("الحسابات الأساسية"));
    expect(screen.getByLabelText("حساب المخزون الافتراضي")).toHaveValue("a2");                                   // switching tabs kept the edit
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.putInventoryAccounting).toHaveBeenCalledWith({
      defaultInventoryAccountId: "a2", receiptCounterAccountId: "a3", issueCounterAccountId: "a4", note: null,
      groups: expect.arrayContaining([{ groupId: "g1", accountId: "a2" }, { groupId: "g2", accountId: "a1" }]),
    }, 3));
  });

  it("marks the tab of a server validation error and keeps the edits", async () => {
    setup(configured, { putInventoryAccounting: vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "receiptCounterAccountId", code: "counter_equals_inventory_account" }] })) });
    await go("#/inventory/accounting");
    await screen.findByTestId("inventory-accounting");
    await userEvent.selectOptions(screen.getByLabelText("الحساب المقابل للاستلام"), "a4");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect((await screen.findAllByText("الحساب المقابل لا يجوز أن يكون أحد حسابات المخزون.")).length).toBeGreaterThan(0);
    expect(tab("الحسابات الأساسية")).toHaveTextContent("يحتوي على أخطاء");
    expect(screen.getByLabelText("الحساب المقابل للاستلام")).toHaveValue("a4");
  });

  it("shows a server error on the right group row even when an earlier row was set back to inherit", async () => {
    setup(configured, { putInventoryAccounting: vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "groups[0].accountId", code: "account_not_found" }] })) });
    await go("#/inventory/accounting");
    await screen.findByTestId("inventory-accounting");
    await userEvent.click(tab("حسابات المجموعات"));
    await userEvent.selectOptions(screen.getByLabelText("الحساب FOOD"), "");                                       // the first row is blanked, so it is not submitted
    await userEvent.selectOptions(screen.getByLabelText("الحساب FRUIT"), "a1");                                    // the submitted list is therefore [FRUIT], index 0
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    const message = await screen.findByText("الحساب غير موجود.");
    expect(message.closest("div")?.contains(screen.getByLabelText("الحساب FRUIT"))).toBe(true);
    expect(message.closest("div")?.contains(screen.getByLabelText("الحساب FOOD"))).toBe(false);
  });

  it("offers a reload when the configuration was changed elsewhere (412)", async () => {
    const client = setup(configured, { putInventoryAccounting: vi.fn().mockRejectedValue(apiError(412, "accounting_version_conflict")) });
    await go("#/inventory/accounting");
    await screen.findByTestId("inventory-accounting");
    await userEvent.selectOptions(screen.getByLabelText("الحساب المقابل للإخراج"), "a3");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    const conflict = await screen.findByTestId("conflict");
    await userEvent.click(within(conflict).getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(client.getInventoryAccounting).toHaveBeenCalledTimes(2));
  });

  it("lists the versions and shows a read-only view to a user who cannot configure", async () => {
    setup({ ...configured, canConfigure: false }, {}, [...ALL_PERMISSIONS, "company.inventoryaccounting.view"]);
    await go("#/inventory/accounting");
    await screen.findByTestId("inventory-accounting");
    expect(screen.getByTestId("read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "حفظ" })).toBeNull();
    expect(screen.getByLabelText("حساب المخزون الافتراضي")).toBeDisabled();
    await userEvent.click(tab("النسخ"));
    expect(within(screen.getByTestId("versions")).getByText(/3 \(الحالية\)/)).toBeInTheDocument();
  });

  it("works in English (LTR) and inside the Tauri shell", async () => {
    localStorage.setItem("hasm.locale", "en");
    (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {};
    try {
      setup(configured);
      await go("#/inventory/accounting");
      expect(await screen.findByRole("heading", { name: "Inventory accounting", level: 1 })).toBeInTheDocument();
      expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["Main accounts", "Group accounts", "Versions"]);
      expect(document.documentElement.dir).toBe("ltr");
      expect(within(screen.getByLabelText("Default inventory account")).getByRole("option", { name: "1301 — Merchandise stock" })).toBeInTheDocument();
    } finally { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; }
  });
});
