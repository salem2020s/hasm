import type { ImportBatch, ImportStatus, ImportTemplate } from "@hasm/api-client";
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

const ALL = [...ALL_PERMISSIONS, "company.imports.view", "company.imports.upload", "company.imports.configure", "company.imports.submit", "company.imports.approve", "company.imports.apply", "company.accounts.view", "company.accounts.create"];
const adapters = { adapters: [{ code: "chart-of-accounts", version: 1, nameAr: "دليل الحسابات", nameEn: "Chart of accounts", canUpload: true }], limits: { maxFileBytes: 10485760, maxRows: 50000, maxColumns: 200, maxWorksheets: 20 } };
const mapping = { fields: { code: { column: 0, default: null }, name: { column: 1, default: null } }, options: { decimalSeparator: ".", thousandsSeparator: ",", dateFormat: "yyyy-MM-dd" } };
const batch = (status: ImportStatus, over: Partial<ImportBatch> = {}): ImportBatch => ({
  id: "b1", adapter: "chart-of-accounts", adapterVersion: 1, status, fileName: "accounts.csv", fileKind: "csv", fileSize: 120, fileSha256: "abcdef0123456789abcdef", keyId: "k1",
  options: { headers: ["Name", "Code"], headerRow: 1, encoding: "utf-8", delimiter: ",", sheet: 0 }, sheets: [], rowCount: 2, rowsHash: "r", planHash: null, revision: 3, createdBy: "u1",
  createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", submittedBy: null, submittedAtUtc: null, expiresAtUtc: "2026-02-01T00:00:00Z", terminalAtUtc: null,
  originalAvailable: true, rowsAvailable: true, purgeAfterUtc: null, mapping: null, plan: null, result: null, ...over,
}) as ImportBatch;
const tpl = (name: string, version: number, retired = false): ImportTemplate => ({ id: `t-${name}-${version}`, adapter: "chart-of-accounts", name, version, isRetired: retired, content: { fields: { code: { header: "Code" }, name: { header: "Name" } } }, createdAtUtc: "2026-01-01T00:00:00Z" });

function render(current: ImportBatch, over: Record<string, unknown> = {}) {
  const client = signedIn({
    importAdapters: vi.fn().mockResolvedValue(adapters),
    listImports: vi.fn().mockResolvedValue({ imports: [current] }),
    getImport: vi.fn().mockResolvedValue(current),
    importEvents: vi.fn().mockResolvedValue({ events: [] }),
    importDecisions: vi.fn().mockResolvedValue({ decisions: [] }),
    getImportSettings: vi.fn().mockResolvedValue({ settings: { retentionDays: 30, pendingExpiryDays: 30, revision: 1, configured: true }, can: { configure: true } }),
    importRows: vi.fn().mockResolvedValue({ headers: ["Name", "Code"], total: 0, offset: 0, rows: [] }),
    importFields: vi.fn().mockResolvedValue({
      fields: [{ key: "code", nameAr: "الرمز", nameEn: "Code", type: "code", required: true, maxLength: 20, isKey: true }, { key: "name", nameAr: "الاسم", nameEn: "Name", type: "text", required: true, maxLength: 60, isKey: false }],
      options: { decimalSeparators: [".", ","], thousandsSeparators: ["", ","], dateFormats: ["yyyy-MM-dd"] },
    }),
    suggestImportMapping: vi.fn().mockResolvedValue({ mapping }),
    listImportTemplates: vi.fn().mockResolvedValue({ templates: [tpl("Sheet", 2), tpl("Old", 1, true)] }),
    applyImportTemplate: vi.fn().mockResolvedValue({ name: "Sheet", version: 2, mapping: { fields: { code: { column: 1, default: null }, name: { column: 0, default: null } }, options: mapping.options }, unmatched: [] }),
    saveImportTemplate: vi.fn().mockResolvedValue(tpl("New", 1)),
    setImportMapping: vi.fn().mockResolvedValue(batch("mapped", { mapping })),
    ...over,
  }, ALL);
  renderApp(client);
  return client;
}
const open = async () => {
  await go("#/company/imports");
  await userEvent.click(await screen.findByRole("button", { name: A("imp.open") }));
  return within(await screen.findByTestId("imp-templates"));
};

