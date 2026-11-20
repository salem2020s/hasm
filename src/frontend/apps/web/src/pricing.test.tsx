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

const perms = [...ALL_PERMISSIONS, "company.pricing.view"];
const unit = { id: "u1", code: "PCS", nameAr: "قطعة", nameEn: "Piece", decimals: 0, isActive: true, revision: 1 };
const group = { id: "g1", code: "FRUIT", nameAr: "فواكه", nameEn: "Fruit", parentId: null, valuation: 1, isActive: true, revision: 1 };
const apple = { id: "i1", code: "APPLE", nameAr: "تفاح", nameEn: "Apple", groupId: "g1", baseUnitId: "u1", isActive: true, revision: 1 };
const customer = { id: "r1", partyId: "p1", partyCode: "CUST1", partyName: "عميل", role: 1, isActive: true };
const policy = { version: 2, isDefault: false, priceDecimals: 2, moneyDecimals: 2, minMarginPercent: 10, marginAction: 2, listPrecedence: 2, discountPrecedence: 1, createdAtUtc: "2026-06-01T00:00:00Z" };
const retail = { id: "l1", code: "RETAIL", nameAr: "قائمة التجزئة", nameEn: "Retail", priority: 5, currency: "JOD", validFrom: "2026-01-01", validTo: null, isActive: true, customerId: null, revision: 3, updatedAtUtc: "2026-06-01T00:00:00Z" };
const entry = { id: "e1", priceListId: "l1", itemId: "i1", itemGroupId: null, unitId: "u1", minQuantity: 0, unitPrice: 10, isActive: true, revision: 2, updatedAtUtc: "2026-06-01T00:00:00Z" };
const promo = { id: "d1", code: "PROMO", nameAr: "عرض", nameEn: "Promo", priority: 1, validFrom: "2026-01-01", validTo: null, isActive: true, percent: 15, minQuantity: 0, customerId: null, itemId: null, itemGroupId: "g1", revision: 1, updatedAtUtc: "2026-06-01T00:00:00Z" };
const quote = {
  version: "pricing-snapshot-v2", contentHash: "a".repeat(64), policy: { priceDecimals: 2, moneyDecimals: 2, minMarginPercent: 10, marginAction: 2, listPrecedence: 2, discountPrecedence: 1 },
  result: { listUnitPrice: 10, priceRuleRef: "list:1@RETAIL;tier:0", discountPercent: 15, discountRuleRef: "discount:1@PROMO", netUnitPrice: 8.5, lineNet: 17, overridden: false, overrideReason: null, marginPercent: null, margin: 0, requiresApproval: false,
    trace: [{ kind: "policy", ruleRef: "list-precedence:priority-then-scope", outcome: "applied" }, { kind: "price", ruleRef: "list:1@RETAIL;tier:0", outcome: "selected" }] },
};

