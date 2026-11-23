import { ApiError, type CashAccount, type CashAccountsResponse } from "@hasm/api-client";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, renderApp, signedIn } from "./test-utils";

/**
 * The cash and bank account screen (Phase 3.14 slice S1, shared by Web and Desktop): list, filters, create and edit with the shared Tabs (details and bank details), the masked bank number that is never sent back, the audited reveal that lives in
 * memory for a short time, revision conflicts, the server refusals, permission gating, Arabic RTL and English LTR, keyboard. The server decides everything; these tests prove the screen shows it and never leaks or overwrites the number.
 */
const A = (k: string) => catalog.ar[k]!;
const E = (k: string) => catalog.en[k]!;
const PERMS = [...ALL_PERMISSIONS, "company.cashaccounts.view", "company.cashaccounts.create", "company.cashaccounts.update", "company.cashaccounts.activate", "company.cashaccountssensitive.view"];

const BR1 = { id: "b1", code: "MAIN", nameAr: "الفرع الرئيسي", nameEn: "Main Branch", isActive: true };
const BR2 = { id: "b2", code: "NORTH", nameAr: "فرع الشمال", nameEn: "North", isActive: true };
const GL1 = { id: "g1", code: "1101", nameAr: "الصندوق", nameEn: "Cash" };
const GL2 = { id: "g2", code: "1102", nameAr: "البنك", nameEn: "Bank" };
const GL3 = { id: "g3", code: "1103", nameAr: "صندوق الشمال", nameEn: "North cash" };
const box = (over: Partial<CashAccount> = {}): CashAccount => ({
  id: "ca1", kind: "cashbox", code: "BOX1", nameAr: "الصندوق الرئيسي", nameEn: "Main cash", branchId: "b1", accountId: "g1", account: GL1, currency: "JOD", bankName: null, bankBranchName: null, accountNumberMasked: null,
  hasAccountNumber: false, isActive: true, revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", ...over,
});
const bank = (over: Partial<CashAccount> = {}): CashAccount => box({ id: "ca2", kind: "bank", code: "NB1", nameAr: "البنك الوطني", nameEn: "National Bank", accountId: "g2", account: GL2, bankName: "National Bank", bankBranchName: "Downtown", accountNumberMasked: "****7519", hasAccountNumber: true, revision: 4, ...over });
const response = (accounts: CashAccount[], can: Partial<CashAccountsResponse["can"]> = {}): CashAccountsResponse => ({
  accounts, branches: [BR1, BR2], can: { create: ["b1", "b2"], update: ["b1", "b2"], activate: ["b1", "b2"], revealNumber: ["b1"], ...can },
});

const go = async (hash = "#/company/cash-accounts") => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

function setup(accounts: CashAccount[] = [box(), bank()], extra: Record<string, unknown> = {}, permissions = PERMS, can: Partial<CashAccountsResponse["can"]> = {}) {
  const client = signedIn({
    listCashAccounts: vi.fn().mockResolvedValue(response(accounts, can)),
    listCashGlCandidates: vi.fn().mockResolvedValue({ accounts: [GL3], total: 1 }),
    createCashAccount: vi.fn().mockImplementation(async (b: { code: string }) => box({ code: b.code })),
    updateCashAccount: vi.fn().mockResolvedValue(box()),
    activateCashAccount: vi.fn().mockResolvedValue(box()), deactivateCashAccount: vi.fn().mockResolvedValue(box()),
    getCashAccount: vi.fn().mockResolvedValue(bank({ revision: 5 })),
    revealCashAccountNumber: vi.fn().mockResolvedValue({ accountNumber: "SA0380000000608010167519" }),
    ...extra,
  }, permissions);
  renderApp(client);
  return client;
}

const apiErr = (status: number, code: string, details: ConstructorParameters<typeof ApiError>[2] = {}) => new ApiError(status, code, details);
const form = () => within(screen.getByRole("form", { name: A("cash.form_section") }));
const edit = async (code: string) => { await userEvent.click(within(await screen.findByTestId(`cash-${code}`)).getByRole("button", { name: A("cash.edit") })); };
const chooseGl = async (label: string | RegExp) => { const picker = await screen.findByTestId("cash-gl-picker"); await userEvent.selectOptions(within(picker).getByRole("combobox", { name: A("cash.gl_field") }), await within(picker).findByRole("option", { name: label })); };

let setItem: ReturnType<typeof vi.spyOn>;
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; setItem = vi.spyOn(Storage.prototype, "setItem"); });
afterEach(() => { setItem.mockRestore(); vi.useRealTimers(); });

