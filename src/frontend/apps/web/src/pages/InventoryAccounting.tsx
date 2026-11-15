import { ApiError, type InventoryAccountingAccountRef, type InventoryAccountingResponse, type InventoryAccountingVersion } from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { useReportUnsaved } from "../unsaved";
import { blankToNull, groupTree, invName } from "./inventoryCommon";

/**
 * Inventory accounting configuration (Phase 3.7A): ONE shared screen for Web and Desktop. It chooses the ledger account that holds the inventory of each item group (with a company default) and the counter
 * accounts of receipts and issues. Every save is a new immutable VERSION (so a later posting is traceable to the exact configuration it used); every rule (asset accounts for inventory, active postable accounts,
 * base-currency accounts, versioning, concurrency, audit, authorization) is enforced by the server. This screen posts nothing: it only prepares the accounts a later posting slice needs.
 */
interface Form { def: string; receipt: string; issue: string; note: string; groups: Record<string, string> }
const toForm = (c: InventoryAccountingResponse["configuration"] | null): Form => ({
  def: c?.defaultInventoryAccountId ?? "", receipt: c?.receiptCounterAccountId ?? "", issue: c?.issueCounterAccountId ?? "", note: "", groups: Object.fromEntries((c?.groups ?? []).map((g) => [g.groupId, g.accountId])),
});
const snapshot = (f: Form) => JSON.stringify({ ...f, groups: Object.entries(f.groups).sort() });

