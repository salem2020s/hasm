import type { FiscalYear, ReconciliationResponse, ReportDefinitionInfo, TrialBalanceResponse } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const A = (k: string) => catalog.ar[k]!;
const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const lbl = (k: string) => new RegExp(`^${esc(A(k))}\\s*\\*?$`);
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); window.location.hash = ""; });

/** Ledger reports (Phase 3.3 S2): fiscal selectors, comparison, depth and sort, column choice, export formats, reconciliation and the journal drill-down in the shared Web/Desktop screen. */
const year = (id: string, name: string, start: string, end: string): FiscalYear => ({
  id, name, startDate: start, endDate: end, periodType: "monthly", timeZone: "UTC", closed: false, closedAtUtc: null, revision: 1, createdAtUtc: "2026-01-01T00:00:00Z",
  periods: [{ id: `${id}-p1`, yearId: id, number: 1, startDate: start, endDate: end, status: "open", statusChangedAtUtc: null, revision: 1 }],
});
const tb = (withCmp: boolean, unavailable: string | null = null): TrialBalanceResponse => ({
  from: "2026-01-01", to: "2026-12-31", baseCurrency: "JOD", balanced: true, sort: "code", maxLevel: null,
  period: { current: { from: "2026-01-01", to: "2026-12-31", fiscalYearId: "y26", fiscalPeriodId: null, label: "FY2026" }, comparative: withCmp ? { from: "2025-01-01", to: "2025-12-31", fiscalYearId: "y25", fiscalPeriodId: null, label: "FY2025" } : null, comparativeUnavailable: unavailable },
  totals: { opening: "0.0000", debit: "300.0000", credit: "300.0000", closing: "0.0000" }, comparativeTotals: withCmp ? { opening: "0.0000", debit: "10.0000", credit: "10.0000", closing: "0.0000" } : null,
  rows: [{ accountId: "a1", code: "1101", nameAr: "الصندوق", nameEn: "Cash", parentId: null, level: 0, isGroup: false, opening: "0.0000", debit: "300.0000", credit: "0.0000", closing: "300.0000",
    comparative: withCmp ? { opening: "0.0000", debit: "10.0000", credit: "0.0000", closing: "10.0000" } : null, change: withCmp ? "290.0000" : null }],
});
const gl = { from: "2026-01-01", to: "2026-12-31", baseCurrency: "JOD", accountId: "a1", includesDescendants: false, opening: "0.0000", totalDebit: "300.0000", totalCredit: "0.0000", closing: "300.0000", totalLines: 1, offset: 0,
  lines: [{ entryId: "j1", number: "JV-2026-000001", entryDate: "2026-03-10", entryType: "normal", entryDescription: "Rent", lineDescription: null, accountId: "a1", currency: "JOD", debit: "300.0000", credit: "0.0000", rate: "1.0000000000", rateId: null, baseDebit: "300.0000", baseCredit: "0.0000", branchId: "b1", isRounding: false, runningBalance: "300.0000" }] };
const def = (id: string, formats: string[]): ReportDefinitionInfo => ({ id, titleAr: id, titleEn: id, canExport: true, formats, supportsComparison: id === "trial-balance", supportsGrouping: true, parameters: [], columns: [] });
const rec = (ok: boolean): ReconciliationResponse => ({ from: "2026-01-01", to: "2026-12-31", baseCurrency: "JOD", allPassed: ok, checks: [
  { code: "trial_balance_balances", status: "passed", differences: 0, samples: [] },
  { code: "ledger_matches_trial_balance", status: ok ? "passed" : "failed", differences: ok ? 0 : 1, samples: ok ? [] : [{ accountId: "a1", code: "1101", item: "closing", expected: "300.0000", actual: "301.0000" }] },
  { code: "group_roll_up", status: "passed", differences: 0, samples: [] }, { code: "opening_equals_previous_closing", status: "not_applicable", differences: 0, samples: [] }] });

