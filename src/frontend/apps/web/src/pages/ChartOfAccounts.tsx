import { ApiError, type Account, type AccountCurrencyPolicy, type AccountNature, type AccountsResponse, type AccountType } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactElement } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, fieldErrors, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";

/**
 * Chart of Accounts (Phase 3.2A). ONE implementation for the Web app and the Windows Desktop app. The tree, the uniqueness of codes, the group/leaf
 * rules, cycle prevention, history protection, audit, concurrency and RBAC are enforced by the server and PostgreSQL; this screen validates for
 * convenience only and shows or hides controls from what the server reports the caller may do. The hierarchy is the real parent relationship,
 * never the code prefix. Business-type templates and Excel import are outstanding Release 1 requirements and are stated as such on the screen.
 */

const CODE_SHAPE = /^[A-Z0-9][A-Z0-9._-]{0,29}$/;
const TYPES: AccountType[] = ["asset", "liability", "equity", "revenue", "expense"];
const defaultNature = (t: AccountType): AccountNature => (t === "asset" || t === "expense" ? "debit" : "credit");
const toLatinDigits = (s: string) => s.replace(/[٠-٩]/g, (c) => String(c.charCodeAt(0) - 0x0660)).replace(/[۰-۹]/g, (c) => String(c.charCodeAt(0) - 0x06f0));
const normalizeCode = (s: string) => toLatinDigits(s.trim()).toUpperCase();
const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

/** The account name for the current language, falling back to the other language, then the code (same convention as branches and companies). */
export function accountName(a: Pick<Account, "nameAr" | "nameEn" | "code">, locale: string): string {
  const preferred = locale === "en" ? a.nameEn : a.nameAr;
  const other = locale === "en" ? a.nameAr : a.nameEn;
  return preferred?.trim() || other?.trim() || a.code;
}

type Editor = { mode: "new"; parentId: string | null } | { mode: "edit"; account: Account };
type Dialog =
  | { kind: "deactivate" | "activate"; account: Account }
  | { kind: "recode" | "move"; account: Account }
  | { kind: "template" };

