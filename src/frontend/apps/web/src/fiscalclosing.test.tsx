import type { Account, ClosingPreview, ClosingSettingsResponse, YearEndStatus } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const A = (k: string) => catalog.ar[k]!;
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const perms = [...ALL_PERMISSIONS, "company.fiscalclosing.view", "company.accounts.view", "company.branches.view", "company.costcenters.view"];
const account = (over: Partial<Account>): Account => ({ id: "a1", code: "3101", nameAr: "أرباح مرحلة", nameEn: "Retained earnings", type: "equity", isGroup: false, isActive: true, ...over }) as unknown as Account;
const settings = (over: Partial<ClosingSettingsResponse["settings"]> = {}, can = { configure: true, create: true, reverse: true }): ClosingSettingsResponse => ({
  settings: { equityAccountId: "a1", equityCode: "3101", equityNameAr: "أرباح مرحلة", equityNameEn: "Retained earnings", requireApproval: true, revision: 2, configured: true, ...over }, can });
const preview = (over: Partial<ClosingPreview> = {}): ClosingPreview => ({
  yearId: "y1", yearName: "FY2026", yearStart: "2026-01-01", yearEnd: "2026-12-31", baseCurrency: "JOD", equityAccountId: "a1", netResult: "-900.0000", isProfit: true,
  closingLines: [{ accountId: "a1", branchId: "b1", debit: "0.0000", credit: "700.0000" }], openingLines: [{ accountId: "a1", branchId: "b1", debit: "0.0000", credit: "700.0000" }],
  nextYearId: "y2", nextYearStart: "2027-01-01",
  checks: [{ code: "settings", status: "passed", detail: "3101", phase: "close" }, { code: "pending_entries", status: "blocked", detail: "2", phase: "close" }, { code: "domain:inventory_valuation_differences", status: "not_applicable", detail: "module_not_part_of_this_release", phase: "both" }],
  canClose: false, canCarryForward: false, nextVersion: 1, evidenceHash: "abcdef0123456789ffff", activeClosingId: null, ...over });
const years = (over: Partial<YearEndStatus> = {}): { years: YearEndStatus[] } => ({ years: [{ yearId: "y1", name: "FY2026", startDate: "2026-01-01", endDate: "2026-12-31", closings: [], carryforwards: [], ...over }] });

