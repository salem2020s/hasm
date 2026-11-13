import { ApiError, type Branch, type BranchesResponse, type BranchInput } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, fieldErrors, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";

/**
 * Branch management (Phase 3.1B). ONE implementation for the Web app and the Windows Desktop app. All rules (the exactly-one-main invariant,
 * the Trial limit, immutable codes, optimistic concurrency, branch-scoped RBAC, auditing) are enforced by the server and PostgreSQL; this
 * screen validates for convenience only and shows or hides controls from what the server reports the caller may do.
 */

const CODE_SHAPE = /^[A-Z0-9][A-Z0-9_-]{0,19}$/;
const PHONE_SHAPE = /^[\p{Nd}+()\-. ]{5,30}$/u;
const EMAIL_SHAPE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
const URL_SHAPE = /^https?:\/\/\S+$/i;

type FieldKey = "code" | "nameAr" | "nameEn" | "addressLine1" | "addressLine2" | "city" | "region" | "postalCode" | "phone" | "email" | "website";
type Form = Record<FieldKey, string>;
const KEYS: FieldKey[] = ["code", "nameAr", "nameEn", "addressLine1", "addressLine2", "city", "region", "postalCode", "phone", "email", "website"];
const empty = (): Form => Object.fromEntries(KEYS.map((k) => [k, ""])) as Form;
const toForm = (b: Branch | null): Form => (b ? (Object.fromEntries(KEYS.map((k) => [k, b[k] ?? ""])) as Form) : empty());
const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

/** The branch name for the current language, falling back to the other language (same convention as company names). */
export function branchName(b: Pick<Branch, "nameAr" | "nameEn" | "code">, locale: string): string {
  const preferred = locale === "en" ? b.nameEn : b.nameAr;
  const other = locale === "en" ? b.nameAr : b.nameEn;
  return preferred?.trim() || other?.trim() || b.code;
}

type Editor = { mode: "new" } | { mode: "edit"; branch: Branch };
type Confirm = { kind: "main" | "activate" | "deactivate"; branch: Branch };

