import type { Attachment, AttachmentSubjectKind, AttachmentsResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, ConfirmDialog, errorText, Field, Loading } from "../components";
import { useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/**
 * The attachments of ONE record (Phase 3.12), shown as a tab of the existing item and party editors for users who may view attachments. Files live on the server; the browser only uploads and
 * downloads through the authorized API (a download is always saved as a file, never rendered inline). Upload, removal and the sensitive option follow the server's capabilities; a removal needs a reason.
 * An unsaved choice of file is reported to the shared unsaved-changes guard.
 */
function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
const size = (n: number) => (n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`);

export function AttachmentsPanel({ kind, subjectId }: { kind: AttachmentSubjectKind; subjectId: string }) {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<AttachmentsResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [sensitive, setSensitive] = useState(false);
  const [removing, setRemoving] = useState<Attachment | null>(null);
  const [reason, setReason] = useState("");
  const [fileKey, setFileKey] = useState(0);
  const load = useCallback(async () => {
    try { setData(await auth.api.listAttachments(kind, subjectId)); setLoadError(null); }
    catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "att.error.")); }
  }, [auth.api, auth.handleError, kind, subjectId]);
  useEffect(() => { void load(); }, [load]);
  const runner = useRunner("att.error.", load, "att");
  useReportUnsaved(`attachments-${kind}-${subjectId}`, file !== null, t("att.title"));

  if (!data) return loadError ? <Banner kind="error" testId="att-load-error">{loadError}</Banner> : <Loading />;

  async function upload() {
    if (!file) return;
    const done = await runner.run(() => auth.api.uploadAttachment(kind, subjectId, file, file.name, sensitive), t("att.uploaded"));
    if (done) { setFile(null); setSensitive(false); setFileKey((k) => k + 1); }
  }
  async function download(a: Attachment) {
    await runner.run(async () => saveBlob(await auth.api.downloadAttachment(a.id), a.fileName), "");
  }
  async function remove() {
    if (!removing) return;
    const target = removing;
    const done = await runner.run(() => auth.api.removeAttachment(target.id, reason.trim(), target.revision), t("att.removed"));
    if (done) { setRemoving(null); setReason(""); }
  }

  return (
    <div data-testid="att-panel">
      <p className="hint">{t("att.hint")}</p>
      {runner.error && <Banner kind="error" testId="att-error">{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning" testId="att-conflict">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      {data.items.length === 0 ? <p data-testid="att-empty">{t("att.empty")}</p> : (
        <table className="table" data-testid="att-table">
          <thead><tr><th>{t("att.name")}</th><th>{t("att.type")}</th><th>{t("att.size")}</th><th>{t("att.added")}</th><th>{t("att.actions")}</th></tr></thead>
          <tbody>
            {data.items.map((a) => (
              <tr key={a.id} data-testid={`att-${a.fileName}`}>
                <td><bdi>{a.fileName}</bdi>{a.isSensitive && <> <span className="chip chip-off">{t("att.sensitive")}</span></>}</td>
                <td><bdi dir="ltr">{a.contentType}</bdi></td><td><bdi dir="ltr">{size(a.sizeBytes)}</bdi></td><td><bdi dir="ltr">{a.createdAtUtc.slice(0, 10)}</bdi></td>
                <td>
                  <button type="button" className="ghost" disabled={runner.busy} onClick={() => void download(a)}>{t("att.download")}</button>
                  {data.can.delete && <button type="button" className="ghost" disabled={runner.busy} onClick={() => { setRemoving(a); setReason(""); }}>{t("att.remove")}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {data.can.upload && (
        <fieldset disabled={runner.busy} data-testid="att-upload">
          <legend>{t("att.upload")}</legend>
          <p className="hint">{t("att.allowed")}</p>
          <Field id={`att-file-${kind}`} label={t("att.choose_file")} error={runner.fields.file}>
            {(a) => <input {...a} key={fileKey} type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />}
          </Field>
          {data.can.sensitiveUpload && <div className="field"><label><input type="checkbox" checked={sensitive} onChange={(e) => setSensitive(e.target.checked)} /> {t("att.sensitive_option")}</label></div>}
          <button type="button" className="primary" disabled={!file} onClick={() => void upload()}>{t("att.upload")}</button>
        </fieldset>
      )}
      {removing && (
        <ConfirmDialog title={t("att.remove_title")} message={t("att.remove_message")} confirmLabel={t("att.confirm")} cancelLabel={t("att.cancel")} onCancel={() => setRemoving(null)} onConfirm={() => { if (reason.trim()) void remove(); }}>
          <Field id="att-reason" label={t("att.reason")} required error={runner.fields.reason}>{(a) => <input {...a} value={reason} onChange={(e) => setReason(e.target.value)} />}</Field>
        </ConfirmDialog>
      )}
    </div>
  );
}
