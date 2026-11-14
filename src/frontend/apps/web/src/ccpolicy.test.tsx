import type { Account, Branch, CostCenter } from "@hasm/api-client";
import { fireEvent, screen, waitFor } from "@testing-library/react";
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

const perms = [...ALL_PERMISSIONS, "company.costcenterpolicies.view", "company.accounts.view", "company.costcenters.view", "company.branches.view"];
const account = (over: Partial<Account>): Account => ({ id: "a1", code: "4101", nameAr: "المبيعات", nameEn: "Sales", isGroup: false, isActive: true, ...over }) as unknown as Account;
const center = (over: Partial<CostCenter>): CostCenter => ({ id: "c1", code: "OPS", nameAr: "التشغيل", nameEn: "Operations", parentId: null, branchId: null, isGroup: false, isActive: true, canReceiveAllocations: true, hasChildren: false, revision: 1, createdAtUtc: "", updatedAtUtc: "", ...over });
const branch = (id: string, nameEn: string) => ({ id, code: nameEn, nameAr: nameEn, nameEn, isActive: true, isMain: false }) as unknown as Branch;

function render(over: Record<string, unknown> = {}, can = true) {
  const client = signedIn({
    listRequiredCostCenterAccounts: vi.fn().mockResolvedValue({ accounts: [{ accountId: "a1", code: "4101", nameAr: "المبيعات", nameEn: "Sales", required: true, revision: 3 }], can: { configure: can } }),
    listRestrictedCostCenters: vi.fn().mockResolvedValue({ costCenters: [], can: { configure: can } }),
    listAccounts: vi.fn().mockResolvedValue({ accounts: [account({}), account({ id: "a2", code: "5101", nameEn: "Rent", nameAr: "إيجار" }), account({ id: "g", code: "5", isGroup: true })], settings: null }),
    listCostCenters: vi.fn().mockResolvedValue({ costCenters: [center({}), center({ id: "grp", code: "G", isGroup: true })], can: {} }),
    listBranches: vi.fn().mockResolvedValue({ branches: [branch("b1", "Main"), branch("b2", "Second")], can: {} }),
    getAccountCostCenterRule: vi.fn().mockResolvedValue({ accountId: "a2", code: "5101", nameAr: "إيجار", nameEn: "Rent", required: false, revision: 0 }),
    setAccountCostCenterRule: vi.fn().mockResolvedValue({ accountId: "a2", code: "5101", nameAr: "إيجار", nameEn: "Rent", required: true, revision: 1 }),
    getCostCenterBranchRule: vi.fn().mockResolvedValue({ costCenterId: "c1", restricted: false, branchIds: [], revision: 0 }),
    setCostCenterBranchRule: vi.fn().mockResolvedValue({ costCenterId: "c1", restricted: true, branchIds: ["b2"], revision: 1 }),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}

describe("cost center policies screen", () => {
  it("lists the accounts that require a cost center and offers only postable accounts that do not already", async () => {
    render();
    await go("#/company/cost-center-policies");
    expect(await screen.findByRole("heading", { name: A("ccp.title"), level: 1 })).toBeInTheDocument();
    expect(await screen.findByTestId("ccp-account-4101")).toBeInTheDocument();
    const options = Array.from((screen.getByLabelText(A("ccp.require_for")) as HTMLSelectElement).options).map((o) => o.textContent);
    expect(options.some((o) => o?.includes("5101"))).toBe(true);
    expect(options.some((o) => o?.includes("4101"))).toBe(false);       // already required
    expect(options.some((o) => o?.startsWith("5 "))).toBe(false);       // a group account is never offered
  });

  it("requires a cost center for an account with the rule revision it read (0 for a never-configured account)", async () => {
    const client = render();
    await go("#/company/cost-center-policies");
    await userEvent.selectOptions(await screen.findByLabelText(A("ccp.require_for")), "a2");
    await userEvent.click(screen.getByRole("button", { name: A("ccp.require") }));
    await waitFor(() => expect(client.setAccountCostCenterRule).toHaveBeenCalledWith("a2", true, 0));
    expect(await screen.findByText(A("ccp.saved"))).toBeInTheDocument();
  });

  it("stops requiring with the stored revision and reports an edit made elsewhere without losing the screen", async () => {
    const set = vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 4 })).mockResolvedValue({ accountId: "a1", code: "4101", nameAr: "", nameEn: "", required: false, revision: 4 });
    const client = render({ setAccountCostCenterRule: set });
    await go("#/company/cost-center-policies");
    const row = await screen.findByTestId("ccp-account-4101");
    await userEvent.click(row.querySelector("button")!);
    await waitFor(() => expect(set).toHaveBeenCalledWith("a1", false, 3));
    expect(await screen.findByTestId("ccp-accounts-conflict")).toBeInTheDocument();
    expect(client.listRequiredCostCenterAccounts).toHaveBeenCalledTimes(2);       // refreshed after the conflict
  });

  it("restricts a leaf cost center to chosen branches and refuses saving without a branch", async () => {
    const client = render();
    await go("#/company/cost-center-policies");
    await userEvent.selectOptions(await screen.findByLabelText(A("ccp.cost_center")), "c1");
    await userEvent.click(await screen.findByLabelText(A("ccp.restrict")));
    const save = screen.getByRole("button", { name: A("common.save") });
    expect(save).toBeDisabled();                                                  // restricted without any branch
    await userEvent.click(screen.getByLabelText("Second"));
    expect(save).toBeEnabled();
    await userEvent.click(save);
    await waitFor(() => expect(client.setCostCenterBranchRule).toHaveBeenCalledWith("c1", { restricted: true, branchIds: ["b2"] }, 0));
  });

  it("is read-only without the configure permission (the server decides; the controls follow)", async () => {
    render({}, false);
    await go("#/company/cost-center-policies");
    expect(await screen.findByTestId("ccp-account-4101")).toBeInTheDocument();
    expect(screen.queryByTestId("ccp-add-account")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: A("ccp.stop_requiring") })).not.toBeInTheDocument();
  });
});
