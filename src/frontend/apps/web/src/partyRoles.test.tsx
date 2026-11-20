import type { Account, LocalPartySummary, PartyRole } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const A = (k: string) => catalog.ar[k]!;
const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const lbl = (k: string) => new RegExp("^" + esc(A(k)));
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const perms = [...ALL_PERMISSIONS, "company.partyroles.view", "company.salesreps.view", "company.parties.view", "company.accounts.view"];
const party = (id: string, code: string): LocalPartySummary => ({ id, code, displayName: `طرف ${code}`, isActive: true, linkState: 2, legalNameAr: null, legalNameEn: null, hasOpenRequest: false, revision: 1, updatedAtUtc: "2026-01-01T00:00:00Z" }) as unknown as LocalPartySummary;
const account = (id: string, code: string, type: string, over: Partial<Account> = {}): Account => ({ id, code, nameAr: `حساب ${code}`, nameEn: `Acc ${code}`, type, isGroup: false, isActive: true, postable: true, ...over }) as unknown as Account;
const customer: PartyRole = { id: "r1", partyId: "p1", partyCode: "C1", partyName: "طرف C1", role: 1, isActive: true, paymentMode: 2, termDays: 30, creditLimit: 1500, controlAccountId: "a1", controlAccountCode: "1201", representativeId: "s1", revision: 4 };
const rep = { id: "s1", code: "REP1", nameAr: "مندوب", nameEn: "Rep", userId: null, isActive: true, revision: 2 };

function render(over: Record<string, unknown> = {}, can = true, p = perms) {
  const client = signedIn({
    listPartyRoles: vi.fn().mockImplementation((kind?: number) => Promise.resolve({ items: kind === 1 ? [customer] : [], can: { configure: can } })),
    listSalesRepresentatives: vi.fn().mockResolvedValue({ items: [rep, { ...rep, id: "s2", code: "OFF", isActive: false }], can: { configure: can } }),
    listParties: vi.fn().mockResolvedValue({ parties: [party("p1", "C1"), party("p2", "C2")], total: 2 }),
    listAccounts: vi.fn().mockResolvedValue({ accounts: [account("a1", "1201", "asset"), account("a2", "2101", "liability"), account("a3", "1", "asset", { isGroup: true, postable: false })], settings: {} }),
    createPartyRole: vi.fn().mockResolvedValue(customer), updatePartyRole: vi.fn().mockResolvedValue(customer),
    createSalesRepresentative: vi.fn().mockResolvedValue(rep), updateSalesRepresentative: vi.fn().mockResolvedValue(rep),
    ...over,
  }, p);
  renderApp(client);
  return client;
}

