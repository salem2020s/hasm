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
const vipGroup = { id: "cg1", code: "VIP", nameAr: "كبار العملاء", nameEn: "VIP", isActive: true, revision: 2, updatedAtUtc: "2026-06-01T00:00:00Z" };
const assignment = { partyId: "p1", customerGroupId: null, customerGroupCode: null, revision: 0 };
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
    listCustomerGroups: vi.fn().mockResolvedValue({ customerGroups: [vipGroup], can: { configure: can } }),
    listCustomerGroupAssignments: vi.fn().mockResolvedValue({ assignments: [assignment], can: { configure: can } }),
    createCustomerGroup: vi.fn().mockResolvedValue(vipGroup),
    updateCustomerGroup: vi.fn().mockResolvedValue({ ...vipGroup, isActive: false, revision: 3 }),
    setCustomerGroupAssignment: vi.fn().mockResolvedValue({ ...assignment, customerGroupId: "cg1", customerGroupCode: "VIP", revision: 1 }),
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
const baseCurrencyOf = (code: string | null) => ({ currencies: [], baseCurrency: code, baseCurrencySelected: code !== null, baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] } });
const label = (k: string) => new RegExp(`^${A(k)}`);
/** The item picker is a server search: its list (a combobox) fills asynchronously after the first load. */
/** The customer picker (a search over party roles): its list is a combobox that fills after the first load. */
const pickCustomer = async (scope: ReturnType<typeof within>, value = "p1") => {
  const box = await scope.findByRole("combobox", { name: label("pricing.customer") });
  await waitFor(() => expect(within(box).getByRole("option", { name: /CUST1/ })).toBeInTheDocument());
  await userEvent.selectOptions(box, value);
  return box;
};
const pickItem = async (scope: ReturnType<typeof within>, k: string) => {
  const box = await scope.findByRole("combobox", { name: label(k) });
  await waitFor(() => expect(within(box).getByRole("option", { name: /APPLE/ })).toBeInTheDocument());
  return box;
};

