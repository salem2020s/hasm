import type { LocalParty, PartyCapabilities } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, fakeClient, renderApp, USER } from "./test-utils";

const A = (k: string) => catalog.ar[k]!;
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const stamp = "2026-11-17T10:00:00Z";
const caps: PartyCapabilities = { view: true, create: true, update: true, submit: true, configure: true };
const party: LocalParty = {
  id: "p1", code: "NR-1", displayName: "النور", notes: null, isActive: true, linkState: 1, globalPartyId: null, revision: 3, createdBy: "u1", createdAtUtc: stamp, updatedAtUtc: stamp, identity: null, requests: [],
} as unknown as LocalParty;
const exporter = { id: "k2", kind: 2, code: "EXPORTER", nameAr: "مصدّر", nameEn: "Exporter", isActive: true, revision: 1 };
const retired = { id: "k3", kind: 2, code: "OLD", nameAr: "قديم", nameEn: "Old", isActive: false, revision: 2 };
const itemClass = { id: "k1", kind: 1, code: "FOOD", nameAr: "غذاء", nameEn: "Food", isActive: true, revision: 1 };

function setup(perms: string[], configure = true, assigned: { subjectId: string; classId: string }[] = [], over: Record<string, unknown> = {}) {
  const client = fakeClient({
    status: vi.fn().mockResolvedValue({ authenticated: true, user: USER }),
    getPartyCapabilities: vi.fn().mockResolvedValue(caps),
    listParties: vi.fn().mockResolvedValue({ parties: [{ id: "p1", code: "NR-1", displayName: "النور", isActive: true, linkState: 1, legalNameAr: null, legalNameEn: null, hasOpenRequest: false, revision: 3, updatedAtUtc: stamp }], total: 1 }),
    getParty: vi.fn().mockResolvedValue(party),
    listTaxClasses: vi.fn().mockResolvedValue({ classes: [itemClass, exporter, retired], can: { configure } }),
    listPartyTaxClasses: vi.fn().mockResolvedValue({ assignments: assigned, can: { configure } }),
    assignPartyTaxClass: vi.fn().mockResolvedValue({ ok: true }), updateParty: vi.fn(),
    ...over,
  }, [...ALL_PERMISSIONS, "company.parties.view", "company.parties.update", ...perms]);
  renderApp(client);
  return client;
}
const open = async () => {
  await go("#/company/parties");
  await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
  await screen.findByTestId("pty-editor");
};

describe("tax class on the party editor", () => {
  it("adds a tax class tab only for a user who may view taxes", async () => {
    setup([]);
    await open();
    expect(screen.queryByRole("tab", { name: new RegExp(`^${A("tax.tab.class")}`) })).not.toBeInTheDocument();
  });

  it("offers only party classes (active ones and the stored one) and saves independently of the party form", async () => {
    const client = setup(["company.tax.view"], true, [{ subjectId: "p1", classId: "k3" }]);
    await open();
    await userEvent.click(screen.getByRole("tab", { name: new RegExp(`^${A("tax.tab.class")}`) }));
    const select = (await screen.findByLabelText(A("tax.picker.title"))) as HTMLSelectElement;
    expect(select.value).toBe("k3");                                                                   // the stored (now inactive) class is still shown
    const labels = Array.from(select.options).map((o) => o.textContent ?? "");
    expect(labels.some((l) => l.includes("FOOD"))).toBe(false);                                        // an item class is never offered to a party
    expect(labels.some((l) => l.includes("EXPORTER"))).toBe(true);
    const save = within(screen.getByTestId("tax-class-picker")).getByRole("button", { name: A("tax.save") });
    expect(save).toBeDisabled();
    await userEvent.selectOptions(select, "k2");
    expect(save).toBeEnabled();
    await userEvent.click(save);
    await waitFor(() => expect(client.assignPartyTaxClass).toHaveBeenCalledWith("p1", "k2"));
    expect(client.updateParty).not.toHaveBeenCalled();                                                 // the party record itself is untouched
  });

  it("removes the class with an explicit null (never an omitted value)", async () => {
    const assign = vi.fn().mockResolvedValue({ ok: true });
    const client = setup(["company.tax.view"], true, [{ subjectId: "p1", classId: "k2" }], { assignPartyTaxClass: assign });
    await open();
    await userEvent.click(screen.getByRole("tab", { name: new RegExp(`^${A("tax.tab.class")}`) }));
    await userEvent.selectOptions(await screen.findByLabelText(A("tax.picker.title")), "");
    await userEvent.click(within(screen.getByTestId("tax-class-picker")).getByRole("button", { name: A("tax.save") }));
    await waitFor(() => expect(client.assignPartyTaxClass).toHaveBeenCalledWith("p1", null));
  });

  it("is read-only without the configure permission", async () => {
    setup(["company.tax.view"], false, [{ subjectId: "p1", classId: "k2" }]);
    await open();
    await userEvent.click(screen.getByRole("tab", { name: new RegExp(`^${A("tax.tab.class")}`) }));
    expect(await screen.findByLabelText(A("tax.picker.title"))).toBeDisabled();
    expect(within(screen.getByTestId("tax-class-picker")).queryByRole("button", { name: A("tax.save") })).not.toBeInTheDocument();
  });
});
