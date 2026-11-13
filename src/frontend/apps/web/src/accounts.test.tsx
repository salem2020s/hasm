import type { Account, AccountsResponse } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const PERMS = [...ALL_PERMISSIONS, "company.accounts.view"];
const CODE_LABEL = /^الرمز\s*\*?$/;

const acc = (over: Partial<Account> = {}): Account => ({
  id: "a1", code: "1", nameAr: "الأصول", nameEn: "Assets", parentId: null, type: "asset", nature: "debit", statement: "balance_sheet", isGroup: true, isActive: true,
  postable: false, hasChildren: true, currencyPolicy: "base", currencyCode: null, template: null, revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", ...over,
});
const root = acc();
const current = acc({ id: "a2", code: "11", nameAr: "الأصول المتداولة", nameEn: "Current assets", parentId: "a1", hasChildren: true });
const cash = acc({ id: "a3", code: "1101", nameAr: "الصندوق", nameEn: "Cash", parentId: "a2", isGroup: false, postable: true, hasChildren: false });
const sales = acc({ id: "a4", code: "4", nameAr: "الإيرادات", nameEn: "Revenue", type: "revenue", nature: "credit", statement: "profit_and_loss", hasChildren: false });
const closed = acc({ id: "a5", code: "1102", nameAr: null, nameEn: "Old bank", parentId: "a2", isGroup: false, isActive: false, postable: false, hasChildren: false, revision: 3 });

const ALL = { create: true, update: true, activate: true, recode: true, restructure: true, configure: true };
const NONE = { create: false, update: false, activate: false, recode: false, restructure: false, configure: false };
const response = (accounts: Account[], over: Partial<AccountsResponse> = {}, can: Partial<AccountsResponse["can"]> = {}): AccountsResponse => ({
  accounts, settings: { segmentWidth: 2, autoCodes: true, revision: 0, configured: false },
  templates: [{ key: "generic", nameAr: "دليل حسابات عام", nameEn: "Generic chart of accounts", accounts: 38 }],
  usageChecksImplemented: false, can: { ...ALL, ...can }, ...over,
});

const render = (listAccounts: unknown, extra: Record<string, unknown> = {}, permissions = PERMS) => {
  const client = signedIn({ listAccounts, getCurrencies: vi.fn().mockResolvedValue({ currencies: [{ code: "SAR", nameEn: "Riyal", minorUnits: 2, isBase: true, enabled: true, revision: 1 }, { code: "USD", nameEn: "Dollar", minorUnits: 2, isBase: false, enabled: true, revision: 1 }], baseCurrency: "SAR", baseCurrencySelected: true, baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] } }), ...extra }, permissions);
  renderApp(client);
  return client;
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("chart of accounts screen: tree, search and filters", () => {
  it("shows the hierarchy from the real parent relationships with type, kind and status, and is honest about what is outstanding", async () => {
    render(vi.fn().mockResolvedValue(response([root, current, cash, sales, closed])));
    await go("#/company/accounts");
    expect(await screen.findByRole("heading", { name: "دليل الحسابات", level: 1 })).toBeInTheDocument();
    expect(screen.getByTestId("account-1")).toBeInTheDocument();
    expect(within(screen.getByTestId("account-1101")).getByText("فرعي (قابل للترحيل)")).toBeInTheDocument();
    expect(within(screen.getByTestId("account-1")).getByText("رئيسي (مجموعة)")).toBeInTheDocument();
    expect(within(screen.getByTestId("account-1102")).getByText("فرعي موقوف")).toBeInTheDocument();
    expect(within(screen.getByTestId("account-1102")).getByText("Old bank")).toBeInTheDocument();                              // falls back to the other language
    expect(within(screen.getByTestId("account-4")).getByText(/إيرادات · دائن/)).toBeInTheDocument();
    expect(screen.getByTestId("outstanding")).toHaveTextContent("استيراد Excel");
    expect(screen.getByTestId("usage-unverified")).toBeInTheDocument();
  });

  it("collapses and expands branches without losing the tree", async () => {
    render(vi.fn().mockResolvedValue(response([root, current, cash])));
    await go("#/company/accounts");
    await screen.findByTestId("account-1101");
    await userEvent.click(screen.getByRole("button", { name: "طي 11" }));
    expect(screen.queryByTestId("account-1101")).not.toBeInTheDocument();
    expect(screen.getByTestId("account-11")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "توسيع 11" }));
    expect(screen.getByTestId("account-1101")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "طي الكل" }));
    expect(screen.queryByTestId("account-11")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "توسيع الكل" }));
    expect(screen.getByTestId("account-1101")).toBeInTheDocument();
  });

  it("searches by code or either name keeping the ancestors visible, and filters by type and status", async () => {
    render(vi.fn().mockResolvedValue(response([root, current, cash, sales, closed])));
    await go("#/company/accounts");
    await userEvent.type(await screen.findByLabelText("بحث بالرمز أو الاسم"), "cash");
    expect(screen.getByTestId("account-1101")).toBeInTheDocument();
    expect(screen.getByTestId("account-11")).toBeInTheDocument();                                                                 // ancestors stay so the match has context
    expect(screen.getByTestId("account-1")).toBeInTheDocument();
    expect(screen.queryByTestId("account-4")).not.toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText("بحث بالرمز أو الاسم"));
    await userEvent.selectOptions(screen.getByLabelText("تصفية حسب النوع"), "revenue");
    expect(screen.getByTestId("account-4")).toBeInTheDocument();
    expect(screen.queryByTestId("account-1101")).not.toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("تصفية حسب النوع"), "all");
    await userEvent.selectOptions(screen.getByLabelText("الحالة"), "inactive");
    expect(screen.getByTestId("account-1102")).toBeInTheDocument();
    expect(screen.queryByTestId("account-1101")).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("بحث بالرمز أو الاسم"), "zzz");
    expect(screen.getByText("لا توجد حسابات مطابقة.")).toBeInTheDocument();
  });

  it("offers the generic template on an empty chart only, with an explanation, and applies it after confirmation", async () => {
    const applyChartTemplate = vi.fn().mockResolvedValue({ accounts: [] });
    const list = vi.fn().mockResolvedValueOnce(response([])).mockResolvedValue(response([root]));
    render(list, { applyChartTemplate });
    await go("#/company/accounts");
    expect(await screen.findByText("دليل الحسابات فارغ.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "تطبيق القالب العام" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("38 حساباً");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(applyChartTemplate).toHaveBeenCalledWith("generic"));
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم تطبيق القالب.");
    expect(screen.queryByTestId("empty-hint")).not.toBeInTheDocument();
  });
});