export function ChartOfAccountsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [data, setData] = useState<AccountsResponse | null>(null);
  const [currencies, setCurrencies] = useState<string[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [templateKey, setTemplateKey] = useState("generic");
  const [type, setType] = useState<"all" | AccountType>("all");
  const [status, setStatus] = useState<"all" | "active" | "inactive">("all");
  const [expanded, setExpanded] = useState<Set<string> | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const response = await auth.api.listAccounts();
      setData(response);
      setLoadError(null);
      if (response.can.create || response.can.update) {
        try { setCurrencies((await auth.api.getCurrencies()).currencies.filter((c) => c.enabled).map((c) => c.code)); } catch { setCurrencies([]); }
      }
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "accounts.error."));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  const accounts = data?.accounts ?? [];
  const byId = useMemo(() => new Map(accounts.map((a) => [a.id, a])), [accounts]);
  const children = useMemo(() => {
    const m = new Map<string | null, Account[]>();
    for (const a of accounts) { const k = a.parentId; if (!m.has(k)) m.set(k, []); m.get(k)!.push(a); }
    for (const list of m.values()) list.sort((x, y) => x.code.localeCompare(y.code, "en", { numeric: true }));
    return m;
  }, [accounts]);

  // First view: everything expanded; afterwards the user.s choice stays.
  const allGroups = useMemo(() => new Set(accounts.filter((a) => a.hasChildren).map((a) => a.id)), [accounts]);
  const filtering = search.trim() !== "" || type !== "all" || status !== "all";
  const visibleIds = useMemo(() => {
    const q = search.trim().toLowerCase();
    const matches = accounts.filter((a) =>
      (type === "all" || a.type === type) && (status === "all" || (status === "active") === a.isActive)
      && (!q || a.code.toLowerCase().includes(q) || (a.nameAr ?? "").toLowerCase().includes(q) || (a.nameEn ?? "").toLowerCase().includes(q)));
    const keep = new Set<string>();
    for (const m of matches) for (let cur: Account | undefined = m; cur && !keep.has(cur.id); cur = cur.parentId ? byId.get(cur.parentId) : undefined) keep.add(cur.id);
    return keep;
  }, [accounts, byId, search, type, status]);


  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!data) return <Loading />;

  const can = data.can;
  const anyAction = can.create || can.update || can.activate || can.recode || can.restructure || can.configure;
  const open = expanded ?? allGroups;

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
      return true;
    } catch (err) {
      if (auth.handleError(err)) return false;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else setError(errorText(t, err, "accounts.error."));
      return false;
    } finally {
      setBusy(false);
    }
  }

  const toggle = (id: string) => setExpanded((prev) => { const n = new Set(prev ?? allGroups); if (n.has(id)) n.delete(id); else n.add(id); return n; });

  function rows(parentId: string | null, depth: number): ReactElement[] {
    return (children.get(parentId) ?? []).filter((a) => visibleIds.has(a.id) || !filtering).flatMap((a) => {
      const isOpen = filtering || open.has(a.id);
      const canUpdate = can.update;
      const kind = a.isGroup ? t("accounts.kind_group") : a.isActive ? t("accounts.kind_postable") : t("accounts.kind_inactive");
      const row = (
        <tr key={a.id} data-testid={`account-${a.code}`}>
          <td>
            <span style={{ paddingInlineStart: `${depth * 1.25}rem`, display: "inline-flex", alignItems: "center", gap: "0.25rem" }}>
              {a.hasChildren
                ? <button type="button" className="icon-button" aria-expanded={isOpen} aria-label={`${isOpen ? t("accounts.collapse") : t("accounts.expand")} ${a.code}`} onClick={() => toggle(a.id)}>{isOpen ? "▾" : "▸"}</button>
                : <span aria-hidden="true" style={{ display: "inline-block", width: "1.5rem" }} />}
              <bdi dir="ltr">{a.code}</bdi>
            </span>
          </td>
          <td><bdi>{accountName(a, locale)}</bdi></td>
          <td>{t(`accounts.type.${a.type}`)} · {t(`accounts.nature.${a.nature}`)}</td>
          <td><span className={`chip ${a.isGroup ? "chip-base" : a.isActive ? "chip-ok" : "chip-off"}`}>{kind}</span></td>
          <td><span className={`chip ${a.isActive ? "chip-ok" : "chip-off"}`}>{a.isActive ? t("accounts.active") : t("accounts.inactive")}</span></td>
          <td>
            <button type="button" disabled={busy} onClick={() => openEditor({ mode: "edit", account: a })}>{canUpdate ? t("accounts.edit") : t("accounts.view")}</button>{" "}
            {can.create && a.isGroup && a.isActive && <button type="button" disabled={busy} onClick={() => openEditor({ mode: "new", parentId: a.id })}>{t("accounts.add_child")}</button>}{" "}
            {can.activate && a.isActive && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "deactivate", account: a })}>{t("accounts.deactivate")}</button>}
            {can.activate && !a.isActive && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "activate", account: a })}>{t("accounts.activate")}</button>}
          </td>
        </tr>
      );
      return [row, ...(a.hasChildren && isOpen ? rows(a.id, depth + 1) : [])];
    });
  }

  const tableRows = rows(null, 0);
  const template = data.templates.find((x) => x.key === templateKey) ?? data.templates[0];

  return (
    <section aria-labelledby="page-title">
      <PageHeader
        title={t("accounts.title")}
        description={t("accounts.subtitle")}
        actions={can.create && <button type="button" className="primary" disabled={busy} onClick={() => openEditor({ mode: "new", parentId: null })}>{t("accounts.new")}</button>}
      />
      {!anyAction && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      <div className="notices">
        <Banner kind="info" testId="outstanding">{t("accounts.outstanding")}</Banner>
        {!data.usageChecksImplemented && <Banner kind="info" testId="usage-unverified">{t("accounts.usage_unverified")}</Banner>}
      </div>
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("accounts.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}

      <Card title={t("accounts.tree")}>
        <div className="toolbar">
          <Field id="a-search" label={t("accounts.search")}>
            {(aria) => <input {...aria} type="search" value={search} onChange={(e) => setSearch(e.target.value)} autoComplete="off" />}
          </Field>
          <Field id="a-type" label={t("accounts.filter_type")}>
            {(aria) => (
              <select {...aria} value={type} onChange={(e) => setType(e.target.value as typeof type)}>
                <option value="all">{t("accounts.filter_all")}</option>
                {TYPES.map((x) => <option key={x} value={x}>{t(`accounts.type.${x}`)}</option>)}
              </select>
            )}
          </Field>
          <Field id="a-status" label={t("accounts.filter_status")}>
            {(aria) => (
              <select {...aria} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
                <option value="all">{t("accounts.filter_all")}</option>
                <option value="active">{t("accounts.filter_active")}</option>
                <option value="inactive">{t("accounts.filter_inactive")}</option>
              </select>
            )}
          </Field>
          <div className="actions">
            <button type="button" onClick={() => setExpanded(new Set(accounts.filter((a) => a.hasChildren).map((a) => a.id)))}>{t("accounts.expand_all")}</button>
            <button type="button" onClick={() => setExpanded(new Set())}>{t("accounts.collapse_all")}</button>
          </div>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("accounts.tree")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("accounts.col_code")}</th><th scope="col">{t("accounts.col_name")}</th><th scope="col">{t("accounts.col_type")}</th>
                <th scope="col">{t("accounts.col_kind")}</th><th scope="col">{t("accounts.col_status")}</th><th scope="col">{t("accounts.col_actions")}</th>
              </tr>
            </thead>
            <tbody>
              {tableRows.length === 0 && <tr><td colSpan={6}>{accounts.length === 0 ? t("accounts.none") : t("accounts.none_filtered")}</td></tr>}
              {tableRows}
            </tbody>
          </table>
        </div>
        {accounts.length === 0 && (
          <p className="hint" data-testid="empty-hint">
            {t("accounts.empty_hint")}{" "}
            {can.create && template && <button type="button" disabled={busy} onClick={() => setDialog({ kind: "template" })}>{t("accounts.template_apply")}</button>}
          </p>
        )}
      </Card>

      {data.settings && <SettingsCard settings={data.settings} canConfigure={can.configure} onSaved={(m) => { setNotice(m); void load(); }} />}

      {editor && (
        <AccountForm
          key={editor.mode === "edit" ? editor.account.id : `new-${editor.parentId ?? "root"}`}
          editor={editor}
          accounts={accounts}
          currencies={currencies}
          autoCodes={data.settings?.autoCodes ?? true}
          can={can}
          onClose={closeEditor}
          onSaved={(message) => { setEditor(null); setNotice(message); void load(); }}
          onDialog={setDialog}
        />
      )}

      {dialog?.kind === "recode" && (
        <CodeDialog
          account={dialog.account}
          onCancel={() => setDialog(null)}
          onConfirm={async (code) => { const a = dialog.account; setDialog(null); if (await run(() => auth.api.recodeAccount(a.id, code, a.revision), t("accounts.changed"))) setEditor(null); }}
        />
      )}
      {dialog?.kind === "move" && (
        <MoveDialog
          account={dialog.account}
          accounts={accounts}
          onCancel={() => setDialog(null)}
          onConfirm={async (parentId) => { const a = dialog.account; setDialog(null); if (await run(() => auth.api.moveAccount(a.id, parentId, a.revision), t("accounts.changed"))) setEditor(null); }}
        />
      )}
      {dialog && (dialog.kind === "deactivate" || dialog.kind === "activate") && (
        <ConfirmDialog
          title={dialog.kind === "deactivate" ? t("accounts.deactivate_title") : t("accounts.activate_title")}
          message={dialog.kind === "deactivate" ? t("accounts.deactivate_message", { code: dialog.account.code }) : t("accounts.activate_message", { code: dialog.account.code })}
          confirmLabel={t("form.confirm")}
          onCancel={() => setDialog(null)}
          onConfirm={() => {
            const d = dialog;
            setDialog(null);
            void run(() => (d.kind === "deactivate" ? auth.api.deactivateAccount(d.account.id, d.account.revision) : auth.api.activateAccount(d.account.id, d.account.revision)), t("accounts.changed"));
          }}
        />
      )}
      {dialog?.kind === "template" && template && (
        <ConfirmDialog
          title={t("accounts.template_title")}
          message={t("accounts.template_message", { name: locale === "en" ? template.nameEn : template.nameAr, count: String(template.accounts) })}
          confirmLabel={t("form.confirm")}
          onCancel={() => setDialog(null)}
          onConfirm={() => { setDialog(null); void run(() => auth.api.applyChartTemplate(template.key), t("accounts.template_applied")); }}
        >
          {data.templates.length > 1 && (
            <Field id="template-choice" label={t("accounts.template_choose")}>
              {(a) => (
                <select {...a} value={template.key} onChange={(e) => setTemplateKey(e.target.value)}>
                  {data.templates.map((x) => <option key={x.key} value={x.key}>{locale === "en" ? x.nameEn : x.nameAr} ({x.accounts})</option>)}
                </select>
              )}
            </Field>
          )}
          <p className="hint" data-testid="template-description">{t(`accounts.template_desc.${template.key}`) === `accounts.template_desc.${template.key}` ? "" : t(`accounts.template_desc.${template.key}`)}</p>
        </ConfirmDialog>
      )}
    </section>
  );
}