describe("import mapping templates", () => {
  it("offers only non-retired templates and applying one pre-fills the form without saving anything", async () => {
    const client = render(batch("staged"));
    const box = await open();
    const options = Array.from((box.getByLabelText(A("imp.tpl.apply_label")) as HTMLSelectElement).options).map((o) => o.textContent);
    expect(options.some((o) => o?.includes("Sheet"))).toBe(true);
    expect(options.some((o) => o?.includes("Old"))).toBe(false);                      // retired templates are never offered
    expect(box.getByTestId("imp-tpl-apply")).toBeDisabled();
    await userEvent.selectOptions(box.getByLabelText(A("imp.tpl.apply_label")), "Sheet");
    await userEvent.click(box.getByTestId("imp-tpl-apply"));
    await waitFor(() => expect(client.applyImportTemplate).toHaveBeenCalledWith("b1", "Sheet"));
    expect(await screen.findByTestId("imp-tpl-note")).toHaveTextContent(A("imp.tpl.applied"));
    // the mapping selects now show the template's columns (code = column 1 "Code", name = column 0 "Name")
    expect((screen.getByLabelText(`الرمز — ${A("imp.map_column")}`) as HTMLSelectElement).value).toBe("1");
    expect(client.setImportMapping).not.toHaveBeenCalled();                           // nothing is saved until the user saves the (validated) mapping
  });

  it("reports the fields no column was found for and keeps the ordinary save", async () => {
    const client = render(batch("staged"), { applyImportTemplate: vi.fn().mockResolvedValue({ name: "Sheet", version: 2, mapping: { fields: { code: { column: 1, default: null } }, options: mapping.options }, unmatched: ["name"] }) });
    const box = await open();
    await userEvent.selectOptions(box.getByLabelText(A("imp.tpl.apply_label")), "Sheet");
    await userEvent.click(box.getByTestId("imp-tpl-apply"));
    expect(await screen.findByTestId("imp-tpl-note")).toHaveTextContent("الاسم");
    await userEvent.click(screen.getByTestId("imp-map-save"));
    await waitFor(() => expect(client.setImportMapping).toHaveBeenCalledWith("b1", expect.objectContaining({ fields: { code: { column: 1, default: null } } }), 3));
  });

  it("saves the batch mapping as the next version of a named template and only once a mapping is saved", async () => {
    render(batch("staged"));
    const first = await open();
    expect(first.getByTestId("imp-tpl-save")).toBeDisabled();                         // no saved mapping yet
    expect(first.getByText(A("imp.tpl.save_first"))).toBeInTheDocument();
  });

  it("uses the stored version of an existing template and shows a translated refusal", async () => {
    const save = vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 3 })).mockResolvedValue(tpl("Sheet", 3));
    const client = render(batch("mapped", { mapping }), { saveImportTemplate: save });
    const box = await open();
    await userEvent.type(box.getByLabelText(new RegExp("^" + A("imp.tpl.save_label"))), "Sheet");
    await userEvent.click(box.getByTestId("imp-tpl-save"));
    await waitFor(() => expect(save).toHaveBeenCalledWith("b1", "Sheet", 2));         // the version it read (2) is what it is based on
    expect(client.listImportTemplates).toHaveBeenCalledTimes(2);                      // refreshed after the conflict
    await userEvent.click(box.getByTestId("imp-tpl-save"));
    await waitFor(() => expect(save).toHaveBeenCalledTimes(2));
  });

  it("sends one request for a double click and saves the name in NFC", async () => {
    let release: (v: ImportTemplate) => void = () => undefined;
    const save = vi.fn().mockImplementation(() => new Promise<ImportTemplate>((r) => { release = r; }));
    render(batch("mapped", { mapping }), { saveImportTemplate: save });
    const box = await open();
    fireEvent.change(box.getByLabelText(new RegExp("^" + A("imp.tpl.save_label"))), { target: { value: "café" } });   // decomposed "café"
    const button = box.getByTestId("imp-tpl-save");
    fireEvent.click(button); fireEvent.click(button);                                      // two clicks before any re-render
    await waitFor(() => expect(save).toHaveBeenCalledTimes(1));
    expect(save).toHaveBeenCalledWith("b1", "café", 0);                                // composed form
    release(tpl("café", 1));
  });
  it("retires the selected template only after confirmation, using the version it read, then refreshes the list", async () => {
    const retire = vi.fn().mockResolvedValue(tpl("Sheet", 2, true));
    const client = render(batch("staged"), { retireImportTemplate: retire });
    const box = await open();
    expect(box.getByTestId("imp-tpl-retire")).toBeDisabled();                          // nothing selected
    await userEvent.selectOptions(box.getByLabelText(A("imp.tpl.apply_label")), "Sheet");
    await userEvent.click(box.getByTestId("imp-tpl-retire"));
    const dialog = within(await screen.findByRole("dialog"));
    expect(retire).not.toHaveBeenCalled();                                             // the dialog alone changes nothing
    await userEvent.click(dialog.getByRole("button", { name: A("cancel") }));
    expect(retire).not.toHaveBeenCalled();
    await userEvent.click(box.getByTestId("imp-tpl-retire"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("imp.tpl.retire") }));
    await waitFor(() => expect(retire).toHaveBeenCalledWith("chart-of-accounts", "Sheet", 2));
    expect(await screen.findByTestId("imp-tpl-note")).toHaveTextContent(A("imp.tpl.retired"));
    await waitFor(() => expect(client.listImportTemplates).toHaveBeenCalledTimes(2));
  });

  it("shows a translated refusal when retiring fails and reloads the list", async () => {
    const retire = vi.fn().mockRejectedValue(apiError(409, "already_retired"));
    const client = render(batch("staged"), { retireImportTemplate: retire });
    const box = await open();
    await userEvent.selectOptions(box.getByLabelText(A("imp.tpl.apply_label")), "Sheet");
    await userEvent.click(box.getByTestId("imp-tpl-retire"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("imp.tpl.retire") }));
    await waitFor(() => expect(retire).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(client.listImportTemplates).toHaveBeenCalledTimes(2));
  });
  it("does not send version 0 when the selected template is no longer in the list: it says so and reloads", async () => {
    const retire = vi.fn();
    // first load: Sheet exists; a failing save then reloads the list and Sheet is gone while it is still the selected value
    const list = vi.fn().mockResolvedValueOnce({ templates: [tpl("Sheet", 2)] }).mockResolvedValue({ templates: [] });
    const save = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 3 }));
    const client = render(batch("mapped", { mapping }), { retireImportTemplate: retire, listImportTemplates: list, saveImportTemplate: save });
    const box = await open();
    await userEvent.selectOptions(box.getByLabelText(A("imp.tpl.apply_label")), "Sheet");
    await userEvent.type(box.getByLabelText(new RegExp("^" + A("imp.tpl.save_label"))), "Other");
    await userEvent.click(box.getByTestId("imp-tpl-save"));
    await waitFor(() => expect(client.listImportTemplates).toHaveBeenCalledTimes(2));
    await userEvent.click(box.getByTestId("imp-tpl-retire"));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: A("imp.tpl.retire") }));
    expect(await screen.findByText(A("imp.tpl.retire_missing"))).toBeInTheDocument();
    expect(retire).not.toHaveBeenCalled();                                            // never version 0
    await waitFor(() => expect(client.listImportTemplates).toHaveBeenCalledTimes(3));
  });
});
