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

/** The party picker is a server search: its list (a combobox) fills asynchronously after the first load. */
const partyBox = async (scope: ReturnType<typeof within>) => {
  const box = await scope.findByRole("combobox", { name: lbl("pr.party") });
  await waitFor(() => expect(within(box).getAllByRole("option").length).toBeGreaterThan(1));
  return box;
};
const pickParty = async (scope: ReturnType<typeof within>, value: string) => userEvent.selectOptions(await partyBox(scope), value);
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
    const partyOptions = Array.from((await partyBox(form) as HTMLSelectElement).options).map((o) => o.textContent ?? "");
    expect(partyOptions.some((o) => o.includes("C1"))).toBe(false);                    // already a customer
    expect(partyOptions.some((o) => o.includes("C2"))).toBe(true);
    const accOptions = Array.from((form.getByLabelText(A("pr.control_account_customer")) as HTMLSelectElement).options).map((o) => o.textContent ?? "");
    expect(accOptions.some((o) => o.includes("1201"))).toBe(true);
    expect(accOptions.some((o) => o.includes("2101"))).toBe(false);                    // a liability is not a customer control account
    expect(accOptions.some((o) => o.startsWith("1 "))).toBe(false);                    // a group account is never offered
    const save = form.getByRole("button", { name: A("pr.save") });
    expect(save).toBeDisabled();
    await pickParty(form, "p2");
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
    await pickParty(form, "p1");
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
    await pickParty(form, "p2");
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
  it("a role whose control account and representative were later deactivated stays editable and keeps both references (PR-56 M2)", async () => {
    const inactiveRep = { ...rep, isActive: false };
    const update = vi.fn().mockResolvedValue(customer);
    render({
      updatePartyRole: update,
      listSalesRepresentatives: vi.fn().mockResolvedValue({ items: [inactiveRep], can: { configure: true } }),
      listAccounts: vi.fn().mockResolvedValue({ accounts: [account("a1", "1201", "asset", { isActive: false })], settings: {} }),
    });
    await go("#/company/party-roles");
    await userEvent.click(within(await screen.findByTestId("pr-role-C1-1")).getByRole("button", { name: A("pr.edit") }));
    const form = within(screen.getByTestId("pr-customer-form"));
    expect((form.getByLabelText(A("pr.control_account_customer")) as HTMLSelectElement).value).toBe("a1");   // still shown, not silently blanked
    expect((form.getByLabelText(A("pr.representative")) as HTMLSelectElement).value).toBe("s1");
    const days = form.getByLabelText(lbl("pr.term_days"));
    await userEvent.clear(days); await userEvent.type(days, "60");
    await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
    await waitFor(() => expect(update).toHaveBeenCalledWith("r1", { paymentMode: 2, termDays: 60, creditLimit: 1500, controlAccountId: "a1", representativeId: "s1" }, 4));
  });

  it("an inactive account is not offered for a NEW role", async () => {
    render({ listAccounts: vi.fn().mockResolvedValue({ accounts: [account("a1", "1201", "asset", { isActive: false })], settings: {} }) });
    await go("#/company/party-roles");
    const form = within(await screen.findByTestId("pr-customer-form"));
    const accOptions = Array.from((form.getByLabelText(A("pr.control_account_customer")) as HTMLSelectElement).options).map((o) => o.textContent ?? "");
    expect(accOptions.some((o) => o.includes("1201"))).toBe(false);
  });

  it("an inactive role can be reactivated with its terms intact", async () => {
    const update = vi.fn().mockResolvedValue(customer);
    render({ updatePartyRole: update, listPartyRoles: vi.fn().mockImplementation((kind?: number) => Promise.resolve({ items: kind === 1 ? [{ ...customer, isActive: false }] : [], can: { configure: true } })) });
    await go("#/company/party-roles");
    await userEvent.click(within(await screen.findByTestId("pr-role-C1-1")).getByRole("button", { name: A("pr.activate") }));
    await waitFor(() => expect(update).toHaveBeenCalledWith("r1", expect.objectContaining({ isActive: true, termDays: 30, creditLimit: 1500, controlAccountId: "a1", representativeId: "s1" }), 4));
  });
  it("words account refusals for party roles with the screen's own scoped keys, leaving the shared ledger wording untouched", async () => {
    const create = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "controlAccountId", code: "account_not_postable" }] }));
    render({ createPartyRole: create });
    await go("#/company/party-roles");
    const form = within(await screen.findByTestId("pr-customer-form"));
    await pickParty(form, "p2");
    await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
    expect(await screen.findByText(A("field.pr.account_not_postable"))).toBeInTheDocument();
    expect(A("field.pr.account_not_postable")).not.toBe(A("field.account_not_postable"));         // ledger and journal screens keep their own text
    expect(A("field.account_not_postable")).toMatch(/موقوف/);
  });
  describe("party picker (server search instead of a list cut off at 500)", () => {
    const many = (n: number, from = 0) => Array.from({ length: n }, (_, i) => party(`px${from + i}`, `P${String(from + i).padStart(4, "0")}`));

    it("never loads a full list: every request is a bounded page (limit 50), and it can search past the first 500 parties", async () => {
      const far = party("p-far", "ZED-0777");
      const list = vi.fn().mockImplementation((q?: { search?: string; limit?: number }) => Promise.resolve(q?.search ? { parties: [far], total: 1 } : { parties: many(50), total: 900 }));
      render({ listParties: list });
      await go("#/company/party-roles");
      const form = within(await screen.findByTestId("pr-customer-form"));
      const box = await partyBox(form);
      expect(await form.findByText(new RegExp(A("picker.showing").replace("{shown}", "50").replace("{total}", "900")))).toBeInTheDocument();   // 50 of 900: the rest is reached by searching
      await userEvent.type(form.getByRole("searchbox", { name: lbl("pr.party") }), "zed");
      await waitFor(() => expect(list).toHaveBeenCalledWith({ search: "zed", limit: 50 }));
      await waitFor(() => expect(within(box).getByRole("option", { name: /ZED-0777/ })).toBeInTheDocument());
      expect(list.mock.calls.every((c) => c[0]?.limit === 50)).toBe(true);                                   // no call without a limit, none for 500
      expect(list.mock.calls.some((c) => c[0] === undefined || c[0].limit === 500)).toBe(false);
    });

    it("sends the party chosen through a search, keeps it when the search changes, and offers only active parties that do not already hold the role", async () => {
      const far = party("p-far", "ZED-0777");
      const inactive = { ...party("p-off", "OFF-1"), isActive: false };
      const list = vi.fn().mockImplementation((q?: { search?: string }) => Promise.resolve(q?.search ? { parties: [far, inactive, party("p1", "C1")], total: 3 } : { parties: [party("p2", "C2")], total: 1 }));
      const client = render({ listParties: list });
      await go("#/company/party-roles");
      const form = within(await screen.findByTestId("pr-customer-form"));
      await userEvent.type(form.getByRole("searchbox", { name: lbl("pr.party") }), "z");
      const box = form.getByRole("combobox", { name: lbl("pr.party") });
      await waitFor(() => expect(within(box).getByRole("option", { name: /ZED-0777/ })).toBeInTheDocument());
      expect(within(box).queryByRole("option", { name: /OFF-1/ })).not.toBeInTheDocument();                  // inactive
      expect(within(box).queryByRole("option", { name: /C1/ })).not.toBeInTheDocument();                     // already a customer
      await userEvent.selectOptions(box, "p-far");
      await userEvent.clear(form.getByRole("searchbox", { name: lbl("pr.party") }));
      await userEvent.type(form.getByRole("searchbox", { name: lbl("pr.party") }), "c2");
      await waitFor(() => expect(within(box).getByRole("option", { name: /C2/ })).toBeInTheDocument());
      expect(box).toHaveValue("p-far");                                                                       // the chosen party survives the new search
      await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
      await waitFor(() => expect(client.createPartyRole).toHaveBeenCalledWith(expect.objectContaining({ partyId: "p-far", role: 1 })));
    });

    it("without permission to view parties the picker is replaced by the existing hint and no party request is sent", async () => {
      const client = render({}, true, [...ALL_PERMISSIONS, "company.partyroles.view", "company.salesreps.view", "company.accounts.view"]);
      await go("#/company/party-roles");
      const none = await screen.findByTestId("pr-customer-form");
      expect(within(none).getByText(A("pr.party_unavailable"))).toBeInTheDocument();
      expect(client.listParties).not.toHaveBeenCalled();
    });

    it("a 403 on the search turns the picker into the hint", async () => {
      render({ listParties: vi.fn().mockRejectedValue(apiError(403, "forbidden")) });
      await go("#/company/party-roles");
      const form = within(await screen.findByTestId("pr-customer-form"));
      expect(await form.findByText(A("pr.party_unavailable"))).toBeInTheDocument();
      expect(form.queryByRole("combobox", { name: lbl("pr.party") })).not.toBeInTheDocument();
    });

    it("a failing search (500) shows a retryable error and keeps search enabled; keyboard reaches the search box", async () => {
      let failNext = true;
      const list = vi.fn().mockImplementation((q?: { search?: string }) => (q?.search === "c" && failNext ? (failNext = false, Promise.reject(apiError(500, "internal"))) : Promise.resolve({ parties: [party("p2", "C2")], total: 1 })));
      render({ listParties: list });
      await go("#/company/party-roles");
      const form = within(await screen.findByTestId("pr-customer-form"));
      const search = await form.findByRole("searchbox", { name: lbl("pr.party") });
      search.focus();
      await userEvent.keyboard("c");
      expect(await form.findByRole("alert")).toHaveTextContent(A("picker.error"));
      expect(search).toBeEnabled();
      await userEvent.click(form.getByRole("button", { name: A("picker.retry") }));
      await waitFor(() => expect(form.queryByRole("alert")).not.toBeInTheDocument());
    });

    it("lists the parties again after a role was added, so the new role's party disappears from the picker", async () => {
      const roles = { items: [customer], can: { configure: true } };
      const list = vi.fn().mockResolvedValue({ parties: [party("p1", "C1"), party("p2", "C2")], total: 2 });
      const create = vi.fn().mockImplementation(() => { roles.items = [customer, { ...customer, id: "r2", partyId: "p2", partyCode: "C2" }]; return Promise.resolve(customer); });
      render({ listParties: list, createPartyRole: create, listPartyRoles: vi.fn().mockImplementation((kind?: number) => Promise.resolve({ items: kind === 1 ? roles.items : [], can: { configure: true } })) });
      await go("#/company/party-roles");
      const form = within(await screen.findByTestId("pr-customer-form"));
      await pickParty(form, "p2");
      await userEvent.click(form.getByRole("button", { name: A("pr.save") }));
      await waitFor(() => expect(create).toHaveBeenCalled());
      const box = form.getByRole("combobox", { name: lbl("pr.party") });
      await waitFor(() => expect(within(box).queryByRole("option", { name: /C2/ })).not.toBeInTheDocument());
    });
  });
});
