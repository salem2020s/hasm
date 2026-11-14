import type { Account, AccountsResponse } from "@hasm/api-client";
import { fireEvent, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import fixture from "./fixtures/proposedChartTemplates.json";
import { ALL_PERMISSIONS, renderApp, signedIn } from "./test-utils";

/**
 * The PROPOSED business-type chart templates (not yet exposed by the product) rendered through the existing Arabic RTL chart screen: every name must reach the DOM exactly as stored (logical Unicode order,
 * never reversed) inside a right-to-left document. The fixture is generated from the same reviewed data as the backend test data.
 */
interface Row { code: string; parent: string | null; nameAr: string; nameEn: string }
const TYPES = ["", "asset", "liability", "equity", "revenue", "expense"] as const;
const u = (...cp: number[]) => String.fromCodePoint(...cp);

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

function accounts(rows: Row[]): Account[] {
  const parents = new Set(rows.map((r) => r.parent).filter(Boolean));
  return rows.map((r) => {
    const type = TYPES[Number(r.code[0])]!;
    const isGroup = parents.has(r.code) || r.parent === null;
    return {
      id: `id-${r.code}`, code: r.code, nameAr: r.nameAr, nameEn: r.nameEn, parentId: r.parent ? `id-${r.parent}` : null, type, nature: type === "asset" || type === "expense" ? "debit" : "credit",
      statement: type === "revenue" || type === "expense" ? "profit_and_loss" : "balance_sheet", isGroup, isActive: true, postable: !isGroup, hasChildren: parents.has(r.code), currencyPolicy: "base", currencyCode: null,
      template: null, revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z",
    } as Account;
  });
}

const response = (list: Account[]): AccountsResponse => ({
  accounts: list, settings: { segmentWidth: 2, autoCodes: true, revision: 0, configured: false }, templates: [], usageChecksImplemented: false,
  can: { create: true, update: true, activate: true, recode: true, restructure: true, configure: true }, 
} as AccountsResponse);

describe("proposed chart templates in the Arabic RTL chart screen", () => {
  for (const [key, rows] of Object.entries(fixture as Record<string, Row[]>)) {
    it(`the ${key} template: every Arabic name reaches the page in logical order inside an RTL document`, async () => {
      const client = signedIn({ listAccounts: vi.fn().mockResolvedValue(response(accounts(rows))), getCurrencies: vi.fn().mockResolvedValue({ currencies: [] }) }, [...ALL_PERMISSIONS, "company.accounts.view"]);
      renderApp(client);
      await go("#/company/accounts");
      await screen.findByTestId("account-1101");
      expect(document.documentElement.dir).toBe("rtl");
      for (const r of rows) {
        const cell = screen.getByTestId(`account-${r.code}`);
        expect(cell.textContent).toContain(r.nameAr);
        expect([...r.nameAr].every((ch) => ch.codePointAt(0)! < 0x600 || ch.codePointAt(0)! <= 0x6ff)).toBe(true);        // only basic Arabic block letters and punctuation
        for (const word of r.nameAr.split(/[ ()/]+/).filter(Boolean)) expect(word[0]).not.toBe("ة");                // ta marbuta never starts a word: a reversed string would fail here
      }
      const retained = u(0x627, 0x644, 0x623, 0x631, 0x628, 0x627, 0x62D) + " " + u(0x627, 0x644, 0x645, 0x62D, 0x62A, 0x62C, 0x632, 0x629);     // الأرباح المحتجزة
      expect(screen.getByTestId("account-3102").textContent).toContain(retained);
    });
  }
});
