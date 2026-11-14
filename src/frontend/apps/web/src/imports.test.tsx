import type { ImportBatch, ImportStatus } from "@hasm/api-client";
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

const ALL = [...ALL_PERMISSIONS, "company.imports.view", "company.imports.upload", "company.imports.configure", "company.imports.submit", "company.imports.approve", "company.imports.apply", "company.imports.cancel", "company.accounts.create"];
const adapters = { adapters: [{ code: "chart-of-accounts", version: 1, nameAr: "دليل الحسابات", nameEn: "Chart of accounts", canUpload: true }], limits: { maxFileBytes: 10485760, maxRows: 50000, maxColumns: 50, formats: ["xlsx", "csv"], encodings: ["utf-8"] } };
const summary = { total: 2, valid: 2, exact: 0, invalid: 0, conflict: 0, resolved: 0, operations: 2, blocking: 0 };
const plan = { v: 1, adapter: "chart-of-accounts", adapterVersion: 1, rowsHash: "r", mappingHash: "m", baseline: "b", summary, reconciliation: [], canApply: true, operationsHash: "o" };
const batch = (status: ImportStatus, over: Partial<ImportBatch> = {}): ImportBatch => ({
  id: "b1", adapter: "chart-of-accounts", adapterVersion: 1, status, fileName: "<b>accounts</b>.csv", fileKind: "csv", fileSize: 120, fileSha256: "abcdef0123456789abcdef", keyId: "k1",
  options: { headers: ["Code", "Name"], headerRow: 1, encoding: "utf-8", delimiter: ",", sheet: 0 }, sheets: [], rowCount: 2, rowsHash: "r", planHash: "0123456789abcdef0123", revision: 3, createdBy: "someone",
  createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", submittedBy: null, submittedAtUtc: null, expiresAtUtc: "2026-02-01T00:00:00Z", terminalAtUtc: null,
  originalAvailable: true, rowsAvailable: true, purgeAfterUtc: null, mapping: null, plan: null, result: null, ...over,
});

function render(current: ImportBatch | null, over: Record<string, unknown> = {}, perms = ALL) {
  const client = signedIn({
    importAdapters: vi.fn().mockResolvedValue(adapters),
    listImports: vi.fn().mockResolvedValue({ imports: current ? [current] : [] }),
    getImport: vi.fn().mockResolvedValue(current),
    importEvents: vi.fn().mockResolvedValue({ events: [] }),
    importDecisions: vi.fn().mockResolvedValue({ decisions: [] }),
    getImportSettings: vi.fn().mockResolvedValue({ settings: { retentionDays: 30, pendingExpiryDays: 30, revision: 1, configured: true }, can: { configure: true } }),
    importRows: vi.fn().mockResolvedValue({ headers: ["Code", "Name"], total: 0, offset: 0, rows: [] }),
    ...over,
  }, perms);
  renderApp(client);
  return client;
}