describe("customers and suppliers screen", () => {
  it("has three tabs, lists customers with terms, limit, control account and representative", async () => {
    render();
    await go("#/company/party-roles");
    expect(await screen.findByRole("heading", { name: A("pr.title"), level: 1 })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual([A("pr.tab.customers"), A("pr.tab.suppliers"), A("pr.tab.reps")]);
    const row = await screen.findByTestId("pr-role-C1-1");
    expect(within(row).getByText("1500")).toBeInTheDocument();
    expect(within(row).getByText("1201")).toBeInTheDocument();
    expect(within(row).getByText(A("pr.mode.2"))).toBeInTheDocument();
    expect(within(row).getByText("مندوب")).toBeInTheDocument();
  });

  it("offers only parties without that role, and only postable accounts of the right kind, and creates the role", async () => {
    const client = render();
    await go("#/company/party-roles");
    const form = within(await screen.findByTestId("pr-customer-form"));
    const partyOptions = Array.from((form.getByLabelText(lbl("pr.party")) as HTMLSelectElement).options).map((o) => o.textContent ?? "");
    expect(partyOptions.some((o) => o.includes("C1"))).toBe(false);                    // already a customer
    expect(partyOptions.some((o) => o.includes("C2"))).toBe(true);
    const accOptions = Array.from((form.getByLabelText(A("pr.control_account_customer")) as HTMLSelectElement).options).map((o) => o.textContent ?? "");
    expect(accOptions.some((o) => o.includes("1201"))).toBe(true);
    expect(accOptions.some((o) => o.includes("2101"))).toBe(false);                    // a liability is not a customer control account
    expect(accOptions.some((o) => o.startsWith("1 "))).toBe(false);                    // a group account is never offered
    const save = form.getByRole("button", { name: A("pr.save") });
    expect(save).toBeDisabled();
    await userEvent.selectOptions(form.getByLabelText(lbl("pr.party")), "p2");
    await userEvent.type(form.getByLabelText(A("pr.credit_limit")), "٢٥٠٠,٥");
    await userEvent.selectOptions(form.getByLabelText(A("pr.control_account_customer")), "a1");
    await userEvent.selectOptions(form.getByLabelText(A("pr.representative")), "s1");
    await userEvent.click(save);
    await waitFor(() => expect(client.createPartyRole).toHaveBeenCalledWith({ partyId: "p2", role: 1, paymentMode: 2, termDays: 30, creditLimit: 2500.5, controlAccountId: "a1", representativeId: "s1" }));
  });

  it("a cash role sends no term, and a supplier form has no representative", async () => {
    const client = render();
    await go("#/company/party-roles");
    await userEvent.click(await screen.findByRole("tab", { name: lbl("pr.tab.suppliers") }));
    const form = within(await screen.findByTestId("pr-supplier-form"));
    expect(form.queryByLabelText(A("pr.representative"))).not.toBeInTheDocument();
    await userEvent.selectOptions(form.getByLabelText(lbl("pr.party")), "p1");
    await userEvent.selectOptions(form.getByLabelText(lbl("pr.mode")), "1");
    expect(form.queryByLabelText(lbl("pr.term_days"))).not.toBeInTheDocument();
    await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
    await waitFor(() => expect(client.createPartyRole).toHaveBeenCalledWith({ partyId: "p1", role: 2, paymentMode: 1, termDays: null, creditLimit: null, controlAccountId: null, representativeId: null }));
  });

  it("edits the terms with the revision it read, keeps the party fixed, and reports an edit made elsewhere", async () => {
    const update = vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 6 })).mockResolvedValue(customer);
    const client = render({ updatePartyRole: update });
    await go("#/company/party-roles");
    await userEvent.click(within(await screen.findByTestId("pr-role-C1-1")).getByRole("button", { name: A("pr.edit") }));
    const form = within(screen.getByTestId("pr-customer-form"));
    expect(form.queryByLabelText(lbl("pr.party"))).not.toBeInTheDocument();     // the party cannot change
    const days = form.getByLabelText(lbl("pr.term_days"));
    await userEvent.clear(days); await userEvent.type(days, "45");
    await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
    await waitFor(() => expect(update).toHaveBeenCalledWith("r1", { paymentMode: 2, termDays: 45, creditLimit: 1500, controlAccountId: "a1", representativeId: "s1" }, 4));
    expect(await screen.findByTestId("pr-customer-conflict")).toBeInTheDocument();
    expect(client.listPartyRoles).toHaveBeenCalledTimes(4);                                      // both lists reloaded after the conflict
  });

  it("deactivates a role without losing its terms, and shows translated refusals", async () => {
    const create = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "controlAccountId", code: "customer_account_must_be_asset" }] }));
    const update = vi.fn().mockResolvedValue(customer);
    render({ createPartyRole: create, updatePartyRole: update });
    await go("#/company/party-roles");
    await userEvent.click(within(await screen.findByTestId("pr-role-C1-1")).getByRole("button", { name: A("pr.deactivate") }));
    await waitFor(() => expect(update).toHaveBeenCalledWith("r1", expect.objectContaining({ isActive: false, termDays: 30, creditLimit: 1500, controlAccountId: "a1" }), 4));
    const form = within(screen.getByTestId("pr-customer-form"));
    await userEvent.selectOptions(form.getByLabelText(lbl("pr.party")), "p2");
    await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
    expect(await screen.findByText(A("field.customer_account_must_be_asset"))).toBeInTheDocument();
  });

  it("manages representatives and is read-only without the configure permission", async () => {
    const client = render();
    await go("#/company/party-roles");
    await userEvent.click(await screen.findByRole("tab", { name: lbl("pr.tab.reps") }));
    const panel = within(await screen.findByTestId("pr-tab-reps"));
    expect(panel.getByTestId("pr-rep-REP1")).toBeInTheDocument();
    await userEvent.type(panel.getByLabelText(lbl("pr.code")), "REP9");
    await userEvent.type(panel.getByLabelText(lbl("pr.name_ar")), "مندوب جديد");
    await userEvent.click(panel.getByRole("button", { name: A("pr.add_rep") }));
    await waitFor(() => expect(client.createSalesRepresentative).toHaveBeenCalledWith({ code: "REP9", nameAr: "مندوب جديد", nameEn: null, userId: null }));
    await userEvent.click(within(panel.getByTestId("pr-rep-REP1")).getByRole("button", { name: A("pr.deactivate") }));
    await waitFor(() => expect(client.updateSalesRepresentative).toHaveBeenCalledWith("s1", { nameAr: "مندوب", nameEn: "Rep", userId: null, isActive: false }, 2));
  });

  it("is read-only without configure, hides tabs the user may not view, and the menu entry needs a view permission", async () => {
    render({}, false, [...ALL_PERMISSIONS, "company.partyroles.view"]);
    await go("#/company/party-roles");
    expect(await screen.findByTestId("pr-role-C1-1")).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((x) => x.textContent)).toEqual([A("pr.tab.customers"), A("pr.tab.suppliers")]);   // no representative permission: no representatives tab
    expect(screen.queryByTestId("pr-customer-form")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("pr.edit") })).not.toBeInTheDocument();
  });

  it("refuses an ambiguous credit limit instead of guessing and retries an edit after a conflict with the stored revision", async () => {
    const update = vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 5 })).mockResolvedValue(customer);
    let revision = 4;
    const client = render({ updatePartyRole: update, listPartyRoles: vi.fn().mockImplementation((kind?: number) => Promise.resolve({ items: kind === 1 ? [{ ...customer, revision }] : [], can: { configure: true } })) });
    await go("#/company/party-roles");
    await userEvent.click(within(await screen.findByTestId("pr-role-C1-1")).getByRole("button", { name: A("pr.edit") }));
    const form = within(screen.getByTestId("pr-customer-form"));
    const limit = form.getByLabelText(lbl("pr.credit_limit"));
    await userEvent.clear(limit); await userEvent.type(limit, "5,000");
    expect(form.getByRole("button", { name: A("pr.save") })).toBeDisabled();           // "5,000" could be five or five thousand
    await userEvent.clear(limit); await userEvent.type(limit, "Infinity");
    expect(form.getByRole("button", { name: A("pr.save") })).toBeDisabled();
    await userEvent.clear(limit); await userEvent.type(limit, "2000");
    revision = 5;                                                                       // someone else saved: the next load returns revision 5
    await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
    await waitFor(() => expect(update).toHaveBeenCalledWith("r1", expect.objectContaining({ creditLimit: 2000 }), 4));
    expect(await screen.findByTestId("pr-customer-conflict")).toBeInTheDocument();
    await userEvent.click(form.getByRole("button", { name: A("pr.save") }));            // retry without leaving the editor
    await waitFor(() => expect(update).toHaveBeenLastCalledWith("r1", expect.objectContaining({ creditLimit: 2000 }), 5));
    expect(client.listPartyRoles).toHaveBeenCalled();
  });
});