function SettingsCard({ settings, canConfigure, onSaved }: { settings: NonNullable<AccountsResponse["settings"]>; canConfigure: boolean; onSaved: (message: string) => void }) {
  const { t } = useI18n();
  const auth = useAuth();
  const [auto, setAuto] = useState(settings.autoCodes);
  const [width, setWidth] = useState(String(settings.segmentWidth));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const dirty = auto !== settings.autoCodes || width !== String(settings.segmentWidth);
  useReportUnsaved("chart-settings", dirty, t("accounts.settings"));
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await auth.api.putChartSettings({ segmentWidth: Number(width), autoCodes: auto }, settings.revision);
      onSaved(t("accounts.settings_saved"));
    } catch (err) {
      if (!auth.handleError(err)) setError(errorText(t, err, "accounts.error."));
    } finally {
      setBusy(false);
    }
  }
  return (
    <Card title={t("accounts.settings")} hint={t("accounts.settings_hint")}>
      {error && <Banner kind="error">{error}</Banner>}
      <form onSubmit={save} noValidate className="fields">
        <label className="check"><input type="checkbox" checked={auto} disabled={!canConfigure} onChange={(e) => setAuto(e.target.checked)} /> {t("accounts.settings_auto")}</label>
        <Field id="cs-width" label={t("accounts.settings_width")}>
          {(aria) => (
            <select {...aria} value={width} disabled={!canConfigure} onChange={(e) => setWidth(e.target.value)}>
              {[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          )}
        </Field>
        {canConfigure && <div className="actions"><button type="submit" className="primary" disabled={busy || !dirty}>{t("accounts.settings_save")}</button></div>}
      </form>
    </Card>
  );
}

function AccountForm({ editor, accounts, currencies, autoCodes, can, onClose, onSaved, onDialog }: {
  editor: Editor; accounts: Account[]; currencies: string[]; autoCodes: boolean; can: AccountsResponse["can"];
  onClose: () => void; onSaved: (message: string) => void; onDialog: (d: Dialog) => void;
}) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const existing = editor.mode === "edit" ? editor.account : null;
  const initial = {
    auto: isNew && autoCodes, code: "", nameAr: existing?.nameAr ?? "", nameEn: existing?.nameEn ?? "",
    parentId: editor.mode === "new" ? editor.parentId ?? "" : existing?.parentId ?? "",
    type: (existing?.type ?? "") as AccountType | "", nature: (existing?.nature ?? "") as AccountNature | "", isGroup: existing?.isGroup ?? false,
    policy: (existing?.currencyPolicy ?? "base") as AccountCurrencyPolicy, currency: existing?.currencyCode ?? "",
  };
  const [f, setF] = useState(initial);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [revision, setRevision] = useState(existing?.revision ?? 0);
  const canEdit = isNew ? can.create : can.update;
  const dirty = JSON.stringify(f) !== JSON.stringify(initial);
  useReportUnsaved("account-form", dirty, t("accounts.form_section"));

  const parent = f.parentId ? accounts.find((a) => a.id === f.parentId) : undefined;
  const effectiveType: AccountType | "" = parent ? parent.type : f.type;
  const nature: AccountNature | "" = f.nature || (effectiveType ? defaultNature(effectiveType) : "");
  const statement = effectiveType ? (effectiveType === "asset" || effectiveType === "liability" || effectiveType === "equity" ? "balance_sheet" : "profit_and_loss") : null;
  const parentOptions = accounts.filter((a) => a.isGroup && a.isActive);
  const set = <K extends keyof typeof f>(k: K, v: (typeof f)[K]) => setF((x) => ({ ...x, [k]: v }));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (isNew && !f.auto) {
      const code = normalizeCode(f.code);
      if (!code) v.code = t("field.required"); else if (!CODE_SHAPE.test(code)) v.code = t("field.invalid_format");
    }
    if (!f.nameAr.trim() && !f.nameEn.trim()) { v.nameAr = t("accounts.name_required"); v.nameEn = t("accounts.name_required"); }
    if (isNew && !parent && !f.type) v.type = t("accounts.type_required");
    if (f.policy === "specific" && !f.currency) v.currencyCode = t("field.required");
    return v;
  }

  function serverErrors(err: unknown): Record<string, string> {
    const out = fieldErrors(t, err);
    if (err instanceof ApiError) for (const e of err.details.fields ?? []) {
      if (e.code === "name_required") out[e.field] = t("accounts.name_required");
      if (e.field === "parentId") out.parentId = t(`accounts.error.${e.code === "parent_not_found" ? "account_not_found" : e.code}`);
      if (e.field === "type" && e.code === "parent_type_mismatch") out.type = t("accounts.error.parent_type_mismatch");
      if (e.field === "type" && e.code === "required") out.type = t("accounts.type_required");
    }
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
      const shared = {
        nameAr: blankToNull(f.nameAr), nameEn: blankToNull(f.nameEn), nature: nature || null, isGroup: f.isGroup,
        currencyPolicy: f.policy, currencyCode: f.policy === "specific" ? f.currency : null,
      };
      if (isNew) {
        await auth.api.createAccount({ ...shared, code: f.auto ? null : normalizeCode(f.code), parentId: f.parentId || null, type: parent ? null : (f.type as AccountType) });
        onSaved(t("accounts.created"));
      } else {
        await auth.api.updateAccount(existing!.id, shared, revision);
        onSaved(t("accounts.updated"));
      }
    } catch (err) {
      // A failed save changes nothing: the form keeps what the user typed.
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) { setErrors(serverErrors(err)); setError(t("form.fix_errors")); }
      else if (err instanceof ApiError && err.status === 409 && err.code === "account_code_taken") { setErrors({ code: t("accounts.error.account_code_taken") }); setError(t("form.fix_errors")); }
      else setError(errorText(t, err, "accounts.error."));
    } finally {
      setBusy(false);
    }
  }

  async function reloadLatest() {
    if (!existing) return;
    try {
      const latest = await auth.api.getAccount(existing.id);
      setRevision(latest.revision);
      setF((x) => ({ ...x, nameAr: latest.nameAr ?? "", nameEn: latest.nameEn ?? "", nature: latest.nature, isGroup: latest.isGroup, policy: latest.currencyPolicy, currency: latest.currencyCode ?? "" }));
      setErrors({});
      setConflict(false);
    } catch (err) {
      if (!auth.handleError(err)) setError(errorText(t, err, "accounts.error."));
    }
  }

  const parentLabel = (a: Account) => `${a.code} — ${accountName(a, locale)}`;

  return (
    <Card title={isNew ? t("accounts.form_new") : t("accounts.form_edit")}>
      {!canEdit && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="form-conflict">
          {t("accounts.error.revision_conflict")} <button type="button" onClick={() => void reloadLatest()}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
        <Card title={t("accounts.identity")} hint={t("accounts.names_hint")}>
          <div className="fields">
            {isNew ? (
              <>
                <label className="check"><input type="checkbox" checked={f.auto} disabled={!canEdit || !autoCodes} onChange={(e) => set("auto", e.target.checked)} /> {t("accounts.code_auto")}</label>
                <Field id="ac-code" label={t("accounts.code")} error={errors.code} required={!f.auto} hint={f.auto ? t("accounts.code_auto_hint") : t("accounts.code_hint")}>
                  {(aria) => <input {...aria} name="code" value={f.code} onChange={(e) => set("code", e.target.value)} readOnly={!canEdit} disabled={f.auto} dir="ltr" autoComplete="off" />}
                </Field>
              </>
            ) : (
              <Field id="ac-code" label={t("accounts.code")}>
                {(aria) => (
                  <span className="inline">
                    <input {...aria} name="code" value={existing!.code} readOnly dir="ltr" />
                    {can.recode && <button type="button" onClick={() => onDialog({ kind: "recode", account: existing! })}>{t("accounts.recode")}</button>}
                  </span>
                )}
              </Field>
            )}
            <Field id="ac-nameAr" label={t("accounts.name_ar")} error={errors.nameAr}>
              {(aria) => <input {...aria} name="nameAr" value={f.nameAr} onChange={(e) => set("nameAr", e.target.value)} readOnly={!canEdit} dir="rtl" autoComplete="off" />}
            </Field>
            <Field id="ac-nameEn" label={t("accounts.name_en")} error={errors.nameEn}>
              {(aria) => <input {...aria} name="nameEn" value={f.nameEn} onChange={(e) => set("nameEn", e.target.value)} readOnly={!canEdit} dir="ltr" autoComplete="off" />}
            </Field>
            {isNew ? (
              <Field id="ac-parent" label={t("accounts.parent")} error={errors.parentId} hint={t("accounts.parent_hint")}>
                {(aria) => (
                  <select {...aria} name="parentId" value={f.parentId} onChange={(e) => set("parentId", e.target.value)} disabled={!canEdit}>
                    <option value="">{t("accounts.parent_none")}</option>
                    {parentOptions.map((a) => <option key={a.id} value={a.id}>{parentLabel(a)}</option>)}
                  </select>
                )}
              </Field>
            ) : (
              <Field id="ac-parent" label={t("accounts.parent")}>
                {(aria) => (
                  <span className="inline">
                    <input {...aria} name="parentId" value={parent ? parentLabel(parent) : t("accounts.parent_none")} readOnly />
                    {can.restructure && <button type="button" onClick={() => onDialog({ kind: "move", account: existing! })}>{t("accounts.move")}</button>}
                  </span>
                )}
              </Field>
            )}
          </div>
        </Card>
        <Card title={t("accounts.classification")}>
          <div className="fields">
            <Field id="ac-type" label={t("accounts.type")} error={errors.type} required={isNew && !parent} hint={parent || !isNew ? t("accounts.type_inherited") : undefined}>
              {(aria) => (
                <select {...aria} name="type" value={effectiveType} onChange={(e) => set("type", e.target.value as AccountType)} disabled={!canEdit || !isNew || !!parent}>
                  <option value="">{t("form.select")}</option>
                  {TYPES.map((x) => <option key={x} value={x}>{t(`accounts.type.${x}`)}</option>)}
                </select>
              )}
            </Field>
            <Field id="ac-nature" label={t("accounts.nature")} hint={t("accounts.nature_hint")}>
              {(aria) => (
                <select {...aria} name="nature" value={nature} onChange={(e) => set("nature", e.target.value as AccountNature)} disabled={!canEdit}>
                  <option value="debit">{t("accounts.nature.debit")}</option>
                  <option value="credit">{t("accounts.nature.credit")}</option>
                </select>
              )}
            </Field>
            <Field id="ac-statement" label={t("accounts.statement")}>
              {(aria) => <input {...aria} value={statement ? t(`accounts.statement.${statement}`) : "—"} readOnly />}
            </Field>
            <label className="check"><input type="checkbox" name="isGroup" checked={f.isGroup} disabled={!canEdit} onChange={(e) => set("isGroup", e.target.checked)} /> {t("accounts.is_group")}</label>
          </div>
        </Card>
        <Card title={t("accounts.currency")}>
          <div className="fields">
            <Field id="ac-policy" label={t("accounts.currency_policy")}>
              {(aria) => (
                <select {...aria} name="currencyPolicy" value={f.policy} onChange={(e) => set("policy", e.target.value as AccountCurrencyPolicy)} disabled={!canEdit}>
                  <option value="base">{t("accounts.currency_base")}</option>
                  <option value="any">{t("accounts.currency_any")}</option>
                  <option value="specific">{t("accounts.currency_specific")}</option>
                </select>
              )}
            </Field>
            {f.policy === "specific" && (
              <Field id="ac-currency" label={t("accounts.currency_code")} error={errors.currencyCode} required>
                {(aria) => (
                  <select {...aria} name="currencyCode" value={f.currency} onChange={(e) => set("currency", e.target.value)} disabled={!canEdit}>
                    <option value="">{currencies.length === 0 ? t("accounts.currency_none") : t("form.select")}</option>
                    {[...new Set([...(f.currency ? [f.currency] : []), ...currencies])].map((c) => <option key={c} value={c}>{c}</option>)}
                  </select>
                )}
              </Field>
            )}
          </div>
        </Card>
        <div className="actions">
          <button type="button" onClick={onClose}>{t("accounts.close_form")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("accounts.save")}</button>}
        </div>
      </form>
    </Card>
  );
}