const perms = [...ALL_PERMISSIONS, "company.accounts.view", "company.ledgerreports.view", "company.ledgerreports.export", "company.journal.view"];
const render = (extra: Record<string, unknown> = {}, permissions = perms) => {
  const client = signedIn({
    listAccounts: vi.fn().mockResolvedValue({ accounts: [{ id: "a1", code: "1101", nameAr: "الصندوق", nameEn: "Cash", isGroup: false }], settings: null, templates: [], usageChecksImplemented: true, can: {} }),
    listBranches: vi.fn().mockResolvedValue({ branches: [], limit: null, everCreated: null, can: { create: false, activate: false, designate: false, update: [] } }),
    listCostCenters: vi.fn().mockResolvedValue({ costCenters: [], can: {} }),
    listFiscalYears: vi.fn().mockResolvedValue({ years: [year("y26", "FY2026", "2026-01-01", "2026-12-31"), year("y25", "FY2025", "2025-01-01", "2025-12-31")], today: "2026-06-01", checksImplemented: false, can: {} }),
    getReportDefinitions: vi.fn().mockResolvedValue({ reports: [def("trial-balance", ["csv", "xlsx", "pdf"]), def("general-ledger", ["csv", "xlsx"])] }),
    getTrialBalance: vi.fn().mockResolvedValue(tb(false)), getGeneralLedger: vi.fn().mockResolvedValue(gl),
    downloadTrialBalanceCsv: vi.fn().mockResolvedValue(new Blob(["x"])), downloadGeneralLedgerCsv: vi.fn().mockResolvedValue(new Blob(["x"])),
    exportTrialBalance: vi.fn().mockResolvedValue(new Blob(["x"])), exportGeneralLedger: vi.fn().mockResolvedValue(new Blob(["x"])), getReconciliation: vi.fn().mockResolvedValue(rec(true)), ...extra,
  }, permissions);
  renderApp(client);
  return client;
};

