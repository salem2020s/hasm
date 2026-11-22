import { ApiError, type CashAccount, type CashAccountGl, type CashAccountInput, type CashAccountKind, type CashAccountsResponse } from "@hasm/api-client";
import { SearchPicker, Tabs, useI18n, type PickerPage, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, fieldErrors, Loading, PageHeader } from "../components";
import { useReportUnsaved, useUnsaved } from "../unsaved";
import { branchName } from "./Branches";

/**
 * Cash and bank accounts (Phase 3.14 slice S1). ONE implementation for the Web app and the Windows Desktop app. Every rule (the binding to one active asset account of the chart, the base currency, the branch scope, the frozen binding,
 * optimistic concurrency, the encryption of the bank account number, auditing) is enforced by the server and PostgreSQL; this screen validates for convenience only and shows or hides controls from what the server reports the caller may do.
 * Nothing here posts to the ledger and no balance is stored or shown. The FULL bank account number is never part of a list or a record: the screen shows the masked form, and only on request (for a holder of the sensitive permission on the
 * branch) the full number, kept in memory for thirty seconds and never written to web storage. A masked value is never sent back as a new number.
 */
const CODE_SHAPE = /^[A-Z0-9][A-Z0-9._-]{0,29}$/;
const NUMBER_SHAPE = /^[A-Za-z0-9]{4,34}$/;
const REVEAL_MS = 30_000;
const isMaskedLooking = (s: string) => /[*•·]/.test(s);

type Editor = { mode: "new" } | { mode: "edit"; account: CashAccount };
type Confirm = { kind: "activate" | "deactivate"; account: CashAccount };

/** The account name for the current language, falling back to the other language and then the code. */
export function cashAccountName(a: Pick<CashAccount, "nameAr" | "nameEn" | "code">, locale: string): string {
  const preferred = locale === "en" ? a.nameEn : a.nameAr;
  const other = locale === "en" ? a.nameAr : a.nameEn;
  return preferred?.trim() || other?.trim() || a.code;
}

const glLabel = (g: CashAccountGl, locale: string) => `⁦${g.code}⁩ ${(locale === "en" ? g.nameEn || g.nameAr : g.nameAr || g.nameEn) ?? ""}`.trim();