export function BranchesPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [data, setData] = useState<BranchesResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<"all" | "active" | "inactive">("all");
  const [editor, setEditor] = useState<Editor | null>(null);
  const [confirm, setConfirm] = useState<Confirm | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      setData(await auth.api.listBranches());
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "branches.error."));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  const visible = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (data?.branches ?? []).filter((b) =>
      (status === "all" || (status === "active") === b.isActive)
      && (!q || b.code.toLowerCase().includes(q) || (b.nameAr ?? "").toLowerCase().includes(q) || (b.nameEn ?? "").toLowerCase().includes(q)));
  }, [data, search, status]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!data) return <Loading />;

  const can = data.can;
  const limitReached = data.limit !== null && data.everCreated !== null && data.everCreated >= data.limit;
  const anyAction = can.create || can.activate || can.designate || can.update.length > 0;

  function openEditor(next: Editor) { guard(() => { setNotice(null); setError(null); setConflict(false); setEditor(next); }); }
  function closeEditor() { guard(() => setEditor(null)); }

  async function run(action: () => Promise<unknown>, message: string) {
    setBusy(true);
    setError(null);
    setConflict(false);
    setNotice(null);
    try {
      await action();
      await load();
      setNotice(message);
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else setError(errorText(t, err, "branches.error."));
    } finally {
      setBusy(false);
    }
  }

  const apply = (c: Confirm, successor?: string) => {
    setConfirm(null);
    if (c.kind === "main") void run(() => auth.api.makeMainBranch(c.branch.id, c.branch.revision), t("branches.changed"));
    else if (c.kind === "activate") void run(() => auth.api.activateBranch(c.branch.id, c.branch.revision), t("branches.changed"));
    else void run(() => auth.api.deactivateBranch(c.branch.id, c.branch.revision, successor), t("branches.changed"));
  };

  return (
    <section aria-labelledby="page-title">
      <PageHeader
        title={t("branches.title")}
        description={t("branches.subtitle")}
        actions={can.create && <button type="button" className="primary" disabled={limitReached || busy} onClick={() => openEditor({ mode: "new" })}>{t("branches.new")}</button>}
      />
      {!anyAction && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {data.limit !== null && data.everCreated !== null && (
        <div className="notices">
          <Banner kind="info" testId="branch-usage">{t("branches.usage", { created: String(data.everCreated), limit: String(data.limit) })}</Banner>
          {limitReached && <Banner kind="warning" testId="branch-limit">{t("branches.limit_reached")}</Banner>}
        </div>
      )}
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("branches.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}

      <Card title={t("branches.list")}>
        <div className="toolbar">
          <Field id="b-search" label={t("branches.search")}>
            {(aria) => <input {...aria} type="search" value={search} onChange={(e) => setSearch(e.target.value)} autoComplete="off" />}
          </Field>
          <Field id="b-status" label={t("branches.filter_status")}>
            {(aria) => (
              <select {...aria} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
                <option value="all">{t("branches.filter_all")}</option>
                <option value="active">{t("branches.filter_active")}</option>
                <option value="inactive">{t("branches.filter_inactive")}</option>
              </select>
            )}
          </Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("branches.list")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("branches.code")}</th><th scope="col">{t("branches.name")}</th>
                <th scope="col">{t("branches.status")}</th><th scope="col">{t("branches.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {visible.length === 0 && <tr><td colSpan={4}>{data.branches.length === 0 ? t("branches.none") : t("branches.none_filtered")}</td></tr>}
              {visible.map((b) => {
                const canUpdate = can.update.includes(b.id);
                return (
                  <tr key={b.id} data-testid={`branch-${b.code}`}>
                    <td><bdi dir="ltr">{b.code}</bdi></td>
                    <td>
                      <bdi>{branchName(b, locale)}</bdi>{" "}
                      {b.isMain && <span className="chip chip-base">{t("branches.main")}</span>}
                    </td>
                    <td><span className={`chip ${b.isActive ? "chip-ok" : "chip-off"}`}>{b.isActive ? t("branches.active") : t("branches.inactive")}</span></td>
                    <td>
                      <button type="button" disabled={busy} onClick={() => openEditor({ mode: "edit", branch: b })}>{canUpdate ? t("branches.edit") : t("branches.view")}</button>{" "}
                      {can.designate && b.isActive && !b.isMain && (
                        <button type="button" disabled={busy} onClick={() => setConfirm({ kind: "main", branch: b })}>{t("branches.make_main")}</button>
                      )}{" "}
                      {can.activate && b.isActive && <button type="button" disabled={busy} onClick={() => setConfirm({ kind: "deactivate", branch: b })}>{t("branches.deactivate")}</button>}
                      {can.activate && !b.isActive && <button type="button" disabled={busy} onClick={() => setConfirm({ kind: "activate", branch: b })}>{t("branches.activate")}</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {data.branches.some((b) => b.seeded) && <p className="hint">{t("branches.seeded_note")}</p>}
      </Card>

      {editor && (
        <BranchForm
          key={editor.mode === "edit" ? editor.branch.id : "new"}
          editor={editor}
          canEdit={editor.mode === "new" ? can.create : can.update.includes(editor.branch.id)}
          onClose={closeEditor}
          onSaved={(message) => { setEditor(null); setNotice(message); void load(); }}
        />
      )}

      {confirm && confirm.kind === "deactivate" && (
        <DeactivateDialog
          branch={confirm.branch}
          candidates={data.branches.filter((b) => b.isActive && b.id !== confirm.branch.id)}
          onCancel={() => setConfirm(null)}
          onConfirm={(successor) => apply(confirm, successor)}
        />
      )}
      {confirm && confirm.kind !== "deactivate" && (
        <ConfirmDialog
          title={confirm.kind === "main" ? t("branches.confirm_main_title") : t("branches.activate_title")}
          message={confirm.kind === "main" ? t("branches.confirm_main_message", { code: confirm.branch.code }) : t("branches.activate_message", { code: confirm.branch.code })}
          confirmLabel={t("form.confirm")}
          onCancel={() => setConfirm(null)}
          onConfirm={() => apply(confirm)}
        />
      )}
    </section>
  );
}

/** Deactivation. The main branch can only be deactivated together with another ACTIVE branch becoming main (one atomic server operation). */
function DeactivateDialog({ branch, candidates, onCancel, onConfirm }: { branch: Branch; candidates: Branch[]; onCancel: () => void; onConfirm: (successor?: string) => void }) {
  const { t, locale } = useI18n();
  const cancel = useRef<HTMLButtonElement>(null);
  const [successor, setSuccessor] = useState("");
  useEffect(() => cancel.current?.focus(), []);
  const needsSuccessor = branch.isMain;
  const blocked = needsSuccessor && candidates.length === 0;
  return (
    <div className="dialog-backdrop">
      <div role="dialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-message" className="dialog" onKeyDown={(e) => { if (e.key === "Escape") onCancel(); }}>
        <h2 id="confirm-title">{t("branches.deactivate_title")}</h2>
        <p id="confirm-message">{needsSuccessor ? t("branches.deactivate_main_message", { code: branch.code }) : t("branches.deactivate_message", { code: branch.code })}</p>
        {needsSuccessor && (blocked ? <Banner kind="warning" testId="no-successor">{t("branches.no_successor")}</Banner> : (
          <Field id="b-successor" label={t("branches.successor")} required>
            {(aria) => (
              <select {...aria} value={successor} onChange={(e) => setSuccessor(e.target.value)}>
                <option value="">{t("form.select")}</option>
                {candidates.map((c) => <option key={c.id} value={c.id}>{c.code} — {branchName(c, locale)}</option>)}
              </select>
            )}
          </Field>
        ))}
        <div className="actions">
          <button type="button" ref={cancel} onClick={onCancel}>{t("cancel")}</button>
          <button type="button" className="danger" disabled={blocked || (needsSuccessor && !successor)} onClick={() => onConfirm(needsSuccessor ? successor : undefined)}>{t("branches.deactivate")}</button>
        </div>
      </div>
    </div>
  );
}

function BranchForm({ editor, canEdit, onClose, onSaved }: { editor: Editor; canEdit: boolean; onClose: () => void; onSaved: (message: string) => void }) {
  const { t } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const [revision, setRevision] = useState(editor.mode === "edit" ? editor.branch.revision : 0);
  const [baseline, setBaseline] = useState<Form>(() => toForm(editor.mode === "edit" ? editor.branch : null));
  const [form, setForm] = useState<Form>(baseline);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const dirty = KEYS.some((k) => form[k] !== baseline[k]);
  useReportUnsaved("branch-form", dirty, t("branches.form_section"));

  const set = (k: FieldKey) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (isNew) {
      const code = form.code.trim().toUpperCase();
      if (!code) v.code = t("field.required"); else if (!CODE_SHAPE.test(code)) v.code = t("field.invalid_format");
    }
    if (!form.nameAr.trim() && !form.nameEn.trim()) { v.nameAr = t("branches.name_required"); v.nameEn = t("branches.name_required"); }
    if (form.phone.trim() && !PHONE_SHAPE.test(form.phone.trim())) v.phone = t("field.invalid_format");
    if (form.email.trim() && !EMAIL_SHAPE.test(form.email.trim())) v.email = t("field.invalid_format");
    if (form.website.trim() && !URL_SHAPE.test(form.website.trim())) v.website = t("field.invalid_url");
    return v;
  }

  /** Server field errors; a missing name uses the branch wording instead of the company one. */
  function serverFieldErrors(err: unknown): Record<string, string> {
    const out = fieldErrors(t, err);
    if (err instanceof ApiError) for (const f of err.details.fields ?? []) if (f.code === "name_required") out[f.field] = t("branches.name_required");
    return out;
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v = validate();
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true);
    setError(null);
    setConflict(false);
    try {
      const body: BranchInput = {
        nameAr: blankToNull(form.nameAr), nameEn: blankToNull(form.nameEn),
        addressLine1: blankToNull(form.addressLine1), addressLine2: blankToNull(form.addressLine2), city: blankToNull(form.city), region: blankToNull(form.region),
        postalCode: blankToNull(form.postalCode), phone: blankToNull(form.phone), email: blankToNull(form.email), website: blankToNull(form.website),
      };
      if (editor.mode === "new") {
        await auth.api.createBranch({ ...body, code: form.code.trim().toUpperCase() });
        onSaved(t("branches.created"));
      } else {
        await auth.api.updateBranch(editor.branch.id, body, revision);
        onSaved(t("branches.updated"));
      }
    } catch (err) {
      // A failed save changes nothing: the form keeps what the user typed.
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) { setErrors(serverFieldErrors(err)); setError(t("form.fix_errors")); }
      else if (err instanceof ApiError && err.status === 409 && err.code === "branch_code_taken") { setErrors({ code: t("branches.error.branch_code_taken") }); setError(t("form.fix_errors")); }
      else setError(errorText(t, err, "branches.error."));
    } finally {
      setBusy(false);
    }
  }

  async function reloadLatest() {
    if (editor.mode !== "edit") return;
    try {
      const latest = await auth.api.getBranch(editor.branch.id);
      const next = toForm(latest);
      setRevision(latest.revision);
      setBaseline(next);
      setForm(next);
      setErrors({});
      setConflict(false);
    } catch (err) {
      if (!auth.handleError(err)) setError(errorText(t, err, "branches.error."));
    }
  }

  const text = (k: FieldKey, label: string, extra: { required?: boolean; dir?: "ltr" | "rtl"; type?: string; hint?: string; readOnly?: boolean } = {}) => (
    <Field id={`b-${k}`} label={label} error={errors[k]} required={extra.required} hint={extra.hint}>
      {(aria) => <input {...aria} name={k} value={form[k]} onChange={set(k)} readOnly={!canEdit || extra.readOnly} dir={extra.dir} type={extra.type ?? "text"} autoComplete="off" />}
    </Field>
  );

  return (
    <Card title={isNew ? t("branches.form_new") : t("branches.form_edit")}>
      {!canEdit && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="form-conflict">
          {t("branches.error.revision_conflict")} <button type="button" onClick={() => void reloadLatest()}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
        <Card title={t("branches.identity")} hint={t("branches.names_hint")}>
          <div className="fields">
            {text("code", t("branches.code"), { required: isNew, dir: "ltr", readOnly: !isNew, hint: isNew ? t("branches.code_hint") : undefined })}
            {text("nameAr", t("branches.name_ar"), { dir: "rtl" })}
            {text("nameEn", t("branches.name_en"), { dir: "ltr" })}
          </div>
        </Card>
        <Card title={t("branches.address")}>
          <div className="fields">
            {text("addressLine1", t("branches.address1"))}
            {text("addressLine2", t("branches.address2"))}
            {text("city", t("branches.city"))}
            {text("region", t("branches.region"))}
            {text("postalCode", t("branches.postal_code"), { dir: "ltr" })}
          </div>
        </Card>
        <Card title={t("branches.contact")}>
          <div className="fields">
            {text("phone", t("branches.phone"), { dir: "ltr", type: "tel" })}
            {text("email", t("branches.email"), { dir: "ltr", type: "email" })}
            {text("website", t("branches.website"), { dir: "ltr", type: "url" })}
          </div>
        </Card>
        <div className="actions">
          <button type="button" onClick={onClose}>{t("branches.close_form")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("branches.save")}</button>}
        </div>
      </form>
    </Card>
  );
}
