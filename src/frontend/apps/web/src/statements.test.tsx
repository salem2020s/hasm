import type { FiscalYear, StatementLineInfo, StatementMappingResponse, StatementResponse } from "@hasm/api-client";
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
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const year = (id: string, name: string, start: string, end: string): FiscalYear => ({
  id, name, startDate: start, endDate: end, periodType: "monthly", timeZone: "UTC", closed: false, closedAtUtc: null, revision: 1, createdAtUtc: "2026-01-01T00:00:00Z",
  periods: [{ id: `${id}-p1`, yearId: id, number: 1, startDate: start, endDate: end, status: "open", statusChangedAtUtc: null, revision: 1 }],
});
const L = (over: Partial<StatementLineInfo> & Pick<StatementLineInfo, "kind" | "key">): StatementLineInfo => ({ accountId: null, code: null, nameAr: null, nameEn: null, level: 0, current: "0.0000", comparative: null, change: null, ...over });
const period = { current: { from: "2026-01-01", to: "2026-12-31", fiscalYearId: "y26", fiscalPeriodId: null, label: "FY2026" }, comparative: null, comparativeUnavailable: null };
const mapping = { id: "m1", versionNo: 3, createdAtUtc: "2026-02-01T10:00:00Z", createdBy: "u1", source: "manual" as const, note: null, entryCount: 12, contentHash: "h" };
const pl = (over: Partial<StatementResponse> = {}): StatementResponse => ({
  kind: "profit-and-loss", applicable: true, notApplicableReason: null, complete: true, asOf: false, baseCurrency: "JOD", hasComparative: false, mapping, period, issues: [],
  checks: [{ code: "classification_complete", status: "passed", difference: "0.0000" }, { code: "result_matches_trial_balance", status: "passed", difference: "0.0000" }],
  lines: [
    L({ kind: "header", key: "operating_revenue" }), L({ kind: "account", key: "operating_revenue", accountId: "a1", code: "4101", nameAr: "مبيعات البضاعة", nameEn: "Merchandise sales", level: 1, current: "1500.0000" }),
    L({ kind: "subtotal", key: "total_operating_revenue", current: "1500.0000" }), L({ kind: "total", key: "net_revenue", current: "1500.0000" }), L({ kind: "total", key: "net_result", current: "630.0000" }),
  ], ...over,
});
const bs = (): StatementResponse => pl({ kind: "balance-sheet", asOf: true, lines: [L({ kind: "total", key: "total_assets", current: "14580.0000" }), L({ kind: "account", key: "unclosed_result", nameAr: "ربح أو خسارة لم تُقفل بعد", nameEn: "Profit or loss not yet closed to equity", current: "630.0000" })], checks: [{ code: "accounting_equation", status: "passed", difference: "0.0000" }] });

const perms = [...ALL_PERMISSIONS, "company.accounts.view", "company.financialstatements.view", "company.financialstatements.export", "company.statementmapping.view", "company.statementmapping.configure"];
const render = (extra: Record<string, unknown> = {}, permissions = perms) => {
  const client = signedIn({
    listFiscalYears: vi.fn().mockResolvedValue({ years: [year("y26", "FY2026", "2026-01-01", "2026-12-31")], today: "2026-06-01", checksImplemented: false, can: {} }),
    listBranches: vi.fn().mockResolvedValue({ branches: [], limit: null, everCreated: null, can: {} }), listCostCenters: vi.fn().mockResolvedValue({ costCenters: [], can: {} }),
    getReportDefinitions: vi.fn().mockResolvedValue({ reports: [{ id: "profit-and-loss", titleAr: "", titleEn: "", canExport: true, formats: ["csv", "xlsx", "pdf"], supportsComparison: true, supportsGrouping: false, parameters: [], columns: [] }] }),
    listStatementMappingVersions: vi.fn().mockResolvedValue({ versions: [mapping, { ...mapping, id: "m0", versionNo: 2 }] }),
    getStatement: vi.fn().mockResolvedValue(pl()), exportStatement: vi.fn().mockResolvedValue(new Blob(["x"])), ...extra,
  }, permissions);
  renderApp(client);
  return client;
};

