import type { InventoryBarcode, InventoryCapabilities, InventoryGroup, InventoryItem, InventoryUnit } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const PERMS = [...ALL_PERMISSIONS, "company.items.view", "company.itemgroups.view", "company.units.view"];
const allCaps: InventoryCapabilities = {
  unitsView: true, unitsCreate: true, unitsUpdate: true, unitsActivate: true, groupsView: true, groupsCreate: true, groupsUpdate: true, groupsActivate: true,
  itemsView: true, itemsCreate: true, itemsUpdate: true, itemsActivate: true, barcodesCreate: true, barcodesActivate: true, settingsView: true, settingsConfigure: true,
};
const viewerCaps: InventoryCapabilities = Object.fromEntries(Object.keys(allCaps).map((k) => [k, k.endsWith("View")])) as unknown as InventoryCapabilities;

const stamp = { createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z" };
const unit = (over: Partial<InventoryUnit> = {}): InventoryUnit => ({ id: "u-pcs", code: "PCS", nameAr: "قطعة", nameEn: "Piece", decimals: 0, isActive: true, revision: 1, ...stamp, ...over });
const box = unit({ id: "u-box", code: "BOX", nameAr: "علبة", nameEn: "Box" });
const group = (over: Partial<InventoryGroup> = {}): InventoryGroup => ({ id: "g-food", code: "FOOD", nameAr: "أغذية", nameEn: "Food", parentId: null, valuation: 1, effectiveValuation: 1, isActive: true, revision: 1, ...stamp, ...over });
const item = (over: Partial<InventoryItem> = {}): InventoryItem => ({
  id: "i-1", code: "ITM-000001", nameAr: "تفاح", nameEn: "Apple", itemType: 1, groupId: "g-food", baseUnitId: "u-pcs", purchaseUnitId: null, salesUnitId: null, isLot: false, isSerial: false, hasExpiry: false,
  notes: null, isActive: true, revision: 1, effectiveValuation: 1, alternateUnits: [], ...stamp, ...over,
});
const barcode = (over: Partial<InventoryBarcode> = {}): InventoryBarcode => ({ id: "b-1", itemId: "i-1", unitId: "u-pcs", barcode: "4006381333931", kind: 1, isActive: true, ...stamp, ...over });

const setup = (extra: Record<string, unknown> = {}, caps = allCaps, items: InventoryItem[] = [item()]) => {
  const client = signedIn({
    getInventoryCapabilities: vi.fn().mockResolvedValue(caps),
    listInventoryGroups: vi.fn().mockResolvedValue({ groups: [group(), group({ id: "g-fruit", code: "FRUIT", nameAr: "فواكه", nameEn: "Fruit", parentId: "g-food", valuation: null })] }),
    listInventoryUnits: vi.fn().mockResolvedValue({ units: [unit(), box] }),
    listInventoryItems: vi.fn().mockResolvedValue({ items, total: items.length }),
    getInventoryItem: vi.fn().mockResolvedValue(items[0]),
    listItemBarcodes: vi.fn().mockResolvedValue({ barcodes: [barcode()] }),
    getInventorySettings: vi.fn().mockResolvedValue({ defaultValuation: null, revision: 0 }),
    createInventoryItem: vi.fn(), updateInventoryItem: vi.fn(),
    ...extra,
  }, PERMS);
  renderApp(client);
  return client;
};

const tab = (name: RegExp | string) => screen.getByRole("tab", { name: typeof name === "string" ? new RegExp(`^${name}`) : name });   // the accessible name may carry an error or unsaved indicator
const openNew = async () => {
  await go("#/inventory/items");
  await userEvent.click(await screen.findByRole("button", { name: "مادة جديدة" }));
  await screen.findByTestId("item-editor");
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("items list", () => {
  it("lists items with group, type, valuation and status, and passes the filters to the server", async () => {
    const client = setup();
    await go("#/inventory/items");
    const row = await screen.findByTestId("item-ITM-000001");
    expect(within(row).getByText("تفاح")).toBeInTheDocument();
    expect(within(row).getByText("أغذية")).toBeInTheDocument();
    expect(within(row).getByText("مادة مخزنية")).toBeInTheDocument();
    expect(within(row).getByText("الوارد أولاً صادر أولاً (FIFO)")).toBeInTheDocument();
    expect(screen.getByTestId("no-stock-note")).toHaveTextContent("الكميات والتكاليف");
    await userEvent.type(screen.getByLabelText("بحث"), "4006381333931");
    await waitFor(() => expect(client.listInventoryItems).toHaveBeenLastCalledWith(expect.objectContaining({ search: "4006381333931", offset: 0, limit: 25 })));
    await userEvent.selectOptions(screen.getByLabelText("النوع"), "2");
    await waitFor(() => expect(client.listInventoryItems).toHaveBeenLastCalledWith(expect.objectContaining({ type: 2 })));
  });

  it("shows read-only users the list without any create, edit-save or status controls", async () => {
    setup({}, viewerCaps);
    await go("#/inventory/items");
    await screen.findByTestId("item-ITM-000001");
    expect(screen.queryByRole("button", { name: "مادة جديدة" })).toBeNull();
    expect(screen.queryByRole("button", { name: "إيقاف" })).toBeNull();
    expect(screen.getByTestId("read-only")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "عرض" }));
    await screen.findByTestId("item-editor");
    expect(screen.queryByRole("button", { name: "حفظ" })).toBeNull();
    expect(screen.getByRole("button", { name: "إلغاء" })).toBeInTheDocument();
  });
});

