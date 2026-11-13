import type { Branch, BranchesResponse } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const CODE_LABEL = /^الرمز\s*\*?$/;   // the code field (not "الرمز البريدي", the postal code)
const PERMS = [...ALL_PERMISSIONS, "company.branches.view", "company.branches.update", "company.branchstructure.create", "company.branchstructure.activate", "company.branchstructure.designate"];

const branch = (over: Partial<Branch> = {}): Branch => ({
  id: "b-main", code: "MAIN", nameAr: "الفرع الرئيسي", nameEn: "Main Branch", isActive: true, isMain: true, activationEpoch: 1, seeded: true,
  addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null,
  revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", ...over,
});
const north = branch({ id: "b-north", code: "NORTH", nameAr: "فرع الشمال", nameEn: "North", isMain: false, seeded: false, revision: 3 });
const closed = branch({ id: "b-old", code: "OLD", nameAr: null, nameEn: "Old Branch", isActive: false, isMain: false, seeded: false, activationEpoch: 2, revision: 4 });

const response = (branches: Branch[], over: Partial<BranchesResponse> = {}, can: Partial<BranchesResponse["can"]> = {}): BranchesResponse => ({
  branches, limit: 5, everCreated: branches.length,
  can: { create: true, activate: true, designate: true, update: branches.map((b) => b.id), ...can }, ...over,
});

const render = (listBranches: unknown, extra: Record<string, unknown> = {}, permissions = PERMS) => {
  const client = signedIn({ listBranches, ...extra }, permissions);
  renderApp(client);
  return client;
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("branches screen: list, search and status filter", () => {
  it("lists branches with the main and status chips and the usage line", async () => {
    render(vi.fn().mockResolvedValue(response([branch(), north, closed])));
    await go("#/company/branches");
    expect(await screen.findByRole("heading", { name: "الفروع", level: 1 })).toBeInTheDocument();
    const main = screen.getByTestId("branch-MAIN");
    expect(within(main).getByText("رئيسي")).toBeInTheDocument();
    expect(within(main).getByText("نشط")).toBeInTheDocument();
    expect(within(screen.getByTestId("branch-OLD")).getByText("موقوف")).toBeInTheDocument();
    expect(screen.getByTestId("branch-usage")).toHaveTextContent("3 من 5");
    expect(screen.getByText("أُنشئ الفرع الرئيسي تلقائياً مع الشركة.")).toBeInTheDocument();
  });

  it("falls back to the other language when a name is missing", async () => {
    render(vi.fn().mockResolvedValue(response([branch(), closed])));
    await go("#/company/branches");
    expect(within(await screen.findByTestId("branch-OLD")).getByText("Old Branch")).toBeInTheDocument();   // Arabic UI, English-only name
  });

  it("searches by code or name and filters by status", async () => {
    render(vi.fn().mockResolvedValue(response([branch(), north, closed])));
    await go("#/company/branches");
    await userEvent.type(await screen.findByLabelText("بحث بالرمز أو الاسم"), "nor");
    expect(screen.getByTestId("branch-NORTH")).toBeInTheDocument();
    expect(screen.queryByTestId("branch-MAIN")).not.toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText("بحث بالرمز أو الاسم"));
    await userEvent.selectOptions(screen.getByLabelText("الحالة"), "inactive");
    expect(screen.getByTestId("branch-OLD")).toBeInTheDocument();
    expect(screen.queryByTestId("branch-NORTH")).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("بحث بالرمز أو الاسم"), "zzz");
    expect(screen.getByText("لا توجد فروع مطابقة للبحث.")).toBeInTheDocument();
  });
});

describe("branches screen: permissions and navigation", () => {
  it("hides every action for a view-only user and shows the read-only notice", async () => {
    render(vi.fn().mockResolvedValue(response([branch(), north], {}, { create: false, activate: false, designate: false, update: [] })));
    await go("#/company/branches");
    expect(await screen.findByTestId("read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "فرع جديد" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "إيقاف" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "تعيين كفرع رئيسي" })).not.toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "عرض" })).toHaveLength(2);
  });

  it("shows the screen to a branch-scoped user (a grant for one branch) and only what the server returns", async () => {
    const scoped = vi.fn().mockResolvedValue(response([north], {}, { create: false, activate: false, designate: false, update: ["b-north"] }));
    const client = signedIn({
      listBranches: scoped,
      accessMe: vi.fn().mockResolvedValue({ companyId: "c1", grants: [{ roleId: "r", scope: "branch", scopeId: "b-north", permissions: ["company.branches.view", "company.branches.update"] }] }),
    });
    renderApp(client);
    await go("#/company/branches");
    expect(await screen.findByTestId("branch-NORTH")).toBeInTheDocument();
    expect(screen.queryByTestId("branch-MAIN")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "تعديل" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "فرع جديد" })).not.toBeInTheDocument();
  });

  it("refuses the route without any branch permission (and the menu does not offer it)", async () => {
    render(vi.fn(), {}, ALL_PERMISSIONS);
    await go("#/company/branches");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });
});