export function CashAccountsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const { guard } = useUnsaved();
  const [data, setData] = useState<CashAccountsResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [kind, setKind] = useState<"all" | CashAccountKind>("all");
  const [branch, setBranch] = useState("all");
  const [status, setStatus] = useState<"all" | "active" | "inactive">("all");
  const [editor, setEditor] = useState<Editor | null>(null);
  const [confirm, setConfirm] = useState<Confirm | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      setData(await auth.api.listCashAccounts());
      setLoadError(null);
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "cash.error."));
    }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);

  const branchOf = useMemo(() => new Map((data?.branches ?? []).map((b) => [b.id, b])), [data]);
  const branchLabel = (id: string) => { const b = branchOf.get(id); return b ? `${b.code} — ${branchName(b, locale)}` : "—"; };

  const visible = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (data?.accounts ?? []).filter((a) =>
      (kind === "all" || a.kind === kind) && (branch === "all" || a.branchId === branch) && (status === "all" || (status === "active") === a.isActive)
      && (!q || a.code.toLowerCase().includes(q) || (a.nameAr ?? "").toLowerCase().includes(q) || (a.nameEn ?? "").toLowerCase().includes(q)));
  }, [data, search, kind, branch, status]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!data) return <Loading />;

  const can = data.can;
  const creatable = data.branches.filter((b) => b.isActive && can.create.includes(b.id));
  const anyAction = can.create.length > 0 || can.update.length > 0 || can.activate.length > 0;

  function openEditor(next: Editor) { guard(() => { setNotice(null); setError(null); setConflict(false); setEditor(next); }); }
  function closeEditor() { guard(() => setEditor(null)); }

  async function run(action: () => Promise<unknown>, message: string) {
    setBusy(true); setError(null); setConflict(false); setNotice(null);
    try {
      await action();
      await load();
      setNotice(message);
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else setError(errorText(t, err, "cash.error."));
    } finally { setBusy(false); }
  }

  const apply = (c: Confirm) => {
    setConfirm(null);
    if (c.kind === "activate") void run(() => auth.api.activateCashAccount(c.account.id, c.account.revision), t("cash.changed"));
    else void run(() => auth.api.deactivateCashAccount(c.account.id, c.account.revision), t("cash.changed"));
  };

  return (
    <section aria-labelledby="page-title">
      <PageHeader
        title={t("cash.title")}
        description={t("cash.subtitle")}
        actions={can.create.length > 0 && <button type="button" className="primary" disabled={busy || creatable.length === 0} onClick={() => openEditor({ mode: "new" })}>{t("cash.new")}</button>}
      />
      {!anyAction && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("cash.error.revision_conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}

      <Card title={t("cash.list")}>
        <div className="toolbar">
          <Field id="cash-search" label={t("cash.search")}>
            {(aria) => <input {...aria} type="search" value={search} onChange={(e) => setSearch(e.target.value)} autoComplete="off" />}
          </Field>
          <Field id="cash-kind" label={t("cash.filter_kind")}>
            {(aria) => (
              <select {...aria} value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
                <option value="all">{t("cash.filter_kind_all")}</option>
                <option value="cashbox">{t("cash.kind.cashbox")}</option>
                <option value="bank">{t("cash.kind.bank")}</option>
              </select>
            )}
          </Field>
          <Field id="cash-branch" label={t("cash.filter_branch")}>
            {(aria) => (
              <select {...aria} value={branch} onChange={(e) => setBranch(e.target.value)}>
                <option value="all">{t("cash.filter_branch_all")}</option>
                {data.branches.map((b) => <option key={b.id} value={b.id}>{b.code} — {branchName(b, locale)}</option>)}
              </select>
            )}
          </Field>
          <Field id="cash-status" label={t("cash.filter_status")}>
            {(aria) => (
              <select {...aria} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
                <option value="all">{t("cash.filter_all")}</option>
                <option value="active">{t("cash.filter_active")}</option>
                <option value="inactive">{t("cash.filter_inactive")}</option>
              </select>
            )}
          </Field>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <caption className="visually-hidden">{t("cash.list")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("cash.code")}</th><th scope="col">{t("cash.name")}</th><th scope="col">{t("cash.kind")}</th><th scope="col">{t("cash.branch")}</th>
                <th scope="col">{t("cash.gl")}</th><th scope="col">{t("cash.status")}</th><th scope="col">{t("cash.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {visible.length === 0 && <tr><td colSpan={7}>{data.accounts.length === 0 ? t("cash.none") : t("cash.none_filtered")}</td></tr>}
              {visible.map((a) => {
                const canUpdate = can.update.includes(a.branchId);
                const canActivate = can.activate.includes(a.branchId);
                return (
                  <tr key={a.id} data-testid={`cash-${a.code}`}>
                    <td><bdi dir="ltr">{a.code}</bdi></td>
                    <td><bdi>{cashAccountName(a, locale)}</bdi>{a.accountNumberMasked && <div className="hint"><bdi dir="ltr">{a.accountNumberMasked}</bdi></div>}</td>
                    <td>{t(`cash.kind.${a.kind}`)}</td>
                    <td><bdi>{branchLabel(a.branchId)}</bdi></td>
                    <td><bdi>{glLabel(a.account, locale)}</bdi></td>
                    <td><span className={`chip ${a.isActive ? "chip-ok" : "chip-off"}`}>{a.isActive ? t("cash.active") : t("cash.inactive")}</span></td>
                    <td>
                      <button type="button" disabled={busy} onClick={() => openEditor({ mode: "edit", account: a })}>{canUpdate ? t("cash.edit") : t("cash.view")}</button>{" "}
                      {canActivate && a.isActive && <button type="button" disabled={busy} onClick={() => setConfirm({ kind: "deactivate", account: a })}>{t("cash.deactivate")}</button>}
                      {canActivate && !a.isActive && <button type="button" disabled={busy} onClick={() => setConfirm({ kind: "activate", account: a })}>{t("cash.activate")}</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>

      {editor && (
        <CashAccountForm
          key={editor.mode === "edit" ? editor.account.id : "new"}
          editor={editor}
          branches={editor.mode === "new" ? creatable : data.branches.filter((b) => b.isActive && can.update.includes(b.id))}
          branchLabel={branchLabel}
          canEdit={editor.mode === "new" ? can.create.length > 0 : can.update.includes(editor.account.branchId)}
          canReveal={editor.mode === "edit" && can.revealNumber.includes(editor.account.branchId)}
          onClose={closeEditor}
          onSaved={(message) => { setEditor(null); setNotice(message); void load(); }}
        />
      )}

      {confirm && (
        <ConfirmDialog
          title={confirm.kind === "activate" ? t("cash.activate_title") : t("cash.deactivate_title")}
          message={confirm.kind === "activate" ? t("cash.activate_message", { code: confirm.account.code }) : t("cash.deactivate_message", { code: confirm.account.code })}
          confirmLabel={t("form.confirm")}
          onCancel={() => setConfirm(null)}
          onConfirm={() => apply(confirm)}
        />
      )}
    </section>
  );
}

interface Form { kind: CashAccountKind; code: string; nameAr: string; nameEn: string; branchId: string; accountId: string; bankName: string; bankBranchName: string }
const toForm = (a: CashAccount | null): Form => ({
  kind: a?.kind ?? "cashbox", code: a?.code ?? "", nameAr: a?.nameAr ?? "", nameEn: a?.nameEn ?? "", branchId: a?.branchId ?? "", accountId: a?.accountId ?? "", bankName: a?.bankName ?? "", bankBranchName: a?.bankBranchName ?? "",
});
const blankToNull = (s: string) => (s.trim() ? s.trim() : null);
const DETAIL_FIELDS = ["nameAr", "nameEn", "branchId", "accountId"];
const BANK_FIELDS = ["bankName", "bankBranchName", "accountNumber"];

function CashAccountForm({ editor, branches, branchLabel, canEdit, canReveal, onClose, onSaved }: {
  editor: Editor; branches: CashAccountsResponse["branches"]; branchLabel: (id: string) => string; canEdit: boolean; canReveal: boolean; onClose: () => void; onSaved: (message: string) => void;
}) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const isNew = editor.mode === "new";
  const [revision, setRevision] = useState(editor.mode === "edit" ? editor.account.revision : 0);
  const [masked, setMasked] = useState(editor.mode === "edit" ? editor.account.accountNumberMasked : null);
  const [baseline, setBaseline] = useState<Form>(() => toForm(editor.mode === "edit" ? editor.account : null));
  const [form, setForm] = useState<Form>(baseline);
  const [glName, setGlName] = useState(editor.mode === "edit" ? glLabel(editor.account.account, locale) : "");
  const [newNumber, setNewNumber] = useState("");
  const [replacing, setReplacing] = useState(false);
  const [revealed, setRevealed] = useState<string | null>(null);
  const [revealError, setRevealError] = useState<string | null>(null);
  const [tab, setTab] = useState("details");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const bank = form.kind === "bank";
  const dirty = (Object.keys(baseline) as (keyof Form)[]).some((k) => form[k] !== baseline[k]) || newNumber !== "";
  useReportUnsaved("cash-account-form", dirty, t("cash.form_section"));

  // the full number is memory only: hidden after a short time, when the form closes, and never part of the dirty state or the save
  const hide = useCallback(() => { if (timer.current) clearTimeout(timer.current); timer.current = null; setRevealed(null); }, []);
  useEffect(() => hide, [hide]);

  const set = (k: keyof Form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }));

  function validate(): Record<string, string> {
    const v: Record<string, string> = {};
    if (isNew) {
      const code = form.code.trim().toUpperCase();
      if (!code) v.code = t("field.required"); else if (!CODE_SHAPE.test(code)) v.code = t("field.invalid_format");
    }
    if (!form.nameAr.trim() && !form.nameEn.trim()) { v.nameAr = t("cash.name_required"); v.nameEn = t("cash.name_required"); }
    if (!form.branchId) v.branchId = t("cash.branch_required");
    if (!form.accountId) v.accountId = t("cash.gl_required");
    if (bank && newNumber.trim()) {
      const n = newNumber.trim();
      if (isMaskedLooking(n)) v.accountNumber = t("cash.number_masked_input");
      else if (!NUMBER_SHAPE.test(n.replace(/[\s-]/g, ""))) v.accountNumber = t("cash.number_invalid");
    }
    return v;
  }

  function serverErrors(err: unknown): Record<string, string> {
    const out = fieldErrors(t, err, "cash");
    return out;
  }

  const tabOf = (errs: Record<string, string>) => (Object.keys(errs).some((k) => BANK_FIELDS.includes(k)) && !Object.keys(errs).some((k) => DETAIL_FIELDS.includes(k) || k === "code") ? "bank" : "details");

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    const v = validate();
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); if (bank) setTab(tabOf(v)); return; }
    setBusy(true); setError(null); setConflict(false);
    try {
      const body: CashAccountInput = {
        nameAr: blankToNull(form.nameAr), nameEn: blankToNull(form.nameEn), branchId: form.branchId, accountId: form.accountId,
        bankName: bank ? blankToNull(form.bankName) : null, bankBranchName: bank ? blankToNull(form.bankBranchName) : null,
      };
      if (bank && newNumber.trim()) body.accountNumber = newNumber.trim();   // omitted otherwise: the stored number stays as it is
      if (editor.mode === "new") {
        await auth.api.createCashAccount({ ...body, kind: form.kind, code: form.code.trim().toUpperCase() });
        onSaved(t("cash.created"));
      } else {
        await auth.api.updateCashAccount(editor.account.id, body, revision);
        onSaved(t("cash.updated"));
      }
    } catch (err) {
      // A failed save changes nothing: the form keeps what the user typed (the number too).
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400 && err.details.fields?.length) {
        const fe = serverErrors(err);
        setErrors(fe); setError(t("form.fix_errors"));
        if (bank) setTab(tabOf(fe));
      } else setError(errorText(t, err, "cash.error."));
    } finally { setBusy(false); }
  }

  async function reloadLatest() {
    if (editor.mode !== "edit") return;
    try {
      const latest = await auth.api.getCashAccount(editor.account.id);
      const next = toForm(latest);
      setRevision(latest.revision); setMasked(latest.accountNumberMasked); setBaseline(next); setForm(next); setGlName(glLabel(latest.account, locale));
      setNewNumber(""); setReplacing(false); hide(); setErrors({}); setConflict(false);
    } catch (err) {
      if (!auth.handleError(err)) setError(errorText(t, err, "cash.error."));
    }
  }

  async function reveal() {
    if (editor.mode !== "edit") return;
    setRevealError(null);
    try {
      const r = await auth.api.revealCashAccountNumber(editor.account.id);
      setRevealed(r.accountNumber);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(hide, REVEAL_MS);
    } catch (err) {
      if (!auth.handleError(err)) setRevealError(errorText(t, err, "cash.error."));
    }
  }

  const loadCandidates = useCallback(async (q: string): Promise<PickerPage<CashAccountGl>> => {
    const r = await auth.api.listCashGlCandidates(q);
    return { items: r.accounts, total: r.total };
  }, [auth.api]);

  const text = (k: keyof Form, label: string, extra: { required?: boolean; dir?: "ltr" | "rtl"; hint?: string; readOnly?: boolean } = {}) => (
    <Field id={`cash-${k}`} label={label} error={errors[k]} required={extra.required} hint={extra.hint}>
      {(aria) => <input {...aria} name={k} value={form[k]} onChange={set(k)} readOnly={!canEdit || extra.readOnly} dir={extra.dir} type="text" autoComplete="off" />}
    </Field>
  );

  const detailsErr = DETAIL_FIELDS.some((k) => errors[k]);
  const bankErr = BANK_FIELDS.some((k) => errors[k]);
  const detailsPanel = (
    <div className="fields">
      {text("nameAr", t("cash.name_ar"), { dir: "rtl", hint: t("cash.names_hint") })}
      {text("nameEn", t("cash.name_en"), { dir: "ltr" })}
      <Field id="cash-branchId" label={t("cash.branch_field")} error={errors.branchId} required hint={branches.length === 0 && isNew ? t("cash.no_branch") : t("cash.branch_hint")}>
        {(aria) => (
          <select {...aria} name="branchId" value={form.branchId} onChange={set("branchId")} disabled={!canEdit}>
            <option value="">{t("form.select")}</option>
            {branches.map((b) => <option key={b.id} value={b.id}>{b.code} — {branchName(b, locale)}</option>)}
            {form.branchId && !branches.some((b) => b.id === form.branchId) && <option value={form.branchId}>{branchLabel(form.branchId)}</option>}
          </select>
        )}
      </Field>
      <Field id="cash-accountId" label={t("cash.gl_field")} error={errors.accountId} required hint={t("cash.gl_hint")}>
        {(aria) => (
          <SearchPicker<CashAccountGl>
            id="cash-gl-list" label={t("cash.gl_field")} value={form.accountId} load={loadCandidates} getId={(g) => g.id} getLabel={(g) => glLabel(g, locale)}
            selectedLabel={glName || undefined} noneLabel={t("cash.gl_none")} disabled={!canEdit} required invalid={!!errors.accountId} describedBy={aria["aria-describedby"]}
            onChange={(id, item) => { setForm((f) => ({ ...f, accountId: id })); if (item) setGlName(glLabel(item, locale)); }} testId="cash-gl-picker"
          />
        )}
      </Field>
      <Field id="cash-currency" label={t("cash.currency")} hint={t("cash.currency_hint")}>
        {(aria) => <input {...aria} name="currency" value={editor.mode === "edit" ? editor.account.currency : "—"} readOnly dir="ltr" />}
      </Field>
    </div>
  );

  const bankPanel = (
    <div className="fields">
      {text("bankName", t("cash.bank_name"))}
      {text("bankBranchName", t("cash.bank_branch"))}
      {!isNew && (
        <div className="field" data-testid="cash-number-current">
          <p>{masked ? t("cash.number_masked", { number: masked }) : t("cash.number_none")}</p>
          {canReveal && masked && (
            <>
              <button type="button" onClick={() => (revealed ? hide() : void reveal())} data-testid="cash-number-reveal">{revealed ? t("cash.number_hide") : t("cash.number_reveal")}</button>
              <p className="hint">{t("cash.number_reveal_note")}</p>
              {revealed && <p data-testid="cash-number-shown">{t("cash.number_shown")} <output dir="ltr"><bdi>{revealed}</bdi></output></p>}
              {revealError && <Banner kind="error">{revealError}</Banner>}
            </>
          )}
          {canEdit && !replacing && <button type="button" onClick={() => setReplacing(true)} data-testid="cash-number-replace">{t("cash.number_replace")}</button>}
          {canEdit && replacing && <button type="button" onClick={() => { setReplacing(false); setNewNumber(""); }}>{t("cash.number_keep")}</button>}
        </div>
      )}
      {(isNew || replacing) && (
        <Field id="cash-accountNumber" label={t("cash.number_new")} error={errors.accountNumber} hint={isNew ? t("cash.number_hint") : t("cash.number_new_hint")}>
          {(aria) => <input {...aria} name="accountNumber" value={newNumber} onChange={(e) => setNewNumber(e.target.value)} readOnly={!canEdit} dir="ltr" type="text" autoComplete="off" spellCheck={false} />}
        </Field>
      )}
    </div>
  );

  const tabs: TabItem[] = [
    { id: "details", label: t("cash.tab.details"), content: detailsPanel, invalid: detailsErr, dirty: (["nameAr", "nameEn", "branchId", "accountId"] as (keyof Form)[]).some((k) => form[k] !== baseline[k]) },
    { id: "bank", label: t("cash.tab.bank"), content: bankPanel, invalid: bankErr, dirty: form.bankName !== baseline.bankName || form.bankBranchName !== baseline.bankBranchName || newNumber !== "" },
  ];

  return (
    <Card title={isNew ? t("cash.form_new") : t("cash.form_edit")}>
      {!canEdit && <Banner kind="info" testId="form-read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="form-conflict">
          {t("cash.error.revision_conflict")} <button type="button" onClick={() => void reloadLatest()}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      <form onSubmit={submit} noValidate className="form-grid">
        {/* the identity of the record stays visible whichever tab is open */}
        <div className="fields">
          <Field id="cash-kind-field" label={t("cash.kind_field")} hint={t("cash.kind_hint")} error={errors.kind}>
            {(aria) => isNew ? (
              <select {...aria} name="kind" value={form.kind} onChange={(e) => { setForm((f) => ({ ...f, kind: e.target.value as CashAccountKind })); setTab("details"); }} disabled={!canEdit}>
                <option value="cashbox">{t("cash.kind.cashbox")}</option>
                <option value="bank">{t("cash.kind.bank")}</option>
              </select>
            ) : <input {...aria} name="kind" value={t(`cash.kind.${form.kind}`)} readOnly />}
          </Field>
          {text("code", t("cash.code_field"), { required: isNew, dir: "ltr", readOnly: !isNew, hint: isNew ? t("cash.code_hint") : undefined })}
        </div>
        {bank
          ? <Tabs label={t("cash.tabs_label")} tabs={tabs} value={tab} onChange={setTab} testId="cash-tabs" />
          : detailsPanel}
        <div className="actions">
          <button type="button" onClick={onClose}>{t("cash.close_form")}</button>
          {canEdit && <button type="submit" className="primary" disabled={busy}>{busy ? t("loading") : t("cash.save")}</button>}
        </div>
      </form>
    </Card>
  );
}
