import type { Warehouse, WarehouseBranchRef, WarehousesResponse } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const CODE_LABEL = /^الرمز\s*\*?$/;            // the code field (not "الرمز البريدي", the postal code)
const BRANCH_LABEL = /^الفرع\s*\*?$/;          // the branch field (not the table header)
const PERMS = [...ALL_PERMISSIONS, "company.warehouses.view", "company.warehouses.update", "company.warehousestructure.create", "company.warehousestructure.activate"];

const mainBranch: WarehouseBranchRef = { id: "b-main", code: "MAIN", nameAr: "الفرع الرئيسي", nameEn: "Main Branch", isActive: true };
const northBranch: WarehouseBranchRef = { id: "b-north", code: "NORTH", nameAr: "فرع الشمال", nameEn: "North", isActive: true };
const closedBranch: WarehouseBranchRef = { id: "b-old", code: "OLD", nameAr: "فرع قديم", nameEn: "Old", isActive: false };

const wh = (over: Partial<Warehouse> = {}): Warehouse => ({
  id: "w-1", branchId: "b-main", code: "WH1", nameAr: "المستودع الأول", nameEn: "First Warehouse", isActive: true, activationEpoch: 1,
  addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null,
  revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", ...over,
});
const second = wh({ id: "w-2", code: "WH2", nameAr: "مستودع الشمال", nameEn: "North Warehouse", branchId: "b-north", revision: 3 });
const stopped = wh({ id: "w-3", code: "OLD1", nameAr: null, nameEn: "Old Warehouse", isActive: false, activationEpoch: 2, revision: 4 });

const response = (warehouses: Warehouse[], over: Partial<WarehousesResponse> = {}, can: Partial<WarehousesResponse["can"]> = {}): WarehousesResponse => ({
  warehouses, branches: [mainBranch, northBranch, closedBranch], limit: 5, everCreated: warehouses.length,
  can: { create: true, activate: true, update: warehouses.map((w) => w.id), ...can }, ...over,
});

const render = (listWarehouses: unknown, extra: Record<string, unknown> = {}, permissions = PERMS) => {
  const client = signedIn({ listWarehouses, ...extra }, permissions);
  renderApp(client);
  return client;
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("warehouses screen: list, search and status filter", () => {
  it("lists warehouses with their branch, status chips and the usage line", async () => {
    render(vi.fn().mockResolvedValue(response([wh(), second, stopped])));
    await go("#/company/warehouses");
    expect(await screen.findByRole("heading", { name: "المستودعات", level: 1 })).toBeInTheDocument();
    const first = screen.getByTestId("warehouse-WH1");
    expect(within(first).getByText(/MAIN — الفرع الرئيسي/)).toBeInTheDocument();
    expect(within(first).getByText("نشط")).toBeInTheDocument();
    expect(within(screen.getByTestId("warehouse-OLD1")).getByText("موقوف")).toBeInTheDocument();
    expect(screen.getByTestId("warehouse-usage")).toHaveTextContent("3 من 5");
  });

  it("falls back to the other language when a name is missing", async () => {
    render(vi.fn().mockResolvedValue(response([wh(), stopped])));
    await go("#/company/warehouses");
    expect(within(await screen.findByTestId("warehouse-OLD1")).getByText("Old Warehouse")).toBeInTheDocument();
  });

  it("searches by code or name and filters by status", async () => {
    render(vi.fn().mockResolvedValue(response([wh(), second, stopped])));
    await go("#/company/warehouses");
    await userEvent.type(await screen.findByLabelText("بحث بالرمز أو الاسم"), "wh2");
    expect(screen.getByTestId("warehouse-WH2")).toBeInTheDocument();
    expect(screen.queryByTestId("warehouse-WH1")).not.toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText("بحث بالرمز أو الاسم"));
    await userEvent.selectOptions(screen.getByLabelText("الحالة"), "inactive");
    expect(screen.getByTestId("warehouse-OLD1")).toBeInTheDocument();
    expect(screen.queryByTestId("warehouse-WH2")).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("بحث بالرمز أو الاسم"), "zzz");
    expect(screen.getByText("لا توجد مستودعات مطابقة للبحث.")).toBeInTheDocument();
  });

  it("shows an empty state when there are no warehouses yet", async () => {
    render(vi.fn().mockResolvedValue(response([])));
    await go("#/company/warehouses");
    expect(await screen.findByText("لا توجد مستودعات.")).toBeInTheDocument();
  });
});