describe("branches screen: trial limit", () => {
  it("shows the limit notice and disables New branch when the limit is reached", async () => {
    render(vi.fn().mockResolvedValue(response([branch()], { limit: 1, everCreated: 1 })));
    await go("#/company/branches");
    expect(await screen.findByTestId("branch-limit")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "فرع جديد" })).toBeDisabled();
  });

  it("keeps New branch enabled below the limit", async () => {
    render(vi.fn().mockResolvedValue(response([branch()], { limit: 3, everCreated: 1 })));
    await go("#/company/branches");
    await screen.findByTestId("branch-usage");
    expect(screen.queryByTestId("branch-limit")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "فرع جديد" })).toBeEnabled();
  });
});

describe("branches screen: create and edit", () => {
  it("validates code and names client-side without calling the API", async () => {
    const client = render(vi.fn().mockResolvedValue(response([branch()])));
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("aria-invalid", "true");
    expect(screen.getAllByText("أدخل اسم الفرع بالعربية أو بالإنجليزية (واحد على الأقل).")).toHaveLength(2);
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "bad code");
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "فرع");
    await userEvent.type(screen.getByLabelText("البريد الإلكتروني"), "nope");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    expect(screen.getByLabelText("البريد الإلكتروني")).toHaveAttribute("aria-invalid", "true");
    expect(client.createBranch).not.toHaveBeenCalled();
  });

  it("creates a branch with an upper-case code and null for empty fields, then reloads the list", async () => {
    const created = branch({ id: "b-new", code: "WEST", nameAr: null, nameEn: "West", isMain: false, seeded: false });
    const listBranches = vi.fn().mockResolvedValueOnce(response([branch()])).mockResolvedValue(response([branch(), created]));
    const createBranch = vi.fn().mockResolvedValue(created);
    render(listBranches, { createBranch });
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "west");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "West");
    await userEvent.type(screen.getByLabelText("الهاتف"), "+963 11 123456");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    await waitFor(() => expect(createBranch).toHaveBeenCalled());
    expect(createBranch.mock.calls[0][0]).toMatchObject({ code: "WEST", nameAr: null, nameEn: "West", phone: "+963 11 123456", city: null });
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم إنشاء الفرع.");
    expect(await screen.findByTestId("branch-WEST")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "حفظ الفرع" })).not.toBeInTheDocument();    // the form closed
  });

  it("shows a duplicate-code error on the code field and keeps the typed values", async () => {
    const createBranch = vi.fn().mockRejectedValue(apiError(409, "branch_code_taken"));
    render(vi.fn().mockResolvedValue(response([branch()])), { createBranch });
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "MAIN");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Again");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    expect(await screen.findByText("رمز الفرع مستخدم بالفعل.")).toBeInTheDocument();
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Again");
  });

  it("explains the edition limit when the server refuses the creation (the server enforces it, not the UI)", async () => {
    const createBranch = vi.fn().mockRejectedValue(apiError(403, "entitlement_denied"));
    render(vi.fn().mockResolvedValue(response([branch()])), { createBranch });
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "EXTRA");
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "فرع");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent("وصلت الشركة إلى الحد الأقصى للفروع");
  });

  it("maps server validation errors to fields", async () => {
    const createBranch = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "email", code: "invalid_format" }, { field: "nameAr", code: "name_required" }] }));
    render(vi.fn().mockResolvedValue(response([branch()])), { createBranch });
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "OK1");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "x");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    expect(await screen.findByText("الصيغة غير صحيحة.")).toBeInTheDocument();
    expect(screen.getByText("أدخل اسم الفرع بالعربية أو بالإنجليزية (واحد على الأقل).")).toBeInTheDocument();
  });

  it("edits a branch: the code is read-only and the stored revision is sent as If-Match", async () => {
    const updateBranch = vi.fn().mockResolvedValue({ ...north, nameEn: "North 2", revision: 4 });
    render(vi.fn().mockResolvedValue(response([branch(), north])), { updateBranch });
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-NORTH")).getByRole("button", { name: "تعديل" }));
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("readonly");
    const en = screen.getByLabelText("الاسم بالإنجليزية");
    await userEvent.clear(en);
    await userEvent.type(en, "North 2");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    await waitFor(() => expect(updateBranch).toHaveBeenCalled());
    const [id, body, revision] = updateBranch.mock.calls[0];
    expect(id).toBe("b-north");
    expect(revision).toBe(3);
    expect(body).toMatchObject({ nameAr: "فرع الشمال", nameEn: "North 2" });
    expect(body.code).toBeUndefined();                                                      // the code can never be changed
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم حفظ الفرع.");
  });

  it("keeps the user's edits on a concurrency conflict (412) and reloads the latest on request", async () => {
    const updateBranch = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 9 }));
    const getBranch = vi.fn().mockResolvedValue({ ...north, nameEn: "Changed elsewhere", revision: 9 });
    render(vi.fn().mockResolvedValue(response([north])), { updateBranch, getBranch });
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-NORTH")).getByRole("button", { name: "تعديل" }));
    const en = screen.getByLabelText("الاسم بالإنجليزية");
    await userEvent.clear(en);
    await userEvent.type(en, "Mine");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الفرع" }));
    expect(await screen.findByTestId("form-conflict")).toBeInTheDocument();
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Mine");                // edits are kept
    await userEvent.click(screen.getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Changed elsewhere"));
    expect(screen.queryByTestId("form-conflict")).not.toBeInTheDocument();
  });

  it("opens a read-only form without a save button for a branch the user cannot edit", async () => {
    render(vi.fn().mockResolvedValue(response([north], {}, { update: [], create: false })));
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-NORTH")).getByRole("button", { name: "عرض" }));
    expect(screen.getByTestId("form-read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "حفظ الفرع" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveAttribute("readonly");
  });
});