describe("item editor: tabs standard", () => {
  it("organises a new item into named tabs, with Save and Cancel outside the panels and no barcodes tab before the item exists", async () => {
    setup();
    await openNew();
    expect(screen.getByRole("tablist", { name: "أقسام بطاقة المادة" })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["معلومات عامة", "الوحدات والتحويلات", "إعدادات التتبع"]);
    expect(screen.queryByRole("tab", { name: "الباركود" })).toBeNull();
    const actions = screen.getByTestId("item-actions");
    for (const name of ["معلومات عامة", "الوحدات والتحويلات", "إعدادات التتبع"]) {
      await userEvent.click(tab(name));
      expect(within(actions).getByRole("button", { name: "حفظ" })).toBeVisible();
      expect(within(actions).getByRole("button", { name: "إلغاء" })).toBeVisible();
      expect(screen.getByTestId("item-tabs").contains(actions)).toBe(false);
    }
  });

  it("keeps every unsaved value when switching between tabs", async () => {
    setup();
    await openNew();
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Pear");
    await userEvent.selectOptions(screen.getByLabelText(/^المجموعة/), "g-food");
    await userEvent.click(tab("الوحدات والتحويلات"));
    await userEvent.selectOptions(screen.getByLabelText(/^الوحدة الأساسية/), "u-pcs");
    await userEvent.click(screen.getByRole("button", { name: "إضافة وحدة بديلة" }));
    await userEvent.selectOptions(within(screen.getByTestId("alt-0")).getByLabelText("الوحدة"), "u-box");
    await userEvent.type(within(screen.getByTestId("alt-0")).getByLabelText("معامل التحويل"), "12");
    await userEvent.click(tab("إعدادات التتبع"));
    await userEvent.click(screen.getByLabelText("تتبع بالدفعات"));
    await userEvent.click(tab("معلومات عامة"));
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Pear");
    await userEvent.click(tab("الوحدات والتحويلات"));
    expect(within(screen.getByTestId("alt-0")).getByLabelText("معامل التحويل")).toHaveValue("12");
    expect(screen.getByLabelText(/^الوحدة الأساسية/)).toHaveValue("u-pcs");
    await userEvent.click(tab("إعدادات التتبع"));
    expect(screen.getByLabelText("تتبع بالدفعات")).toBeChecked();
  });

  it("marks the tab that has errors, opens it, and never silently saves", async () => {
    const client = setup();
    await openNew();
    await userEvent.click(tab("الوحدات والتحويلات"));
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));                                             // name, group and base unit are missing
    expect(client.createInventoryItem).not.toHaveBeenCalled();
    expect(screen.getByTestId("form-error")).toHaveTextContent("صحّح الأخطاء");
    expect(tab("معلومات عامة")).toHaveTextContent("يحتوي على أخطاء");
    expect(tab("الوحدات والتحويلات")).toHaveTextContent("يحتوي على أخطاء");
    expect(tab("إعدادات التتبع")).not.toHaveTextContent("يحتوي على أخطاء");
    expect(tab("معلومات عامة")).toHaveAttribute("aria-selected", "true");                                          // the first tab with an error is opened
    expect(screen.getAllByText("أدخل اسماً بالعربية أو بالإنجليزية.").length).toBeGreaterThan(0);
  });

  it("hides the tracking tab for a service (no placeholder tab) and sends a service without tracking flags", async () => {
    const created = item({ id: "i-9", code: "ITM-000009", itemType: 2 });
    const client = setup({ createInventoryItem: vi.fn().mockResolvedValue(created) });
    await openNew();
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "صيانة");
    await userEvent.selectOptions(screen.getByLabelText(/^النوع/), "2");
    expect(screen.queryByRole("tab", { name: "إعدادات التتبع" })).toBeNull();
    await userEvent.selectOptions(screen.getByLabelText(/^المجموعة/), "g-food");
    await userEvent.click(tab("الوحدات والتحويلات"));
    await userEvent.selectOptions(screen.getByLabelText(/^الوحدة الأساسية/), "u-pcs");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.createInventoryItem).toHaveBeenCalledTimes(1));
    expect(client.createInventoryItem).toHaveBeenCalledWith(expect.objectContaining({ itemType: 2, code: null, isLot: false, isSerial: false, hasExpiry: false, groupId: "g-food", baseUnitId: "u-pcs", alternateUnits: [] }));
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم الإنشاء");
  });

  it("creates a stock item with alternate units and exact factors, and shows the resolved valuation of the group", async () => {
    const client = setup({ createInventoryItem: vi.fn().mockResolvedValue(item({ id: "i-2" })) });
    await openNew();
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Pear");
    await userEvent.selectOptions(screen.getByLabelText(/^المجموعة/), "g-fruit");
    expect(screen.getByLabelText("طريقة التقييم")).toHaveValue("الوارد أولاً صادر أولاً (FIFO)");                      // FRUIT inherits FOOD
    await userEvent.click(tab("الوحدات والتحويلات"));
    await userEvent.selectOptions(screen.getByLabelText(/^الوحدة الأساسية/), "u-pcs");
    await userEvent.click(screen.getByRole("button", { name: "إضافة وحدة بديلة" }));
    await userEvent.selectOptions(within(screen.getByTestId("alt-0")).getByLabelText("الوحدة"), "u-box");
    await userEvent.type(within(screen.getByTestId("alt-0")).getByLabelText("معامل التحويل"), "12.5");
    await userEvent.selectOptions(screen.getByLabelText("وحدة الشراء الافتراضية"), "u-box");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.createInventoryItem).toHaveBeenCalledTimes(1));
    expect(client.createInventoryItem).toHaveBeenCalledWith(expect.objectContaining({ nameEn: "Pear", groupId: "g-fruit", purchaseUnitId: "u-box", alternateUnits: [{ unitId: "u-box", factor: 12.5 }] }));
  });

  it("refuses a factor that is not an exact decimal and flags the units tab", async () => {
    const client = setup();
    await openNew();
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "X");
    await userEvent.selectOptions(screen.getByLabelText(/^المجموعة/), "g-food");
    await userEvent.click(tab("الوحدات والتحويلات"));
    await userEvent.selectOptions(screen.getByLabelText(/^الوحدة الأساسية/), "u-pcs");
    await userEvent.click(screen.getByRole("button", { name: "إضافة وحدة بديلة" }));
    await userEvent.selectOptions(within(screen.getByTestId("alt-0")).getByLabelText("الوحدة"), "u-box");
    await userEvent.type(within(screen.getByTestId("alt-0")).getByLabelText("معامل التحويل"), "0.00000000001");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(client.createInventoryItem).not.toHaveBeenCalled();
    expect(within(screen.getByTestId("alt-0")).getByText(/معامل غير صالح/)).toBeInTheDocument();
    expect(tab("الوحدات والتحويلات")).toHaveTextContent("يحتوي على أخطاء");
  });

  it("maps a server validation error to its field and tab and keeps everything the user typed", async () => {
    const failing = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "groupId", code: "valuation_method_unresolved" }] }));
    setup({ createInventoryItem: failing });
    await openNew();
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Keep me");
    await userEvent.selectOptions(screen.getByLabelText(/^المجموعة/), "g-food");
    await userEvent.click(tab("الوحدات والتحويلات"));
    await userEvent.selectOptions(screen.getByLabelText(/^الوحدة الأساسية/), "u-pcs");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await screen.findByText("حدّد طريقة التقييم في الشركة أو في مجموعة المواد أولاً.");
    expect(tab("معلومات عامة")).toHaveAttribute("aria-selected", "true");
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Keep me");
  });
});