function render(over: Record<string, unknown> = {}, can?: { configure: boolean; create: boolean; reverse: boolean }) {
  const client = signedIn({
    getClosingSettings: vi.fn().mockResolvedValue(settings({}, can)),
    listYearEnd: vi.fn().mockResolvedValue(years()),
    previewYearEnd: vi.fn().mockResolvedValue(preview()),
    listAccounts: vi.fn().mockResolvedValue({ accounts: [account({}), account({ id: "a2", code: "1101", type: "asset" }), account({ id: "a3", code: "3", isGroup: true }), account({ id: "a4", code: "3102", isActive: false })], settings: null }),
    listBranches: vi.fn().mockResolvedValue({ branches: [{ id: "b1", code: "MAIN", nameAr: "الرئيسي", nameEn: "Main", isActive: true, isMain: true }] }),
    putClosingSettings: vi.fn().mockResolvedValue({}),
    startYearClosing: vi.fn().mockResolvedValue({}),
    carryForwardYear: vi.fn().mockResolvedValue({}),
    reverseYearOpening: vi.fn().mockResolvedValue({ entryId: "e1", status: "draft" }),
    reverseYearClosing: vi.fn().mockResolvedValue({ entryId: "e2", status: "draft" }),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}

const closingRow = (over: Record<string, unknown> = {}) => ({
  id: "c1", yearId: "y1", version: 1, netResult: "-900.0000", equityAccountId: "a1", closingEntryId: "e1", closingStatus: "posted", closingNumber: "JV-2026-000010", carryforwardId: "k1",
  openingEntryId: "e3", openingStatus: "posted", openingNumber: "JV-2027-000001", toYearId: "y2", state: "carried_forward", stale: true, evidenceHash: "x", createdAtUtc: "", createdBy: "u", ...over,
});

describe("fiscal-year closing screen", () => {
  it("shows the result, the server validations and lists a check of an absent module as not applicable, never as passed", async () => {
    render();
    await go("#/company/fiscal-closing");
    expect(await screen.findByRole("heading", { name: A("fyc.title"), level: 1 })).toBeInTheDocument();
    expect(await screen.findByTestId("fyc-net")).toHaveTextContent(A("fyc.profit"));
    expect(screen.getByTestId("fyc-net")).toHaveTextContent("900.0000");
    expect(screen.getByTestId("fyc-check-settings")).toHaveTextContent(A("fyc.check_passed"));
    expect(screen.getByTestId("fyc-check-pending_entries")).toHaveTextContent(A("fyc.check_blocked"));
    const na = screen.getByTestId("fyc-check-domain:inventory_valuation_differences");
    expect(na).toHaveTextContent(A("fyc.check_na"));
    expect(na).not.toHaveTextContent(new RegExp(`^${A("fyc.check_passed")}$`));
    expect(screen.getByTestId("fyc-hash")).toHaveTextContent("abcdef0123456789");
    expect(screen.getByRole("button", { name: A("fyc.start_closing") })).toBeDisabled();            // the server said it cannot close
    expect(screen.getByRole("button", { name: A("fyc.start_carry") })).toBeDisabled();
  });

  it("offers only active postable equity accounts and saves the setting with its revision", async () => {
    const client = render({ getClosingSettings: vi.fn().mockResolvedValue(settings({ equityAccountId: null, equityCode: null, configured: false, revision: 0 })) });
    await go("#/company/fiscal-closing");
    const select = await screen.findByLabelText(new RegExp(A("fyc.equity_account")));
    const options = Array.from((select as HTMLSelectElement).options).map((o) => o.textContent ?? "");
    expect(options.some((o) => o.includes("3101"))).toBe(true);
    expect(options.some((o) => o.includes("1101"))).toBe(false);       // an asset
    expect(options.some((o) => o.startsWith("3 "))).toBe(false);       // a group
    expect(options.some((o) => o.includes("3102"))).toBe(false);       // inactive
    await userEvent.selectOptions(select, "a1");
    expect(screen.getByTestId("fyc-approval-mandatory")).toBeInTheDocument();   // not a setting: there is no toggle
    expect(screen.queryByRole("checkbox")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: A("common.save") }));
    await waitFor(() => expect(client.putClosingSettings).toHaveBeenCalledWith({ equityAccountId: "a1" }, 0));
  });

  it("renders only the first 200 lines of a very large generated entry, always shows the true line count and the cost center of each line", async () => {
    const many = Array.from({ length: 450 }, (_, i) => ({ accountId: "a1", branchId: "b1", debit: "1.0000", credit: "0.0000", costCenterId: i === 0 ? "c1" : null }));
    render({ previewYearEnd: vi.fn().mockResolvedValue(preview({ openingLines: many, canClose: true, canCarryForward: true, checks: [] })), listCostCenters: vi.fn().mockResolvedValue({ costCenters: [{ id: "c1", code: "CC1", nameAr: "مركز", nameEn: "Center", isGroup: false, isActive: true }], can: {} }) });
    await go("#/company/fiscal-closing");
    const table = await screen.findByTestId("fyc-opening-lines");
    expect(within(table).getAllByRole("row")).toHaveLength(201);                       // the header and 200 lines
    expect(screen.getByTestId("fyc-opening-lines-count").textContent).toContain("450");
  });

  it("starts the closing and the carryforward through the server", async () => {
    const client = render({ previewYearEnd: vi.fn().mockResolvedValue(preview({ canClose: true, canCarryForward: true, checks: [] })) });
    await go("#/company/fiscal-closing");
    await userEvent.click(await screen.findByRole("button", { name: A("fyc.start_closing") }));
    await waitFor(() => expect(client.startYearClosing).toHaveBeenCalledWith("y1"));
    expect(await screen.findByTestId("fyc-notice")).toHaveTextContent(A("fyc.closing_started"));
    await userEvent.click(screen.getByRole("button", { name: A("fyc.start_carry") }));
    await waitFor(() => expect(client.carryForwardYear).toHaveBeenCalledWith("y1"));
  });

  it("explains a server refusal without losing the screen", async () => {
    render({ previewYearEnd: vi.fn().mockResolvedValue(preview({ canClose: true, checks: [] })), startYearClosing: vi.fn().mockRejectedValue(apiError(409, "closing_blocked")) });
    await go("#/company/fiscal-closing");
    await userEvent.click(await screen.findByRole("button", { name: A("fyc.start_closing") }));
    expect(await screen.findByTestId("fyc-error")).toHaveTextContent(A("fyc.error.closing_blocked"));
    expect(screen.getByTestId("fyc-net")).toBeInTheDocument();
  });

  it("hides every action without the create, configure and reverse permissions", async () => {
    render({ previewYearEnd: vi.fn().mockResolvedValue(preview({ canClose: true, canCarryForward: true, checks: [] })), listYearEnd: vi.fn().mockResolvedValue(years({ closings: [closingRow({ stale: false })] as never })) }, { configure: false, create: false, reverse: false });
    await go("#/company/fiscal-closing");
    await screen.findByTestId("fyc-net");
    expect(screen.queryByRole("button", { name: A("fyc.start_closing") })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("fyc.start_carry") })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("common.save") })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("fyc.reverse_opening") })).not.toBeInTheDocument();
  });

  it("shows the closings with their state and staleness and offers the reversals in the right order", async () => {
    const client = render({ listYearEnd: vi.fn().mockResolvedValue(years({ closings: [closingRow()] as never })) });
    await go("#/company/fiscal-closing");
    const row = await screen.findByTestId("fyc-run-1");
    expect(row).toHaveTextContent(A("fyc.state.carried_forward"));
    expect(screen.getByTestId("fyc-stale-1")).toHaveTextContent(A("fyc.stale"));
    // With the opening balances posted only THEY can be reversed first; the closing reversal is not offered yet.
    expect(within(row).queryByRole("button", { name: A("fyc.reverse_closing") })).not.toBeInTheDocument();
    await userEvent.click(within(row).getByRole("button", { name: A("fyc.reverse_opening") }));
    await waitFor(() => expect(client.reverseYearOpening).toHaveBeenCalledWith("y1"));
  });

  it("offers the closing reversal once the opening balances are no longer posted", async () => {
    const closed = closingRow({ carryforwardId: null, openingEntryId: null, openingStatus: null, openingNumber: null, toYearId: null, state: "closed", stale: false });
    const client = render({ listYearEnd: vi.fn().mockResolvedValue(years({ closings: [closed] as never })) });
    await go("#/company/fiscal-closing");
    await userEvent.click(await within(await screen.findByTestId("fyc-run-1")).findByRole("button", { name: A("fyc.reverse_closing") }));
    await waitFor(() => expect(client.reverseYearClosing).toHaveBeenCalledWith("y1"));
  });
});