describe("cash accounts screen: list and permissions", () => {
  it("lists the accounts with kind, branch, ledger account and status, and shows only the masked number", async () => {
    setup();
    await go();
    expect(await screen.findByRole("heading", { name: A("cash.title"), level: 1 })).toBeInTheDocument();
    const row = screen.getByTestId("cash-NB1");
    expect(within(row).getByText(A("cash.kind.bank"))).toBeInTheDocument();
    expect(within(row).getByText("****7519")).toBeInTheDocument();
    expect(within(row).getByText(/1102/)).toBeInTheDocument();
    expect(within(row).getByText(/MAIN — الفرع الرئيسي/)).toBeInTheDocument();
    expect(screen.getByTestId("cash-BOX1")).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/0380000000/);
  });

  it("filters by kind, branch, status and text", async () => {
    setup([box(), bank(), box({ id: "ca3", code: "OLD", nameAr: "قديم", nameEn: "Old", isActive: false, branchId: "b2" })]);
    await go();
    await screen.findByTestId("cash-BOX1");
    await userEvent.selectOptions(screen.getByLabelText(A("cash.filter_kind")), "bank");
    expect(screen.queryByTestId("cash-BOX1")).toBeNull();
    expect(screen.getByTestId("cash-NB1")).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText(A("cash.filter_kind")), "all");
    await userEvent.selectOptions(screen.getByLabelText(A("cash.filter_status")), "inactive");
    expect(screen.getByTestId("cash-OLD")).toBeInTheDocument();
    expect(screen.queryByTestId("cash-BOX1")).toBeNull();
    await userEvent.selectOptions(screen.getByLabelText(A("cash.filter_status")), "all");
    await userEvent.selectOptions(screen.getByLabelText(A("cash.filter_branch")), "b2");
    expect(screen.queryByTestId("cash-BOX1")).toBeNull();
    await userEvent.selectOptions(screen.getByLabelText(A("cash.filter_branch")), "all");
    await userEvent.type(screen.getByLabelText(A("cash.search")), "national");
    expect(screen.getByTestId("cash-NB1")).toBeInTheDocument();
    expect(screen.queryByTestId("cash-BOX1")).toBeNull();
  });

  it("without the view permission the route is not offered and the API is never called", async () => {
    const client = setup([], {}, ALL_PERMISSIONS);
    await go();
    await waitFor(() => expect(screen.queryByRole("heading", { name: A("cash.title"), level: 1 })).toBeNull());
    expect(client.listCashAccounts).not.toHaveBeenCalled();
    expect(screen.queryByText(A("nav.cash_accounts"))).toBeNull();
  });

  it("with view only the screen is read-only: no new, no activate, edit becomes view and the form has no save", async () => {
    setup([box(), bank()], {}, [...ALL_PERMISSIONS, "company.cashaccounts.view"], { create: [], update: [], activate: [], revealNumber: [] });
    await go();
    expect(await screen.findByTestId("read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("cash.new") })).toBeNull();
    expect(screen.queryByRole("button", { name: A("cash.deactivate") })).toBeNull();
    await userEvent.click(within(screen.getByTestId("cash-BOX1")).getByRole("button", { name: A("cash.view") }));
    expect(await screen.findByTestId("form-read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("cash.save") })).toBeNull();
  });

  it("is left-to-right in English with English wording and right-to-left in Arabic", async () => {
    setup();
    await go();
    await screen.findByTestId("cash-BOX1");
    expect(document.documentElement.dir).toBe("rtl");
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(await screen.findByRole("heading", { name: E("cash.title"), level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(within(screen.getByTestId("cash-NB1")).getByText(E("cash.kind.bank"))).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "العربية" }));
    await screen.findByRole("heading", { name: A("cash.title"), level: 1 });
    expect(document.documentElement.dir).toBe("rtl");
  });

  it("the Arabic and English catalogs carry the same cash keys", () => {
    const keys = (c: Record<string, string>) => Object.keys(c).filter((k) => k.startsWith("cash.") || k.startsWith("field.cash.") || k === "nav.cash_accounts").sort();
    expect(keys(catalog.ar).length).toBeGreaterThan(80);
    expect(keys(catalog.ar)).toEqual(keys(catalog.en));
  });
});

describe("cash accounts screen: create", () => {
  it("creates a cashbox with a ledger account chosen through the picker and sends no bank fields", async () => {
    const client = setup();
    await go();
    await userEvent.click(await screen.findByRole("button", { name: A("cash.new") }));
    expect(screen.queryByTestId("cash-tabs")).toBeNull();                                   // a cashbox is one group: no tabs
    await userEvent.type(form().getByLabelText(new RegExp(`^${A("cash.code_field")}`)), "north-cash");
    await userEvent.type(form().getByLabelText(A("cash.name_en")), "North cash");
    await userEvent.selectOptions(form().getByLabelText(new RegExp(`^${A("cash.branch_field")}`)), "b2");
    await chooseGl(/1103/);
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    await waitFor(() => expect(client.createCashAccount).toHaveBeenCalledTimes(1));
    expect(client.createCashAccount).toHaveBeenCalledWith({ kind: "cashbox", code: "NORTH-CASH", nameAr: null, nameEn: "North cash", branchId: "b2", accountId: "g3", bankName: null, bankBranchName: null });
    expect(await screen.findByTestId("saved")).toHaveTextContent(A("cash.created"));
  });

  it("validates before sending: code, a name, branch and ledger account; nothing is sent", async () => {
    const client = setup();
    await go();
    await userEvent.click(await screen.findByRole("button", { name: A("cash.new") }));
    await userEvent.type(form().getByLabelText(new RegExp(`^${A("cash.code_field")}`)), "bad code!");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent(A("form.fix_errors"));
    expect(screen.getByText(A("cash.branch_required"))).toBeInTheDocument();
    expect(screen.getByText(A("cash.gl_required"))).toBeInTheDocument();
    expect(screen.getAllByText(A("cash.name_required")).length).toBeGreaterThan(0);
    expect(client.createCashAccount).not.toHaveBeenCalled();
  });

  it("a bank account has the Tabs (details, bank details); switching a tab keeps what was typed and the identity and Save stay visible", async () => {
    const client = setup();
    await go();
    await userEvent.click(await screen.findByRole("button", { name: A("cash.new") }));
    await userEvent.selectOptions(form().getByLabelText(new RegExp(`^${A("cash.kind_field")}`)), "bank");
    const tabs = await screen.findByTestId("cash-tabs");
    expect(within(tabs).getAllByRole("tab").map((x) => x.textContent)).toEqual([expect.stringContaining(A("cash.tab.details")), expect.stringContaining(A("cash.tab.bank"))]);
    await userEvent.type(form().getByLabelText(new RegExp(`^${A("cash.code_field")}`)), "nb2");
    await userEvent.type(form().getByLabelText(A("cash.name_en")), "Second bank");
    await userEvent.click(within(tabs).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    await userEvent.type(form().getByLabelText(A("cash.bank_name")), "Second Bank");
    await userEvent.type(form().getByLabelText(A("cash.number_new")), "SA03 8000 0000 6080 1016 7519");
    expect(screen.getByRole("button", { name: A("cash.save") })).toBeVisible();              // Save and the record identity are outside the tabs
    expect(form().getByLabelText(new RegExp(`^${A("cash.code_field")}`))).toHaveValue("nb2");
    await userEvent.click(within(tabs).getByRole("tab", { name: new RegExp(A("cash.tab.details")) }));
    expect(form().getByLabelText(A("cash.name_en"))).toHaveValue("Second bank");               // nothing was reset
    await userEvent.selectOptions(form().getByLabelText(new RegExp(`^${A("cash.branch_field")}`)), "b1");
    await chooseGl(/1103/);
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    await waitFor(() => expect(client.createCashAccount).toHaveBeenCalledTimes(1));
    expect(client.createCashAccount).toHaveBeenCalledWith({ kind: "bank", code: "NB2", nameAr: null, nameEn: "Second bank", branchId: "b1", accountId: "g3", bankName: "Second Bank", bankBranchName: null, accountNumber: "SA03 8000 0000 6080 1016 7519" });
  });

  it("an invalid number marks the bank tab and opens it; a masked-looking value is refused on the screen and never sent", async () => {
    const client = setup();
    await go();
    await userEvent.click(await screen.findByRole("button", { name: A("cash.new") }));
    await userEvent.selectOptions(form().getByLabelText(new RegExp(`^${A("cash.kind_field")}`)), "bank");
    await userEvent.type(form().getByLabelText(new RegExp(`^${A("cash.code_field")}`)), "nb2");
    await userEvent.type(form().getByLabelText(A("cash.name_en")), "Second");
    await userEvent.selectOptions(form().getByLabelText(new RegExp(`^${A("cash.branch_field")}`)), "b1");
    await chooseGl(/1103/);
    const tabs = screen.getByTestId("cash-tabs");
    await userEvent.click(within(tabs).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    await userEvent.type(form().getByLabelText(A("cash.number_new")), "****7519");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByText(A("cash.number_masked_input"))).toBeInTheDocument();
    await userEvent.clear(form().getByLabelText(A("cash.number_new")));
    await userEvent.type(form().getByLabelText(A("cash.number_new")), "12");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByText(A("cash.number_invalid"))).toBeInTheDocument();
    expect(client.createCashAccount).not.toHaveBeenCalled();
  });

  it("a server refusal keeps everything typed and names the field: code taken, ledger account already bound, an account that is not an asset", async () => {
    const client = setup([box()], { createCashAccount: vi.fn()
      .mockRejectedValueOnce(apiErr(409, "cash_account_code_taken"))
      .mockRejectedValueOnce(apiErr(409, "gl_account_already_bound"))
      .mockRejectedValueOnce(apiErr(400, "validation_failed", { fields: [{ field: "accountId", code: "account_not_asset" }] })) });
    await go();
    await userEvent.click(await screen.findByRole("button", { name: A("cash.new") }));
    await userEvent.type(form().getByLabelText(new RegExp(`^${A("cash.code_field")}`)), "x1");
    await userEvent.type(form().getByLabelText(A("cash.name_en")), "X");
    await userEvent.selectOptions(form().getByLabelText(new RegExp(`^${A("cash.branch_field")}`)), "b1");
    await chooseGl(/1103/);
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent(A("cash.error.cash_account_code_taken"));
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent(A("cash.error.gl_account_already_bound"));
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByText(A("field.cash.account_not_asset"))).toBeInTheDocument();
    expect(form().getByLabelText(A("cash.name_en"))).toHaveValue("X");                                  // nothing was lost
    expect(client.createCashAccount).toHaveBeenCalledTimes(3);
  });
});

describe("cash accounts screen: edit, the bank number and conflicts", () => {
  it("edits a bank account without touching the number: the masked number is shown, the save omits accountNumber and sends the revision", async () => {
    const client = setup();
    await go();
    await edit("NB1");
    expect(await screen.findByText(A("cash.number_masked").replace("{number}", "****7519"))).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/0380000000/);
    await userEvent.click(within(screen.getByTestId("cash-tabs")).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    await userEvent.clear(form().getByLabelText(A("cash.bank_branch")));
    await userEvent.type(form().getByLabelText(A("cash.bank_branch")), "Uptown");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    await waitFor(() => expect(client.updateCashAccount).toHaveBeenCalledTimes(1));
    const [id, body, revision] = (client.updateCashAccount as ReturnType<typeof vi.fn>).mock.calls[0] as [string, Record<string, unknown>, number];
    expect([id, revision]).toEqual(["ca2", 4]);
    expect(body.bankBranchName).toBe("Uptown");
    expect("accountNumber" in body).toBe(false);                                                         // the stored number stays as it is
    expect(JSON.stringify(body)).not.toContain("7519");                                                  // the mask is never sent back
  });

  it("replacing the number needs the full new value and sends it; keeping the current number discards what was typed", async () => {
    const client = setup();
    await go();
    await edit("NB1");
    await userEvent.click(within(await screen.findByTestId("cash-tabs")).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    expect(screen.queryByLabelText(A("cash.number_new"))).toBeNull();                                    // not offered until the user chooses to replace
    await userEvent.click(screen.getByTestId("cash-number-replace"));
    await userEvent.type(form().getByLabelText(A("cash.number_new")), "DE89 3704 0044 0532 0130 00");
    await userEvent.click(screen.getByRole("button", { name: A("cash.number_keep") }));
    expect(screen.queryByLabelText(A("cash.number_new"))).toBeNull();
    await userEvent.click(screen.getByTestId("cash-number-replace"));
    expect(form().getByLabelText(A("cash.number_new"))).toHaveValue("");
    await userEvent.type(form().getByLabelText(A("cash.number_new")), "DE89 3704 0044 0532 0130 00");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    await waitFor(() => expect(client.updateCashAccount).toHaveBeenCalledTimes(1));
    expect((client.updateCashAccount as ReturnType<typeof vi.fn>).mock.calls[0]![1]).toMatchObject({ accountNumber: "DE89 3704 0044 0532 0130 00" });
  });

  it("the full number appears only after the reveal, only for the branch where the permission is held, is hidden after thirty seconds, and is never written to web storage", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const client = setup([box(), bank(), bank({ id: "ca4", code: "NB-N", branchId: "b2", accountNumberMasked: "****1111" })]);
    await go();
    await edit("NB-N");
    await userEvent.click(within(await screen.findByTestId("cash-tabs")).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    expect(screen.queryByTestId("cash-number-reveal")).toBeNull();                                       // no sensitive permission on branch 2
    await userEvent.click(screen.getByRole("button", { name: A("cash.close_form") }));
    await edit("NB1");
    await userEvent.click(within(await screen.findByTestId("cash-tabs")).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    expect(screen.queryByTestId("cash-number-shown")).toBeNull();
    await userEvent.click(screen.getByTestId("cash-number-reveal"));
    expect(await screen.findByTestId("cash-number-shown")).toHaveTextContent("SA0380000000608010167519");
    expect(client.revealCashAccountNumber).toHaveBeenCalledWith("ca2");
    for (const c of setItem.mock.calls) expect(String(c[1] ?? "")).not.toContain("0380");
    for (const store of [localStorage, sessionStorage]) for (let i = 0; i < store.length; i++) expect(store.getItem(store.key(i)!) ?? "").not.toContain("0380");
    await act(async () => { vi.advanceTimersByTime(30_001); });
    await waitFor(() => expect(screen.queryByTestId("cash-number-shown")).toBeNull());
    expect(document.body.textContent).not.toContain("0380000000");
    // closing the form also drops it
    await userEvent.click(screen.getByTestId("cash-number-reveal"));
    await screen.findByTestId("cash-number-shown");
    await userEvent.click(screen.getByRole("button", { name: A("cash.close_form") }));
    expect(document.body.textContent).not.toContain("0380000000");
  });

  it("the revealed number is dropped the moment the session locks (D-236 keeps the screen mounted, hidden): it is in neither the DOM nor the state, and does not come back after signing in again (L1 of PR 206)", async () => {
    const client = setup([bank()], { updateCashAccount: vi.fn().mockRejectedValue(apiErr(401, "unauthenticated")) });
    await go();
    await edit("NB1");
    await userEvent.type(await form().findByLabelText(A("cash.name_en")), "x");                       // unsaved input: the 401 will LOCK instead of signing out
    await userEvent.click(within(screen.getByTestId("cash-tabs")).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    await userEvent.click(screen.getByTestId("cash-number-reveal"));
    expect(await screen.findByTestId("cash-number-shown")).toHaveTextContent("SA0380000000608010167519");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    await screen.findByTestId("reauth-dialog");                                                         // locked
    expect(screen.getByTestId("shell-root")).toHaveAttribute("hidden");
    expect(document.body.textContent).not.toContain("0380000000");                                       // gone at once, though the shell is still mounted
    const dialog = within(screen.getByTestId("reauth-dialog"));
    await userEvent.type(dialog.getByLabelText(/اسم المستخدم/), "amal");
    await userEvent.type(dialog.getByLabelText(/كلمة المرور/), "secret-password");
    await userEvent.click(dialog.getByRole("button", { name: "دخول" }));
    await waitFor(() => expect(screen.queryByTestId("reauth-dialog")).toBeNull());
    expect(client.login).toHaveBeenCalled();
    expect(screen.queryByTestId("cash-number-shown")).toBeNull();                                        // resumed: still hidden until asked again
    expect(document.body.textContent).not.toContain("0380000000");
  });

  it("a failed reveal (the key is unavailable) says so and shows no number", async () => {
    setup([bank()], { revealCashAccountNumber: vi.fn().mockRejectedValue(apiErr(503, "encryption_unavailable")) });
    await go();
    await edit("NB1");
    await userEvent.click(within(await screen.findByTestId("cash-tabs")).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    await userEvent.click(screen.getByTestId("cash-number-reveal"));
    expect(await screen.findByText(A("cash.error.encryption_unavailable"))).toBeInTheDocument();
    expect(screen.queryByTestId("cash-number-shown")).toBeNull();
  });

  it("412 keeps the typed edits, offers the latest version, and the reload replaces the baseline and the revision", async () => {
    const client = setup([bank()], { updateCashAccount: vi.fn().mockRejectedValueOnce(apiErr(412, "revision_conflict", { currentRevision: 5 })).mockResolvedValue(bank({ revision: 6 })) });
    await go();
    await edit("NB1");
    await userEvent.click(within(await screen.findByTestId("cash-tabs")).getByRole("tab", { name: new RegExp(A("cash.tab.bank")) }));
    await userEvent.clear(form().getByLabelText(A("cash.bank_name")));
    await userEvent.type(form().getByLabelText(A("cash.bank_name")), "Changed Bank");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByTestId("form-conflict")).toHaveTextContent(A("cash.error.revision_conflict"));
    expect(form().getByLabelText(A("cash.bank_name"))).toHaveValue("Changed Bank");
    await userEvent.click(within(screen.getByTestId("form-conflict")).getByRole("button", { name: A("form.reload_latest") }));
    await waitFor(() => expect(client.getCashAccount).toHaveBeenCalledWith("ca2"));
    await waitFor(() => expect(form().getByLabelText(A("cash.bank_name"))).toHaveValue("National Bank"));
    await userEvent.type(form().getByLabelText(A("cash.bank_branch")), "x");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    await waitFor(() => expect((client.updateCashAccount as ReturnType<typeof vi.fn>).mock.calls.at(-1)![2]).toBe(5));
  });

  it("the frozen binding is explained and the typed edits stay", async () => {
    setup([box()], { updateCashAccount: vi.fn().mockRejectedValue(apiErr(409, "binding_frozen")) });
    await go();
    await edit("BOX1");
    await userEvent.selectOptions(await form().findByLabelText(new RegExp(`^${A("cash.branch_field")}`)), "b2");
    await userEvent.click(screen.getByRole("button", { name: A("cash.save") }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent(A("cash.error.binding_frozen"));
    expect(form().getByLabelText(new RegExp(`^${A("cash.branch_field")}`))).toHaveValue("b2");
  });

  it("deactivate and activate ask for confirmation, send the revision and report the change; a refusal is worded", async () => {
    const client = setup([box(), box({ id: "ca3", code: "OLD", isActive: false, revision: 7 })], { deactivateCashAccount: vi.fn().mockRejectedValueOnce(apiErr(409, "cash_account_has_open_documents")).mockResolvedValue(box()) });
    await go();
    await userEvent.click(within(await screen.findByTestId("cash-BOX1")).getByRole("button", { name: A("cash.deactivate") }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: A("form.confirm") }));
    expect(await screen.findByText(A("cash.error.cash_account_has_open_documents"))).toBeInTheDocument();
    await userEvent.click(within(screen.getByTestId("cash-OLD")).getByRole("button", { name: A("cash.activate") }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: A("form.confirm") }));
    await waitFor(() => expect(client.activateCashAccount).toHaveBeenCalledWith("ca3", 7));
    expect(await screen.findByTestId("saved")).toHaveTextContent(A("cash.changed"));
  });

  it("keyboard: the tabs move with the arrow keys (mirrored in Arabic) and Enter in a field submits the form", async () => {
    const client = setup();
    await go();
    await edit("NB1");
    const tabs = await screen.findByTestId("cash-tabs");
    const [details, bankTab] = within(tabs).getAllByRole("tab");
    details!.focus();
    await userEvent.keyboard("{ArrowLeft}");                                                            // RTL: left is "next"
    expect(bankTab).toHaveAttribute("aria-selected", "true");
    // The shared Tabs moves the focus to the chosen tab on the next animation frame. Wait for it before the next key: a frame still pending when the typing below starts would take the focus
    // out of the field in the middle of the text, and Enter would then land on the tab instead of submitting the form (the intermittent failure of this test on a fast runner).
    await waitFor(() => expect(bankTab).toHaveFocus());
    await userEvent.keyboard("{ArrowRight}");
    expect(details).toHaveAttribute("aria-selected", "true");
    await waitFor(() => expect(details).toHaveFocus());
    await userEvent.clear(form().getByLabelText(A("cash.name_en")));
    await userEvent.type(form().getByLabelText(A("cash.name_en")), "Renamed{Enter}");
    await waitFor(() => expect(client.updateCashAccount).toHaveBeenCalledTimes(1));
  });

  it("an unsaved form is reported to the unsaved-changes registry through the page (the session-expiry lock keeps it)", async () => {
    setup([bank()]);
    await go();
    await edit("NB1");
    await userEvent.type(await form().findByLabelText(A("cash.name_en")), "x");
    await userEvent.click(screen.getByRole("button", { name: A("cash.close_form") }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();                                      // the Stay / Discard question: the form counts as unsaved
  });
});