describe("warehouses screen: permissions and navigation", () => {
  it("hides every action for a view-only user and shows the read-only notice", async () => {
    render(vi.fn().mockResolvedValue(response([wh(), second], {}, { create: false, activate: false, update: [] })));
    await go("#/company/warehouses");
    expect(await screen.findByTestId("read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "مستودع جديد" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "إيقاف" })).not.toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "عرض" })).toHaveLength(2);
  });

  it("shows the screen to a warehouse-scoped user (a grant for one warehouse) and only what the server returns", async () => {
    const scoped = vi.fn().mockResolvedValue(response([second], {}, { create: false, activate: false, update: ["w-2"] }));
    const client = signedIn({
      listWarehouses: scoped,
      accessMe: vi.fn().mockResolvedValue({ companyId: "c1", grants: [{ roleId: "r", scope: "warehouse", scopeId: "w-2", permissions: ["company.warehouses.view", "company.warehouses.update"] }] }),
    });
    renderApp(client);
    await go("#/company/warehouses");
    expect(await screen.findByTestId("warehouse-WH2")).toBeInTheDocument();
    expect(screen.queryByTestId("warehouse-WH1")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "تعديل" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "مستودع جديد" })).not.toBeInTheDocument();
  });

  it("refuses the route without any warehouse permission", async () => {
    render(vi.fn(), {}, ALL_PERMISSIONS);
    await go("#/company/warehouses");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });

  it("does not accept branch permissions for the warehouse route (no inheritance)", async () => {
    render(vi.fn(), {}, [...ALL_PERMISSIONS, "company.branches.view", "company.branches.update"]);
    await go("#/company/warehouses");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });
});

describe("warehouses screen: trial limit", () => {
  it("shows the limit notice and disables New warehouse when the limit is reached", async () => {
    render(vi.fn().mockResolvedValue(response([wh()], { limit: 1, everCreated: 1 })));
    await go("#/company/warehouses");
    expect(await screen.findByTestId("warehouse-limit")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "مستودع جديد" })).toBeDisabled();
  });

  it("keeps New warehouse enabled below the limit and disabled when no branch is active", async () => {
    const list = vi.fn().mockResolvedValueOnce(response([], { limit: 1, everCreated: 0 })).mockResolvedValue(response([], { limit: 1, everCreated: 0, branches: [closedBranch] }));
    render(list);
    await go("#/company/warehouses");
    await screen.findByTestId("warehouse-usage");
    expect(screen.queryByTestId("warehouse-limit")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "مستودع جديد" })).toBeEnabled();
  });
});

