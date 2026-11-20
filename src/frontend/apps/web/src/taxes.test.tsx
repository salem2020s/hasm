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

const perms = [...ALL_PERMISSIONS, "company.tax.view"];
const vat = { id: "t1", code: "VAT", nameAr: "ضريبة القيمة المضافة", nameEn: "VAT", isActive: true, revision: 2, updatedAtUtc: "2026-01-01T00:00:00Z", rates: [{ effectiveFrom: "2020-01-01", treatment: 1, ratePercent: 15 }] };
const excise = { ...vat, id: "t2", code: "EXC", nameAr: "ضريبة الانتقائية", nameEn: "Excise", revision: 1, rates: [] };
const food = { id: "k1", kind: 1, code: "FOOD", nameAr: "غذاء", nameEn: "Food", isActive: true, revision: 1 };
const exporter = { id: "k2", kind: 2, code: "EXPORTER", nameAr: "مصدّر", nameEn: "Exporter", isActive: true, revision: 1 };
const rule = { id: "r1", documentType: "sales_invoice", operation: "sale", itemClassId: "k1", partyClassId: null, taxId: "t1", priority: 10, includesTaxIds: [], validFrom: "2026-01-01", validTo: null, isActive: true, treatmentOverride: null, revision: 3 };

function render(over: Record<string, unknown> = {}, can = true) {
  const client = signedIn({
    listTaxes: vi.fn().mockResolvedValue({ taxes: [vat, excise], can: { configure: can } }),
    listTaxClasses: vi.fn().mockResolvedValue({ classes: [food, exporter], can: { configure: can } }),
    listTaxRules: vi.fn().mockResolvedValue({ rules: [rule], can: { configure: can } }),
    getTaxPolicy: vi.fn().mockResolvedValue({ policy: { configured: true, id: "p1", version: 2, moneyDecimals: 2, mode: 2, level: 1 }, can: { configure: can } }),
    createTax: vi.fn().mockResolvedValue({ ...vat, id: "t3", code: "NEW", rates: [] }),
    updateTax: vi.fn().mockResolvedValue({ ...vat, isActive: false, revision: 3 }),
    addTaxRate: vi.fn().mockResolvedValue(vat),
    createTaxClass: vi.fn().mockResolvedValue(food),
    updateTaxClass: vi.fn().mockResolvedValue(food),
    createTaxRule: vi.fn().mockResolvedValue(rule),
    updateTaxRule: vi.fn().mockResolvedValue(rule),
    addTaxPolicy: vi.fn().mockResolvedValue({ configured: true, version: 3 }),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}
const inTaxes = () => within(screen.getByTestId("tax-tab-taxes"));
const tab = (n: string) => screen.getByRole("tab", { name: new RegExp(`^${n}`) });

describe("tax configuration screen", () => {
  it("is organised in four tabs by business meaning and lists the taxes with their current dated rate", async () => {
    render();
    await go("#/company/taxes");
    expect(await screen.findByRole("heading", { name: A("tax.title"), level: 1 })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual([A("tax.tab.taxes"), A("tax.tab.classes"), A("tax.tab.rules"), A("tax.tab.policy")]);
    expect(within(await screen.findByTestId("tax-row-VAT")).getByText("15%")).toBeInTheDocument();
    expect(within(screen.getByTestId("tax-row-EXC")).getByText(A("tax.no_rate"))).toBeInTheDocument();
  });

  it("creates a tax and appends a rate version without any delete control anywhere", async () => {
    const client = render();
    await go("#/company/taxes");
    await screen.findByTestId("tax-tab-taxes");
    await userEvent.type(await inTaxes().findByLabelText(new RegExp(`^${A("tax.code")}`)), "NEW");
    await userEvent.type(inTaxes().getByLabelText(new RegExp(`^${A("tax.name_ar")}`)), "ضريبة جديدة");
    await userEvent.click(screen.getByRole("button", { name: A("tax.create_tax") }));
    await waitFor(() => expect(client.createTax).toHaveBeenCalledWith({ code: "NEW", nameAr: "ضريبة جديدة", nameEn: null }));

    await userEvent.click(within(await screen.findByTestId("tax-row-VAT")).getByRole("button", { name: A("tax.rates") }));
    expect(await screen.findByTestId("tax-rates-table")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(new RegExp(`^${A("tax.rate_percent")}`)), "16");
    fireEvent.change(screen.getByLabelText(new RegExp(`^${A("tax.effective_from")}`)), { target: { value: "2027-01-01" } });
    await userEvent.click(screen.getByRole("button", { name: A("tax.add_rate") }));
    await waitFor(() => expect(client.addTaxRate).toHaveBeenCalledWith("t1", { effectiveFrom: "2027-01-01", treatment: 1, ratePercent: 16 }));
    expect(screen.queryByRole("button", { name: /delete|حذف/i })).not.toBeInTheDocument();
  });

  it("deactivates with the revision it read and reports an edit made elsewhere", async () => {
    const update = vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 5 })).mockResolvedValue(vat);
    const client = render({ updateTax: update });
    await go("#/company/taxes");
    await userEvent.click(within(await screen.findByTestId("tax-row-VAT")).getByRole("button", { name: A("tax.deactivate") }));
    await waitFor(() => expect(update).toHaveBeenCalledWith("t1", { nameAr: vat.nameAr, nameEn: "VAT", isActive: false }, 2));
    expect(await screen.findByTestId("tax-conflict")).toBeInTheDocument();
    expect(client.listTaxes).toHaveBeenCalledTimes(2);                                   // refreshed after the conflict
  });

  it("keeps typed input when switching tabs and marks the tab with unsaved edits", async () => {
    render();
    await go("#/company/taxes");
    await screen.findByTestId("tax-tab-taxes");
    await userEvent.type(await inTaxes().findByLabelText(new RegExp(`^${A("tax.code")}`)), "ABC");
    await userEvent.click(tab(A("tax.tab.classes")));
    await userEvent.click(tab(A("tax.tab.taxes")));
    expect(inTaxes().getByLabelText(new RegExp(`^${A("tax.code")}`))).toHaveValue("ABC");
  });

  it("creates item and party classes separately and a rule with both, sending nothing a server would not accept", async () => {
    const client = render();
    await go("#/company/taxes");
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${A("tax.tab.classes")}`) }));
    expect(await screen.findByTestId("tax-class-1-FOOD")).toBeInTheDocument();
    expect(screen.getByTestId("tax-class-2-EXPORTER")).toBeInTheDocument();
    const panel = screen.getByTestId("tax-tab-classes");
    await userEvent.selectOptions(within(panel).getByLabelText(A("tax.class_kind")), "2");
    await userEvent.type(within(panel).getByLabelText(new RegExp(`^${A("tax.code")}`)), "gov");
    await userEvent.type(within(panel).getByLabelText(new RegExp(`^${A("tax.name_ar")}`)), "جهة حكومية");
    await userEvent.click(within(panel).getByRole("button", { name: A("tax.create_class") }));
    await waitFor(() => expect(client.createTaxClass).toHaveBeenCalledWith({ kind: 2, code: "gov", nameAr: "جهة حكومية", nameEn: null }));

    await userEvent.click(tab(A("tax.tab.rules")));
    const rules = screen.getByTestId("tax-tab-rules");
    expect(within(rules).getByTestId("tax-rule-r1")).toBeInTheDocument();
    const create = within(rules).getByRole("button", { name: A("tax.rule.create") });
    expect(create).toBeDisabled();                                                       // document type, operation and tax are required
    await userEvent.type(within(rules).getByLabelText(new RegExp(`^${A("tax.rule.document_type")}`)), "sales_invoice");
    await userEvent.type(within(rules).getByLabelText(new RegExp(`^${A("tax.rule.operation")}`)), "sale");
    await userEvent.selectOptions(within(rules).getByLabelText(new RegExp(`^${A("tax.rule.tax")}`)), "t2");
    await userEvent.selectOptions(within(rules).getByLabelText(A("tax.rule.item_class")), "k1");
    await userEvent.selectOptions(within(rules).getByLabelText(A("tax.rule.party_class")), "k2");
    await userEvent.click(within(rules).getByLabelText(/VAT/));                          // VAT is part of the excise base
    fireEvent.change(within(rules).getByLabelText(new RegExp(`^${A("tax.rule.valid_from")}`)), { target: { value: "2026-03-01" } });
    await userEvent.click(create);
    await waitFor(() => expect(client.createTaxRule).toHaveBeenCalledWith({
      documentType: "sales_invoice", operation: "sale", taxId: "t2", itemClassId: "k1", partyClassId: "k2", priority: 10, includesTaxIds: ["t1"], validFrom: "2026-03-01", validTo: null, isActive: true, treatmentOverride: null,
    }));
  });

  it("shows the translated reason when a rule is refused and switches a rule off with its revision", async () => {
    const create = vi.fn().mockRejectedValue(apiError(409, "rule_overlap"));
    const update = vi.fn().mockResolvedValue(rule);
    render({ createTaxRule: create, updateTaxRule: update });
    await go("#/company/taxes");
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${A("tax.tab.rules")}`) }));
    await userEvent.click(within(screen.getByTestId("tax-rule-r1")).getByRole("button", { name: A("tax.deactivate") }));
    await waitFor(() => expect(update).toHaveBeenCalledWith("r1", expect.objectContaining({ isActive: false, taxId: "t1", itemClassId: "k1" }), 3));
    const rules = screen.getByTestId("tax-tab-rules");
    await userEvent.type(within(rules).getByLabelText(new RegExp(`^${A("tax.rule.document_type")}`)), "sales_invoice");
    await userEvent.type(within(rules).getByLabelText(new RegExp(`^${A("tax.rule.operation")}`)), "sale");
    await userEvent.selectOptions(within(rules).getByLabelText(new RegExp(`^${A("tax.rule.tax")}`)), "t1");
    await userEvent.click(within(rules).getByRole("button", { name: A("tax.rule.create") }));
    expect(await screen.findByText(A("tax.error.rule_overlap"))).toBeInTheDocument();
  });

  it("adds a rounding policy version and shows the current one", async () => {
    const client = render();
    await go("#/company/taxes");
    await userEvent.click(await screen.findByRole("tab", { name: new RegExp(`^${A("tax.tab.policy")}`) }));
    expect(await screen.findByTestId("tax-policy-current")).toHaveTextContent(A("tax.policy.mode.2"));
    await userEvent.selectOptions(screen.getByLabelText(A("tax.policy.level")), "2");
    await userEvent.click(screen.getByRole("button", { name: A("tax.policy.add") }));
    await waitFor(() => expect(client.addTaxPolicy).toHaveBeenCalledWith({ moneyDecimals: 2, mode: 2, level: 2 }));
  });

  it("is read-only without the configure permission", async () => {
    render({}, false);
    await go("#/company/taxes");
    expect(await screen.findByTestId("tax-row-VAT")).toBeInTheDocument();
    expect(screen.queryByTestId("tax-create")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("tax.deactivate") })).not.toBeInTheDocument();
    await userEvent.click(tab(A("tax.tab.rules")));
    expect(screen.queryByTestId("tax-rule-create")).not.toBeInTheDocument();
  });

  it("is not offered in the menu without the tax view permission", async () => {
    const client = signedIn({}, ALL_PERMISSIONS);
    renderApp(client);
    await screen.findByTestId("company-name");
    expect(screen.queryByText(A("nav.taxes"))).not.toBeInTheDocument();
  });
});