export function InventoryAccountingPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<InventoryAccountingResponse | null>(null);
  const [versions, setVersions] = useState<InventoryAccountingVersion[]>([]);
  const [form, setForm] = useState<Form>(toForm(null));
  const [base, setBase] = useState(snapshot(toForm(null)));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState("accounts");

  const load = useCallback(async () => {
    try {
      const d = await auth.api.getInventoryAccounting();
      setData(d);
      const f = toForm(d.configuration);
      setForm(f); setBase(snapshot(f)); setErrors({}); setConflict(false);
      setVersions((await auth.api.listInventoryAccountingVersions()).versions);
    } catch (e) { if (!auth.handleError(e)) setError(errorText(tRef.current, e, "invacc.error.")); }
  }, [auth.api, auth.handleError]);
  useEffect(() => { void load(); }, [load]);
  const dirty = snapshot(form) !== base;
  useReportUnsaved("inventory-accounting", dirty, t("invacc.section"));
  if (!data) return error ? <Banner kind="error">{error}</Banner> : <Loading />;

  const canEdit = data.canConfigure;
  const byId = new Map(data.accounts.map((a) => [a.id, a]));
  const accountLabel = (a: InventoryAccountingAccountRef) => `${a.code} — ${invName(a, locale)}`;
  const assets = data.accounts.filter((a) => a.type === 1 || Object.values(form.groups).includes(a.id) || form.def === a.id);
  const select = (id: string, label: string, value: string, options: InventoryAccountingAccountRef[], onChange: (v: string) => void, err?: string, hint?: string) => (
    <Field id={id} label={label} error={err} hint={hint}>
      {(aria) => (
        <select {...aria} value={value} disabled={!canEdit} onChange={(e) => onChange(e.target.value)}>
          <option value="">{t("invacc.not_configured")}</option>
          {options.map((a) => <option key={a.id} value={a.id}>{accountLabel(a)}</option>)}
        </select>
      )}
    </Field>
  );
  const serverErrors = (e: ApiError) => {
    const out: Record<string, string> = {};
    for (const f of e.details.fields ?? []) out[f.field] = t(`invacc.field.${f.code}`) === `invacc.field.${f.code}` ? t("field.invalid") : t(`invacc.field.${f.code}`);
    return out;
  };

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    setBusy(true); setError(null); setConflict(false); setNotice(null);
    try {
      await auth.api.putInventoryAccounting({
        defaultInventoryAccountId: form.def || null, receiptCounterAccountId: form.receipt || null, issueCounterAccountId: form.issue || null, note: blankToNull(form.note),
        groups: Object.entries(form.groups).filter(([, a]) => a).map(([groupId, accountId]) => ({ groupId, accountId })),
      }, data!.configuration.versionNo);
      await load();
      setNotice(t("invacc.saved"));
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else if (err instanceof ApiError && err.status === 400) {
        const out = serverErrors(err);
        setErrors(out); setError(t("form.fix_errors"));
        setTab(Object.keys(out).some((k) => k.startsWith("groups")) && !Object.keys(out).some((k) => !k.startsWith("groups")) ? "groups" : "accounts");
      } else setError(errorText(t, err, "invacc.error."));
    } finally { setBusy(false); }
  }

  const invalid = (pred: (k: string) => boolean) => Object.keys(errors).some(pred);
  const tabs: TabItem[] = [
    {
      id: "accounts", label: t("invacc.tab.accounts"), invalid: invalid((k) => !k.startsWith("groups")),
      content: (
        <div className="fields">
          {select("ia-default", t("invacc.default_inventory"), form.def, assets, (v) => setForm((f) => ({ ...f, def: v })), errors.defaultInventoryAccountId, t("invacc.default_hint"))}
          {select("ia-receipt", t("invacc.receipt_counter"), form.receipt, data.accounts, (v) => setForm((f) => ({ ...f, receipt: v })), errors.receiptCounterAccountId, t("invacc.receipt_hint"))}
          {select("ia-issue", t("invacc.issue_counter"), form.issue, data.accounts, (v) => setForm((f) => ({ ...f, issue: v })), errors.issueCounterAccountId, t("invacc.issue_hint"))}
          <Field id="ia-note" label={t("invacc.note")}>{(aria) => <input {...aria} value={form.note} readOnly={!canEdit} maxLength={500} onChange={(e) => setForm((f) => ({ ...f, note: e.target.value }))} />}</Field>
        </div>
      ),
    },
    {
      id: "groups", label: t("invacc.tab.groups"), invalid: invalid((k) => k.startsWith("groups")),
      content: (
        <div>
          <p className="hint">{t("invacc.groups_hint")}</p>
          <div className="table-wrap">
            <table className="data-table">
              <caption className="visually-hidden">{t("invacc.tab.groups")}</caption>
              <thead><tr><th scope="col">{t("inv.code")}</th><th scope="col">{t("inv.name")}</th><th scope="col">{t("invacc.account")}</th></tr></thead>
              <tbody>
                {data.groups.length === 0 && <tr><td colSpan={3}>{t("inv.groups.none")}</td></tr>}
                {groupTree(data.groups.map((g) => ({ ...g, valuation: null, effectiveValuation: null, isActive: true, revision: 1, createdAtUtc: "", updatedAtUtc: "" }))).map(({ group: g, depth }, i) => (
                  <tr key={g.id} data-testid={`ia-group-${g.code}`}>
                    <td><bdi dir="ltr" style={{ paddingInlineStart: `${depth * 1.25}rem` }}>{g.code}</bdi></td><td><bdi>{invName(g, locale)}</bdi></td>
                    <td>
                      <Field id={`ia-g-${i}`} label={`${t("invacc.account")} ${g.code}`} error={errors[`groups[${Object.keys(form.groups).indexOf(g.id)}].accountId`]}>
                        {(aria) => (
                          <select {...aria} value={form.groups[g.id] ?? ""} disabled={!canEdit} onChange={(e) => setForm((f) => ({ ...f, groups: { ...f.groups, [g.id]: e.target.value } }))}>
                            <option value="">{t("invacc.inherit")}</option>
                            {assets.map((a) => <option key={a.id} value={a.id}>{accountLabel(a)}</option>)}
                          </select>
                        )}
                      </Field>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ),
    },
    {
      id: "versions", label: t("invacc.tab.versions"),
      content: (
        <div className="table-wrap">
          <table className="data-table" data-testid="versions">
            <caption className="visually-hidden">{t("invacc.tab.versions")}</caption>
            <thead><tr><th scope="col">{t("invacc.version")}</th><th scope="col">{t("stock.event_at")}</th><th scope="col">{t("invacc.groups_count")}</th><th scope="col">{t("invacc.note")}</th></tr></thead>
            <tbody>
              {versions.length === 0 && <tr><td colSpan={4}>{t("invacc.no_versions")}</td></tr>}
              {versions.map((v) => <tr key={v.id}><td>{v.versionNo}{v.versionNo === data.configuration.versionNo ? ` (${t("invacc.current")})` : ""}</td><td><bdi dir="ltr">{v.createdAtUtc.slice(0, 19).replace("T", " ")}</bdi></td><td>{v.groupCount}</td><td><bdi>{v.note ?? ""}</bdi></td></tr>)}
            </tbody>
          </table>
        </div>
      ),
    },
  ];
  void byId;

  return (
    <section aria-labelledby="page-title" data-testid="inventory-accounting">
      <PageHeader title={t("invacc.title")} description={t("invacc.subtitle")} actions={<span className="chip" data-testid="version-chip">{data.configuration.versionNo === 0 ? t("invacc.not_configured_yet") : t("invacc.version_n", { n: String(data.configuration.versionNo) })}</span>} />
      <Banner kind="info" testId="no-posting-note">{t("invacc.no_posting_note")}</Banner>
      {!canEdit && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && <Banner kind="error" testId="conflict">{t("invacc.error.accounting_version_conflict")} <button type="button" onClick={() => void load()}>{t("form.reload_latest")}</button></Banner>}
      {error && <Banner kind="error" testId="form-error">{error}</Banner>}
      {notice && <Banner kind="success" testId="saved">{notice}</Banner>}
      <Card>
        <form onSubmit={save} noValidate>
          <Tabs label={t("invacc.tabs_label")} tabs={tabs} value={tab} onChange={setTab} testId="accounting-tabs" />
          <div className="actions" data-testid="accounting-actions">
            <button type="button" disabled={!dirty || busy} onClick={() => { const f = toForm(data.configuration); setForm(f); setErrors({}); setError(null); }}>{t("inv.cancel")}</button>
            {canEdit && <button type="submit" className="primary" disabled={busy || !dirty}>{busy ? t("loading") : t("inv.save")}</button>}
          </div>
        </form>
      </Card>
    </section>
  );
}