function CodeDialog({ account, onCancel, onConfirm }: { account: Account; onCancel: () => void; onConfirm: (code: string) => void }) {
  const { t } = useI18n();
  const cancel = useRef<HTMLButtonElement>(null);
  const [code, setCode] = useState(account.code);
  const [error, setError] = useState<string | undefined>();
  useEffect(() => cancel.current?.focus(), []);
  return (
    <div className="dialog-backdrop">
      <div role="dialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-message" className="dialog" onKeyDown={(e) => { if (e.key === "Escape") onCancel(); }}>
        <h2 id="confirm-title">{t("accounts.recode_title")}</h2>
        <p id="confirm-message">{t("accounts.recode_message")}</p>
        <Field id="rc-code" label={t("accounts.code")} error={error} required hint={t("accounts.code_hint")}>
          {(aria) => <input {...aria} value={code} onChange={(e) => setCode(e.target.value)} dir="ltr" autoComplete="off" />}
        </Field>
        <div className="actions">
          <button type="button" ref={cancel} onClick={onCancel}>{t("cancel")}</button>
          <button type="button" className="danger" onClick={() => { const n = normalizeCode(code); if (!CODE_SHAPE.test(n)) setError(t("field.invalid_format")); else onConfirm(n); }}>{t("accounts.recode_confirm")}</button>
        </div>
      </div>
    </div>
  );
}

