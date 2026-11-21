import type { Branch, Warehouse } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

/**
 * Release 1 acceptance C (optimistic concurrency, shared screens): when another user changed a record, the stale writer gets the
 * server's 412, sees the screen's own translated "changed elsewhere" message in the current language, keeps everything typed,
 * and can reload the latest version. Both languages; the server side (exactly one writer wins) is ConcurrencyInvariantTests.
 */
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const stamp = "2026-01-01T00:00:00Z";
const north: Branch = { id: "b-north", code: "NORTH", nameAr: "فرع الشمال", nameEn: "North", isActive: true, isMain: false, activationEpoch: 1, seeded: false,
  addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null, revision: 3, createdAtUtc: stamp, updatedAtUtc: stamp };
const store: Warehouse = { id: "w-2", branchId: "b-north", code: "WH2", nameAr: "مستودع الشمال", nameEn: "North Warehouse", isActive: true, activationEpoch: 1,
  addressLine1: null, addressLine2: null, city: null, region: null, postalCode: null, phone: null, email: null, website: null, revision: 3, createdAtUtc: stamp, updatedAtUtc: stamp };

const L = (lang: "ar" | "en", p: "branches" | "warehouses") => {
  const c = catalog[lang];
  return { edit: c[`${p}.edit`]!, nameEn: c[`${p}.name_en`]!, save: c[`${p}.save`]!, reload: c["form.reload_latest"]! };
};

describe("catalog: every concurrency refusal is worded in both languages", () => {
  const keys = (lang: "ar" | "en") => Object.keys(catalog[lang]).filter((k) => /revision_conflict$/.test(k)).sort();
  it("has the same revision-conflict keys in Arabic and English, each non-empty and actually translated", () => {
    expect(keys("ar")).toEqual(keys("en"));
    expect(keys("en").length).toBeGreaterThan(8);
    for (const k of keys("en")) {
      expect(catalog.ar[k]!.trim(), k).not.toBe("");
      expect(catalog.en[k]!.trim(), k).not.toBe("");
      expect(catalog.ar[k], k).not.toBe(catalog.en[k]);
    }
  });
});

describe.each(["ar", "en"] as const)("stale writer (%s)", (lang) => {
  it("branch: translated conflict message, edit kept, latest loadable", async () => {
    localStorage.setItem("hasm.locale", lang);
    const t = L(lang, "branches");
    const updateBranch = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 9 }));
    const getBranch = vi.fn().mockResolvedValue({ ...north, nameEn: "Changed elsewhere", revision: 9 });
    renderApp(signedIn({
      listBranches: vi.fn().mockResolvedValue({ branches: [north], limit: 5, everCreated: 1, can: { create: true, activate: true, designate: true, update: [north.id] } }),
      updateBranch, getBranch,
    }, [...ALL_PERMISSIONS, "company.branches.view", "company.branches.update"]));
    await go("#/company/branches");
    await userEvent.click(within(await screen.findByTestId("branch-NORTH")).getByRole("button", { name: t.edit }));
    const en = screen.getByLabelText(t.nameEn);
    await userEvent.clear(en);
    await userEvent.type(en, "Mine");
    await userEvent.click(screen.getByRole("button", { name: t.save }));
    expect(await screen.findByTestId("form-conflict")).toHaveTextContent(catalog[lang]["branches.error.revision_conflict"]!);
    expect(screen.getByLabelText(t.nameEn)).toHaveValue("Mine");
    await userEvent.click(screen.getByRole("button", { name: t.reload }));
    await waitFor(() => expect(screen.getByLabelText(t.nameEn)).toHaveValue("Changed elsewhere"));
  });

  it("warehouse: translated conflict message, edit kept, latest loadable", async () => {
    localStorage.setItem("hasm.locale", lang);
    const t = L(lang, "warehouses");
    const updateWarehouse = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 9 }));
    const getWarehouse = vi.fn().mockResolvedValue({ ...store, nameEn: "Changed elsewhere", revision: 9 });
    renderApp(signedIn({
      listWarehouses: vi.fn().mockResolvedValue({ warehouses: [store], branches: [{ id: "b-north", code: "NORTH", nameAr: "فرع الشمال", nameEn: "North", isActive: true }],
        limit: 5, everCreated: 1, can: { create: true, activate: true, update: [store.id] } }),
      updateWarehouse, getWarehouse,
    }, [...ALL_PERMISSIONS, "company.warehouses.view", "company.warehouses.update"]));
    await go("#/company/warehouses");
    await userEvent.click(within(await screen.findByTestId("warehouse-WH2")).getByRole("button", { name: t.edit }));
    const en = screen.getByLabelText(t.nameEn);
    await userEvent.clear(en);
    await userEvent.type(en, "Mine");
    await userEvent.click(screen.getByRole("button", { name: t.save }));
    expect(await screen.findByTestId("form-conflict")).toHaveTextContent(catalog[lang]["warehouses.error.revision_conflict"]!);
    expect(screen.getByLabelText(t.nameEn)).toHaveValue("Mine");
    await userEvent.click(screen.getByRole("button", { name: t.reload }));
    await waitFor(() => expect(screen.getByLabelText(t.nameEn)).toHaveValue("Changed elsewhere"));
  });
});