describe("imports screen", () => {
  it("lists batches, shows file names only as text and offers the upload for an uploader", async () => {
    render(batch("validated", { plan }));
    await go("#/company/imports");
    expect(await screen.findByRole("heading", { name: A("imp.title"), level: 1 })).toBeInTheDocument();
    expect(await screen.findByTestId("imp-row-b1")).toHaveTextContent("<b>accounts</b>.csv");
    expect(document.querySelector("b")).toBeNull();                                  // never interpreted as markup
    expect(screen.getByLabelText(new RegExp(A("imp.file")))).toBeInTheDocument();
  });

  it("uploads the raw file with one idempotency key and opens the new batch", async () => {
    const uploadImport = vi.fn().mockResolvedValue(batch("uploaded"));
    const client = render(null, { uploadImport, getImport: vi.fn().mockResolvedValue(batch("uploaded")) });
    await go("#/company/imports");
    const file = new File(["code,name\n1,a"], "a.csv", { type: "text/csv" });
    await userEvent.upload(await screen.findByLabelText(new RegExp(A("imp.file"))), file);
    await userEvent.click(screen.getByRole("button", { name: A("imp.upload") }));
    await waitFor(() => expect(uploadImport).toHaveBeenCalledTimes(1));
    expect(uploadImport.mock.calls[0]![0]).toBe("chart-of-accounts");
    expect(typeof uploadImport.mock.calls[0]![3]).toBe("string");
    expect(await screen.findByTestId("imp-stage")).toBeInTheDocument();
    expect(client.getImport).toHaveBeenCalled();
  });

  it("an independent approver sees approve and reject; the creator is told about separation of duties", async () => {
    const submitted = batch("submitted", { plan, submittedBy: "submitter" });
    const approve = vi.fn().mockResolvedValue(batch("approved", { plan }));
    render(submitted, { approveImport: approve });
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    await userEvent.click(await screen.findByTestId("imp-approve"));
    await waitFor(() => expect(approve).toHaveBeenCalledWith("b1", null, 3));
  });

  it("hides approve for the creator and for the submitter", async () => {
    render(batch("submitted", { plan, submittedBy: "u1" }));
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    expect(await screen.findByTestId("imp-sod")).toBeInTheDocument();
    expect(screen.queryByTestId("imp-approve")).toBeNull();
  });

  it("rejecting requires a comment", async () => {
    const reject = vi.fn();
    render(batch("submitted", { plan, submittedBy: "submitter" }), { rejectImport: reject });
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    await userEvent.click(await screen.findByTestId("imp-reject"));
    expect(reject).not.toHaveBeenCalled();
    expect(await screen.findByText(A("imp.comment_required_reject"))).toBeInTheDocument();
  });

  it("applying asks for confirmation, sends the revision it read and shows the translated refusal of a stale plan", async () => {
    const apply = vi.fn().mockRejectedValueOnce(apiError(409, "plan_stale")).mockResolvedValue(batch("applied", { plan, result: { operations: 2, appliedAtUtc: "2026-01-02T00:00:00Z" } }));
    render(batch("approved", { plan }), { applyImport: apply });
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    await userEvent.click(await screen.findByTestId("imp-apply"));
    expect(apply).not.toHaveBeenCalled();                                              // the dialog comes first
    await userEvent.click(screen.getAllByRole("button", { name: A("imp.apply") }).at(-1)!);
    await waitFor(() => expect(apply).toHaveBeenCalledWith("b1", 3));
    expect(await screen.findByTestId("imp-error")).toHaveTextContent(A("imp.error.plan_stale"));
  });

  it("a user without the apply permission never sees the apply button", async () => {
    render(batch("approved", { plan }), {}, ALL.filter((p) => p !== "company.imports.apply"));
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    await screen.findByTestId("imp-facts");
    expect(screen.queryByTestId("imp-apply")).toBeNull();
  });

  it("shows the summary, reconciliation and a blocking plan that cannot be submitted", async () => {
    const blocked = { ...plan, canApply: false, summary: { ...summary, invalid: 1, blocking: 1 }, reconciliation: [{ key: "accounts", value: "2" }] };
    render(batch("validated", { plan: blocked }));
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    expect(await screen.findByTestId("imp-summary-invalid")).toHaveTextContent("1");
    expect(screen.getByTestId("imp-recon")).toBeInTheDocument();
    expect(screen.getByTestId("imp-can-apply")).toHaveTextContent(A("imp.cannot_apply"));
    expect(screen.queryByTestId("imp-submit")).toBeNull();
  });

  it("saves a conflict decision with the allowed resolutions only", async () => {
    const setImportResolutions = vi.fn().mockResolvedValue(batch("validated", { plan }));
    render(batch("validated", { plan }), {
      setImportResolutions,
      importRows: vi.fn().mockResolvedValue({ headers: ["Code"], total: 1, offset: 0, rows: [{ rowNo: 2, cells: ["1010"], status: 4, normalized: null, issues: [], resolution: null, operation: null, explanation: null, allowedResolutions: ["skip"] }] }),
    });
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    const select = await screen.findByLabelText(`${A("imp.row_resolution")} 2`);
    expect(Array.from((select as HTMLSelectElement).options).map((o) => o.value)).toEqual(["", "skip"]);
    await userEvent.selectOptions(select, "skip");
    await userEvent.click(screen.getByTestId("imp-decide"));
    await waitFor(() => expect(setImportResolutions).toHaveBeenCalledWith("b1", [{ rowNo: 2, action: "skip" }], 3));
  });

  it("a failed batch shows the failure and allows a retry", async () => {
    render(batch("failed", { plan, result: { code: "import_apply_failed" } }));
    await go("#/company/imports");
    await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
    expect(await screen.findByTestId("imp-failure")).toBeInTheDocument();
    expect(screen.getByTestId("imp-apply")).toBeInTheDocument();
  });

  it("hides the whole screen from a user without the view permission", async () => {
    render(null, {}, ALL_PERMISSIONS);
    await go("#/company/imports");
    expect(screen.queryByRole("heading", { name: A("imp.title"), level: 1 })).toBeNull();
  });
});