describe("pricing administration screen", () => {
  it("is organised in five tabs by business meaning and shows the saved policy with its configurable precedence", async () => {
    render();
    await go("#/company/pricing");
    expect(await screen.findByRole("heading", { name: A("pricing.title"), level: 1 })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual([A("pricing.tab.policy"), A("pricing.tab.lists"), A("pricing.tab.discounts"), A("pricing.tab.groups"), A("pricing.tab.quote")]);
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
    expect(form.getByLabelText(label("pricing.policy.list_precedence"))).toHaveValue("1");                           // a conflict never discards what was typed
    expect(form.getByLabelText(label("pricing.policy.discount_precedence"))).toHaveValue("2");
    expect(client.getPricingPolicy).toHaveBeenCalledTimes(2);                                                     // refreshed after the conflict
  });

  it("creates a price list for a customer, adds an item entry with a tier, reprices with the revision it read and never offers a delete", async () => {
    const client = render({ getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
    expect(await screen.findByTestId("pricing-list-RETAIL")).toBeInTheDocument();
    const panel = within(screen.getByTestId("pricing-list-create"));
    await userEvent.type(panel.getByLabelText(label("pricing.code")), "vip");
    await userEvent.type(panel.getByLabelText(label("pricing.name_ar")), "قائمة كبار العملاء");
    await pickCustomer(panel);
    await userEvent.click(panel.getByRole("button", { name: A("pricing.create_list") }));
    await waitFor(() => expect(client.createPriceList).toHaveBeenCalledWith(expect.objectContaining({ code: "vip", nameAr: "قائمة كبار العملاء", priority: 10, currency: "JOD", customerId: "p1", nameEn: null, validTo: null })));

    await userEvent.click(within(screen.getByTestId("pricing-list-RETAIL")).getByRole("button", { name: A("pricing.entries") }));
    expect(await screen.findByTestId("pricing-entries-table")).toBeInTheDocument();
    expect(client.listPriceEntries).toHaveBeenCalledWith("l1");
    const add = within(screen.getByTestId("pricing-entry-create"));
    expect(add.getByRole("button", { name: A("pricing.add_entry") })).toBeDisabled();                             // target, unit and price are required
    await userEvent.selectOptions(await pickItem(add, "pricing.target"), "i1");
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
    await userEvent.type(form.getByLabelText(label("pricing.min_quantity")), "x");                                    // a mistyped minimum is never sent as 0
    expect(form.getByRole("button", { name: A("pricing.create_discount") })).toBeDisabled();
    await userEvent.clear(form.getByLabelText(label("pricing.min_quantity")));
    await userEvent.type(form.getByLabelText(label("pricing.min_quantity")), "0");
    await userEvent.selectOptions(form.getByLabelText(label("pricing.scope")), "group");
    expect(form.getByRole("button", { name: A("pricing.create_discount") })).toBeDisabled();                         // a group scope needs its group
    await userEvent.selectOptions(form.getByLabelText(label("pricing.target")), "g1");
    await userEvent.click(form.getByRole("button", { name: A("pricing.create_discount") }));
    await waitFor(() => expect(client.createDiscountRule).toHaveBeenCalledWith(expect.objectContaining({ code: "bulk", percent: 7.5, itemId: null, itemGroupId: "g1", customerId: null, minQuantity: 0 })));
  });

  it("previews a quote with its rule, snapshot and trace, translates a refusal and requires a reason for an override", async () => {
    const quotePrice = vi.fn().mockRejectedValueOnce(apiError(400, "validation_failed", { fields: [{ field: "pricing", code: "no_price" }] })).mockResolvedValue(quote);
    render({ quotePrice, getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.quote") }));
    const form = within(await screen.findByTestId("pricing-quote-form"));
    expect(form.getByRole("button", { name: A("pricing.quote.run") })).toBeDisabled();                               // item and unit are required
    await userEvent.selectOptions(await pickItem(form, "pricing.kind_item"), "i1");
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

  it("tells the user when a helper list could not be loaded instead of silently showing empty choices", async () => {
    render({ listInventoryUnits: vi.fn().mockRejectedValue(apiError(403, "forbidden")) });
    await go("#/company/pricing");
    expect(await screen.findByTestId("pricing-pickers-failed")).toHaveTextContent(A("pricing.pickers_unavailable"));
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

  it("creates and deactivates a customer group with the revision it read and assigns a customer to ONE group", async () => {
    const client = render();
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.groups") }));
    expect(await screen.findByTestId("pricing-group-VIP")).toBeInTheDocument();
    const form = within(screen.getByTestId("pricing-group-create"));
    expect(form.getByRole("button", { name: A("pricing.groups.create") })).toBeDisabled();
    await userEvent.type(form.getByLabelText(label("pricing.code")), "wholesale");
    await userEvent.type(form.getByLabelText(label("pricing.name_ar")), "جملة");
    await userEvent.click(form.getByRole("button", { name: A("pricing.groups.create") }));
    await waitFor(() => expect(client.createCustomerGroup).toHaveBeenCalledWith({ code: "wholesale", nameAr: "جملة", nameEn: null }));
    await userEvent.click(within(screen.getByTestId("pricing-group-VIP")).getByRole("button", { name: A("pricing.deactivate") }));
    await waitFor(() => expect(client.updateCustomerGroup).toHaveBeenCalledWith("cg1", { nameAr: "كبار العملاء", nameEn: "VIP", isActive: false }, 2));
    expect(screen.getByTestId("pricing-groups-deactivate-note")).toHaveTextContent(A("pricing.groups.deactivate_note"));   // stated prominently
    const row = within(screen.getByTestId("pricing-assignment-CUST1"));
    expect(row.getByRole("button", { name: A("pricing.groups.assign") })).toBeDisabled();                          // nothing changed yet
    await userEvent.selectOptions(row.getByRole("combobox"), "cg1");
    await userEvent.click(row.getByRole("button", { name: A("pricing.groups.assign") }));
    await waitFor(() => expect(client.setCustomerGroupAssignment).toHaveBeenCalledWith("p1", "cg1", 0));          // revision 0 = never assigned
    expect(screen.queryByRole("button", { name: /delete|حذف/i })).not.toBeInTheDocument();
  });

  it("scopes a price list and a discount to a customer OR a group: choosing one disables the other and the group is sent", async () => {
    const client = render({ getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
    const panel = within(await screen.findByTestId("pricing-list-create"));
    await userEvent.type(panel.getByLabelText(label("pricing.code")), "vipl");
    await userEvent.type(panel.getByLabelText(label("pricing.name_ar")), "قائمة المجموعة");
    await userEvent.selectOptions(panel.getByLabelText(label("pricing.customer_group")), "cg1");
    expect(panel.getByRole("combobox", { name: label("pricing.customer") })).toBeDisabled();                                       // never both
    await userEvent.click(panel.getByRole("button", { name: A("pricing.create_list") }));
    await waitFor(() => expect(client.createPriceList).toHaveBeenCalledWith(expect.objectContaining({ code: "vipl", customerId: null, customerGroupId: "cg1" })));
    await userEvent.click(tab(A("pricing.tab.discounts")));
    const form = within(await screen.findByTestId("pricing-discount-create"));
    await pickCustomer(form);
    expect(form.getByLabelText(label("pricing.customer_group"))).toBeDisabled();
    expect(form.getByRole("combobox", { name: label("pricing.customer") })).toBeEnabled();
  });

  it("disables the customer's search box together with its list while a customer group is chosen", async () => {
    render({ getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
    const panel = within(await screen.findByTestId("pricing-list-create"));
    await userEvent.selectOptions(panel.getByLabelText(label("pricing.customer_group")), "cg1");
    expect(panel.getByRole("combobox", { name: label("pricing.customer") })).toBeDisabled();
    expect(panel.getByRole("searchbox", { name: label("pricing.customer") })).toBeDisabled();
  });

  it("keeps an inactive CURRENT group selectable in the membership row but never offers it for a new list, and shows an id when the names cannot be read", async () => {
    const oldGroup = { ...vipGroup, id: "cg2", code: "OLD", nameAr: "قديمة", nameEn: "Old", isActive: false };
    render({
      listCustomerGroups: vi.fn().mockResolvedValue({ customerGroups: [vipGroup, oldGroup], can: { configure: true } }),
      listCustomerGroupAssignments: vi.fn().mockResolvedValue({ assignments: [{ ...assignment, customerGroupId: "cg2", customerGroupCode: "OLD", revision: 4 }, { partyId: "zz-unknown-party", customerGroupId: null, customerGroupCode: null, revision: 0 }], can: { configure: true } }),
    });
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.groups") }));
    const row = within(await screen.findByTestId("pricing-assignment-CUST1"));
    const select = row.getByRole("combobox") as HTMLSelectElement;
    expect(select.value).toBe("cg2");                                                                              // the inactive current group is still shown and selected
    expect(within(select).getAllByRole("option").map((o) => o.getAttribute("value"))).toEqual(["", "cg1", "cg2"]);
    expect(screen.getByTestId("pricing-assignment-zz-unkno")).toBeInTheDocument();                                   // a customer the user cannot name is shown by id
    await userEvent.click(screen.getByRole("tab", { name: label("pricing.tab.lists") }));
    const panel = within(await screen.findByTestId("pricing-list-create"));
    const offered = within(panel.getByLabelText(label("pricing.customer_group"))).getAllByRole("option").map((o) => o.getAttribute("value"));
    expect(offered).toEqual(["", "cg1"]);                                                                           // an inactive group is never offered for a new list
  });

  it("shows a viewer the groups and assignments without any form", async () => {
    render({}, false);
    await go("#/company/pricing");
    await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.groups") }));
    expect(await screen.findByTestId("pricing-groups-table")).toBeInTheDocument();
    expect(screen.queryByTestId("pricing-group-create")).not.toBeInTheDocument();
    expect(within(screen.getByTestId("pricing-assignment-CUST1")).queryByRole("combobox")).not.toBeInTheDocument();
  });
  describe("follow-ups of the PR-69 review", () => {
    it("searches items on the server (bounded, with the search text), says when more match, and sends the group of an item that is not in the preloaded page", async () => {
      const far = { ...apple, id: "i9", code: "ZZZ-FAR", nameAr: "بعيد", nameEn: "Far", groupId: "g1" };
      const list = vi.fn().mockImplementation((q: { search?: string } = {}) => Promise.resolve(q.search ? { items: [far], total: 1 } : { items: [apple], total: 900 }));
      const quotePrice = vi.fn().mockResolvedValue(quote);
      render({ listInventoryItems: list, quotePrice, getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.quote") }));
      const form = within(await screen.findByTestId("pricing-quote-form"));
      await pickItem(form, "pricing.kind_item");
      expect(await form.findByText(/900/)).toBeInTheDocument();                                           // "showing 1 of 900 ... refine"
      expect(list).toHaveBeenCalledWith({ limit: 500 });
      await userEvent.type(form.getByRole("searchbox", { name: label("pricing.kind_item") }), "far");
      await waitFor(() => expect(list).toHaveBeenLastCalledWith({ search: "far", limit: 500 }));
      const box = form.getByRole("combobox", { name: label("pricing.kind_item") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /ZZZ-FAR/ })).toBeInTheDocument());
      await userEvent.selectOptions(box, "i9");
      await userEvent.selectOptions(form.getByLabelText(label("pricing.unit")), "u1");
      await userEvent.click(form.getByRole("button", { name: A("pricing.quote.run") }));
      await waitFor(() => expect(quotePrice).toHaveBeenCalledWith(expect.objectContaining({ itemId: "i9", itemGroupId: "g1", currency: "JOD" })));
    });

    it("the item picker is keyboard operable and a failing item search shows a retryable error", async () => {
      const list = vi.fn().mockResolvedValueOnce({ items: [apple], total: 1 }).mockRejectedValueOnce(apiError(500, "internal")).mockResolvedValue({ items: [apple], total: 1 });
      render({ listInventoryItems: list });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.quote") }));
      const form = within(await screen.findByTestId("pricing-quote-form"));
      const search = await form.findByRole("searchbox", { name: label("pricing.kind_item") });
      search.focus();
      await userEvent.keyboard("a");
      expect(await form.findByRole("alert")).toHaveTextContent(A("picker.error"));
      await userEvent.click(form.getByRole("button", { name: A("picker.retry") }));
      await waitFor(() => expect(form.queryByRole("alert")).not.toBeInTheDocument());
    });

    it("takes the default currency from the company base currency, never guesses one without it, and keeps creation disabled until a code is typed", async () => {
      render();                                                                                        // no base currency selected
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      const panel = within(await screen.findByTestId("pricing-list-create"));
      expect(panel.getByLabelText(label("pricing.currency"))).toHaveValue("");
      await userEvent.type(panel.getByLabelText(label("pricing.code")), "x");
      await userEvent.type(panel.getByLabelText(label("pricing.name_ar")), "ق");
      expect(panel.getByRole("button", { name: A("pricing.create_list") })).toBeDisabled();
      await userEvent.type(panel.getByLabelText(label("pricing.currency")), "sar");
      expect(panel.getByLabelText(label("pricing.currency"))).toHaveValue("SAR");
      expect(panel.getByRole("button", { name: A("pricing.create_list") })).toBeEnabled();
    });

    it("pre-fills the currency of a new list and a quote with the base currency", async () => {
      render({ getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("SAR")) });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      expect(within(await screen.findByTestId("pricing-list-create")).getByLabelText(label("pricing.currency"))).toHaveValue("SAR");
      await userEvent.click(screen.getByRole("tab", { name: label("pricing.tab.quote") }));
      expect(within(await screen.findByTestId("pricing-quote-form")).getByLabelText(label("pricing.currency"))).toHaveValue("SAR");
    });

    it("lists customer assignments sorted by code with the name from the party-role list, and a short id when the names are unavailable", async () => {
      const mk = (id: string, code: string, name: string) => ({ ...customer, id: `r-${id}`, partyId: id, partyCode: code, partyName: name });
      render({
        listPartyRoles: vi.fn().mockResolvedValue({ items: [mk("p2", "C10", "عشرة"), mk("p1", "C2", "اثنان")], can: { configure: true } }),
        listCustomerGroupAssignments: vi.fn().mockResolvedValue({ assignments: [{ ...assignment, partyId: "zzzzzzzz-9" }, { ...assignment, partyId: "p2" }, { ...assignment, partyId: "aaaaaaaa-1" }, { ...assignment, partyId: "p1" }], can: { configure: true } }),
      });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.groups") }));
      const rows = within(await screen.findByTestId("pricing-assignments-table")).getAllByRole("row").slice(1);
      expect(rows.map((r) => within(r).getAllByRole("cell")[0]!.textContent)).toEqual(["C2 — \u2068اثنان\u2069", "C10 — \u2068عشرة\u2069", "aaaaaaaa", "zzzzzzzz"]);   // numeric-aware code order; ids last by their short form
    });

    it("accepts a negative minimum margin and explains the range and meaning (ar and en)", async () => {
      const save = vi.fn().mockResolvedValue({ ...policy, version: 3 });
      render({ savePricingPolicy: save });
      await go("#/company/pricing");
      const form = within(await screen.findByTestId("pricing-policy-form"));
      const margin = form.getByLabelText(label("pricing.policy.min_margin"));
      await userEvent.clear(margin);
      await userEvent.type(margin, "-5");
      await userEvent.click(form.getByRole("button", { name: A("pricing.policy.save") }));
      await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({ minMarginPercent: -5 }), 2));
      expect(catalog.en["pricing.policy.min_margin_hint"]).toMatch(/-1000/);
      expect(catalog.ar["pricing.policy.min_margin_hint"]).toContain("\u2066\u22121000\u2069");          // an isolated U+2212: never "1000-" in RTL
      expect(catalog.ar["pricing.policy.min_margin_hint"]).not.toContain("-1000");
      expect(form.getByText(A("pricing.policy.min_margin_hint"))).toBeInTheDocument();
    });

    it("tells the user that customer (or group) and item scopes combine on a discount", async () => {
      render();
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.discounts") }));
      expect(await screen.findByText(A("pricing.scope_combine_hint"))).toBeInTheDocument();
    });

    it("uses the customer-group wording for field.ambiguous_rule (single source; the catalog-wide duplicate check lives in messagesUnique.test.ts)", () => {
      expect(catalog.en["field.ambiguous_rule"]).toMatch(/group rules/);
      expect(catalog.ar["field.ambiguous_rule"]).toBeTruthy();
    });

    it("sends no single-item reads when the item list itself failed (they would be refused too) and shows short ids", async () => {
      const getItem = vi.fn();
      render({
        getInventoryItem: getItem,
        listInventoryItems: vi.fn().mockRejectedValue(apiError(403, "forbidden")),
        listPriceEntries: vi.fn().mockResolvedValue({ entries: [{ ...entry, id: "e2", itemId: "denied-item-9999" }], can: { configure: true } }),
      });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      await userEvent.click(within(await screen.findByTestId("pricing-list-RETAIL")).getByRole("button", { name: A("pricing.entries") }));
      expect(within(await screen.findByTestId("pricing-entries-table")).getByText("denied-i")).toBeInTheDocument();
      expect(getItem).not.toHaveBeenCalled();
    });

    it("does not read items that are already preloaded, and reads at most 50 unknown ids per render", async () => {
      const getItem = vi.fn().mockImplementation((id: string) => Promise.resolve({ ...apple, id, code: `X${id}` }));
      const many = Array.from({ length: 60 }, (_, n) => ({ ...entry, id: `e${n + 10}`, itemId: `unknown-${String(n).padStart(3, "0")}` }));
      render({
        getInventoryItem: getItem,
        listPriceEntries: vi.fn().mockResolvedValue({ entries: [entry, ...many], can: { configure: true } }),   // entry e1 uses the preloaded item i1
      });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      await userEvent.click(within(await screen.findByTestId("pricing-list-RETAIL")).getByRole("button", { name: A("pricing.entries") }));
      await screen.findByTestId("pricing-entries-table");
      await waitFor(() => expect(getItem).toHaveBeenCalledTimes(50));
      expect(getItem).not.toHaveBeenCalledWith("i1");                                                    // preloaded: no read
    });
    it("shows the name of an entry's item outside the preloaded page by one getInventoryItem call per id, and a short id when it cannot be read", async () => {
      const far = { ...apple, id: "far-item-0000-1111", code: "FAR1", nameAr: "بعيد", nameEn: "Far" };
      const getItem = vi.fn().mockImplementation((id: string) => id === far.id ? Promise.resolve(far) : Promise.reject(apiError(403, "forbidden")));
      render({
        getInventoryItem: getItem,
        listPriceEntries: vi.fn().mockResolvedValue({ entries: [{ ...entry, id: "e2", itemId: far.id }, { ...entry, id: "e3", itemId: "denied-item-9999" }, { ...entry, id: "e4", itemId: far.id }], can: { configure: true } }),
      });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      await userEvent.click(within(await screen.findByTestId("pricing-list-RETAIL")).getByRole("button", { name: A("pricing.entries") }));
      const table = await screen.findByTestId("pricing-entries-table");
      await waitFor(() => expect(within(table).getAllByText("FAR1 — \u2068بعيد\u2069")).toHaveLength(2));
      expect(within(table).getByText("denied-i")).toBeInTheDocument();                                  // short id, never the full UUID
      expect(getItem).toHaveBeenCalledTimes(2);                                                         // once per distinct id
    });

    it("links the item picker's field error to its list (aria-invalid, aria-describedby) through a real label", async () => {
      const create = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "itemId", code: "item_or_group_required" }] }));
      render({ createPriceEntry: create });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      await userEvent.click(within(await screen.findByTestId("pricing-list-RETAIL")).getByRole("button", { name: A("pricing.entries") }));
      const add = within(await screen.findByTestId("pricing-entry-create"));
      await userEvent.selectOptions(await pickItem(add, "pricing.target"), "i1");
      await userEvent.selectOptions(add.getByLabelText(label("pricing.unit")), "u1");
      await userEvent.type(add.getByLabelText(label("pricing.unit_price")), "5");
      await userEvent.click(add.getByRole("button", { name: A("pricing.add_entry") }));
      await waitFor(() => expect(create).toHaveBeenCalled());
      const box = await add.findByRole("combobox", { name: label("pricing.target") });
      await waitFor(() => expect(box).toHaveAttribute("aria-invalid", "true"));
      const errorId = box.getAttribute("aria-describedby")!;
      expect(document.getElementById(errorId)).toHaveTextContent(A("field.item_or_group_required"));
      expect(document.querySelector(`label[for="${box.id}"]`)).not.toBeNull();                          // a real label names the list
    });

    it("keeps the code first and unwrapped in option labels (type-ahead) and isolates only the name", async () => {
      render();
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.quote") }));
      const form = within(await screen.findByTestId("pricing-quote-form"));
      const box = await pickItem(form, "pricing.kind_item");
      const option = within(box).getByRole("option", { name: "APPLE — \u2068تفاح\u2069" });
      expect(option).toBeInTheDocument();
      expect(option.textContent!.startsWith("APPLE")).toBe(true);                                       // native-select type-ahead matches the start of the text
    });

    it("warns when the base currency cannot be read (non-401 failure) and not when it can or is simply unset", async () => {
      render({ getCurrencies: vi.fn().mockRejectedValue(apiError(500, "internal")) });
      await go("#/company/pricing");
      expect(await screen.findByTestId("pricing-currency-unavailable")).toHaveTextContent(A("pricing.currency_unavailable"));
    });

    it("with the base currency simply unset (null, nothing failed) shows no warning, starts the currency empty and keeps create disabled", async () => {
      render({ getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf(null)) });
      await go("#/company/pricing");
      await screen.findByTestId("pricing-note");
      expect(screen.queryByTestId("pricing-currency-unavailable")).not.toBeInTheDocument();
      await userEvent.click(screen.getByRole("tab", { name: label("pricing.tab.lists") }));
      const panel = within(await screen.findByTestId("pricing-list-create"));
      expect(panel.getByLabelText(label("pricing.currency"))).toHaveValue("");
      await userEvent.type(panel.getByLabelText(label("pricing.code")), "x");
      await userEvent.type(panel.getByLabelText(label("pricing.name_ar")), "ق");
      expect(panel.getByRole("button", { name: A("pricing.create_list") })).toBeDisabled();
    });

    it("shows no currency warning when the base currency loads", async () => {
      render({ getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
      await go("#/company/pricing");
      await screen.findByTestId("pricing-note");
      expect(screen.queryByTestId("pricing-currency-unavailable")).not.toBeInTheDocument();
    });
    it("searches customers on the server (bounded, with the search text), shows how many more exist, and keeps the chosen customer when the search changes", async () => {
      const other = { ...customer, id: "r2", partyId: "p2", partyCode: "ZED9", partyName: "زيد" };
      const list = vi.fn().mockImplementation((_kind?: number, q?: { search?: string }) => Promise.resolve(q?.search ? { items: [other], total: 1, can: { configure: true } } : q ? { items: [customer], total: 700, can: { configure: true } } : { items: [customer], can: { configure: true } }));
      render({ listPartyRoles: list });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      const panel = within(await screen.findByTestId("pricing-list-create"));
      const box = await pickCustomer(panel);
      expect(await panel.findByText(/700/)).toBeInTheDocument();                                             // "showing 1 of 700 ... refine"
      expect(list).toHaveBeenCalledWith(1, { limit: 50 });
      await userEvent.type(panel.getByRole("searchbox", { name: label("pricing.customer") }), "zed");
      await waitFor(() => expect(list).toHaveBeenLastCalledWith(1, { search: "zed", limit: 50 }));
      await waitFor(() => expect(within(box).getByRole("option", { name: /ZED9/ })).toBeInTheDocument());
      expect(within(box).getByRole("option", { name: /CUST1/ })).toBeInTheDocument();                        // the chosen customer stays selectable
      expect(box).toHaveValue("p1");
    });

    it("sends the picked customer (found by search, not in the preloaded list) with the new price list and discount", async () => {
      const other = { ...customer, id: "r2", partyId: "p2", partyCode: "ZED9", partyName: "زيد" };
      const list = vi.fn().mockImplementation((_kind?: number, q?: { search?: string }) => Promise.resolve(q?.search ? { items: [other], total: 1, can: { configure: true } } : q ? { items: [customer], total: 1, can: { configure: true } } : { items: [customer], can: { configure: true } }));
      const createDiscountRule = vi.fn().mockResolvedValue(promo);
      render({ listPartyRoles: list, createDiscountRule });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.discounts") }));
      const form = within(await screen.findByTestId("pricing-discount-create"));
      await userEvent.type(form.getByRole("searchbox", { name: label("pricing.customer") }), "zed");
      const box = form.getByRole("combobox", { name: label("pricing.customer") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /ZED9/ })).toBeInTheDocument());
      await userEvent.selectOptions(box, "p2");
      await userEvent.type(form.getByLabelText(label("pricing.code")), "z");
      await userEvent.type(form.getByLabelText(label("pricing.name_ar")), "ز");
      await userEvent.type(form.getByLabelText(label("pricing.percent")), "5");
      await userEvent.click(form.getByRole("button", { name: A("pricing.create_discount") }));
      await waitFor(() => expect(createDiscountRule).toHaveBeenCalledWith(expect.objectContaining({ customerId: "p2", customerGroupId: null })));
    });

    it("without the party-role permission the customer picker offers only all customers, sends no search and keeps the existing helper warning", async () => {
      const list = vi.fn().mockRejectedValue(apiError(403, "forbidden"));
      render({ listPartyRoles: list });
      await go("#/company/pricing");
      expect(await screen.findByTestId("pricing-pickers-failed")).toBeInTheDocument();
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      const panel = within(await screen.findByTestId("pricing-list-create"));
      const box = await panel.findByRole("combobox", { name: label("pricing.customer") });
      await waitFor(() => expect(panel.queryByText(A("picker.loading"))).not.toBeInTheDocument());
      expect(within(box).getAllByRole("option").map((o) => o.textContent)).toEqual([A("pricing.all_customers")]);
      expect(panel.getByRole("searchbox", { name: label("pricing.customer") })).toBeDisabled();             // nothing can be searched without the permission
      expect(panel.queryByRole("alert")).not.toBeInTheDocument();                                            // no per-picker error: the page warning already says it
      expect(list).toHaveBeenCalledTimes(1);                                                                 // the preload only
    });

    it("a transient preload failure (500) does not lock a permitted user out: search stays enabled and a search request is made", async () => {
      const list = vi.fn().mockImplementation((_k?: number, q?: { search?: string }) => (q ? Promise.resolve({ items: [customer], total: 1, can: { configure: true } }) : Promise.reject(apiError(500, "internal"))));
      render({ listPartyRoles: list });
      await go("#/company/pricing");
      expect(await screen.findByTestId("pricing-pickers-failed")).toBeInTheDocument();                       // the page's helper-lists warning may stay
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      const panel = within(await screen.findByTestId("pricing-list-create"));
      expect(panel.getByRole("searchbox", { name: label("pricing.customer") })).toBeEnabled();
      const box = await panel.findByRole("combobox", { name: label("pricing.customer") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /CUST1/ })).toBeInTheDocument());
      expect(list).toHaveBeenCalledWith(1, { limit: 50 });                                                   // the picker's own request
    });

    it("the customer picker is keyboard operable and a failing search shows a retryable error", async () => {
      let failNext = true;                                                                                   // all three customer pickers (the panels stay mounted) load; only the SEARCH "c" fails, once
      const list = vi.fn().mockImplementation((_k?: number, q?: { search?: string }) => (q?.search === "c" && failNext ? (failNext = false, Promise.reject(apiError(500, "internal"))) : Promise.resolve(q ? { items: [customer], total: 1, can: { configure: true } } : { items: [customer], can: { configure: true } })));
      render({ listPartyRoles: list });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.lists") }));
      const panel = within(await screen.findByTestId("pricing-list-create"));
      const search = await panel.findByRole("searchbox", { name: label("pricing.customer") });
      search.focus();
      await userEvent.keyboard("c");
      expect(await panel.findByRole("alert")).toHaveTextContent(A("picker.error"));
      await userEvent.click(panel.getByRole("button", { name: A("picker.retry") }));
      await waitFor(() => expect(panel.queryByRole("alert")).not.toBeInTheDocument());
    });

    it("sends the customer picked on the quote tab with the quote", async () => {
      const quotePrice = vi.fn().mockResolvedValue(quote);
      render({ quotePrice, getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.quote") }));
      const form = within(await screen.findByTestId("pricing-quote-form"));
      await userEvent.selectOptions(await pickItem(form, "pricing.kind_item"), "i1");
      await userEvent.selectOptions(form.getByLabelText(label("pricing.unit")), "u1");
      await pickCustomer(form);
      await userEvent.click(form.getByRole("button", { name: A("pricing.quote.run") }));
      await waitFor(() => expect(quotePrice).toHaveBeenCalledWith(expect.objectContaining({ itemId: "i1", customerId: "p1", currency: "JOD" })));
    });

    it("sends customerId null when the quote customer is left on all customers", async () => {
      const quotePrice = vi.fn().mockResolvedValue(quote);
      render({ quotePrice, getCurrencies: vi.fn().mockResolvedValue(baseCurrencyOf("JOD")) });
      await go("#/company/pricing");
      await userEvent.click(await screen.findByRole("tab", { name: label("pricing.tab.quote") }));
      const form = within(await screen.findByTestId("pricing-quote-form"));
      await userEvent.selectOptions(await pickItem(form, "pricing.kind_item"), "i1");
      await userEvent.selectOptions(form.getByLabelText(label("pricing.unit")), "u1");
      await userEvent.click(form.getByRole("button", { name: A("pricing.quote.run") }));
      await waitFor(() => expect(quotePrice).toHaveBeenCalledWith(expect.objectContaining({ customerId: null })));
    });
  });
});