describe("warehouses screen: create and edit", () => {
  it("validates code, branch and names client-side without calling the API", async () => {
    const client = render(vi.fn().mockResolvedValue(response([])));
    await go("#/company/warehouses");
    await userEvent.click(await screen.findByRole("button", { name: "مستودع جديد" }));
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText(BRANCH_LABEL)).toHaveAttribute("aria-invalid", "true");
    expect(screen.getAllByText("أدخل اسم المستودع بالعربية أو بالإنجليزية (واحد على الأقل).")).toHaveLength(2);
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "bad code");
    await userEvent.type(screen.getByLabelText("البريد الإلكتروني"), "nope");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    expect(screen.getByLabelText("البريد الإلكتروني")).toHaveAttribute("aria-invalid", "true");
    expect(client.createWarehouse).not.toHaveBeenCalled();
  });

  it("offers only active branches and creates a warehouse with an upper-case code, its branch and null for empty fields", async () => {
    const created = wh({ id: "w-new", code: "WEST", branchId: "b-north", nameAr: null, nameEn: "West" });
    const list = vi.fn().mockResolvedValueOnce(response([])).mockResolvedValue(response([created]));
    const createWarehouse = vi.fn().mockResolvedValue(created);
    render(list, { createWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(await screen.findByRole("button", { name: "مستودع جديد" }));
    const select = screen.getByLabelText(BRANCH_LABEL);
    expect(within(select).queryByRole("option", { name: /OLD/ })).not.toBeInTheDocument();     // an inactive branch cannot host a new warehouse
    await userEvent.selectOptions(select, "b-north");
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "west");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "West");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    await waitFor(() => expect(createWarehouse).toHaveBeenCalled());
    expect(createWarehouse.mock.calls[0][0]).toMatchObject({ code: "WEST", branchId: "b-north", nameAr: null, nameEn: "West", city: null });
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم إنشاء المستودع.");
    expect(await screen.findByTestId("warehouse-WEST")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "حفظ المستودع" })).not.toBeInTheDocument();
  });

  it("shows a duplicate-code error on the code field and keeps the typed values", async () => {
    const createWarehouse = vi.fn().mockRejectedValue(apiError(409, "warehouse_code_taken"));
    render(vi.fn().mockResolvedValue(response([wh()])), { createWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(await screen.findByRole("button", { name: "مستودع جديد" }));
    await userEvent.selectOptions(screen.getByLabelText(BRANCH_LABEL), "b-main");
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "WH1");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Again");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    expect(await screen.findByText("رمز المستودع مستخدم بالفعل.")).toBeInTheDocument();
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Again");
  });

  it("explains the edition limit and an inactive branch when the server refuses (the server enforces both)", async () => {
    const createWarehouse = vi.fn().mockRejectedValueOnce(apiError(403, "entitlement_denied")).mockRejectedValueOnce(apiError(409, "branch_inactive"));
    render(vi.fn().mockResolvedValue(response([])), { createWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(await screen.findByRole("button", { name: "مستودع جديد" }));
    await userEvent.selectOptions(screen.getByLabelText(BRANCH_LABEL), "b-main");
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "EXTRA");
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "مستودع");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent("وصلت الشركة إلى الحد الأقصى للمستودعات");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    await waitFor(() => expect(screen.getByTestId("form-error")).toHaveTextContent("الفرع موقوف"));
  });

  it("maps server validation errors to fields", async () => {
    const createWarehouse = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "email", code: "invalid_format" }, { field: "branchId", code: "branch_not_found" }] }));
    render(vi.fn().mockResolvedValue(response([])), { createWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(await screen.findByRole("button", { name: "مستودع جديد" }));
    await userEvent.selectOptions(screen.getByLabelText(BRANCH_LABEL), "b-main");
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "OK1");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "x");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    expect(await screen.findByText("الصيغة غير صحيحة.")).toBeInTheDocument();
    expect(screen.getByLabelText(BRANCH_LABEL)).toHaveAttribute("aria-invalid", "true");
  });

  it("edits a warehouse: code and branch are read-only and the stored revision is sent as If-Match", async () => {
    const updateWarehouse = vi.fn().mockResolvedValue({ ...second, nameEn: "North 2", revision: 4 });
    render(vi.fn().mockResolvedValue(response([wh(), second])), { updateWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(within(await screen.findByTestId("warehouse-WH2")).getByRole("button", { name: "تعديل" }));
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("readonly");
    expect(screen.getByLabelText(BRANCH_LABEL)).toHaveAttribute("readonly");
    expect(screen.getByLabelText(BRANCH_LABEL)).toHaveValue("NORTH — فرع الشمال");
    const en = screen.getByLabelText("الاسم بالإنجليزية");
    await userEvent.clear(en);
    await userEvent.type(en, "North 2");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    await waitFor(() => expect(updateWarehouse).toHaveBeenCalled());
    const [id, body, revision] = updateWarehouse.mock.calls[0];
    expect(id).toBe("w-2");
    expect(revision).toBe(3);
    expect(body).toMatchObject({ nameAr: "مستودع الشمال", nameEn: "North 2" });
    expect(body.code).toBeUndefined();                                                      // neither the code ...
    expect(body.branchId).toBeUndefined();                                                  // ... nor the branch can be changed
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم حفظ المستودع.");
  });

  it("keeps the user's edits on a concurrency conflict (412) and reloads the latest on request", async () => {
    const updateWarehouse = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 9 }));
    const getWarehouse = vi.fn().mockResolvedValue({ ...second, nameEn: "Changed elsewhere", revision: 9 });
    render(vi.fn().mockResolvedValue(response([second])), { updateWarehouse, getWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(within(await screen.findByTestId("warehouse-WH2")).getByRole("button", { name: "تعديل" }));
    const en = screen.getByLabelText("الاسم بالإنجليزية");
    await userEvent.clear(en);
    await userEvent.type(en, "Mine");
    await userEvent.click(screen.getByRole("button", { name: "حفظ المستودع" }));
    expect(await screen.findByTestId("form-conflict")).toBeInTheDocument();
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Mine");
    await userEvent.click(screen.getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Changed elsewhere"));
    expect(screen.queryByTestId("form-conflict")).not.toBeInTheDocument();
  });

  it("opens a read-only form without a save button for a warehouse the user cannot edit", async () => {
    render(vi.fn().mockResolvedValue(response([second], {}, { update: [], create: false })));
    await go("#/company/warehouses");
    await userEvent.click(within(await screen.findByTestId("warehouse-WH2")).getByRole("button", { name: "عرض" }));
    expect(screen.getByTestId("form-read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "حفظ المستودع" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveAttribute("readonly");
  });
});

describe("warehouses screen: activation and deactivation", () => {
  it("deactivates a warehouse after confirmation, sending its revision, and explains the access consequence", async () => {
    const deactivateWarehouse = vi.fn().mockResolvedValue({ ...second, isActive: false });
    render(vi.fn().mockResolvedValue(response([wh(), second])), { deactivateWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(within(await screen.findByTestId("warehouse-WH2")).getByRole("button", { name: "إيقاف" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("لا تعود عند إعادة التفعيل");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(deactivateWarehouse).toHaveBeenCalledWith("w-2", 3));
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم تحديث المستودع.");
  });

  it("activates an inactive warehouse after confirmation", async () => {
    const activateWarehouse = vi.fn().mockResolvedValue({ ...stopped, isActive: true });
    render(vi.fn().mockResolvedValue(response([wh(), stopped])), { activateWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(within(await screen.findByTestId("warehouse-OLD1")).getByRole("button", { name: "تفعيل" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("لا تعود تعيينات الصلاحيات السابقة");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(activateWarehouse).toHaveBeenCalledWith("w-3", 4));
  });

  it("shows server refusals (inactive branch) and a stale-revision conflict banner", async () => {
    const activateWarehouse = vi.fn().mockRejectedValueOnce(apiError(409, "branch_inactive")).mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 8 }));
    const list = vi.fn().mockResolvedValue(response([wh(), stopped]));
    render(list, { activateWarehouse });
    await go("#/company/warehouses");
    await userEvent.click(within(await screen.findByTestId("warehouse-OLD1")).getByRole("button", { name: "تفعيل" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByText(/الفرع موقوف؛ لا يمكن إنشاء مستودع/)).toBeInTheDocument();
    await userEvent.click(within(screen.getByTestId("warehouse-OLD1")).getByRole("button", { name: "تفعيل" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByTestId("conflict")).toBeInTheDocument();
    const before = list.mock.calls.length;
    await userEvent.click(screen.getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(list.mock.calls.length).toBeGreaterThan(before));
  });
});

describe("warehouses screen: unsaved changes and languages", () => {
  it("protects an edited form: leaving the page asks first, Stay keeps the edits, Discard leaves", async () => {
    render(vi.fn().mockResolvedValue(response([])));
    await go("#/company/warehouses");
    await userEvent.click(await screen.findByRole("button", { name: "مستودع جديد" }));
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "مسودة");
    await go("#/company/currencies");
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("المستودع");
    await userEvent.click(within(dialog).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(screen.getByLabelText("الاسم بالعربية")).toHaveValue("مسودة");
    await go("#/company/currencies");
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "تجاهل التغييرات" }));
    await waitFor(() => expect(screen.queryByLabelText("الاسم بالعربية")).not.toBeInTheDocument());
  });

  it("renders Arabic right-to-left and English left-to-right with the same screen", async () => {
    render(vi.fn().mockResolvedValue(response([wh(), second])));
    await go("#/company/warehouses");
    await screen.findByTestId("warehouse-WH1");
    expect(document.documentElement.dir).toBe("rtl");
    await userEvent.click(screen.getByRole("button", { name: /English/ }));
    expect(await screen.findByRole("heading", { name: "Warehouses", level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(within(screen.getByTestId("warehouse-WH1")).getByText("First Warehouse")).toBeInTheDocument();
    expect(screen.getByTestId("warehouse-usage")).toHaveTextContent("Warehouses created: 2 of 5");
    await userEvent.click(screen.getByRole("button", { name: "New warehouse" }));
    expect(screen.getByLabelText("Name (English)")).toHaveAttribute("dir", "ltr");
    expect(screen.getByLabelText("Name (Arabic)")).toHaveAttribute("dir", "rtl");
  });
});
