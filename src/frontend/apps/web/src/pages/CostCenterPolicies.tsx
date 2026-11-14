import type { Account, AccountCostCenterRulesResponse, Branch, CostCenter, CostCenterBranchRule, CostCenterBranchRulesResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName, useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/**
 * Cost-center accounting policies (Phase 3.2C S1). Cost centers are OPTIONAL by default. A company may (1) require a cost-center allocation on every line posted to particular accounts and
 * (2) restrict a cost center to particular branches. Both are enforced for submission and posting by the server and PostgreSQL; this screen only maintains the rules.
 */

function RequiredAccounts({ data, accounts, reload }: { data: AccountCostCenterRulesResponse; accounts: Account[]; reload: () => Promise<void> }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("ccp.error.", reload);
  const [pick, setPick] = useState("");
  const requiredIds = new Set(data.accounts.map((a) => a.accountId));
  const candidates = accounts.filter((a) => !a.isGroup && a.isActive && !requiredIds.has(a.id));
  const configure = data.can.configure;

  async function add() {
    if (!pick) return;
    const current = await auth.api.getAccountCostCenterRule(pick).catch(() => null);
    const done = await runner.run(() => auth.api.setAccountCostCenterRule(pick, true, current?.revision ?? 0), t("ccp.saved"));
    if (done) setPick("");
  }

  return (
    <Card title={t("ccp.accounts_title")} hint={t("ccp.accounts_hint")}>
      {runner.error && <Banner kind="error" testId="ccp-accounts-error">{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning" testId="ccp-accounts-conflict">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      {data.accounts.length === 0 ? <p data-testid="ccp-accounts-empty">{t("ccp.accounts_empty")}</p> : (
        <table className="table" data-testid="ccp-accounts-table">
          <thead><tr><th>{t("ccp.account")}</th><th>{t("common.actions")}</th></tr></thead>
          <tbody>
            {data.accounts.map((a) => (
              <tr key={a.accountId} data-testid={`ccp-account-${a.code}`}>
                <td><bdi dir="ltr">{a.code}</bdi> — {localName(a, locale)}</td>
                <td>{configure && <button type="button" className="ghost" disabled={runner.busy} onClick={() => void runner.run(() => auth.api.setAccountCostCenterRule(a.accountId, false, a.revision), t("ccp.saved"))}>{t("ccp.stop_requiring")}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {configure && (
        <div className="field" data-testid="ccp-add-account">
          <Field id="ccp-account" label={t("ccp.require_for")}>
            {(a) => (
              <select {...a} value={pick} onChange={(e) => setPick(e.target.value)} disabled={runner.busy}>
                <option value="">{t("ccp.choose_account")}</option>
                {candidates.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}
              </select>
            )}
          </Field>
          <button type="button" className="primary" disabled={!pick || runner.busy} onClick={() => void add()}>{t("ccp.require")}</button>
        </div>
      )}
    </Card>
  );
}

function BranchEligibility({ data, centers, branches, reload }: { data: CostCenterBranchRulesResponse; centers: CostCenter[]; branches: Branch[]; reload: () => Promise<void> }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("ccp.error.", reload);
  const [centerId, setCenterId] = useState("");
  const [rule, setRule] = useState<CostCenterBranchRule | null>(null);
  const [restricted, setRestricted] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  const configure = data.can.configure;
  const leaves = centers.filter((c) => !c.isGroup);
  const dirty = rule !== null && (restricted !== rule.restricted || JSON.stringify([...selected].sort()) !== JSON.stringify([...rule.branchIds].sort()));
  useReportUnsaved("ccp-branches", dirty, t("ccp.branches_title"));

  const choose = useCallback(async (id: string) => {
    setCenterId(id); setRule(null);
    if (!id) return;
    try { const r = await auth.api.getCostCenterBranchRule(id); setRule(r); setRestricted(r.restricted); setSelected(r.branchIds); }
    catch (e) { if (!auth.handleError(e)) runner.setError(errorText(t, e, "ccp.error.")); }
  }, [auth.api]); // eslint-disable-line react-hooks/exhaustive-deps

  async function save() {
    if (!rule) return;
    const done = await runner.run(() => auth.api.setCostCenterBranchRule(rule.costCenterId, { restricted, branchIds: restricted ? selected : [] }, rule.revision), t("ccp.saved"));
    if (done) { setRule(done); setRestricted(done.restricted); setSelected(done.branchIds); }
  }

  return (
    <Card title={t("ccp.branches_title")} hint={t("ccp.branches_hint")}>
      {runner.error && <Banner kind="error" testId="ccp-branches-error">{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning" testId="ccp-branches-conflict">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      <p data-testid="ccp-restricted-count">{t("ccp.restricted_count", { n: String(data.costCenters.length) })}</p>
      <Field id="ccp-center" label={t("ccp.cost_center")}>
        {(a) => (
          <select {...a} value={centerId} onChange={(e) => void choose(e.target.value)} disabled={runner.busy}>
            <option value="">{t("ccp.choose_cost_center")}</option>
            {leaves.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}
          </select>
        )}
      </Field>
      {rule && (
        <div data-testid="ccp-branch-editor">
          <div className="field"><label><input type="checkbox" checked={restricted} disabled={!configure || runner.busy} onChange={(e) => setRestricted(e.target.checked)} /> {t("ccp.restrict")}</label></div>
          {restricted && (
            <fieldset disabled={!configure || runner.busy}>
              <legend>{t("ccp.eligible_branches")}</legend>
              {branches.map((b) => (
                <div className="field" key={b.id}>
                  <label><input type="checkbox" checked={selected.includes(b.id)} onChange={(e) => setSelected(e.target.checked ? [...selected, b.id] : selected.filter((x) => x !== b.id))} /> {localName(b, locale)}</label>
                </div>
              ))}
            </fieldset>
          )}
          {configure && <button type="button" className="primary" disabled={!dirty || runner.busy || (restricted && selected.length === 0)} onClick={() => void save()}>{t("common.save")}</button>}
        </div>
      )}
    </Card>
  );
}

export function CostCenterPoliciesPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [accountRules, setAccountRules] = useState<AccountCostCenterRulesResponse | null>(null);
  const [branchRules, setBranchRules] = useState<CostCenterBranchRulesResponse | null>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [centers, setCenters] = useState<CostCenter[]>([]);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try {
      const [ar, br] = await Promise.all([auth.api.listRequiredCostCenterAccounts(), auth.api.listRestrictedCostCenters()]);
      setAccountRules(ar); setBranchRules(br);
      if (auth.canAny("company.accounts.view")) { try { setAccounts((await auth.api.listAccounts()).accounts); } catch { setAccounts([]); } }
      if (auth.canAny("company.costcenters.view")) { try { setCenters((await auth.api.listCostCenters()).costCenters.filter((c) => c.isActive)); } catch { setCenters([]); } }
      if (auth.canAny("company.branches.view")) { try { setBranches((await auth.api.listBranches()).branches.filter((b) => b.isActive)); } catch { setBranches([]); } }
      setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "ccp.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);

  if (loadError) return <Banner kind="error" testId="ccp-load-error">{loadError}</Banner>;
  if (!accountRules || !branchRules) return <Loading />;
  return (
    <>
      <PageHeader title={t("ccp.title")} description={t("ccp.subtitle")} />
      <Banner kind="info">{t("ccp.optional_note")}</Banner>
      <RequiredAccounts data={accountRules} accounts={accounts} reload={load} />
      <BranchEligibility data={branchRules} centers={centers} branches={branches} reload={load} />
    </>
  );
}