describe("branches screen: main branch, activation and deactivation", () => {
  it("designates the main branch after confirmation, sending the branch revision", async () => {
    const makeMainBranch = vi.fn().mockResolvedValue({ ...north, isMain: true });
    render(vi.fn().mockResolvedValue(response([branch(), north])), { makeMainBranch });
    await go("#/company/branches");
    expect(within(await screen.findByTestId("branch-MAIN")).queryByRole("button", { name: "تعيين كفرع رئيسي" })).not.toBeInTheDocument();
    await userEvent.click(within(screen.getByTestId("branch-NORTH")).getByRole("button", { name: "تعيين كفرع رئيسي" }));
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent("NORTH");
    expect(makeMainBranch).not.toHaveBeenCalled();
    await userEvent.click(within(dialog).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(makeMainBranch).toHaveBeenCalledWith("b-north", 3));
  });

  it("deactivates a non-main branch after confirmation", async () => {
    const deactivateBranch = vi.fn().mockResolvedValue({ ...north, isActive: false });
    render(vi.fn().mockResolvedValue(response([branch(), north])), { deactivateBranch });
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-NORTH")).getByRole("button", { name: "إيقاف" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).queryByLabelText(/الفرع الرئيسي الجديد/)).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "إيقاف" }));
    await waitFor(() => expect(deactivateBranch).toHaveBeenCalledWith("b-north", 3, undefined));
    expect(await screen.findByTestId("saved")).toBeInTheDocument();
  });

  it("requires choosing an active successor to deactivate the main branch", async () => {
    const deactivateBranch = vi.fn().mockResolvedValue({ ...branch(), isActive: false, isMain: false });
    render(vi.fn().mockResolvedValue(response([branch(), north, closed])), { deactivateBranch });
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-MAIN")).getByRole("button", { name: "إيقاف" }));
    const dialog = screen.getByRole("dialog");
    const confirm = within(dialog).getByRole("button", { name: "إيقاف" });
    expect(confirm).toBeDisabled();
    const select = within(dialog).getByLabelText(/الفرع الرئيسي الجديد/);
    expect(within(select).queryByRole("option", { name: /OLD/ })).not.toBeInTheDocument();      // inactive branches are not candidates
    expect(within(select).queryByRole("option", { name: /MAIN/ })).not.toBeInTheDocument();     // the branch itself is not a candidate
    await userEvent.selectOptions(select, "b-north");
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);
    await waitFor(() => expect(deactivateBranch).toHaveBeenCalledWith("b-main", 1, "b-north"));
  });

  it("blocks deactivating the only active branch and explains why", async () => {
    render(vi.fn().mockResolvedValue(response([branch(), closed])));
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-MAIN")).getByRole("button", { name: "إيقاف" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByTestId("no-successor")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "إيقاف" })).toBeDisabled();
  });

  it("activates an inactive branch after confirmation and explains that old assignments do not return", async () => {
    const activateBranch = vi.fn().mockResolvedValue({ ...closed, isActive: true });
    render(vi.fn().mockResolvedValue(response([branch(), closed])), { activateBranch });
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-OLD")).getByRole("button", { name: "تفعيل" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("لا تعود تعيينات الصلاحيات السابقة");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(activateBranch).toHaveBeenCalledWith("b-old", 4));
  });

  it("shows server refusals of structural changes and a stale-revision conflict banner", async () => {
    const makeMainBranch = vi.fn().mockRejectedValueOnce(apiError(409, "branch_inactive")).mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 8 }));
    const listBranches = vi.fn().mockResolvedValue(response([branch(), north]));
    render(listBranches, { makeMainBranch });
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-NORTH")).getByRole("button", { name: "تعيين كفرع رئيسي" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByText("الفرع موقوف.")).toBeInTheDocument();
    await userEvent.click(within(screen.getByTestId("branch-NORTH")).getByRole("button", { name: "تعيين كفرع رئيسي" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByTestId("conflict")).toBeInTheDocument();
    const before = listBranches.mock.calls.length;
    await userEvent.click(screen.getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(listBranches.mock.calls.length).toBeGreaterThan(before));
  });
});