describe("chart of accounts screen: permissions", () => {
  it("hides every action for a view-only user and shows the read-only notice", async () => {
    render(vi.fn().mockResolvedValue(response([root, current, cash], {}, NONE)));
    await go("#/company/accounts");
    expect(await screen.findByTestId("read-only")).toBeInTheDocument();
    for (const name of ["حساب جديد", "إضافة فرعي", "إيقاف", "تفعيل"]) expect(screen.queryByRole("button", { name })).not.toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "عرض" })).toHaveLength(3);
  });

  it("separates the permissions: re-coding and moving are offered only to those who hold them", async () => {
    render(vi.fn().mockResolvedValue(response([root, current, cash], {}, { recode: false, restructure: false })));
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-1101")).getByRole("button", { name: "تعديل" }));
    expect(screen.queryByRole("button", { name: "تغيير الرمز" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "نقل" })).not.toBeInTheDocument();
  });

  it("refuses the route without the accounts view permission", async () => {
    render(vi.fn(), {}, ALL_PERMISSIONS);
    await go("#/company/accounts");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });

  it("only a configurer can change the numbering settings", async () => {
    render(vi.fn().mockResolvedValue(response([root], {}, { configure: false })));
    await go("#/company/accounts");
    await screen.findByTestId("account-1");
    expect(screen.getByLabelText("تعيين الرمز تلقائياً عند الإنشاء")).toBeDisabled();
    expect(screen.queryByRole("button", { name: "حفظ الإعدادات" })).not.toBeInTheDocument();
  });
});