describe("item editor: an existing item", () => {
  it("shows which item is edited, keeps Save and Cancel on every tab, and saves with the revision as If-Match", async () => {
    const client = setup({ updateInventoryItem: vi.fn().mockResolvedValue(item({ revision: 2 })) });
    await go("#/inventory/items");
    await userEvent.click(await screen.findByRole("button", { name: "تعديل" }));
    await screen.findByTestId("item-editor");
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("ITM-000001");
    expect(screen.getByText("تفاح", { selector: "p" })).toBeInTheDocument();
    expect(screen.getByLabelText("الرمز")).toHaveAttribute("readonly");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["معلومات عامة", "الوحدات والتحويلات", "إعدادات التتبع", "الباركود"]);
    await userEvent.clear(screen.getByLabelText("الاسم بالعربية"));
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "تفاح أحمر");
    await userEvent.click(tab("الباركود"));
    expect(screen.getByRole("button", { name: "حفظ" })).toBeVisible();
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.updateInventoryItem).toHaveBeenCalledWith("i-1", expect.objectContaining({ nameAr: "تفاح أحمر", code: undefined }), 1));
  });

  it("adds a barcode immediately without touching the unsaved edits on the other tabs", async () => {
    const add = vi.fn().mockResolvedValue(barcode({ id: "b-2", barcode: "CARTON-1", kind: 4 }));
    const client = setup({ addItemBarcode: add });
    await go("#/inventory/items");
    await userEvent.click(await screen.findByRole("button", { name: "تعديل" }));
    await screen.findByTestId("item-editor");
    await userEvent.type(screen.getByLabelText("ملاحظات"), "my unsaved note");
    await userEvent.click(tab("الباركود"));
    expect(await within(screen.getByTestId("barcodes-panel")).findByTestId("barcode-4006381333931")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("الباركود", { selector: "input" }), "CARTON-1");
    await userEvent.click(screen.getByRole("button", { name: "إضافة باركود" }));
    await waitFor(() => expect(add).toHaveBeenCalledWith("i-1", { barcode: "CARTON-1", unitId: null }));
    expect(client.updateInventoryItem).not.toHaveBeenCalled();
    await userEvent.click(tab("معلومات عامة"));
    expect(screen.getByLabelText("ملاحظات")).toHaveValue("my unsaved note");
  });

  it("shows a taken barcode as a field error instead of a generic failure", async () => {
    setup({ addItemBarcode: vi.fn().mockRejectedValue(apiError(409, "barcode_taken")) });
    await go("#/inventory/items");
    await userEvent.click(await screen.findByRole("button", { name: "تعديل" }));
    await userEvent.click(tab("الباركود"));
    await userEvent.type(screen.getByLabelText("الباركود", { selector: "input" }), "4006381333931");
    await userEvent.click(screen.getByRole("button", { name: "إضافة باركود" }));
    expect(await screen.findByText(/هذا الباركود مستخدم/)).toBeInTheDocument();
  });

  it("offers to reload when someone else changed the item (412) and keeps the user's edits until then", async () => {
    const latest = item({ revision: 5, nameAr: "تفاح جديد" });
    setup({ updateInventoryItem: vi.fn().mockRejectedValue(apiError(412, "revision_conflict")), getInventoryItem: vi.fn().mockResolvedValue(latest) });
    await go("#/inventory/items");
    await userEvent.click(await screen.findByRole("button", { name: "تعديل" }));
    await userEvent.clear(screen.getByLabelText("الاسم بالعربية"));
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "تعديلي");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    const conflict = await screen.findByTestId("form-conflict");
    expect(screen.getByLabelText("الاسم بالعربية")).toHaveValue("تعديلي");
    await userEvent.click(within(conflict).getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(screen.getByLabelText("الاسم بالعربية")).toHaveValue("تفاح جديد"));
  });

  it("asks before discarding unsaved edits when the editor is closed from the menu", async () => {
    setup();
    await go("#/inventory/items");
    await userEvent.click(await screen.findByRole("button", { name: "تعديل" }));
    await userEvent.type(screen.getByLabelText("ملاحظات"), "x");
    await userEvent.click(screen.getByRole("button", { name: "إلغاء" }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });
});