function MoveDialog({ account, accounts, onCancel, onConfirm }: { account: Account; accounts: Account[]; onCancel: () => void; onConfirm: (parentId: string | null) => void }) {
  const { t, locale } = useI18n();
  const cancel = useRef<HTMLButtonElement>(null);
  const [parentId, setParentId] = useState(account.parentId ?? "");
  useEffect(() => cancel.current?.focus(), []);
  // Candidates: active groups of the same type that are not the account itself or one of its descendants (the server enforces this again).
  const descendants = useMemo(() => {
    const out = new Set<string>([account.id]);
    for (let grew = true; grew;) { grew = false; for (const a of accounts) if (a.parentId && out.has(a.parentId) && !out.has(a.id)) { out.add(a.id); grew = true; } }
    return out;
  }, [account.id, accounts]);
  const options = accounts.filter((a) => a.isGroup && a.isActive && a.type === account.type && !descendants.has(a.id));
  return (
    <div className="dialog-backdrop">
      <div role="dialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-message" className="dialog" onKeyDown={(e) => { if (e.key === "Escape") onCancel(); }}>
        <h2 id="confirm-title">{t("accounts.move_title")}</h2>
        <p id="confirm-message">{t("accounts.move_message")}</p>
        <Field id="mv-parent" label={t("accounts.parent")}>
          {(aria) => (
            <select {...aria} value={parentId} onChange={(e) => setParentId(e.target.value)}>
              <option value="">{t("accounts.parent_none")}</option>
              {options.map((a) => <option key={a.id} value={a.id}>{a.code} — {accountName(a, locale)}</option>)}
            </select>
          )}
        </Field>
        <div className="actions">
          <button type="button" ref={cancel} onClick={onCancel}>{t("cancel")}</button>
          <button type="button" className="danger" onClick={() => onConfirm(parentId || null)}>{t("accounts.move_confirm")}</button>
        </div>
      </div>
    </div>
  );
}
