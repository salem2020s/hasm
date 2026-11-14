import type {
  Account, ApprovalPoliciesResponse, CostCenter, CostCentersResponse, ExchangeRatesOverview, JournalEntry, JournalHeader, JournalListResponse, NumberingResponse,
} from "@hasm/api-client";
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

const base = [...ALL_PERMISSIONS, "company.accounts.view"];

// ------------------------------------------------------------------------------------------------------------------------------ numbering
const rule = (over: Partial<NumberingResponse["rules"][number]> = {}): NumberingResponse["rules"][number] =>
  ({ docType: "journal_entry", prefix: "JV", separator: "-", width: 6, perBranch: false, perFiscalYear: true, revision: 0, configured: false, numbersIssued: false, ...over });

describe("document numbering screen", () => {
  const render = (rules = [rule()], can = true, put = vi.fn().mockResolvedValue(rule({ revision: 1 }))) => {
    const client = signedIn({ getNumbering: vi.fn().mockResolvedValue({ rules, can: { configure: can } } satisfies NumberingResponse), putNumberingRule: put }, [...base, "company.numbering.view", "company.numbering.configure"]);
    renderApp(client);
    return client;
  };

  it("shows the format with a live sample number and saves exactly what was entered with the rule revision", async () => {
    const client = render();
    await go("#/company/numbering");
    expect(await screen.findByRole("heading", { name: A("numbering.title"), level: 1 })).toBeInTheDocument();
    expect(screen.getByTestId("numbering-sample-journal_entry")).toHaveTextContent("JV-2026-000001");
    await userEvent.clear(screen.getByLabelText(lbl("numbering.prefix")));
    await userEvent.type(screen.getByLabelText(lbl("numbering.prefix")), "GL");
    await userEvent.clear(screen.getByLabelText(lbl("numbering.width")));
    await userEvent.type(screen.getByLabelText(lbl("numbering.width")), "٤");
    await userEvent.click(screen.getByLabelText(A("numbering.per_branch")));
    expect(screen.getByTestId("numbering-sample-journal_entry")).toHaveTextContent("GL-2026-BR1-0001");
    await userEvent.click(screen.getByRole("button", { name: A("common.save") }));
    await waitFor(() => expect(client.putNumberingRule).toHaveBeenCalledWith("journal_entry", { prefix: "GL", separator: "-", width: 4, perBranch: true, perFiscalYear: true }, 0));
  });

  it("locks the sequence scope after numbers were issued and hides saving without the configure permission", async () => {
    render([rule({ numbersIssued: true, configured: true, revision: 2 })], false);
    await go("#/company/numbering");
    expect(await screen.findByTestId("numbering-scope-locked")).toBeInTheDocument();
    expect(screen.getByLabelText(A("numbering.per_year"))).toBeDisabled();
    expect(screen.queryByRole("button", { name: A("common.save") })).not.toBeInTheDocument();
  });

  it("reports an edit made elsewhere without losing the screen", async () => {
    const put = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 3 }));
    render([rule()], true, put);
    await go("#/company/numbering");
    await userEvent.type(await screen.findByLabelText(lbl("numbering.prefix")), "X");
    await userEvent.click(screen.getByRole("button", { name: A("common.save") }));
    expect(await screen.findByTestId("numbering-conflict")).toHaveTextContent(A("common.conflict"));
  });

  it("is not reachable without the view permission", async () => {
    renderApp(signedIn({}, base));
    await go("#/company/numbering");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------------------------------------ exchange rates
const overview = (over: Partial<ExchangeRatesOverview> = {}, can = true): ExchangeRatesOverview => ({
  direction: "base_per_foreign_unit", baseCurrency: "JOD", baseMinorUnits: 3, can: { create: can },
  currencies: [
    { currencyCode: "USD", nameEn: "US Dollar", minorUnits: 2, latestId: 7, latest: { id: 7, currencyCode: "USD", rate: "0.7100000000", validFrom: "2026-01-01", enteredAtUtc: "2026-01-01T08:00:00Z", enteredBy: "u", note: null } },
    { currencyCode: "EUR", nameEn: "Euro", minorUnits: 2, latestId: 0, latest: null },
  ], ...over,
});

describe("exchange rates screen", () => {
  const render = (o = overview(), enter = vi.fn().mockResolvedValue({ id: 8 }), extra: Record<string, unknown> = {}) => {
    const client = signedIn({
      getExchangeRates: vi.fn().mockResolvedValue(o), getExchangeRateHistory: vi.fn().mockResolvedValue({ rates: [] }), enterExchangeRate: enter,
      getLedgerSettings: vi.fn().mockResolvedValue({ roundingAccountId: null, revision: 0, configured: false }), ...extra,
    }, [...base, "company.exchangerates.view", "company.exchangerates.create"]);
    renderApp(client);
    return client;
  };

  it("shows the latest rate per currency, marks a missing one and explains the direction", async () => {
    render();
    await go("#/company/exchange-rates");
    expect(await screen.findByTestId("fx-row-USD")).toHaveTextContent("0.7100000000");
    expect(screen.getByTestId("fx-row-EUR")).toHaveTextContent(A("fx.missing"));
    expect(screen.getByText(/وحدة واحدة من العملة الأجنبية/)).toBeInTheDocument();
  });

  it("validates the rate and sends the exact decimal text with the latest-entry guard of the chosen currency", async () => {
    const client = render();
    await go("#/company/exchange-rates");
    const rate = await screen.findByLabelText(lbl("fx.rate"));
    await userEvent.type(rate, "0.12345678901");
    expect(screen.getByText(A("fx.rate_invalid"))).toBeInTheDocument();
    expect(screen.getByRole("button", { name: A("fx.save") })).toBeDisabled();
    await userEvent.clear(rate);
    await userEvent.type(rate, "٠٫٧٢٥٠");
    await userEvent.click(screen.getByRole("button", { name: A("fx.save") }));
    await waitFor(() => expect(client.enterExchangeRate).toHaveBeenCalledWith({ currencyCode: "USD", rate: "0.7250", validFrom: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), note: null }, 7));
  });

  it("shows the concurrent-entry conflict and the translated rule errors", async () => {
    render(overview(), vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 9 })).mockRejectedValueOnce(apiError(409, "rate_unchanged")));
    await go("#/company/exchange-rates");
    await userEvent.type(await screen.findByLabelText(lbl("fx.rate")), "0.8");
    await userEvent.click(screen.getByRole("button", { name: A("fx.save") }));
    expect(await screen.findByTestId("fx-conflict")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: A("fx.save") }));
    expect(await screen.findByTestId("fx-error")).toHaveTextContent(A("fx.error.rate_unchanged"));
  });

  it("offers no entry form without the create permission and says so when no foreign currency is enabled", async () => {
    render(overview({}, false));
    await go("#/company/exchange-rates");
    await screen.findByTestId("fx-table");
    expect(screen.queryByTestId("fx-form")).not.toBeInTheDocument();
  });

  it("reports an empty currency list", async () => {
    render(overview({ currencies: [] }));
    await go("#/company/exchange-rates");
    expect(await screen.findByTestId("fx-none")).toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------------------------------------ cost centers
const cc = (over: Partial<CostCenter> = {}): CostCenter => ({
  id: "c1", code: "OPS", nameAr: "التشغيل", nameEn: "Operations", parentId: null, branchId: null, isGroup: true, isActive: true, canReceiveAllocations: false, hasChildren: true, revision: 1,
  createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", ...over,
});
const ccAll = { create: true, update: true, activate: true, recode: true, restructure: true };
const ccResponse = (list: CostCenter[], can = ccAll): CostCentersResponse => ({ costCenters: list, can });

describe("cost centers screen", () => {
  const tree = [cc(), cc({ id: "c2", code: "OPS-1", nameAr: "المبيعات", nameEn: "Sales", parentId: "c1", isGroup: false, canReceiveAllocations: true, hasChildren: false, revision: 4 })];
  const render = (resp = ccResponse(tree), extra: Record<string, unknown> = {}) => {
    const client = signedIn({ listCostCenters: vi.fn().mockResolvedValue(resp), createCostCenter: vi.fn().mockResolvedValue(cc()), updateCostCenter: vi.fn().mockResolvedValue(cc()),
      recodeCostCenter: vi.fn().mockResolvedValue(cc()), moveCostCenter: vi.fn().mockResolvedValue(cc()), deactivateCostCenter: vi.fn().mockResolvedValue(cc()), activateCostCenter: vi.fn().mockResolvedValue(cc()), ...extra },
      [...base, "company.costcenters.view"]);
    renderApp(client);
    return client;
  };

  it("shows the tree with group and leaf kinds and states that groups receive no allocation", async () => {
    render();
    await go("#/company/cost-centers");
    expect(await screen.findByTestId("cc-row-OPS")).toHaveTextContent(A("cc.group"));
    expect(screen.getByTestId("cc-row-OPS-1")).toHaveTextContent(A("cc.leaf"));
    expect(screen.getByText(A("cc.allocation_note"))).toBeInTheDocument();
  });

  it("creates a child under a group with the normalised code and both names", async () => {
    const client = render();
    await go("#/company/cost-centers");
    await userEvent.click(await within(await screen.findByTestId("cc-row-OPS")).findByRole("button", { name: A("cc.add_child") }));
    await userEvent.type(screen.getByLabelText(lbl("cc.code")), " ops-٢ ");
    await userEvent.type(screen.getByLabelText(lbl("cc.name_en")), "Support");
    await userEvent.click(screen.getByRole("button", { name: A("common.save") }));
    await waitFor(() => expect(client.createCostCenter).toHaveBeenCalledWith({ code: "OPS-2", parentId: "c1", nameAr: null, nameEn: "Support", branchId: null, isGroup: false }));
  });

  it("re-codes, moves and deactivates with the item revision and maps a duplicate code to a clear message", async () => {
    const client = render(ccResponse(tree), { recodeCostCenter: vi.fn().mockRejectedValue(apiError(409, "code_in_use")) });
    await go("#/company/cost-centers");
    const row = await screen.findByTestId("cc-row-OPS-1");
    await userEvent.click(within(row).getByRole("button", { name: A("cc.deactivate") }));
    await waitFor(() => expect(client.deactivateCostCenter).toHaveBeenCalledWith("c2", 4));
    await userEvent.click(within(screen.getByTestId("cc-row-OPS-1")).getByRole("button", { name: A("cc.move") }));
    await userEvent.click(await screen.findByRole("button", { name: A("common.save") }));
    await waitFor(() => expect(client.moveCostCenter).toHaveBeenCalledWith("c2", "c1", 4));
    await userEvent.click(within(screen.getByTestId("cc-row-OPS-1")).getByRole("button", { name: A("cc.recode") }));
    const code = await screen.findByLabelText(lbl("cc.code"));
    await userEvent.clear(code);
    await userEvent.type(code, "OPS");
    await userEvent.click(screen.getByRole("button", { name: A("common.save") }));
    expect(await screen.findByTestId("cc-error")).toHaveTextContent(A("cc.error.code_in_use"));
  });

  it("hides every action the server did not grant and the new button", async () => {
    render(ccResponse(tree, { create: false, update: false, activate: false, recode: false, restructure: false }));
    await go("#/company/cost-centers");
    const row = await screen.findByTestId("cc-row-OPS-1");
    expect(within(row).queryByRole("button")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("cc.new") })).not.toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------------------------------------ approval policies
describe("approval policies screen", () => {
  const pol = { id: "p1", docType: "journal_entry", source: "manual" as const, branchId: null, minAmount: "1000.0000", requiredApprovals: 2, nameAr: null, nameEn: "Large manual", isActive: true, revision: 3, createdAtUtc: "", updatedAtUtc: "" };
  const render = (resp: ApprovalPoliciesResponse = { policies: [pol], documentTypes: ["journal_entry"], can: { create: true, update: true, activate: true } }) => {
    const client = signedIn({ listApprovalPolicies: vi.fn().mockResolvedValue(resp), createApprovalPolicy: vi.fn().mockResolvedValue(pol), updateApprovalPolicy: vi.fn().mockResolvedValue(pol), deactivateApprovalPolicy: vi.fn().mockResolvedValue(pol), activateApprovalPolicy: vi.fn().mockResolvedValue(pol) },
      [...base, "company.approvalpolicies.view"]);
    renderApp(client);
    return client;
  };

  it("lists the policies and states that the creator can never approve their own entry", async () => {
    render();
    await go("#/company/approval-policies");
    expect(await screen.findByTestId("pol-row-p1")).toHaveTextContent("1000.0000");
    expect(screen.getByText(A("pol.sod_note"))).toBeInTheDocument();
  });

  it("creates a policy with an exact decimal threshold and refuses invalid values before calling the server", async () => {
    const client = render();
    await go("#/company/approval-policies");
    await userEvent.click(await screen.findByRole("button", { name: A("pol.new") }));
    await userEvent.type(screen.getByLabelText(lbl("cc.name_en")), "Big");
    const min = screen.getByLabelText(lbl("pol.min_amount"));
    await userEvent.clear(min);
    await userEvent.type(min, "1.00001");
    expect(screen.getByRole("button", { name: A("common.save") })).toBeDisabled();
    await userEvent.clear(min);
    await userEvent.type(min, "٥٠٠٠٫٥");
    await userEvent.clear(screen.getByLabelText(lbl("pol.required")));
    await userEvent.type(screen.getByLabelText(lbl("pol.required")), "3");
    await userEvent.click(screen.getByRole("button", { name: A("common.save") }));
    await waitFor(() => expect(client.createApprovalPolicy).toHaveBeenCalledWith({ docType: "journal_entry", source: "any", branchId: null, minAmount: "5000.5", requiredApprovals: 3, nameAr: null, nameEn: "Big", journalType: null, rateOverride: null, softClosed: null, closingKind: null }));
  });

  it("creates a staged policy with risk conditions sending the ordered stages instead of a single count", async () => {
    const client = render();
    await go("#/company/approval-policies");
    await userEvent.click(await screen.findByRole("button", { name: A("pol.new") }));
    await userEvent.type(screen.getByLabelText(lbl("cc.name_en")), "Risky");
    await userEvent.selectOptions(screen.getByLabelText(A("pol.cond_type")), "reversal");
    await userEvent.selectOptions(screen.getByLabelText(A("pol.cond_rate")), "yes");
    await userEvent.click(screen.getByLabelText(A("pol.staged")));
    expect(screen.queryByLabelText(lbl("pol.required"))).not.toBeInTheDocument();       // the classic count is replaced by the stages
    await userEvent.click(screen.getByRole("button", { name: A("pol.add_stage") }));
    await userEvent.clear(screen.getByLabelText(A("pol.stage_required").replace("{n}", "1")));
    await userEvent.type(screen.getByLabelText(A("pol.stage_required").replace("{n}", "1")), "1");
    await userEvent.clear(screen.getByLabelText(A("pol.stage_required").replace("{n}", "2")));
    await userEvent.type(screen.getByLabelText(A("pol.stage_required").replace("{n}", "2")), "٢");
    await userEvent.click(screen.getByRole("button", { name: A("common.save") }));
    await waitFor(() => expect(client.createApprovalPolicy).toHaveBeenCalledWith(expect.objectContaining({
      journalType: "reversal", rateOverride: true, softClosed: null, stages: [{ required: 1, roleId: null }, { required: 2, roleId: null }],
    })));
    expect((client.createApprovalPolicy as unknown as { mock: { calls: Record<string, unknown>[][] } }).mock.calls[0]![0]).not.toHaveProperty("requiredApprovals");
  });

  it("refuses a staged policy whose stage count is invalid before calling the server", async () => {
    const client = render();
    await go("#/company/approval-policies");
    await userEvent.click(await screen.findByRole("button", { name: A("pol.new") }));
    await userEvent.type(screen.getByLabelText(lbl("cc.name_en")), "Bad");
    await userEvent.click(screen.getByLabelText(A("pol.staged")));
    await userEvent.clear(screen.getByLabelText(A("pol.stage_required").replace("{n}", "1")));
    await userEvent.type(screen.getByLabelText(A("pol.stage_required").replace("{n}", "1")), "0");
    expect(screen.getByRole("button", { name: A("common.save") })).toBeDisabled();
    expect(client.createApprovalPolicy).not.toHaveBeenCalled();
  });

  it("edits and deactivates with the policy revision", async () => {
    const client = render();
    await go("#/company/approval-policies");
    await userEvent.click(await within(await screen.findByTestId("pol-row-p1")).findByRole("button", { name: A("cc.deactivate") }));
    await waitFor(() => expect(client.deactivateApprovalPolicy).toHaveBeenCalledWith("p1", 3));
  });
});

// ------------------------------------------------------------------------------------------------------------------------------ journal
const header = (over: Partial<JournalHeader> = {}): JournalHeader => ({
  id: "j1", number: null, status: "draft", type: "normal", source: "manual", sourceKey: null, entryDate: "2026-03-10", description: "Rent", branchId: "b1", baseCurrency: "JOD", baseTotal: "100.0000",
  revision: 1, createdBy: "u1", createdAtUtc: "2026-03-10T00:00:00Z", updatedAtUtc: "2026-03-10T00:00:00Z", submittedBy: null, submittedAtUtc: null, postedBy: null, postedAtUtc: null,
  periodId: null, postedInSoftClosedPeriod: false, reversesId: null, adjustsId: null, ...over,
});
const line = (over: Record<string, unknown> = {}) => ({
  id: "l1", lineNo: 1, accountId: "a1", description: null, currency: "JOD", debit: "100.0000", credit: "0.0000", rate: "1.0000000000", rateId: null, rateOverrideReason: null,
  baseDebit: "100.0000", baseCredit: "0.0000", branchId: "b1", isRounding: false, reversesLineId: null, allocations: [], ...over,
});
const entry = (status: JournalHeader["status"], over: Partial<JournalHeader> = {}, extra: Partial<JournalEntry> = {}): JournalEntry => ({
  entry: header({ status, ...over }), balanced: true, baseDebitTotal: "100.0000", baseCreditTotal: "100.0000", requiredApprovals: 1,
  lines: [line(), line({ id: "l2", lineNo: 2, accountId: "a2", debit: "0.0000", credit: "100.0000", baseDebit: "0.0000", baseCredit: "100.0000" })], approvals: [], events: [{ id: 1, kind: "Created", actor: "u1", atUtc: "2026-03-10T00:00:00Z", revision: 1, detail: null }], ...extra,
});
const accounts: Account[] = [
  { id: "a1", code: "1101", nameAr: "الصندوق", nameEn: "Cash", parentId: null, type: "asset", nature: "debit", statement: "balance_sheet", isGroup: false, isActive: true, postable: true, hasChildren: false, currencyPolicy: "any", currencyCode: null, template: null, revision: 1, createdAtUtc: "", updatedAtUtc: "" },
  { id: "a2", code: "4101", nameAr: "المبيعات", nameEn: "Sales", parentId: null, type: "revenue", nature: "credit", statement: "profit_and_loss", isGroup: false, isActive: true, postable: true, hasChildren: false, currencyPolicy: "any", currencyCode: null, template: null, revision: 1, createdAtUtc: "", updatedAtUtc: "" },
];
const jcan = (over: Partial<JournalListResponse["can"]> = {}): JournalListResponse["can"] => ({ create: true, approve: true, post: true, reverse: true, rateOverride: false, postInSoftClosed: false, ...over });

describe("journal screen", () => {
  const render = (current: JournalEntry | null, can = jcan(), extra: Record<string, unknown> = {}, list: JournalHeader[] | null = null) => {
    const client = signedIn({
      listJournal: vi.fn().mockResolvedValue({ entries: list ?? (current ? [current.entry] : []), can } satisfies JournalListResponse),
      getJournalEntry: vi.fn().mockResolvedValue(current),
      listAccounts: vi.fn().mockResolvedValue({ accounts, settings: null, templates: [], usageChecksImplemented: true, can: {} }),
      getCurrencies: vi.fn().mockResolvedValue({ currencies: [{ code: "JOD", nameEn: "Dinar", minorUnits: 3, isBase: true, enabled: true, revision: 1 }, { code: "USD", nameEn: "Dollar", minorUnits: 2, isBase: false, enabled: true, revision: 1 }], baseCurrency: "JOD", baseCurrencySelected: true, baseCurrencyLock: { locked: false, checksImplemented: true, unimplementedDomains: [] } }),
      listBranches: vi.fn().mockResolvedValue({ branches: [{ id: "b1", code: "MAIN", nameAr: "الرئيسي", nameEn: "Main", isActive: true, isMain: true }], limit: null, everCreated: null, can: { create: false, activate: false, designate: false, update: [] } }),
      listAllocatableCostCenters: vi.fn().mockResolvedValue({ costCenters: [{ id: "cc1", code: "OPS-1", nameAr: "المبيعات", nameEn: "Sales", parentId: null, branchId: null }, { id: "cc2", code: "OPS-2", nameAr: "الإنتاج", nameEn: "Production", parentId: null, branchId: null }] }),
      createJournalEntry: vi.fn().mockResolvedValue(entry("draft")), updateJournalEntry: vi.fn().mockResolvedValue(entry("draft", { revision: 2 })),
      submitJournalEntry: vi.fn().mockResolvedValue(entry("approved")), withdrawJournalEntry: vi.fn().mockResolvedValue(entry("draft", { revision: 2 })), cancelJournalEntry: vi.fn().mockResolvedValue(entry("cancelled")),
      decideJournalEntry: vi.fn().mockResolvedValue(entry("approved")), postJournalEntry: vi.fn().mockResolvedValue(entry("posted", { number: "JV-2026-000001" })), reverseJournalEntry: vi.fn().mockResolvedValue(entry("draft", { id: "j2", type: "reversal" })),
      ...extra,
    }, [...base, "company.journal.view", "company.currencies.view", "company.costcenters.view"]);
    renderApp(client);
    return client;
  };
  const open = async (id = "j1") => { await go("#/accounting/journal"); await userEvent.click(await within(await screen.findByTestId(`jrn-row-${id}`)).findByRole("button", { name: A("jrn.open") })); };

  it("lists entries with their status and number and shows an empty state", async () => {
    render(entry("posted", { number: "JV-2026-000001" }));
    await go("#/accounting/journal");
    const row = await screen.findByTestId("jrn-row-j1");
    expect(row).toHaveTextContent("JV-2026-000001");
    expect(row).toHaveTextContent(A("jrn.status.posted"));
  });

  it("shows an empty message when nothing matches", async () => {
    render(null);
    await go("#/accounting/journal");
    expect(await screen.findByTestId("jrn-empty")).toBeInTheDocument();
  });

  it("shows the ordered approval stages with their progress for a pending entry", async () => {
    render(entry("pending_approval", {}, { requiredApprovals: 4, plan: [{ stage: 1, required: 1, approved: 1, roleId: null }, { stage: 2, required: 2, approved: 1, roleId: null }, { stage: 3, required: 1, approved: 0, roleId: null }] }));
    await open();
    const plan = await screen.findByTestId("jrn-plan");
    expect(within(plan).getByTestId("jrn-plan-1")).toHaveTextContent(A("jrn.plan_done"));
    expect(within(plan).getByTestId("jrn-plan-2")).toHaveTextContent(A("jrn.plan_open"));
    expect(within(plan).getByTestId("jrn-plan-2")).toHaveTextContent("1/2");
    expect(within(plan).getByTestId("jrn-plan-3")).toHaveTextContent(A("jrn.plan_waiting"));
  });

  it("applies the filters through the server query", async () => {
    const client = render(entry("draft"));
    await go("#/accounting/journal");
    await userEvent.selectOptions(await screen.findByLabelText(lbl("jrn.filter_status")), "posted");
    await userEvent.click(screen.getByRole("button", { name: A("jrn.apply_filters") }));
    await waitFor(() => expect(client.listJournal).toHaveBeenLastCalledWith(expect.objectContaining({ status: "posted" })));
  });

  it("creates a new draft sending exact decimal text, blocks saving until every line is valid and shows typed totals", async () => {
    const client = render(null);
    await go("#/accounting/journal");
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.new") }));
    const save = await screen.findByRole("button", { name: A("jrn.save_draft") });
    expect(save).toBeDisabled();
    const accountSelects = screen.getAllByLabelText(A("jrn.account"));
    await userEvent.selectOptions(accountSelects[0]!, "a1");
    await userEvent.selectOptions(accountSelects[1]!, "a2");
    await userEvent.type(screen.getAllByLabelText(A("jrn.debit"))[0]!, "١٢٥٫٥٠٠");
    await userEvent.type(screen.getAllByLabelText(A("jrn.credit"))[1]!, "125.5");
    expect(screen.getByTestId("jrn-total-JOD")).toHaveTextContent("125.5000");
    expect(save).toBeEnabled();
    await userEvent.click(save);
    await waitFor(() => expect(client.createJournalEntry).toHaveBeenCalledWith(expect.objectContaining({
      branchId: "b1", lines: [expect.objectContaining({ accountId: "a1", debit: "125.500", credit: null }), expect.objectContaining({ accountId: "a2", debit: null, credit: "125.5" })],
    })));
  });

  it("allocates a line by percentage, sending percentages (never guessed amounts) and offering only the cost centers the server allows", async () => {
    const client = render(null);
    await go("#/accounting/journal");
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.new") }));
    const accountSelects = await screen.findAllByLabelText(A("jrn.account"));
    await userEvent.selectOptions(accountSelects[0]!, "a1");
    await userEvent.selectOptions(accountSelects[1]!, "a2");
    await userEvent.type(screen.getAllByLabelText(A("jrn.debit"))[0]!, "100");
    await userEvent.type(screen.getAllByLabelText(A("jrn.credit"))[1]!, "100");
    await userEvent.click(screen.getAllByRole("button", { name: A("jrn.add_allocation") })[0]!);
    await userEvent.click(screen.getAllByRole("button", { name: A("jrn.add_allocation") })[0]!);
    const centers = screen.getAllByLabelText(A("jrn.cost_center"));
    expect(Array.from((centers[0] as HTMLSelectElement).options).map((o) => o.value)).toEqual(["", "cc1", "cc2"]);
    await userEvent.selectOptions(centers[0]!, "cc1");
    await userEvent.selectOptions(centers[1]!, "cc2");
    await userEvent.selectOptions(screen.getByLabelText(A("jrn.alloc_mode")), "percent");
    const pct = screen.getAllByLabelText(A("jrn.allocation_percent"));
    await userEvent.type(pct[0]!, "٣٣٫٣٣٣٣");
    await userEvent.type(pct[1]!, "66.6667");
    await userEvent.click(screen.getByRole("button", { name: A("jrn.save_draft") }));
    await waitFor(() => expect(client.createJournalEntry).toHaveBeenCalledWith(expect.objectContaining({
      lines: [expect.objectContaining({ allocations: [{ costCenterId: "cc1", percent: "33.3333" }, { costCenterId: "cc2", percent: "66.6667" }] }), expect.objectContaining({ allocations: [] })],
    })));
  });

  it("flags an invalid amount and a line with both sides", async () => {
    render(null);
    await go("#/accounting/journal");
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.new") }));
    const accountSelects = await screen.findAllByLabelText(A("jrn.account"));
    await userEvent.selectOptions(accountSelects[0]!, "a1");
    await userEvent.selectOptions(accountSelects[1]!, "a2");
    await userEvent.type(screen.getAllByLabelText(A("jrn.debit"))[0]!, "1.23456");
    expect(screen.getByTestId("jrn-problems")).toHaveTextContent(A("jrn.problem_amount"));
    expect(screen.getByRole("button", { name: A("jrn.save_draft") })).toBeDisabled();
  });

  it("walks a draft through submit, and an approved entry through post, reporting the server's rule errors translated", async () => {
    const client = render(entry("draft"), jcan(), { getJournalEntry: vi.fn().mockResolvedValueOnce(entry("draft")).mockResolvedValue(entry("approved")), postJournalEntry: vi.fn().mockRejectedValue(apiError(409, "fiscal_period_final_closed")) });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.submit") }));
    await waitFor(() => expect(client.submitJournalEntry).toHaveBeenCalledWith("j1", 1));
    // After the submit the screen shows the approved entry: posting is offered and a refused posting shows the translated reason.
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.post") }));
    expect(await screen.findByTestId("jrn-error")).toHaveTextContent(A("jrn.error.fiscal_period_final_closed"));
  });

  it("offers post only to those who may post and only on an approved entry", async () => {
    render(entry("approved"), jcan({ post: false }));
    await open();
    await screen.findByTestId("jrn-lines");
    expect(screen.queryByRole("button", { name: A("jrn.post") })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("jrn.save_draft") })).not.toBeInTheDocument();           // an approved entry is read-only
  });

  it("records an approval with an optional comment and a rejection only with a reason, both against the revision", async () => {
    const client = render(entry("pending_approval", { revision: 5 }));
    await open();
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.approve") }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: A("jrn.approve") }));
    await waitFor(() => expect(client.decideJournalEntry).toHaveBeenCalledWith("j1", true, null, 5));
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.reject") }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByRole("button", { name: A("jrn.reject") })).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(lbl("jrn.reject_reason")), "wrong account");
    await userEvent.click(within(dialog).getByRole("button", { name: A("jrn.reject") }));
    await waitFor(() => expect(client.decideJournalEntry).toHaveBeenCalledWith("j1", false, "wrong account", 5));
  });

  it("shows the separation-of-duties refusal from the server", async () => {
    render(entry("pending_approval"), jcan(), { decideJournalEntry: vi.fn().mockRejectedValue(apiError(403, "separation_of_duties")) });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.approve") }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: A("jrn.approve") }));
    expect(await screen.findByTestId("jrn-error")).toHaveTextContent(A("jrn.error.separation_of_duties"));
  });

  it("reverses a posted entry with a chosen date and opens the drafted reversal", async () => {
    const client = render(entry("posted", { number: "JV-2026-000001" }), jcan(), { getJournalEntry: vi.fn().mockImplementation((id: string) => Promise.resolve(id === "j2" ? entry("draft", { id: "j2", type: "reversal" }) : entry("posted", { number: "JV-2026-000001" }))) });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: A("jrn.reverse") }));
    const dialog = screen.getByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText(lbl("jrn.reverse_date")), { target: { value: "2026-04-02" } });
    await userEvent.click(within(dialog).getByRole("button", { name: A("jrn.reverse") }));
    await waitFor(() => expect(client.reverseJournalEntry).toHaveBeenCalledWith("j1", "2026-04-02", null, 1));
    expect(await screen.findByText(A("jrn.is_reversal"))).toBeInTheDocument();
  });

  it("shows approvals and the event history of an entry and flags an unbalanced one", async () => {
    render({ ...entry("draft"), balanced: false, baseDebitTotal: "100.0000", baseCreditTotal: "90.0000", approvals: [{ id: 1, documentRevision: 1, approver: "abcdefgh-0000", approved: true, decidedAtUtc: "2026-03-11T00:00:00Z", comment: "ok" }] });
    await open();
    expect(await screen.findByTestId("jrn-balance")).toHaveTextContent(A("jrn.unbalanced"));
    expect(screen.getByTestId("jrn-approvals")).toHaveTextContent("ok");
    expect(screen.getByTestId("jrn-events")).toHaveTextContent(A("jrn.event.Created"));
  });

  it("shows the concurrent-edit conflict and the missing chart permission", async () => {
    const client = render(entry("draft"), jcan(), { updateJournalEntry: vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 4 })), listAccounts: vi.fn().mockRejectedValue(apiError(403, "forbidden")) });
    await open();
    expect(await screen.findByTestId("jrn-no-accounts")).toBeInTheDocument();
    expect(client.listAccounts).toHaveBeenCalled();
  });
});