function render(over: Record<string, unknown> = {}, can = true) {
  const client = signedIn({
    getPricingPolicy: vi.fn().mockResolvedValue({ policy, can: { configure: can } }),
    listPriceLists: vi.fn().mockResolvedValue({ priceLists: [retail], can: { configure: can } }),
    listDiscountRules: vi.fn().mockResolvedValue({ discounts: [promo], can: { configure: can } }),
    listPriceEntries: vi.fn().mockResolvedValue({ entries: [entry], can: { configure: can } }),
    listInventoryUnits: vi.fn().mockResolvedValue({ units: [unit] }),
    listInventoryGroups: vi.fn().mockResolvedValue({ groups: [group] }),
    listInventoryItems: vi.fn().mockResolvedValue({ items: [apple], total: 1 }),
    listPartyRoles: vi.fn().mockResolvedValue({ items: [customer], can: { configure: can } }),
    savePricingPolicy: vi.fn().mockResolvedValue({ ...policy, version: 3 }),
    createPriceList: vi.fn().mockResolvedValue(retail),
    updatePriceList: vi.fn().mockResolvedValue({ ...retail, isActive: false, revision: 4 }),
    createPriceEntry: vi.fn().mockResolvedValue(entry),
    updatePriceEntry: vi.fn().mockResolvedValue({ ...entry, unitPrice: 12, revision: 3 }),
    createDiscountRule: vi.fn().mockResolvedValue(promo),
    updateDiscountRule: vi.fn().mockResolvedValue(promo),
    quotePrice: vi.fn().mockResolvedValue(quote),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}
const tab = (n: string) => screen.getByRole("tab", { name: new RegExp(`^${n}`) });
const label = (k: string) => new RegExp(`^${A(k)}`);

describe("pricing administration screen", () => {
  it("is organised in four tabs by business meaning and shows the saved policy with its configurable precedence", async () => {
    render();
    await go("#/company/pricing");
    expect(await screen.findByRole("heading", { name: A("pricing.title"), level: 1 })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual([A("pricing.tab.policy"), A("pricing.tab.lists"), A("pricing.tab.discounts"), A("pricing.tab.quote")]);
    const current = await screen.findByTestId("pricing-policy-current");
    expect(current).toHaveTextContent(A("pricing.list_precedence.2"));
    expect(current).toHaveTextContent(A("pricing.margin_action.2"));
    expect(screen.getByText(A("pricing.policy.no_stacking"))).toBeInTheDocument();                               // stacking is not offered
  });

  it("saves a new policy version sending the version it read (the server fences it) and reports a conflict", async () => {
    const save = vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 5 })).mockResolvedValue({ ...policy, version: 3 });
    const client = render({ savePricingPolicy: save });
    await go("#/company/pricing");
    const form = within(await screen.findByTestId("pricing-policy-form"));
    await userEvent.selectOptions(form.getByLabelText(label("pricing.policy.list_precedence")), "1");
    await userEvent.selectOptions(form.getByLabelText(label("pricing.policy.discount_precedence")), "2");
    await userEvent.click(form.getByRole("button", { name: A("pricing.policy.save") }));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ priceDecimals: 2, moneyDecimals: 2, minMarginPercent: 10, marginAction: 2, listPrecedence: 1, discountPrecedence: 2 }, 2));
    expect(await screen.findByTestId("pricing-policy-conflict")).toBeInTheDocument();
    expect(client.getPricingPolicy).toHaveBeenCalledTimes(2);                                                     // refreshed after the conflict
  });

  it("creates a price list for a customer, adds an item entry with a tier, reprices with the revision it read and never offers a delete", async () => {
    const client = render();
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
    expect(await screen.findByTestId("pricing-list-RETAIL")).toBeInTheDocument();
    const panel = within(screen.getByTestId("pricing-list-create"));
    await userEvent.type(panel.getByLabelText(label("pricing.code")), "vip");
    await userEvent.type(panel.getByLabelText(label("pricing.name_ar")), "قائمة كبار العملاء");
    await userEvent.selectOptions(panel.getByLabelText(label("pricing.customer")), "p1");
    await userEvent.click(panel.getByRole("button", { name: A("pricing.create_list") }));
    await waitFor(() => expect(client.createPriceList).toHaveBeenCalledWith(expect.objectContaining({ code: "vip", nameAr: "قائمة كبار العملاء", priority: 10, currency: "JOD", customerId: "p1", nameEn: null, validTo: null })));

    await userEvent.click(within(screen.getByTestId("pricing-list-RETAIL")).getByRole("button", { name: A("pricing.entries") }));
    expect(await screen.findByTestId("pricing-entries-table")).toBeInTheDocument();
    expect(client.listPriceEntries).toHaveBeenCalledWith("l1");
    const add = within(screen.getByTestId("pricing-entry-create"));
    expect(add.getByRole("button", { name: A("pricing.add_entry") })).toBeDisabled();                             // target, unit and price are required
    await userEvent.selectOptions(add.getByLabelText(label("pricing.target")), "i1");
    await userEvent.selectOptions(add.getByLabelText(label("pricing.unit")), "u1");
    await userEvent.clear(add.getByLabelText(label("pricing.min_quantity")));
    await userEvent.type(add.getByLabelText(label("pricing.min_quantity")), "10");
    await userEvent.type(add.getByLabelText(label("pricing.unit_price")), "8.5");
    await userEvent.click(add.getByRole("button", { name: A("pricing.add_entry") }));
    await waitFor(() => expect(client.createPriceEntry).toHaveBeenCalledWith("l1", { itemId: "i1", itemGroupId: null, unitId: "u1", minQuantity: 10, unitPrice: 8.5 }));

    await userEvent.click(within(screen.getByTestId("pricing-entry-e1")).getByRole("button", { name: A("pricing.edit_price") }));
    const price = within(screen.getByTestId("pricing-entry-e1")).getByLabelText(A("pricing.unit_price"));
    await userEvent.clear(price);
    await userEvent.type(price, "12");
    await userEvent.click(within(screen.getByTestId("pricing-entry-e1")).getByRole("button", { name: A("pricing.save") }));
    await waitFor(() => expect(client.updatePriceEntry).toHaveBeenCalledWith("e1", { unitPrice: 12, isActive: true }, 2));
    expect(screen.queryByRole("button", { name: /delete|حذف/i })).not.toBeInTheDocument();
  });

  it("deactivates a list with its stored values and revision, and creates a discount scoped to an item group", async () => {
    const client = render();
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
    await userEvent.click(within(await screen.findByTestId("pricing-list-RETAIL")).getByRole("button", { name: A("pricing.deactivate") }));
    await waitFor(() => expect(client.updatePriceList).toHaveBeenCalledWith("l1", { nameAr: retail.nameAr, nameEn: "Retail", priority: 5, validFrom: "2026-01-01", validTo: null, isActive: false }, 3));

    await userEvent.click(tab(A("pricing.tab.discounts")));
    expect(within(await screen.findByTestId("pricing-discount-PROMO")).getByText("FRUIT")).toBeInTheDocument();     // the scope is shown
    const form = within(screen.getByTestId("pricing-discount-create"));
    expect(form.getByRole("button", { name: A("pricing.create_discount") })).toBeDisabled();
    await userEvent.type(form.getByLabelText(label("pricing.code")), "bulk");
    await userEvent.type(form.getByLabelText(label("pricing.name_ar")), "خصم الكمية");
    await userEvent.type(form.getByLabelText(label("pricing.percent")), "7.5");
    await userEvent.selectOptions(form.getByLabelText(label("pricing.scope")), "group");
    expect(form.getByRole("button", { name: A("pricing.create_discount") })).toBeDisabled();                         // a group scope needs its group
    await userEvent.selectOptions(form.getByLabelText(label("pricing.target")), "g1");
    await userEvent.click(form.getByRole("button", { name: A("pricing.create_discount") }));
    await waitFor(() => expect(client.createDiscountRule).toHaveBeenCalledWith(expect.objectContaining({ code: "bulk", percent: 7.5, itemId: null, itemGroupId: "g1", customerId: null, minQuantity: 0 })));
  });

  it("previews a quote with its rule, snapshot and trace, translates a refusal and requires a reason for an override", async () => {
    const quotePrice = vi.fn().mockRejectedValueOnce(apiError(400, "validation_failed", { fields: [{ field: "pricing", code: "no_price" }] })).mockResolvedValue(quote);
    render({ quotePrice });
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.quote") }));
    const form = within(await screen.findByTestId("pricing-quote-form"));
    expect(form.getByRole("button", { name: A("pricing.quote.run") })).toBeDisabled();                               // item and unit are required
    await userEvent.selectOptions(form.getByLabelText(label("pricing.kind_item")), "i1");
    await userEvent.selectOptions(form.getByLabelText(label("pricing.unit")), "u1");
    await userEvent.click(form.getByRole("button", { name: A("pricing.quote.run") }));
    expect(await screen.findByTestId("pricing-quote-error")).toHaveTextContent(A("pricing.quote_error.no_price"));
    await userEvent.type(form.getByLabelText(label("pricing.quote.override_price")), "7");
    expect(form.getByRole("button", { name: A("pricing.quote.run") })).toBeDisabled();                               // an override needs its reason
    await userEvent.type(form.getByLabelText(label("pricing.quote.override_reason")), "negotiated");
    await userEvent.click(form.getByRole("button", { name: A("pricing.quote.run") }));
    await waitFor(() => expect(quotePrice).toHaveBeenLastCalledWith(expect.objectContaining({ itemId: "i1", itemGroupId: "g1", unitId: "u1", quantity: 1, override: { unitPrice: 7, discountPercent: null, reason: "negotiated" } })));
    expect(await screen.findByTestId("pricing-quote-line")).toHaveTextContent("17");
    expect(screen.getByTestId("pricing-quote-net")).toHaveTextContent("8.5");
    expect(screen.getByTestId("pricing-quote-hash")).toHaveTextContent("aaaaaaaaaaaaaaaa");
    expect(screen.getByTestId("pricing-quote-trace")).toHaveTextContent("policy: list-precedence:priority-then-scope");
  });

  it("shows a viewer the configuration without any form and keeps typed input when switching tabs", async () => {
    render({}, false);
    await go("#/company/pricing");
    await screen.findByTestId("pricing-policy-current");
    expect(screen.queryByTestId("pricing-policy-form")).not.toBeInTheDocument();
    await userEvent.click(tab(A("pricing.tab.lists")));
    expect(await screen.findByTestId("pricing-lists-table")).toBeInTheDocument();
    expect(screen.queryByTestId("pricing-list-create")).not.toBeInTheDocument();
    expect(within(screen.getByTestId("pricing-list-RETAIL")).queryByRole("button", { name: A("pricing.deactivate") })).not.toBeInTheDocument();
    await userEvent.click(tab(A("pricing.tab.quote")));                                                              // the preview is open to every viewer
    expect(screen.getByTestId("pricing-quote-form")).toBeInTheDocument();
    await userEvent.type(within(screen.getByTestId("pricing-quote-form")).getByLabelText(label("pricing.quote.unit_cost")), "5");
    await userEvent.click(tab(A("pricing.tab.policy")));
    await userEvent.click(tab(A("pricing.tab.quote")));
    expect(within(screen.getByTestId("pricing-quote-form")).getByLabelText(label("pricing.quote.unit_cost"))).toHaveValue("5");
  });
});