describe("ledger reports: period, comparison, columns, exports, reconciliation and drill-down", () => {
  it("selects a fiscal year and asks for the previous year, then shows the comparative columns and the change", async () => {
    const client = render({ getTrialBalance: vi.fn().mockResolvedValue(tb(true)) });
    await go("#/accounting/reports");
    await userEvent.selectOptions(await screen.findByLabelText(lbl("rep.period_mode")), "year");
    await userEvent.selectOptions(screen.getByLabelText(lbl("rep.fiscal_year")), "y26");
    await userEvent.selectOptions(screen.getByLabelText(lbl("rep.comparison")), "previous_year");
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    await waitFor(() => expect(client.getTrialBalance).toHaveBeenCalledWith(expect.objectContaining({ fiscalYearId: "y26", comparison: "previous_year" })));
    const row = await screen.findByTestId("rep-tb-1101");
    expect(row).toHaveTextContent("300.0000");
    expect(row).toHaveTextContent("10.0000");
    expect(row).toHaveTextContent("290.0000");
    expect(screen.getByTestId("rep-comparative-period")).toHaveTextContent("2025-01-01 .. 2025-12-31");
    expect(screen.getByTestId("rep-tb-total")).toHaveTextContent("0.0000");
  });

  it("says plainly when a comparison has no counterpart and shows no invented figures", async () => {
    render({ getTrialBalance: vi.fn().mockResolvedValue(tb(false, "no_previous_year")) });
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("rep-comparative-unavailable")).toHaveTextContent(A("rep.unavailable.no_previous_year"));
    expect(screen.queryByText(A("rep.col.cmpClosing"))).not.toBeInTheDocument();
  });

  it("offers depth and sort options and sends them to the server", async () => {
    const client = render();
    await go("#/accounting/reports");
    await userEvent.selectOptions(await screen.findByLabelText(lbl("rep.max_level")), "0");
    await userEvent.selectOptions(screen.getByLabelText(lbl("rep.sort")), "closing_desc");
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    await waitFor(() => expect(client.getTrialBalance).toHaveBeenCalledWith(expect.objectContaining({ maxLevel: 0, sort: "closing_desc" })));
  });

  it("hides an unchecked column on screen and leaves it out of the export, using the server's column keys", async () => {
    const created = vi.fn().mockReturnValue("blob:x");
    URL.createObjectURL = created; URL.revokeObjectURL = vi.fn();
    const client = render();
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByRole("button", { name: A("rep.show") }));
    await screen.findByTestId("rep-tb-1101");
    await userEvent.click(within(screen.getByTestId("rep-columns")).getByLabelText(A("rep.col.credit")));
    expect(within(screen.getByTestId("rep-tb-table")).queryByText(A("rep.col.credit"))).not.toBeInTheDocument();
    await userEvent.click(screen.getByTestId("rep-download-xlsx"));
    await waitFor(() => expect(client.exportTrialBalance).toHaveBeenCalled());
    const [q, format] = (client.exportTrialBalance as unknown as { mock: { calls: [{ columns: string[] }, string][] } }).mock.calls[0]!;
    expect(format).toBe("xlsx");
    expect(q.columns).toEqual(["code", "nameAr", "nameEn", "opening", "debit", "closing"]);                      // credit left out
    expect(created).toHaveBeenCalled();
  });

  it("offers exactly the export formats the server offers for the report and none without the export permission", async () => {
    render();
    await go("#/accounting/reports");
    await screen.findByTestId("rep-form");
    expect(screen.getByTestId("rep-download-csv")).toBeInTheDocument();
    expect(screen.getByTestId("rep-download-xlsx")).toBeInTheDocument();
    expect(screen.getByTestId("rep-download-pdf")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("tab", { name: A("rep.general_ledger") }));
    expect(screen.queryByTestId("rep-download-pdf")).not.toBeInTheDocument();                                       // this report offers csv and xlsx only
  });

  it("does not show any export button to a user without the export permission", async () => {
    render({}, [...ALL_PERMISSIONS, "company.accounts.view", "company.ledgerreports.view"]);
    await go("#/accounting/reports");
    await screen.findByTestId("rep-form");
    for (const f of ["csv", "xlsx", "pdf"]) expect(screen.queryByTestId(`rep-download-${f}`)).not.toBeInTheDocument();
  });

  it("shows the reconciliation checks with their status and lists the differences of a failing check", async () => {
    render({ getReconciliation: vi.fn().mockResolvedValueOnce(rec(true)).mockResolvedValue(rec(false)) });
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByTestId("rep-reconcile"));
    expect(await screen.findByTestId("rep-rec-summary")).toHaveTextContent(A("rep.reconcile_ok"));
    expect(screen.getByTestId("rep-rec-opening_equals_previous_closing")).toHaveTextContent(A("rep.status.not_applicable"));
    await userEvent.click(screen.getByTestId("rep-reconcile"));
    await waitFor(() => expect(screen.getByTestId("rep-rec-summary")).toHaveTextContent(A("rep.reconcile_failed")));
    expect(screen.getByTestId("rep-rec-ledger_matches_trial_balance")).toHaveTextContent("301.0000");
  });

  it("opens the journal entry of a general-ledger line (drill-down) only for a user who may view the journal", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    render();
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByRole("tab", { name: A("rep.general_ledger") }));
    await userEvent.selectOptions(screen.getByLabelText(lbl("rep.account")), "a1");
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    const link = await screen.findByTestId("rep-gl-open-0");
    expect(link).toHaveAttribute("href", "#/accounting/journal");
    await userEvent.click(link);
    expect(setItem).toHaveBeenCalledWith("hasm.openJournalEntry", "j1");           // the journal screen consumes it and opens the entry
    setItem.mockRestore();
  });

  it("shows no drill-down link without the journal permission", async () => {
    render({}, [...ALL_PERMISSIONS, "company.accounts.view", "company.ledgerreports.view"]);
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByRole("tab", { name: A("rep.general_ledger") }));
    await userEvent.selectOptions(screen.getByLabelText(lbl("rep.account")), "a1");
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    await screen.findByTestId("rep-gl-row-0");
    expect(screen.queryByTestId("rep-gl-open-0")).not.toBeInTheDocument();
  });

  it("translates a refused sort, a refused export size and an unknown fiscal year", async () => {
    render({ getTrialBalance: vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "sort", code: "invalid_sort" }] })), downloadTrialBalanceCsv: vi.fn().mockRejectedValue(apiError(409, "export_too_large")) });
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("rep-error")).toHaveTextContent(A("rep.error.invalid_sort"));
    await userEvent.click(screen.getByTestId("rep-download-csv"));
    await waitFor(() => expect(screen.getByTestId("rep-error")).toHaveTextContent(A("rep.error.export_too_large")));
  });

  it("works without fiscal years or report definitions (a company that has not created a year yet)", async () => {
    render({ listFiscalYears: vi.fn().mockRejectedValue(apiError(403, "forbidden")), getReportDefinitions: vi.fn().mockRejectedValue(apiError(403, "forbidden")) });
    await go("#/accounting/reports");
    await screen.findByTestId("rep-form");
    expect(screen.queryByLabelText(lbl("rep.period_mode"))).not.toBeInTheDocument();                                // only the date range
    expect(screen.getByTestId("rep-download-csv")).toBeInTheDocument();                                              // csv is the safe default
  });
});