describe("item editor: English and Desktop shell", () => {
  it("works in English (LTR) with the same tab structure", async () => {
    localStorage.setItem("hasm.locale", "en");
    setup();
    await go("#/inventory/items");
    await userEvent.click(await screen.findByRole("button", { name: "New item" }));
    await screen.findByTestId("item-editor");
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual(["General information", "Units and conversions", "Tracking settings"]);
    expect(document.documentElement.dir).toBe("ltr");
    tab("General information").focus();
    await userEvent.keyboard("{ArrowRight}");
    await waitFor(() => expect(tab("Units and conversions")).toHaveFocus());
  });

  it("behaves the same inside the Tauri shell", async () => {
    (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {};
    try {
      setup();
      await openNew();
      await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "W");
      await userEvent.click(tab("الوحدات والتحويلات"));
      await userEvent.click(tab("معلومات عامة"));
      expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("W");
    } finally { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; }
  });
});

describe("units and item groups screens", () => {
  it("creates a unit with a normalised code and fixed decimals", async () => {
    const client = setup({ createInventoryUnit: vi.fn().mockResolvedValue(unit({ id: "u-new", code: "KG", decimals: 3 })) });
    await go("#/inventory/units");
    await userEvent.click(await screen.findByRole("button", { name: "وحدة جديدة" }));
    await userEvent.type(screen.getByLabelText(/^الرمز/), "kg");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Kilogram");
    await userEvent.clear(screen.getByLabelText(/^الخانات العشرية/));
    await userEvent.type(screen.getByLabelText(/^الخانات العشرية/), "3");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.createInventoryUnit).toHaveBeenCalledWith({ code: "KG", nameAr: null, nameEn: "Kilogram", decimals: 3 }));
  });

  it("shows a taken unit code on the code field", async () => {
    setup({ createInventoryUnit: vi.fn().mockRejectedValue(apiError(409, "unit_code_taken")) });
    await go("#/inventory/units");
    await userEvent.click(await screen.findByRole("button", { name: "وحدة جديدة" }));
    await userEvent.type(screen.getByLabelText(/^الرمز/), "PCS");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Piece");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText("رمز الوحدة مستخدم.")).toBeInTheDocument();
  });

  it("shows the group tree with inherited valuation and saves the company default valuation with the revision", async () => {
    const client = setup({ putInventorySettings: vi.fn().mockResolvedValue({ defaultValuation: 2, revision: 1 }) });
    await go("#/inventory/groups");
    const child = await screen.findByTestId("group-FRUIT");
    expect(within(child).getByText(/موروثة/)).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("طريقة التقييم الافتراضية"), "2");
    await userEvent.click(within(screen.getByText("سياسة التقييم للشركة").closest("section")!).getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(client.putInventorySettings).toHaveBeenCalledWith(2, 0));
  });

  it("creates a group under a parent with an explicit valuation", async () => {
    const client = setup({ createInventoryGroup: vi.fn().mockResolvedValue(group({ id: "g-new" })) });
    await go("#/inventory/groups");
    await userEvent.click(await screen.findByRole("button", { name: "مجموعة جديدة" }));
    await userEvent.type(screen.getByLabelText(/^الرمز/), "dairy");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Dairy");
    await userEvent.selectOptions(screen.getByLabelText("المجموعة الأم"), "g-food");
    await userEvent.selectOptions(screen.getByLabelText("طريقة التقييم", { selector: "#g-valuation" }), "2");
    await userEvent.click(screen.getAllByRole("button", { name: "حفظ" }).at(-1)!);
    await waitFor(() => expect(client.createInventoryGroup).toHaveBeenCalledWith({ code: "DAIRY", nameAr: null, nameEn: "Dairy", parentId: "g-food", valuation: 2 }));
  });
});
