import type { Account, LocalPartySummary, PartyRole, PartyRolesResponse, SalesRepresentative, SalesRepresentativesResponse } from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName, useRunner } from "../ledgerCommon";
import { parseDecimalInput } from "../numberInput";
import { useReportUnsaved } from "../unsaved";

/**
 * Customers, suppliers and sales representatives (Phase 3.9B): ONE shared screen for Web and Desktop. Tabs by business meaning: the CUSTOMER roles, the SUPPLIER roles and the REPRESENTATIVES.
 * A role belongs to a local party (the shared identity is untouched) and holds its payment terms, credit-limit data, control account and (customers) representative. Everything is enforced by
 * the server and PostgreSQL (control account kind and postability, revision fences, no deletion); this screen only shows and submits. Roles and representatives are deactivated, never deleted.
 */
interface Data { customers: PartyRolesResponse | null; suppliers: PartyRolesResponse | null; reps: SalesRepresentativesResponse | null }
const blank = (v: string) => (v.trim() === "" ? null : v.trim());
const num = parseDecimalInput;
const bad = (s: string) => s.trim() !== "" && Number.isNaN(num(s));

export function PartyRolesPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const canRoles = auth.can("company.partyroles.view"), canReps = auth.can("company.salesreps.view");
  const [data, setData] = useState<Data | null>(null);
  const [parties, setParties] = useState<LocalPartySummary[] | null>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState(canRoles ? "customers" : "reps");
  const [dirty, setDirty] = useState<Record<string, boolean>>({});
  const setTabDirty = useCallback((id: string, v: boolean) => setDirty((d) => (d[id] === v ? d : { ...d, [id]: v })), []);
  const load = useCallback(async () => {
    try {
      const [customers, suppliers, reps] = await Promise.all([
        canRoles ? auth.api.listPartyRoles(1) : Promise.resolve(null), canRoles ? auth.api.listPartyRoles(2) : Promise.resolve(null), canReps ? auth.api.listSalesRepresentatives() : Promise.resolve(null)]);
      setData({ customers, suppliers, reps }); setLoadError(null);
      if (canRoles && auth.canAny("company.parties.view")) { try { setParties((await auth.api.listParties({ limit: 500 })).parties); } catch { setParties(null); } }
      if (canRoles && auth.can("company.accounts.view")) { try { setAccounts((await auth.api.listAccounts()).accounts); } catch { setAccounts([]); } }
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "pr.error.")); }
  }, [auth.api, auth.handleError, canRoles, canReps]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);

  if (!data) return loadError ? <Banner kind="error" testId="pr-load-error">{loadError}</Banner> : <Loading />;
  const reps = data.reps?.items ?? [];
  const tabs: TabItem[] = [
    ...(data.customers ? [{ id: "customers", label: t("pr.tab.customers"), dirty: dirty.customers, content: <RoleTab kind={1} resp={data.customers} parties={parties} accounts={accounts} reps={reps} reload={load} onDirty={(v) => setTabDirty("customers", v)} /> } as TabItem] : []),
    ...(data.suppliers ? [{ id: "suppliers", label: t("pr.tab.suppliers"), dirty: dirty.suppliers, content: <RoleTab kind={2} resp={data.suppliers} parties={parties} accounts={accounts} reps={[]} reload={load} onDirty={(v) => setTabDirty("suppliers", v)} /> } as TabItem] : []),
    ...(data.reps ? [{ id: "reps", label: t("pr.tab.reps"), dirty: dirty.reps, content: <RepsTab resp={data.reps} reload={load} onDirty={(v) => setTabDirty("reps", v)} /> } as TabItem] : []),
  ];
  return (
    <section aria-labelledby="page-title" data-testid="party-roles">
      <PageHeader title={t("pr.title")} description={t("pr.subtitle")} />
      {loadError && <Banner kind="error" testId="pr-load-error">{loadError}</Banner>}
      <Banner kind="info" testId="pr-note">{t("pr.note")}</Banner>
      <Card><Tabs label={t("pr.tabs_label")} tabs={tabs} value={tabs.some((x) => x.id === tab) ? tab : tabs[0].id} onChange={setTab} testId="pr-tabs" /></Card>
    </section>
  );
}