// ------------------------------------------------------------------------------------------------------------------------------ reports
describe("ledger reports screen", () => {
  const tb = { from: "2026-01-01", to: "2026-12-31", baseCurrency: "JOD", balanced: true, totals: { opening: "0.0000", debit: "300.0000", credit: "300.0000", closing: "0.0000" },
    rows: [{ accountId: "a1", code: "1101", nameAr: "الصندوق", nameEn: "Cash", parentId: null, level: 0, isGroup: false, opening: "0.0000", debit: "300.0000", credit: "0.0000", closing: "300.0000" }] };
  const gl = { from: "2026-01-01", to: "2026-12-31", baseCurrency: "JOD", accountId: "a1", includesDescendants: false, opening: "0.0000", totalDebit: "300.0000", totalCredit: "0.0000", closing: "300.0000", totalLines: 1, offset: 0,
    lines: [{ entryId: "j1", number: "JV-2026-000001", entryDate: "2026-03-10", entryType: "normal", entryDescription: "Rent", lineDescription: null, accountId: "a1", currency: "USD", debit: "100.0000", credit: "0.0000", rate: "0.7100000000", rateId: 1, baseDebit: "71.0000", baseCredit: "0.0000", branchId: "b1", isRounding: false, runningBalance: "71.0000" }] };
  const render = (permissions: string[] = [...base, "company.ledgerreports.view"], extra: Record<string, unknown> = {}) => {
    const client = signedIn({
      listAccounts: vi.fn().mockResolvedValue({ accounts, settings: null, templates: [], usageChecksImplemented: true, can: {} }),
      listBranches: vi.fn().mockResolvedValue({ branches: [], limit: null, everCreated: null, can: { create: false, activate: false, designate: false, update: [] } }),
      listCostCenters: vi.fn().mockResolvedValue({ costCenters: [], can: ccAll }),
      getTrialBalance: vi.fn().mockResolvedValue(tb), getGeneralLedger: vi.fn().mockResolvedValue(gl),
      downloadTrialBalanceCsv: vi.fn().mockResolvedValue(new Blob(["x"])), downloadGeneralLedgerCsv: vi.fn().mockResolvedValue(new Blob(["x"])), ...extra,
    }, permissions);
    renderApp(client);
    return client;
  };

  it("shows the trial balance with exact totals for the chosen dates", async () => {
    const client = render();
    await go("#/accounting/reports");
    fireEvent.change(await screen.findByLabelText(lbl("rep.from")), { target: { value: "2026-01-01" } });
    fireEvent.change(screen.getByLabelText(lbl("rep.to")), { target: { value: "2026-12-31" } });
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("rep-tb-1101")).toHaveTextContent("300.0000");
    expect(screen.getByTestId("rep-tb-total")).toHaveTextContent("300.0000");
    expect(client.getTrialBalance).toHaveBeenCalledWith(expect.objectContaining({ from: "2026-01-01", to: "2026-12-31" }));
    expect(screen.queryByTestId("rep-unbalanced")).not.toBeInTheDocument();
  });

  it("shows the general ledger of an account with the original currency, rate and running balance", async () => {
    const client = render();
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByRole("tab", { name: A("rep.general_ledger") }));
    expect(screen.getByRole("button", { name: A("rep.show") })).toBeDisabled();                       // an account is required
    await userEvent.selectOptions(screen.getByLabelText(lbl("rep.account")), "a1");
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    const row = await screen.findByTestId("rep-gl-row-0");
    expect(row).toHaveTextContent("USD");
    expect(row).toHaveTextContent("0.7100000000");
    expect(row).toHaveTextContent("71.0000");
    expect(client.getGeneralLedger).toHaveBeenCalledWith("a1", expect.objectContaining({ from: expect.any(String) }), 0);
  });

  it("offers the CSV download only with the export permission", async () => {
    render();
    await go("#/accounting/reports");
    await screen.findByTestId("rep-form");
    expect(screen.queryByRole("button", { name: A("rep.download_csv") })).not.toBeInTheDocument();
  });

  it("downloads the CSV through the client when exporting is allowed", async () => {
    const created = vi.fn().mockReturnValue("blob:x");
    URL.createObjectURL = created;
    URL.revokeObjectURL = vi.fn();
    const client = render([...base, "company.ledgerreports.view", "company.ledgerreports.export"]);
    await go("#/accounting/reports");
    await userEvent.click(await screen.findByRole("button", { name: A("rep.download_csv") }));
    await waitFor(() => expect(client.downloadTrialBalanceCsv).toHaveBeenCalled());
    expect(created).toHaveBeenCalled();
  });

  it("shows a translated error and does not call the server for an inverted range", async () => {
    const client = render(undefined, { getTrialBalance: vi.fn().mockRejectedValue(apiError(400, "invalid_range")) });
    await go("#/accounting/reports");
    fireEvent.change(await screen.findByLabelText(lbl("rep.from")), { target: { value: "2026-05-01" } });
    fireEvent.change(screen.getByLabelText(lbl("rep.to")), { target: { value: "2026-01-01" } });
    expect(screen.getByRole("button", { name: A("rep.show") })).toBeDisabled();
    fireEvent.change(screen.getByLabelText(lbl("rep.to")), { target: { value: "2026-12-31" } });
    await userEvent.click(screen.getByRole("button", { name: A("rep.show") }));
    expect(await screen.findByTestId("rep-error")).toHaveTextContent(A("rep.error.invalid_range"));
    expect(client.getTrialBalance).toHaveBeenCalledTimes(1);
  });
});

