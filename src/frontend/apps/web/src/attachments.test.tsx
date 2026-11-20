import type { Attachment, LocalParty } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { catalog } from "./messages";
import { ALL_PERMISSIONS, apiError, fakeClient, renderApp, USER } from "./test-utils";

const A = (k: string) => catalog.ar[k]!;
const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};
beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const stamp = "2026-11-17T10:00:00Z";
const party = { id: "p1", code: "NR-1", displayName: "النور", notes: null, isActive: true, linkState: 1, globalPartyId: null, revision: 3, createdBy: "u1", createdAtUtc: stamp, updatedAtUtc: stamp, identity: null, requests: [] } as unknown as LocalParty;
const doc: Attachment = { id: "a1", subjectKind: "local_party", subjectId: "p1", fileName: "contract.pdf", contentType: "application/pdf", sizeBytes: 2048, sha256: "a".repeat(64), isSensitive: false, revision: 1, createdAtUtc: stamp };
const secret: Attachment = { ...doc, id: "a2", fileName: "salary.pdf", isSensitive: true, sizeBytes: 5 * 1024 * 1024 };

function setup(perms: string[], can = { upload: true, delete: true, sensitiveUpload: true }, over: Record<string, unknown> = {}) {
  const client = fakeClient({
    status: vi.fn().mockResolvedValue({ authenticated: true, user: USER }),
    getPartyCapabilities: vi.fn().mockResolvedValue({ view: true, create: true, update: true, submit: true, configure: true }),
    listParties: vi.fn().mockResolvedValue({ parties: [{ id: "p1", code: "NR-1", displayName: "النور", isActive: true, linkState: 1, legalNameAr: null, legalNameEn: null, hasOpenRequest: false, revision: 3, updatedAtUtc: stamp }], total: 1 }),
    getParty: vi.fn().mockResolvedValue(party),
    listAttachments: vi.fn().mockResolvedValue({ items: [doc, secret], can }),
    uploadAttachment: vi.fn().mockResolvedValue(doc),
    downloadAttachment: vi.fn().mockResolvedValue(new Blob(["x"], { type: "application/pdf" })),
    removeAttachment: vi.fn().mockResolvedValue({ ...doc, revision: 2 }),
    updateParty: vi.fn(),
    ...over,
  }, [...ALL_PERMISSIONS, "company.parties.view", "company.parties.update", ...perms]);
  renderApp(client);
  return client;
}
const open = async () => {
  await go("#/company/parties");
  await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
  await screen.findByTestId("pty-editor");
  await userEvent.click(await screen.findByRole("tab", { name: new RegExp("^" + esc(A("att.tab"))) }));
  return within(await screen.findByTestId("att-panel"));
};

describe("attachments tab of the party editor", () => {
  it("is offered only to a user who may view attachments", async () => {
    setup([]);
    await go("#/company/parties");
    await userEvent.click(within(await screen.findByTestId("pty-p1")).getByRole("button", { name: "عرض" }));
    await screen.findByTestId("pty-editor");
    expect(screen.queryByRole("tab", { name: new RegExp("^" + esc(A("att.tab"))) })).not.toBeInTheDocument();
  });

  it("lists the files with size and the sensitive marker, and saves a download as a file", async () => {
    const create = vi.fn().mockReturnValue("blob:x");
    Object.assign(URL, { createObjectURL: create, revokeObjectURL: vi.fn() });
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    const client = setup(["company.attachments.view"]);
    const panel = await open();
    expect(within(panel.getByTestId("att-contract.pdf")).getByText("2.0 KB")).toBeInTheDocument();
    const row = panel.getByTestId("att-salary.pdf");
    expect(within(row).getByText("5.0 MB")).toBeInTheDocument();
    expect(within(row).getByText(A("att.sensitive"))).toBeInTheDocument();
    await userEvent.click(within(panel.getByTestId("att-contract.pdf")).getByRole("button", { name: A("att.download") }));
    await waitFor(() => expect(client.downloadAttachment).toHaveBeenCalledWith("a1"));
    await waitFor(() => expect(click).toHaveBeenCalled());
    expect(create).toHaveBeenCalled();
    click.mockRestore();
  });

  it("uploads a chosen file with the sensitive option and reports the choice as unsaved until it is sent", async () => {
    const client = setup(["company.attachments.view"]);
    const panel = await open();
    const file = new File(["%PDF-1.4 x"], "new.pdf", { type: "application/pdf" });
    const send = panel.getByRole("button", { name: A("att.upload"), hidden: false });
    expect(send).toBeDisabled();
    await userEvent.upload(panel.getByLabelText(A("att.choose_file")), file);
    expect(send).toBeEnabled();
    await userEvent.click(panel.getByLabelText(A("att.sensitive_option")));
    await userEvent.click(send);
    await waitFor(() => expect(client.uploadAttachment).toHaveBeenCalledWith("local_party", "p1", file, "new.pdf", true));
    expect(await screen.findByText(A("att.uploaded"))).toBeInTheDocument();
    expect(send).toBeDisabled();                                                                    // the chosen file was cleared
  });

  it("offers the sensitive option only with the sensitive upload capability and shows a translated refusal", async () => {
    const upload = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "file", code: "content_type_mismatch" }] }));
    setup(["company.attachments.view"], { upload: true, delete: false, sensitiveUpload: false }, { uploadAttachment: upload });
    const panel = await open();
    expect(panel.queryByLabelText(A("att.sensitive_option"))).not.toBeInTheDocument();
    expect(panel.queryByRole("button", { name: A("att.remove") })).not.toBeInTheDocument();
    await userEvent.upload(panel.getByLabelText(A("att.choose_file")), new File(["PNG"], "bad.pdf", { type: "application/pdf" }));
    await userEvent.click(panel.getByRole("button", { name: A("att.upload") }));
    expect(await screen.findByText(A("field.content_type_mismatch"))).toBeInTheDocument();
  });

  it("removes with a reason and the revision it read, and an integrity failure is reported in words", async () => {
    const client = setup(["company.attachments.view"], undefined, {
      downloadAttachment: vi.fn().mockRejectedValue(apiError(409, "integrity_failure")),
    });
    const panel = await open();
    await userEvent.click(within(panel.getByTestId("att-contract.pdf")).getByRole("button", { name: A("att.download") }));
    expect(await screen.findByText(A("att.error.integrity_failure"))).toBeInTheDocument();
    await userEvent.click(within(panel.getByTestId("att-contract.pdf")).getByRole("button", { name: A("att.remove") }));
    const dialog = within(await screen.findByRole("dialog"));
    await userEvent.click(dialog.getByRole("button", { name: A("att.confirm") }));
    expect(client.removeAttachment).not.toHaveBeenCalled();                                         // a reason is required
    await userEvent.type(dialog.getByLabelText(new RegExp("^" + esc(A("att.reason")))), "رُفع بالخطأ");
    await userEvent.click(dialog.getByRole("button", { name: A("att.confirm") }));
    await waitFor(() => expect(client.removeAttachment).toHaveBeenCalledWith("a1", "رُفع بالخطأ", 1));
  });
});