describe("chart of accounts screen: create and edit", () => {
  it("creates a top-level account: the type is required and the code can be automatic", async () => {
    const createAccount = vi.fn().mockResolvedValue(acc({ id: "n1", code: "2" }));
    const client = render(vi.fn().mockResolvedValue(response([root])), { createAccount });
    await go("#/company/accounts");
    await userEvent.click(await screen.findByRole("button", { name: "حساب جديد" }));
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    expect(screen.getByLabelText(/^النوع\s*\*?$/)).toHaveAttribute("aria-invalid", "true");
    expect(screen.getAllByText("أدخل اسم الحساب بالعربية أو بالإنجليزية (واحد على الأقل).")).toHaveLength(2);
    expect(client.createAccount).not.toHaveBeenCalled();
    await userEvent.selectOptions(screen.getByLabelText(/^النوع\s*\*?$/), "liability");
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "الخصوم");
    await userEvent.click(screen.getByRole("checkbox", { name: /هذا الحساب رئيسي|حساب رئيسي/ }));
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    await waitFor(() => expect(createAccount).toHaveBeenCalled());
    expect(createAccount.mock.calls[0][0]).toMatchObject({ code: null, parentId: null, type: "liability", nameAr: "الخصوم", nameEn: null, isGroup: true, currencyPolicy: "base" });
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم إنشاء الحساب.");
  });

  it("a child inherits the type of its parent, defaults the nature, and offers only active groups as parents", async () => {
    const createAccount = vi.fn().mockResolvedValue(acc({ id: "n2", code: "1103" }));
    render(vi.fn().mockResolvedValue(response([root, current, cash, closed])), { createAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-11")).getByRole("button", { name: "إضافة فرعي" }));
    expect(screen.getByLabelText(/^النوع\s*\*?$/)).toBeDisabled();
    expect(screen.getByLabelText(/^النوع\s*\*?$/)).toHaveValue("asset");
    expect(screen.getByLabelText("طبيعة الرصيد")).toHaveValue("debit");
    expect(screen.getByLabelText("القائمة المالية")).toHaveValue("الميزانية العمومية");
    const parent = screen.getByLabelText("الحساب الرئيسي");
    expect(parent).toHaveValue("a2");
    expect(within(parent).queryByRole("option", { name: /1101/ })).not.toBeInTheDocument();                                       // a postable leaf cannot be a parent
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "Receivables");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    await waitFor(() => expect(createAccount).toHaveBeenCalled());
    expect(createAccount.mock.calls[0][0]).toMatchObject({ parentId: "a2", type: null, code: null, isGroup: false });
  });

  it("sends a manual code normalised (Arabic digits become Latin) and maps a duplicate-code refusal to the code field", async () => {
    const createAccount = vi.fn().mockRejectedValue(apiError(409, "account_code_taken"));
    render(vi.fn().mockResolvedValue(response([root])), { createAccount });
    await go("#/company/accounts");
    await userEvent.click(await screen.findByRole("button", { name: "حساب جديد" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "تعيين الرمز تلقائياً" }));
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "bad code");
    await userEvent.selectOptions(screen.getByLabelText(/^النوع\s*\*?$/), "asset");
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "x");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("aria-invalid", "true");                                            // client-side shape check
    expect(createAccount).not.toHaveBeenCalled();
    await userEvent.clear(screen.getByLabelText(CODE_LABEL));
    await userEvent.type(screen.getByLabelText(CODE_LABEL), "٢٠٠١");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    await waitFor(() => expect(createAccount).toHaveBeenCalled());
    expect(createAccount.mock.calls[0][0]).toMatchObject({ code: "2001" });
    expect(await screen.findByText("رمز الحساب مستخدم بالفعل.")).toBeInTheDocument();
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("x");                                                          // the typed values are kept
  });

  it("requires a code when the company switched automatic numbering off", async () => {
    render(vi.fn().mockResolvedValue(response([root], { settings: { segmentWidth: 2, autoCodes: false, revision: 1, configured: true } })));
    await go("#/company/accounts");
    await userEvent.click(await screen.findByRole("button", { name: "حساب جديد" }));
    expect(screen.getByRole("checkbox", { name: "تعيين الرمز تلقائياً" })).toBeDisabled();
    expect(screen.getByRole("checkbox", { name: "تعيين الرمز تلقائياً" })).not.toBeChecked();
    expect(screen.getByLabelText(CODE_LABEL)).toBeEnabled();
  });

  it("requires an enabled currency for a specific-currency policy", async () => {
    const createAccount = vi.fn().mockResolvedValue(acc({ id: "n3" }));
    render(vi.fn().mockResolvedValue(response([root, current])), { createAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-11")).getByRole("button", { name: "إضافة فرعي" }));
    await userEvent.type(screen.getByLabelText("الاسم بالإنجليزية"), "USD cash");
    await userEvent.selectOptions(screen.getByLabelText("سياسة العملة"), "specific");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    expect(screen.getByLabelText(/^العملة المحددة\s*\*?$/)).toHaveAttribute("aria-invalid", "true");
    await userEvent.selectOptions(screen.getByLabelText(/^العملة المحددة\s*\*?$/), "USD");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    await waitFor(() => expect(createAccount).toHaveBeenCalled());
    expect(createAccount.mock.calls[0][0]).toMatchObject({ currencyPolicy: "specific", currencyCode: "USD" });
  });

  it("edits an account: code, parent and type are read-only there and the revision is sent as If-Match", async () => {
    const updateAccount = vi.fn().mockResolvedValue({ ...cash, nameEn: "Petty cash", revision: 2 });
    render(vi.fn().mockResolvedValue(response([root, current, cash])), { updateAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-1101")).getByRole("button", { name: "تعديل" }));
    expect(screen.getByLabelText(CODE_LABEL)).toHaveAttribute("readonly");
    expect(screen.getByLabelText("الحساب الرئيسي")).toHaveAttribute("readonly");
    expect(screen.getByLabelText(/^النوع\s*\*?$/)).toBeDisabled();
    const en = screen.getByLabelText("الاسم بالإنجليزية");
    await userEvent.clear(en);
    await userEvent.type(en, "Petty cash");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    await waitFor(() => expect(updateAccount).toHaveBeenCalled());
    const [id, body, revision] = updateAccount.mock.calls[0];
    expect([id, revision]).toEqual(["a3", 1]);
    expect(body).toMatchObject({ nameAr: "الصندوق", nameEn: "Petty cash", nature: "debit", isGroup: false });
    expect(body.code).toBeUndefined();
    expect(body.parentId).toBeUndefined();
  });

  it("keeps the user's edits on a concurrency conflict (412) and reloads the latest on request", async () => {
    const updateAccount = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 9 }));
    const getAccount = vi.fn().mockResolvedValue({ ...cash, nameEn: "Changed elsewhere", revision: 9 });
    render(vi.fn().mockResolvedValue(response([root, current, cash])), { updateAccount, getAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-1101")).getByRole("button", { name: "تعديل" }));
    const en = screen.getByLabelText("الاسم بالإنجليزية");
    await userEvent.clear(en);
    await userEvent.type(en, "Mine");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    expect(await screen.findByTestId("form-conflict")).toBeInTheDocument();
    expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Mine");
    await userEvent.click(screen.getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(screen.getByLabelText("الاسم بالإنجليزية")).toHaveValue("Changed elsewhere"));
  });

  it("explains history protection refusals in words", async () => {
    const updateAccount = vi.fn().mockRejectedValueOnce(apiError(409, "account_has_postings")).mockRejectedValueOnce(apiError(409, "account_usage_checks_not_implemented"));
    render(vi.fn().mockResolvedValue(response([root, current, cash])), { updateAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-1101")).getByRole("button", { name: "تعديل" }));
    await userEvent.selectOptions(screen.getByLabelText("طبيعة الرصيد"), "credit");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    expect(await screen.findByText(/للحساب حركات مرحّلة/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "حفظ الحساب" }));
    expect(await screen.findByText(/لا يمكن التحقق من حركات الحساب/)).toBeInTheDocument();
  });
});

describe("chart of accounts screen: controlled code and tree changes", () => {
  it("changes the code through its own audited dialog, normalising the new code, and reports a taken code", async () => {
    const recodeAccount = vi.fn().mockRejectedValueOnce(apiError(409, "account_code_taken")).mockResolvedValue({ ...cash, code: "1110", revision: 2 });
    render(vi.fn().mockResolvedValue(response([root, current, cash])), { recodeAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-1101")).getByRole("button", { name: "تعديل" }));
    await userEvent.click(screen.getByRole("button", { name: "تغيير الرمز" }));
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent("الرمز القديم في السجل");
    await userEvent.clear(within(dialog).getByLabelText(/^الرمز/));
    await userEvent.type(within(dialog).getByLabelText(/^الرمز/), "bad code");
    await userEvent.click(within(dialog).getByRole("button", { name: "تغيير الرمز" }));
    expect(recodeAccount).not.toHaveBeenCalled();
    await userEvent.clear(within(dialog).getByLabelText(/^الرمز/));
    await userEvent.type(within(dialog).getByLabelText(/^الرمز/), "١١١٠");
    await userEvent.click(within(dialog).getByRole("button", { name: "تغيير الرمز" }));
    await waitFor(() => expect(recodeAccount).toHaveBeenCalledWith("a3", "1110", 1));
    expect(await screen.findByText("رمز الحساب مستخدم بالفعل.")).toBeInTheDocument();
  });

  it("moves an account only to active groups of the same type outside its own subtree", async () => {
    const moveAccount = vi.fn().mockResolvedValue({ ...cash, parentId: "a1", revision: 2 });
    const liab = acc({ id: "l1", code: "2", type: "liability", nature: "credit", hasChildren: false });
    render(vi.fn().mockResolvedValue(response([root, current, cash, liab])), { moveAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-11")).getByRole("button", { name: "تعديل" }));
    await userEvent.click(screen.getByRole("button", { name: "نقل" }));
    const dialog = screen.getByRole("dialog");
    const select = within(dialog).getByLabelText("الحساب الرئيسي");
    expect(within(select).queryByRole("option", { name: /^2 —/ })).not.toBeInTheDocument();                                     // another type
    expect(within(select).queryByRole("option", { name: /^11 —/ })).not.toBeInTheDocument();                                    // itself
    expect(within(select).queryByRole("option", { name: /^1101 —/ })).not.toBeInTheDocument();                                  // a non-group
    await userEvent.selectOptions(select, "");                                                                                  // to the top level
    await userEvent.click(within(dialog).getByRole("button", { name: "نقل الحساب" }));
    await waitFor(() => expect(moveAccount).toHaveBeenCalledWith("a2", null, 1));
  });

  it("deactivates and activates after confirmation and shows the server's refusals", async () => {
    const deactivateAccount = vi.fn().mockRejectedValueOnce(apiError(409, "account_has_active_children")).mockResolvedValue({ ...cash, isActive: false });
    const activateAccount = vi.fn().mockResolvedValue({ ...closed, isActive: true });
    render(vi.fn().mockResolvedValue(response([root, current, cash, closed])), { deactivateAccount, activateAccount });
    await go("#/company/accounts");
    await userEvent.click(within(await screen.findByTestId("account-11")).getByRole("button", { name: "إيقاف" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByText("له حسابات فرعية نشطة؛ أوقفها أولاً.")).toBeInTheDocument();
    await userEvent.click(within(screen.getByTestId("account-1101")).getByRole("button", { name: "إيقاف" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("لا يُرحَّل إليه بعد الآن");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(deactivateAccount).toHaveBeenLastCalledWith("a3", 1));
    await userEvent.click(within(screen.getByTestId("account-1102")).getByRole("button", { name: "تفعيل" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(activateAccount).toHaveBeenCalledWith("a5", 3));
  });

  it("saves the numbering settings with their revision", async () => {
    const putChartSettings = vi.fn().mockResolvedValue({ segmentWidth: 3, autoCodes: true, revision: 1, configured: true });
    render(vi.fn().mockResolvedValue(response([root])), { putChartSettings });
    await go("#/company/accounts");
    await userEvent.selectOptions(await screen.findByLabelText("عدد خانات الرقم التسلسلي لكل مستوى"), "3");
    await userEvent.click(screen.getByRole("button", { name: "حفظ الإعدادات" }));
    await waitFor(() => expect(putChartSettings).toHaveBeenCalledWith({ segmentWidth: 3, autoCodes: true }, 0));
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم حفظ إعدادات الترقيم.");
  });
});

describe("chart of accounts screen: unsaved changes and languages", () => {
  it("protects an edited form against leaving the page", async () => {
    render(vi.fn().mockResolvedValue(response([root])));
    await go("#/company/accounts");
    await userEvent.click(await screen.findByRole("button", { name: "حساب جديد" }));
    await userEvent.type(screen.getByLabelText("الاسم بالعربية"), "مسودة");
    await go("#/company/currencies");
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("الحساب");
    await userEvent.click(within(dialog).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(screen.getByLabelText("الاسم بالعربية")).toHaveValue("مسودة");
  });

  it("renders Arabic right-to-left and English left-to-right with the same screen and falls back between languages", async () => {
    render(vi.fn().mockResolvedValue(response([root, current, cash, closed])));
    await go("#/company/accounts");
    await screen.findByTestId("account-1");
    expect(document.documentElement.dir).toBe("rtl");
    await userEvent.click(screen.getByRole("button", { name: /English/ }));
    expect(await screen.findByRole("heading", { name: "Chart of accounts", level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(within(screen.getByTestId("account-1101")).getByText("Cash")).toBeInTheDocument();
    expect(within(screen.getByTestId("account-1")).getByText("Group")).toBeInTheDocument();
    expect(screen.getByTestId("outstanding")).toHaveTextContent("Excel import");
    await userEvent.click(screen.getByRole("button", { name: "New account" }));
    expect(screen.getByLabelText("Name (English)")).toHaveAttribute("dir", "ltr");
    expect(screen.getByLabelText("Name (Arabic)")).toHaveAttribute("dir", "rtl");
  });
});