function Notices({ runner, id }: { runner: ReturnType<typeof useRunner>; id: string }) {
  const { t } = useI18n();
  return (
    <>
      {runner.error && <Banner kind="error" testId={`${id}-error`}>{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning" testId={`${id}-conflict`}>{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
    </>
  );
}

interface RoleTabProps { kind: 1 | 2; resp: PartyRolesResponse; parties: LocalPartySummary[] | null; accounts: Account[]; reps: SalesRepresentative[]; reload: () => Promise<void>; onDirty: (v: boolean) => void }
const emptyForm = { partyId: "", mode: "2", days: "30", limit: "", account: "", rep: "" };

function RoleTab({ kind, resp, parties, accounts, reps, reload, onDirty }: RoleTabProps) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const id = kind === 1 ? "customer" : "supplier";
  const runner = useRunner("pr.error.", reload);
  const [editing, setEditing] = useState<PartyRole | null>(null);
  const [form, setForm] = useState(emptyForm);
  const configure = resp.can.configure;
  const wantedType = kind === 1 ? "asset" : "liability";
  const candidates = accounts.filter((a) => a.type === wantedType && a.isActive && !a.isGroup && a.postable);
  const used = new Set(resp.items.map((r) => r.partyId));
  const choices = (parties ?? []).filter((p) => p.isActive && !used.has(p.id));
  const isDirty = editing ? true : form.partyId !== "" || form.limit !== "" || form.account !== "" || form.rep !== "" || form.mode !== "2" || form.days !== "30";
  useReportUnsaved(`pr-${id}`, isDirty && (editing !== null || form.partyId !== ""), t(kind === 1 ? "pr.tab.customers" : "pr.tab.suppliers"));
  useEffect(() => { onDirty(isDirty && (editing !== null || form.partyId !== "")); }, [isDirty, editing, form.partyId]); // eslint-disable-line react-hooks/exhaustive-deps

  // after a reload (for example following a 412) the edited role takes the stored revision, so a retry is not doomed; the typed values stay
  useEffect(() => { if (editing) { const fresh = resp.items.find((x) => x.id === editing.id); if (fresh && fresh.revision !== editing.revision) setEditing(fresh); } }, [resp]); // eslint-disable-line react-hooks/exhaustive-deps

  const cash = form.mode === "1";
  const invalidNumbers = (!cash && !Number.isInteger(num(form.days))) || bad(form.limit);
  const payload = () => ({
    paymentMode: Number(form.mode), termDays: cash ? null : num(form.days), creditLimit: blank(form.limit) === null ? null : num(form.limit),
    controlAccountId: blank(form.account), representativeId: kind === 1 ? blank(form.rep) : null,
  });
  const edit = (r: PartyRole) => { setEditing(r); setForm({ partyId: r.partyId, mode: String(r.paymentMode), days: String(r.termDays), limit: r.creditLimit === null ? "" : String(r.creditLimit), account: r.controlAccountId ?? "", rep: r.representativeId ?? "" }); };
  const reset = () => { setEditing(null); setForm(emptyForm); };
  async function save() {
    const done = await runner.run(() => (editing ? auth.api.updatePartyRole(editing.id, payload(), editing.revision) : auth.api.createPartyRole({ partyId: form.partyId, role: kind, ...payload() })), t("pr.saved"));
    if (done) reset();
  }
  const toggle = (r: PartyRole) => void runner.run(() => auth.api.updatePartyRole(r.id, {
    paymentMode: r.paymentMode, termDays: r.paymentMode === 1 ? null : r.termDays, creditLimit: r.creditLimit, controlAccountId: r.controlAccountId, representativeId: r.representativeId, isActive: !r.isActive }, r.revision), t("pr.saved"));
  const repName = (rid: string | null) => { if (!rid) return t("pr.none"); const x = reps.find((y) => y.id === rid); return x ? localName(x, locale) : rid; };

  return (
    <div data-testid={`pr-tab-${id}`}>
      <Notices runner={runner} id={`pr-${id}`} />
      {resp.items.length === 0 ? <p data-testid={`pr-${id}-empty`}>{t(kind === 1 ? "pr.empty_customers" : "pr.empty_suppliers")}</p> : (
        <table className="table" data-testid={`pr-${id}-table`}>
          <thead><tr><th>{t("pr.party")}</th><th>{t("pr.mode")}</th><th>{t("pr.term_days")}</th><th>{t("pr.credit_limit")}</th><th>{t("pr.control_account")}</th>{kind === 1 && <th>{t("pr.representative")}</th>}<th>{t("pr.status")}</th><th>{t("pr.actions")}</th></tr></thead>
          <tbody>
            {resp.items.map((r) => (
              <tr key={r.id} data-testid={`pr-role-${r.partyCode}-${kind}`}>
                <td><bdi dir="ltr">{r.partyCode}</bdi> — {r.partyName}</td><td>{t(`pr.mode.${r.paymentMode}`)}</td><td><bdi dir="ltr">{r.termDays}</bdi></td>
                <td><bdi dir="ltr">{r.creditLimit === null ? t("pr.none") : r.creditLimit}</bdi></td><td><bdi dir="ltr">{r.controlAccountCode ?? t("pr.none")}</bdi></td>
                {kind === 1 && <td>{repName(r.representativeId)}</td>}
                <td>{r.isActive ? t("pr.active") : t("pr.inactive")}</td>
                <td>{configure && <>
                  <button type="button" className="ghost" disabled={runner.busy} onClick={() => edit(r)}>{t("pr.edit")}</button>
                  <button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(r)}>{r.isActive ? t("pr.deactivate") : t("pr.activate")}</button>
                </>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid={`pr-${id}-form`}>
          <legend>{editing ? `${t("pr.edit_role")} — ${editing.partyCode}` : t(kind === 1 ? "pr.add_customer" : "pr.add_supplier")}</legend>
          {!editing && (parties === null ? <p className="hint">{t("pr.party_unavailable")}</p> : (
            <Field id={`pr-${id}-party`} label={t("pr.party")} required hint={t("pr.party_hint")} error={runner.fields.partyId}>
              {(a) => <select {...a} value={form.partyId} onChange={(e) => setForm({ ...form, partyId: e.target.value })}><option value="">{t("pr.choose_party")}</option>{choices.map((p) => <option key={p.id} value={p.id}>{p.code} — {p.displayName}</option>)}</select>}
            </Field>
          ))}
          <Field id={`pr-${id}-mode`} label={t("pr.mode")} required error={runner.fields.paymentMode}>
            {(a) => <select {...a} value={form.mode} onChange={(e) => setForm({ ...form, mode: e.target.value })}>{[1, 2, 3].map((n) => <option key={n} value={n}>{t(`pr.mode.${n}`)}</option>)}</select>}
          </Field>
          {!cash && <Field id={`pr-${id}-days`} label={t("pr.term_days")} required error={bad(form.days) ? t("field.invalid") : runner.fields.termDays}>{(a) => <input {...a} dir="ltr" inputMode="numeric" value={form.days} onChange={(e) => setForm({ ...form, days: e.target.value })} />}</Field>}
          <Field id={`pr-${id}-limit`} label={t("pr.credit_limit")} hint={t("pr.credit_limit_hint")} error={bad(form.limit) ? t("field.invalid") : runner.fields.creditLimit}>{(a) => <input {...a} dir="ltr" inputMode="decimal" value={form.limit} onChange={(e) => setForm({ ...form, limit: e.target.value })} />}</Field>
          <Field id={`pr-${id}-account`} label={t(kind === 1 ? "pr.control_account_customer" : "pr.control_account_supplier")} error={runner.fields.controlAccountId}>
            {(a) => <select {...a} value={form.account} onChange={(e) => setForm({ ...form, account: e.target.value })}><option value="">{t("pr.none")}</option>{candidates.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}</select>}
          </Field>
          {kind === 1 && (
            <Field id={`pr-${id}-rep`} label={t("pr.representative")} error={runner.fields.representativeId}>
              {(a) => <select {...a} value={form.rep} onChange={(e) => setForm({ ...form, rep: e.target.value })}><option value="">{t("pr.none")}</option>{reps.filter((x) => x.isActive || x.id === form.rep).map((x) => <option key={x.id} value={x.id}>{x.code} — {localName(x, locale)}</option>)}</select>}
            </Field>
          )}
          <button type="button" className="primary" disabled={(!editing && form.partyId === "") || invalidNumbers} onClick={() => void save()}>{t("pr.save")}</button>
          {editing && <button type="button" onClick={reset}>{t("pr.cancel")}</button>}
        </fieldset>
      )}
    </div>
  );
}

function RepsTab({ resp, reload, onDirty }: { resp: SalesRepresentativesResponse; reload: () => Promise<void>; onDirty: (v: boolean) => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("pr.error.", reload);
  const [form, setForm] = useState({ code: "", nameAr: "", nameEn: "", userId: "" });
  const configure = resp.can.configure;
  const isDirty = Object.values(form).some((v) => v !== "");
  useReportUnsaved("pr-reps", isDirty, t("pr.tab.reps"));
  useEffect(() => { onDirty(isDirty); }, [isDirty]); // eslint-disable-line react-hooks/exhaustive-deps
  async function create() {
    const done = await runner.run(() => auth.api.createSalesRepresentative({ code: form.code, nameAr: form.nameAr, nameEn: blank(form.nameEn), userId: blank(form.userId) }), t("pr.saved"));
    if (done) setForm({ code: "", nameAr: "", nameEn: "", userId: "" });
  }
  const toggle = (r: SalesRepresentative) => void runner.run(() => auth.api.updateSalesRepresentative(r.id, { nameAr: r.nameAr, nameEn: r.nameEn, userId: r.userId, isActive: !r.isActive }, r.revision), t("pr.saved"));
  return (
    <div data-testid="pr-tab-reps">
      <Notices runner={runner} id="pr-reps" />
      <p className="hint">{t("pr.reps_hint")}</p>
      {resp.items.length === 0 ? <p data-testid="pr-reps-empty">{t("pr.reps_empty")}</p> : (
        <table className="table" data-testid="pr-reps-table">
          <thead><tr><th>{t("pr.code")}</th><th>{t("pr.name_ar")}</th><th>{t("pr.status")}</th><th>{t("pr.actions")}</th></tr></thead>
          <tbody>{resp.items.map((r) => (
            <tr key={r.id} data-testid={`pr-rep-${r.code}`}><td><bdi dir="ltr">{r.code}</bdi></td><td>{localName(r, locale)}</td><td>{r.isActive ? t("pr.active") : t("pr.inactive")}</td>
              <td>{configure && <button type="button" className="ghost" disabled={runner.busy} onClick={() => toggle(r)}>{r.isActive ? t("pr.deactivate") : t("pr.activate")}</button>}</td></tr>
          ))}</tbody>
        </table>
      )}
      {configure && (
        <fieldset disabled={runner.busy} data-testid="pr-rep-form">
          <legend>{t("pr.add_rep")}</legend>
          <Field id="pr-rep-code" label={t("pr.code")} required error={runner.fields.code}>{(a) => <input {...a} dir="ltr" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />}</Field>
          <Field id="pr-rep-name-ar" label={t("pr.name_ar")} required error={runner.fields.nameAr}>{(a) => <input {...a} value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />}</Field>
          <Field id="pr-rep-name-en" label={t("pr.name_en")} error={runner.fields.nameEn}>{(a) => <input {...a} dir="ltr" value={form.nameEn} onChange={(e) => setForm({ ...form, nameEn: e.target.value })} />}</Field>
          <Field id="pr-rep-user" label={t("pr.user")} error={runner.fields.userId}>{(a) => <input {...a} dir="ltr" value={form.userId} onChange={(e) => setForm({ ...form, userId: e.target.value })} />}</Field>
          <button type="button" className="primary" disabled={!form.code.trim() || !form.nameAr.trim()} onClick={() => void create()}>{t("pr.add_rep")}</button>
        </fieldset>
      )}
    </div>
  );
}
