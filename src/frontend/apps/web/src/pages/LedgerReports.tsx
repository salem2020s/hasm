import type { Account, Branch, CostCenter, GeneralLedgerResponse, TrialBalanceResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName } from "../ledgerCommon";

/**
 * Ledger reports (Phase 3.2B S6): the trial balance and the general ledger, computed by the server from POSTED entries only (reversals net the originals out; nothing is
 * estimated or cached). Amounts are the base-currency amounts stored on the posted lines, shown as exact decimal text. The CSV download needs the separate export permission.
 */

type Tab = "tb" | "gl";
const iso = (d: Date) => d.toISOString().slice(0, 10);

function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function LedgerReportsPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [tab, setTab] = useState<Tab>("tb");
  const year = new Date().getFullYear();
  const [f, setF] = useState({ from: `${year}-01-01`, to: iso(new Date()), branchId: "", costCenterId: "", accountId: "", includeZero: false, excludeClosing: false });
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [centers, setCenters] = useState<CostCenter[]>([]);
  const [tb, setTb] = useState<TrialBalanceResponse | null>(null);
  const [gl, setGl] = useState<GeneralLedgerResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [ready, setReady] = useState(false);
  const canExport = auth.canAny("company.ledgerreports.export");

  useEffect(() => {
    void (async () => {
      try { setAccounts((await auth.api.listAccounts()).accounts); } catch { setAccounts([]); }
      try { setBranches((await auth.api.listBranches()).branches); } catch { setBranches([]); }
      try { setCenters((await auth.api.listCostCenters()).costCenters); }
      catch {
        // A cost-center-scoped viewer cannot list the administration tree: offer the cost centers the server allows them instead.
        try { setCenters((await auth.api.listAllocatableCostCenters()).costCenters.map((c) => ({ ...c, isGroup: false, isActive: true, canReceiveAllocations: true, hasChildren: false, revision: 0, createdAtUtc: "", updatedAtUtc: "" }))); } catch { setCenters([]); }
      }
      setReady(true);
    })();
  }, [auth.api]);

  const query = () => ({ from: f.from, to: f.to, branchId: f.branchId || undefined, costCenterId: f.costCenterId || undefined, excludeClosing: f.excludeClosing || undefined });
  const valid = !!f.from && !!f.to && f.from <= f.to && (tab === "tb" || !!f.accountId);

  async function show(e?: FormEvent, offset = 0) {
    e?.preventDefault();
    if (!valid) return;
    setBusy(true); setError(null);
    try {
      if (tab === "tb") setTb(await auth.api.getTrialBalance({ ...query(), includeZero: f.includeZero }));
      else setGl(await auth.api.getGeneralLedger(f.accountId, query(), offset));
    } catch (err) { if (!auth.handleError(err)) setError(errorText(tRef.current, err, "rep.error.")); }
    finally { setBusy(false); }
  }
  async function download() {
    setBusy(true); setError(null);
    try {
      const blob = tab === "tb" ? await auth.api.downloadTrialBalanceCsv({ ...query(), includeZero: f.includeZero }) : await auth.api.downloadGeneralLedgerCsv(f.accountId, query());
      saveBlob(blob, `${tab === "tb" ? "trial-balance" : "general-ledger"}-${f.from}-${f.to}.csv`);
    } catch (err) { if (!auth.handleError(err)) setError(errorText(tRef.current, err, "rep.error.")); }
    finally { setBusy(false); }
  }

  if (!ready) return <Loading />;
  return (
    <>
      <PageHeader title={t("rep.title")} description={t("rep.subtitle")} />
      <div role="tablist" aria-label={t("rep.title")} className="tabs">
        <button type="button" role="tab" aria-selected={tab === "tb"} onClick={() => setTab("tb")}>{t("rep.trial_balance")}</button>
        <button type="button" role="tab" aria-selected={tab === "gl"} onClick={() => setTab("gl")}>{t("rep.general_ledger")}</button>
      </div>
      <Card>
        <form onSubmit={(e) => void show(e)} className="filters" data-testid="rep-form">
          <fieldset disabled={busy}>
            {tab === "gl" && (
              <Field id="rep-account" label={t("rep.account")} required>
                {(a) => <select {...a} value={f.accountId} onChange={(x) => setF({ ...f, accountId: x.target.value })}><option value="">{t("jrn.choose_account")}</option>{accounts.map((x) => <option key={x.id} value={x.id}>{x.code} — {localName(x, locale)}</option>)}</select>}
              </Field>
            )}
            <Field id="rep-from" label={t("rep.from")} required>{(a) => <input {...a} type="date" value={f.from} onChange={(x) => setF({ ...f, from: x.target.value })} />}</Field>
            <Field id="rep-to" label={t("rep.to")} required error={f.from > f.to ? t("rep.range_invalid") : undefined}>{(a) => <input {...a} type="date" value={f.to} onChange={(x) => setF({ ...f, to: x.target.value })} />}</Field>
            <Field id="rep-branch" label={t("rep.branch")}>{(a) => <select {...a} value={f.branchId} onChange={(x) => setF({ ...f, branchId: x.target.value })}><option value="">{t("common.all")}</option>{branches.map((b) => <option key={b.id} value={b.id}>{localName(b, locale)}</option>)}</select>}</Field>
            {centers.length > 0 && <Field id="rep-cc" label={t("rep.cost_center")}>{(a) => <select {...a} value={f.costCenterId} onChange={(x) => setF({ ...f, costCenterId: x.target.value })}><option value="">{t("common.all")}</option>{centers.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}</select>}</Field>}
            <div className="field"><label><input type="checkbox" checked={f.excludeClosing} onChange={(x) => setF({ ...f, excludeClosing: x.target.checked })} /> {t("rep.exclude_closing")}</label></div>
            {tab === "tb" && <div className="field"><label><input type="checkbox" checked={f.includeZero} onChange={(x) => setF({ ...f, includeZero: x.target.checked })} /> {t("rep.include_zero")}</label></div>}
            <button type="submit" className="primary" disabled={!valid}>{t("rep.show")}</button>
            {canExport && <button type="button" disabled={!valid} onClick={() => void download()}>{t("rep.download_csv")}</button>}
          </fieldset>
        </form>
      </Card>
      {error && <Banner kind="error" testId="rep-error">{error}</Banner>}
      {tab === "tb" && tb && (
        <Card title={t("rep.trial_balance")}>
          {!tb.balanced && <Banner kind="warning" testId="rep-unbalanced">{t("rep.unbalanced_note")}</Banner>}
          <p className="hint">{t("rep.base_currency")}: <bdi dir="ltr">{tb.baseCurrency}</bdi> — {t("rep.posted_only")}</p>
          <div className="table-scroll">
            <table className="table" data-testid="rep-tb-table">
              <thead><tr><th>{t("cc.code")}</th><th>{t("cc.name")}</th><th>{t("rep.opening")}</th><th>{t("jrn.debit")}</th><th>{t("jrn.credit")}</th><th>{t("rep.closing")}</th></tr></thead>
              <tbody>
                {tb.rows.map((r) => (
                  <tr key={r.accountId} data-testid={`rep-tb-${r.code}`} className={r.isGroup ? "row-group" : undefined}>
                    <td><bdi dir="ltr" style={{ paddingInlineStart: `${r.level * 1.25}rem` }}>{r.code}</bdi></td><td>{localName(r, locale)}</td>
                    <td><bdi dir="ltr">{r.opening}</bdi></td><td><bdi dir="ltr">{r.debit}</bdi></td><td><bdi dir="ltr">{r.credit}</bdi></td><td><bdi dir="ltr">{r.closing}</bdi></td>
                  </tr>
                ))}
              </tbody>
              <tfoot><tr data-testid="rep-tb-total"><th colSpan={2}>{t("jrn.total")}</th><td><bdi dir="ltr">{tb.totals.opening}</bdi></td><td><bdi dir="ltr">{tb.totals.debit}</bdi></td><td><bdi dir="ltr">{tb.totals.credit}</bdi></td><td><bdi dir="ltr">{tb.totals.closing}</bdi></td></tr></tfoot>
            </table>
          </div>
          {tb.rows.length === 0 && <p data-testid="rep-empty">{t("rep.empty")}</p>}
        </Card>
      )}
      {tab === "gl" && gl && (
        <Card title={t("rep.general_ledger")}>
          <p className="hint">{t("rep.opening")}: <bdi dir="ltr">{gl.opening}</bdi> — {t("rep.closing")}: <bdi dir="ltr">{gl.closing} {gl.baseCurrency}</bdi>{gl.includesDescendants ? ` — ${t("rep.includes_children")}` : ""}</p>
          <div className="table-scroll">
            <table className="table" data-testid="rep-gl-table">
              <thead><tr><th>{t("jrn.date")}</th><th>{t("jrn.number")}</th><th>{t("jrn.description")}</th><th>{t("jrn.currency")}</th><th>{t("jrn.debit")}</th><th>{t("jrn.credit")}</th><th>{t("rep.rate")}</th><th>{t("rep.base_debit")}</th><th>{t("rep.base_credit")}</th><th>{t("rep.running")}</th></tr></thead>
              <tbody>
                {gl.lines.map((l, i) => (
                  <tr key={`${l.entryId}-${i}`} data-testid={`rep-gl-row-${i}`}>
                    <td>{l.entryDate}</td><td><bdi dir="ltr">{l.number}</bdi></td><td>{l.lineDescription ?? l.entryDescription ?? ""}</td><td><bdi dir="ltr">{l.currency}</bdi></td>
                    <td><bdi dir="ltr">{l.debit}</bdi></td><td><bdi dir="ltr">{l.credit}</bdi></td><td><bdi dir="ltr">{l.rate}</bdi></td>
                    <td><bdi dir="ltr">{l.baseDebit}</bdi></td><td><bdi dir="ltr">{l.baseCredit}</bdi></td><td><bdi dir="ltr">{l.runningBalance}</bdi></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {gl.lines.length === 0 && <p data-testid="rep-empty">{t("rep.empty")}</p>}
          {gl.totalLines > gl.offset + gl.lines.length && <button type="button" onClick={() => void show(undefined, gl.offset + gl.lines.length)}>{t("rep.next_page")}</button>}
          {gl.offset > 0 && <button type="button" onClick={() => void show(undefined, Math.max(0, gl.offset - 200))}>{t("rep.prev_page")}</button>}
        </Card>
      )}
    </>
  );
}