// ------------------------------------------------------------------------------------------------------------------------------ navigation
describe("navigation to the ledger screens", () => {
  it("shows the accounting group with the journal and reports by permission, and keeps the placeholder otherwise", async () => {
    renderApp(signedIn({}, [...base, "company.journal.view", "company.ledgerreports.view"]));
    await screen.findByTestId("company-name");
    await userEvent.hover(screen.getByRole("menuitem", { name: new RegExp(A("nav.accounting")) }));
    expect(await screen.findByRole("menuitem", { name: A("nav.journal") })).toBeInTheDocument();
    expect(screen.getByRole("menuitem", { name: A("nav.gl_reports") })).toBeInTheDocument();
  });

  it("keeps accounting disabled with a coming-soon badge when the user holds no ledger permission", async () => {
    renderApp(signedIn({}, base));
    await screen.findByTestId("company-name");
    expect(screen.getByRole("menuitem", { name: new RegExp(A("nav.accounting")) })).toHaveAttribute("aria-disabled", "true");
    expect(screen.queryByRole("menuitem", { name: A("nav.journal") })).not.toBeInTheDocument();
  });

  it("lists the company configuration screens by their own permissions", async () => {
    renderApp(signedIn({}, [...base, "company.numbering.view", "company.exchangerates.view", "company.costcenters.view", "company.approvalpolicies.view"]));
    await screen.findByTestId("company-name");
    await userEvent.hover(screen.getByRole("menuitem", { name: /إدارة الشركة/ }));
    for (const k of ["nav.numbering", "nav.exchange_rates", "nav.cost_centers", "nav.approval_policies"]) expect(await screen.findByRole("menuitem", { name: A(k) })).toBeInTheDocument();
  });
});