describe("branches screen: unsaved changes and languages", () => {
  it("protects an edited form: leaving the page asks first, Stay keeps the edits, Discard leaves", async () => {
    render(vi.fn().mockResolvedValue(response([branch()])));
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "مسودة");
    await go("#/company/currencies");
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("الفرع");
    await userEvent.click(within(dialog).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(screen.getByLabelText("الاسم بالعربية")).toHaveValue("مسودة");
    await go("#/company/currencies");
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "تجاهل التغييرات" }));
    await waitFor(() => expect(screen.queryByLabelText("الاسم بالعربية")).not.toBeInTheDocument());
  });

  it("asks before closing a dirty form and closes an untouched form at once", async () => {
    render(vi.fn().mockResolvedValue(response([branch()])));
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.click(screen.getByRole("button", { name: "إغلاق النموذج" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "فرع جديد" }));
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "x");
    await userEvent.click(screen.getByRole("button", { name: "إغلاق النموذج" }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });

  it("renders Arabic right-to-left and English left-to-right with the same screen", async () => {
    render(vi.fn().mockResolvedValue(response([branch(), north])));
    await go("#/company/branches");
    await screen.findByTestId("branch-MAIN");
    expect(document.documentElement.dir).toBe("rtl");
    await userEvent.click(screen.getByRole("button", { name: /English/ }));
    expect(await screen.findByRole("heading", { name: "Branches", level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(within(screen.getByTestId("branch-MAIN")).getByText("Main Branch")).toBeInTheDocument();
    expect(screen.getByTestId("branch-usage")).toHaveTextContent("Branches created: 2 of 5");
    await userEvent.click(screen.getByRole("button", { name: "New branch" }));
    expect(screen.getByLabelText("Name in English")).toHaveAttribute("dir", "ltr");
    expect(screen.getByLabelText("Name in Arabic")).toHaveAttribute("dir", "rtl");
  });

  it("does not reload the data or lose the form when the language changes", async () => {
    const listBranches = vi.fn().mockResolvedValue(response([branch()]));
    render(listBranches);
    await go("#/company/branches");
    await userEvent.click(await screen.findByRole("button", { name: "فرع جديد" }));
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Keep me");
    const calls = listBranches.mock.calls.length;
    await userEvent.click(screen.getByRole("button", { name: /English/ }));
    expect(await screen.findByLabelText("Name in English")).toHaveValue("Keep me");
    expect(listBranches.mock.calls.length).toBe(calls);
  });
});
