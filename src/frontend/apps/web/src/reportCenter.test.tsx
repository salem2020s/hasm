import type { ReportDefinitionInfo } from "@hasm/api-client";
import { fireEvent, screen, within } from "@testing-library/react";
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

const def = (id: string, titleAr: string, over: Partial<ReportDefinitionInfo> = {}): ReportDefinitionInfo => ({
  id, titleAr, titleEn: id, canExport: true, formats: ["csv", "xlsx", "pdf"], supportsComparison: false, supportsGrouping: false,
  parameters: [{ name: "from", kind: "date", required: false, titleAr: "من تاريخ", titleEn: "From" }], columns: [], ...over,
});
const perms = [...ALL_PERMISSIONS, "company.ledgerreports.view"];

describe("report center", () => {
  it("lists the reports the server offers with formats, export right, parameters and opens the right screen", async () => {
    renderApp(signedIn({ getReportDefinitions: vi.fn().mockResolvedValue({ reports: [def("trial-balance", "ميزان المراجعة", { supportsComparison: true }), def("profit-and-loss", "قائمة الأرباح والخسائر", { canExport: false })] }) }, perms));
    await go("#/accounting/report-center");
    expect(await screen.findByRole("heading", { name: A("rc.title"), level: 1 })).toBeInTheDocument();
    const tb = screen.getByTestId("rc-trial-balance");
    expect(tb).toHaveTextContent("CSV · XLSX · PDF");
    expect(tb).toHaveTextContent(A("rc.can_export"));
    expect(tb).toHaveTextContent(A("rc.comparison"));
    expect(screen.getByTestId("rc-profit-and-loss")).toHaveTextContent(A("rc.cannot_export"));
    await userEvent.click(within(screen.getByTestId("rc-profit-and-loss")).getByRole("button", { name: A("rc.open") }));
    expect(window.location.hash).toBe("#/accounting/statements");
  });

  it("states that the visual report designer is not delivered yet and that nothing is listed without permission", async () => {
    renderApp(signedIn({ getReportDefinitions: vi.fn().mockResolvedValue({ reports: [] }) }, perms));
    await go("#/accounting/report-center");
    expect(await screen.findByTestId("rc-none")).toHaveTextContent(A("rc.none"));
    expect(screen.getByTestId("rc-designer-note")).toHaveTextContent(A("rc.designer_note"));
  });

  it("shows a translated error and is not reachable without a ledger-report permission", async () => {
    renderApp(signedIn({ getReportDefinitions: vi.fn().mockRejectedValue(apiError(403, "forbidden")) }, perms));
    await go("#/accounting/report-center");
    expect(await screen.findByTestId("rc-error")).toHaveTextContent(A("rep.error.forbidden"));
  });

  it("is hidden from a user without the permission", async () => {
    renderApp(signedIn({ getReportDefinitions: vi.fn().mockResolvedValue({ reports: [] }) }, ALL_PERMISSIONS));
    await go("#/accounting/report-center");
    expect(screen.queryByRole("heading", { name: A("rc.title"), level: 1 })).toBeNull();
  });
});