describe("financial statements screen", () => {
  it("shows the profit and loss from the server with exact amounts, the classification version and the passed checks", async () => {
    const client = render();
    await go("#/accounting/statements");
    await userEvent.selectOptions(await screen.findByLabelText(lbl("rep.period_mode")), "year");
    await userEvent.selectOptions(screen.getByLabelText(lbl("rep.fiscal_year")), "y26");
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    await waitFor(() => expect(client.getStatement).toHaveBeenCalledWith("profit-and-loss", expect.objectContaining({ fiscalYearId: "y26" })));
    expect(await screen.findByTestId("st-line-4101")).toHaveTextContent("1500.0000");
    expect(screen.getByTestId("st-line-net_result")).toHaveTextContent("630.0000");
    expect(screen.getByTestId("st-line-net_revenue")).toHaveTextContent(A("st.line.net_revenue"));
    expect(screen.getByTestId("st-line-total_operating_revenue")).toHaveTextContent(`${A("st.total")} ${A("st.line.operating_revenue")}`);   // a section total is titled from its section
    expect(screen.getByTestId("st-mapping")).toHaveTextContent("3");
    expect(screen.getByTestId("st-check-result_matches_trial_balance")).toHaveTextContent(A("st.status.passed"));
    expect(screen.queryByTestId("st-incomplete")).not.toBeInTheDocument();
  });

  it("says plainly that a statement with unclassified accounts is INCOMPLETE and offers the classification", async () => {
    render({ getStatement: vi.fn().mockResolvedValue(pl({ complete: false, issues: [{ code: "unclassified_accounts", count: 2, amount: "-150.0000" }], checks: [{ code: "classification_complete", status: "failed", difference: "-150.0000" }] })) });
    await go("#/accounting/statements");
    await userEvent.click(await screen.findByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("st-incomplete")).toHaveTextContent(A("st.incomplete_banner"));
    expect(screen.getByTestId("st-issue")).toHaveTextContent("2");
    expect(screen.getByTestId("st-check-classification_complete")).toHaveTextContent(A("st.status.failed"));
    await userEvent.click(screen.getByTestId("st-open-mapping"));
    expect(window.location.hash).toBe("#/accounting/statement-mapping");
  });

  it("explains that there is no classification at all and does not offer the classification without its permission", async () => {
    render({ getStatement: vi.fn().mockResolvedValue(pl({ complete: false, mapping: null, issues: [{ code: "no_mapping", count: 0, amount: "0.0000" }] })) }, [...ALL_PERMISSIONS, "company.financialstatements.view"]);
    await go("#/accounting/statements");
    await userEvent.click(await screen.findByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("st-incomplete")).toHaveTextContent(A("st.no_mapping_banner"));
    expect(screen.getByTestId("st-mapping")).toHaveTextContent(A("st.mapping_none"));
    expect(screen.queryByTestId("st-open-mapping")).not.toBeInTheDocument();
  });

  it("explains a trading account that does not apply and shows no table", async () => {
    render({ getStatement: vi.fn().mockResolvedValue(pl({ kind: "trading-account", applicable: false, notApplicableReason: "no_cost_of_sales_accounts", lines: [], checks: [] })) });
    await go("#/accounting/statements");
    await userEvent.click(await screen.findByRole("tab", { name: A("st.tab.trading-account") }));
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("st-not-applicable")).toHaveTextContent(A("st.not_applicable.no_cost_of_sales_accounts"));
    expect(screen.queryByTestId("st-table")).not.toBeInTheDocument();
  });

  it("uses an as-of date for the balance sheet and shows the derived unclosed result as its own line", async () => {
    const client = render({ getStatement: vi.fn().mockResolvedValue(bs()) });
    await go("#/accounting/statements");
    await userEvent.click(await screen.findByRole("tab", { name: A("st.tab.balance-sheet") }));
    expect(screen.queryByLabelText(lbl("rep.from"))).not.toBeInTheDocument();                                         // a balance sheet has one date
    expect(screen.getByLabelText(new RegExp(A("st.as_of")))).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("st-line-unclosed_result")).toHaveTextContent("630.0000");
    expect(screen.getByTestId("st-line-total_assets")).toHaveTextContent("14580.0000");
    expect(client.getStatement).toHaveBeenCalledWith("balance-sheet", expect.any(Object));
  });

  it("shows comparative columns and the change, and says when no comparison exists", async () => {
    const withCmp = pl({ hasComparative: true, period: { ...period, comparative: { from: "2025-01-01", to: "2025-12-31", fiscalYearId: "y25", fiscalPeriodId: null, label: "FY2025" } },
      lines: [L({ kind: "account", key: "operating_revenue", accountId: "a1", code: "4101", nameAr: "ا", nameEn: "Sales", current: "1500.0000", comparative: "1000.0000", change: "500.0000" })] });
    const client = render({ getStatement: vi.fn().mockResolvedValueOnce(withCmp).mockResolvedValue(pl({ period: { ...period, comparativeUnavailable: "no_previous_year" } })) });
    await go("#/accounting/statements");
    await userEvent.selectOptions(await screen.findByLabelText(lbl("rep.comparison")), "previous_year");
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("st-line-4101")).toHaveTextContent("500.0000");
    expect(screen.getByTestId("st-comparative-period")).toHaveTextContent("2025-01-01 .. 2025-12-31");
    expect(client.getStatement).toHaveBeenCalledWith("profit-and-loss", expect.objectContaining({ comparison: "previous_year" }));
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("st-comparative-unavailable")).toHaveTextContent(A("rep.unavailable.no_previous_year"));
  });

  it("exports through the server in the offered formats with the chosen classification version, only with the export permission", async () => {
    URL.createObjectURL = vi.fn().mockReturnValue("blob:x"); URL.revokeObjectURL = vi.fn();
    const client = render();
    await go("#/accounting/statements");
    await userEvent.selectOptions(await screen.findByLabelText(lbl("st.mapping_version")), "2");
    await userEvent.click(screen.getByTestId("st-download-pdf"));
    await waitFor(() => expect(client.exportStatement).toHaveBeenCalledWith("profit-and-loss", expect.objectContaining({ mappingVersion: 2 }), "pdf"));
    expect(screen.getByTestId("st-download-xlsx")).toBeInTheDocument();
  });

  it("offers no export to a user without the export permission and translates a refused classification version", async () => {
    render({ getStatement: vi.fn().mockRejectedValue(apiError(404, "mapping_version_not_found")) }, [...ALL_PERMISSIONS, "company.financialstatements.view"]);
    await go("#/accounting/statements");
    await userEvent.click(await screen.findByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("st-error")).toHaveTextContent(A("st.error.mapping_version_not_found"));
    for (const f of ["csv", "xlsx", "pdf"]) expect(screen.queryByTestId(`st-download-${f}`)).not.toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------------------------------------ classification
const sections: StatementMappingResponse["sections"] = [
  { key: "current_assets", statement: "balance_sheet", types: ["asset"] }, { key: "non_current_assets", statement: "balance_sheet", types: ["asset"] },
  { key: "operating_revenue", statement: "profit_and_loss", types: ["revenue"] }, { key: "other_income", statement: "profit_and_loss", types: ["revenue"] },
];
const acc = (id: string, code: string, type: string, over: Partial<StatementMappingResponse["accounts"][number]> = {}): StatementMappingResponse["accounts"][number] =>
  ({ id, code, nameAr: `ح ${code}`, nameEn: `Account ${code}`, parentId: null, type, nature: type === "asset" ? "debit" : "credit", isGroup: false, templateKey: null, effectiveSection: null, ...over });
const map = (over: Partial<StatementMappingResponse> = {}): StatementMappingResponse => ({
  version: mapping, sections, entries: [{ accountId: "g1", section: "current_assets" }],
  accounts: [acc("g1", "11", "asset", { isGroup: true, effectiveSection: "current_assets" }), acc("c1", "1101", "asset", { parentId: "g1", effectiveSection: "current_assets" }), acc("c2", "1201", "asset", { parentId: "g1", effectiveSection: "current_assets" }),
    acc("r1", "4101", "revenue")], ...over,
});

describe("statement classification screen", () => {
  const renderMap = (data = map(), extra: Record<string, unknown> = {}, permissions = perms) => {
    const client = signedIn({
      getStatementMapping: vi.fn().mockResolvedValue(data), listStatementMappingVersions: vi.fn().mockResolvedValue({ versions: [mapping, { ...mapping, id: "m0", versionNo: 2, source: "template" as const, note: "first" }] }),
      getStatementMappingSuggestion: vi.fn().mockResolvedValue({ entries: [{ accountId: "r1", section: "operating_revenue" }] }),
      saveStatementMapping: vi.fn().mockResolvedValue({ version: { ...mapping, versionNo: 4 }, entries: [] }), ...extra,
    }, permissions);
    renderApp(client);
    return client;
  };

  it("shows each account with the section it ends up in, flags the unclassified postable ones and inherits from the group", async () => {
    renderMap();
    await go("#/accounting/statement-mapping");
    expect(await screen.findByRole("heading", { name: A("sm.title"), level: 1 })).toBeInTheDocument();
    expect(within(screen.getByTestId("sm-row-1101")).getAllByText(A("sm.section.current_assets")).length).toBeGreaterThan(0);        // inherited from the group
    expect(within(screen.getByTestId("sm-row-4101")).getByText(A("sm.unclassified"), { selector: "strong" })).toBeInTheDocument();
    expect(screen.getByTestId("sm-unclassified")).toHaveTextContent("1");
    expect(screen.getByTestId("sm-version")).toHaveTextContent("3");
  });

  it("changes the effective section locally for the group and its children, then saves the complete mapping with the version it started from", async () => {
    const client = renderMap();
    await go("#/accounting/statement-mapping");
    expect(await screen.findByTestId("sm-save")).toBeDisabled();                                                        // nothing changed yet
    await userEvent.selectOptions(screen.getByLabelText(`4101 — ${A("sm.section")}`), "operating_revenue");
    await userEvent.selectOptions(screen.getByLabelText(`1201 — ${A("sm.section")}`), "non_current_assets");
    await waitFor(() => expect(screen.queryByTestId("sm-unclassified")).not.toBeInTheDocument());                          // every postable account is classified now
    await userEvent.type(screen.getByLabelText(A("sm.note")), "split fixed assets");
    await userEvent.click(screen.getByTestId("sm-save"));
    await waitFor(() => expect(client.saveStatementMapping).toHaveBeenCalledTimes(1));
    const [entries, expected, note] = (client.saveStatementMapping as unknown as { mock: { calls: [{ accountId: string; section: string }[], number, string | null][] } }).mock.calls[0]!;
    expect(expected).toBe(3);
    expect(note).toBe("split fixed assets");
    expect(entries.sort((a, b) => a.accountId.localeCompare(b.accountId))).toEqual([{ accountId: "c2", section: "non_current_assets" }, { accountId: "g1", section: "current_assets" }, { accountId: "r1", section: "operating_revenue" }]);
    expect(await screen.findByTestId("sm-notice")).toHaveTextContent("4");
  });

  it("offers only the sections that fit each account type", async () => {
    renderMap();
    await go("#/accounting/statement-mapping");
    const revenue = await screen.findByLabelText(`4101 — ${A("sm.section")}`) as HTMLSelectElement;
    expect(Array.from(revenue.options).map((o) => o.value)).toEqual(["", "operating_revenue", "other_income"]);
    const asset = screen.getByLabelText(`1101 — ${A("sm.section")}`) as HTMLSelectElement;
    expect(Array.from(asset.options).map((o) => o.value)).toEqual(["", "current_assets", "non_current_assets"]);
  });

  it("loads the template suggestion into the form without saving it", async () => {
    const client = renderMap();
    await go("#/accounting/statement-mapping");
    await userEvent.click(await screen.findByTestId("sm-template"));
    expect(await screen.findByTestId("sm-notice")).toHaveTextContent("1");
    expect(client.saveStatementMapping).not.toHaveBeenCalled();
    expect(screen.getByTestId("sm-save")).toBeEnabled();
  });

  it("is read-only without the configure permission and for an earlier version", async () => {
    renderMap(map(), {}, [...ALL_PERMISSIONS, "company.accounts.view", "company.statementmapping.view"]);
    await go("#/accounting/statement-mapping");
    expect(await screen.findByLabelText(`4101 — ${A("sm.section")}`)).toBeDisabled();
    expect(screen.queryByTestId("sm-save")).not.toBeInTheDocument();
    expect(screen.queryByTestId("sm-template")).not.toBeInTheDocument();
  });

  it("shows an earlier version for reference only", async () => {
    const getStatementMapping = vi.fn().mockImplementation((v?: number) => Promise.resolve(v === 2 ? map({ version: { ...mapping, versionNo: 2 } }) : map()));
    renderMap(map(), { getStatementMapping });
    await go("#/accounting/statement-mapping");
    await userEvent.click(within(await screen.findByTestId("sm-version-2")).getByRole("button", { name: A("sm.view") }));
    expect(await screen.findByTestId("sm-old-version")).toHaveTextContent("2");
    expect(screen.getByLabelText(`4101 — ${A("sm.section")}`)).toBeDisabled();
    expect(screen.queryByTestId("sm-save")).not.toBeInTheDocument();
  });

  it("reports a concurrent change (stale version) and reloads instead of overwriting", async () => {
    const client = renderMap(map(), { saveStatementMapping: vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 5 })) });
    await go("#/accounting/statement-mapping");
    await userEvent.selectOptions(await screen.findByLabelText(`4101 — ${A("sm.section")}`), "other_income");
    await userEvent.click(screen.getByTestId("sm-save"));
    expect(await screen.findByTestId("sm-error")).toHaveTextContent(A("sm.error.version_conflict"));
    await waitFor(() => expect((client.getStatementMapping as unknown as { mock: { calls: unknown[] } }).mock.calls.length).toBeGreaterThan(1));
  });

  it("is not reachable without the classification permission", async () => {
    renderMap(map(), {}, [...ALL_PERMISSIONS, "company.financialstatements.view"]);
    await go("#/accounting/statement-mapping");
    expect(screen.queryByRole("heading", { name: A("sm.title"), level: 1 })).not.toBeInTheDocument();
  });
});
