import type { Account, Branch, ClosingPreview, ClosingSettingsResponse, FiscalYearClosing, YearEndStatus } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { localName, useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/**
 * Fiscal-year closing and opening balances (Phase 3.2C S3). The screen drafts the year-end entries and shows their state; the entries themselves are ordinary journal entries that follow
 * the journal workflow (approval by someone other than the initiator, numbering, period checks, atomic posting). Every validation comes from the server; a check of a module that does
 * not exist is shown as "not applicable", never as passed. Nothing is decided here.
 */

function Settings({ data, accounts, reload }: { data: ClosingSettingsResponse; accounts: Account[]; reload: () => Promise<void> }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const runner = useRunner("fyc.error.", reload);
  const s = data.settings;
  const [equity, setEquity] = useState(s.equityAccountId ?? "");
  const [approval, setApproval] = useState(s.requireApproval);
  const dirty = equity !== (s.equityAccountId ?? "") || approval !== s.requireApproval;
  useReportUnsaved("fyc-settings", dirty, t("fyc.settings_title"));
  const candidates = accounts.filter((a) => a.type === "equity" && !a.isGroup && a.isActive);
  return (
    <Card title={t("fyc.settings_title")} hint={t("fyc.settings_hint")}>
      {runner.error && <Banner kind="error" testId="fyc-settings-error">{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning" testId="fyc-settings-conflict">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      <Field id="fyc-equity" label={t("fyc.equity_account")} required error={runner.fields.equityAccountId}>
        {(a) => (
          <select {...a} value={equity} disabled={!data.can.configure || runner.busy} onChange={(e) => setEquity(e.target.value)}>
            <option value="">{t("fyc.choose_equity")}</option>
            {candidates.map((c) => <option key={c.id} value={c.id}>{c.code} — {localName(c, locale)}</option>)}
          </select>
        )}
      </Field>
      <div className="field"><label><input type="checkbox" checked={approval} disabled={!data.can.configure || runner.busy} onChange={(e) => setApproval(e.target.checked)} /> {t("fyc.require_approval")}</label><p className="hint">{t("fyc.require_approval_hint")}</p></div>
      {data.can.configure && <button type="button" className="primary" disabled={!equity || !dirty || runner.busy} onClick={() => void runner.run(() => auth.api.putClosingSettings({ equityAccountId: equity, requireApproval: approval }, s.revision), t("fyc.settings_saved"))}>{t("common.save")}</button>}
    </Card>
  );
}

const STATUS_KEY: Record<string, string> = { passed: "fyc.check_passed", blocked: "fyc.check_blocked", not_applicable: "fyc.check_na" };

export function FiscalClosingPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [settings, setSettings] = useState<ClosingSettingsResponse | null>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [branches, setBranches] = useState<Branch[]>([]);
  const [years, setYears] = useState<YearEndStatus[]>([]);
  const [yearId, setYearId] = useState("");
  const [preview, setPreview] = useState<ClosingPreview | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const loadPreview = useCallback(async (id: string) => {
    if (!id) { setPreview(null); return; }
    try { setPreview(await auth.api.previewYearEnd(id)); } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "fyc.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps

  const load = useCallback(async () => {
    try {
      const st = await auth.api.getClosingSettings();
      setSettings(st);
      const ys = (await auth.api.listYearEnd()).years;
      setYears(ys);
      if (auth.canAny("company.accounts.view")) { try { setAccounts((await auth.api.listAccounts()).accounts); } catch { setAccounts([]); } }
      if (auth.canAny("company.branches.view")) { try { setBranches((await auth.api.listBranches()).branches); } catch { setBranches([]); } }
      setLoadError(null);
      setYearId((cur) => cur || ys[0]?.yearId || "");
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "fyc.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { void loadPreview(yearId); }, [yearId, loadPreview]);
  const reloadAll = useCallback(async () => { await load(); await loadPreview(yearId); }, [load, loadPreview, yearId]);
  const runner = useRunner("fyc.error.", reloadAll);

  if (loadError) return <Banner kind="error" testId="fyc-load-error">{loadError}</Banner>;
  if (!settings) return <Loading />;
  const status = years.find((y) => y.yearId === yearId);
  const accountName = (id: string | null) => { const a = accounts.find((x) => x.id === id); return a ? `${a.code} — ${localName(a, locale)}` : id?.slice(0, 8) ?? ""; };
  const branchName = (id: string) => { const b = branches.find((x) => x.id === id); return b ? localName(b, locale) : id.slice(0, 8); };
  const can = settings.can;
  const latest: FiscalYearClosing | undefined = status?.closings[status.closings.length - 1];
  const activeOpening = latest?.openingStatus === "posted";
  const lineTable = (lines: ClosingPreview["closingLines"], testId: string) => (
    <table className="table" data-testid={testId}>
      <thead><tr><th>{t("fyc.account")}</th><th>{t("fyc.branch")}</th><th>{t("fyc.debit")}</th><th>{t("fyc.credit")}</th></tr></thead>
      <tbody>{lines.map((l, i) => <tr key={i}><td>{accountName(l.accountId)}</td><td>{branchName(l.branchId)}</td><td><bdi dir="ltr">{l.debit}</bdi></td><td><bdi dir="ltr">{l.credit}</bdi></td></tr>)}</tbody>
    </table>
  );
  return (
    <>
      <PageHeader title={t("fyc.title")} description={t("fyc.subtitle")} />
      <Banner kind="info">{t("fyc.workflow_note")}</Banner>
      {runner.error && <Banner kind="error" testId="fyc-error">{runner.error}</Banner>}
      {runner.conflict && <Banner kind="warning" testId="fyc-conflict">{t("common.conflict")}</Banner>}
      {runner.notice && <Banner kind="success" testId="fyc-notice">{runner.notice}</Banner>}
      <Settings key={settings.settings.revision} data={settings} accounts={accounts} reload={load} />
      <Card title={t("fyc.year_title")}>
        <Field id="fyc-year" label={t("fyc.year")}>
          {(a) => <select {...a} value={yearId} onChange={(e) => setYearId(e.target.value)}>{years.length === 0 && <option value="">—</option>}{years.map((y) => <option key={y.yearId} value={y.yearId}>{y.name} ({y.startDate} → {y.endDate})</option>)}</select>}
        </Field>
      </Card>
      {preview && (
        <Card title={t("fyc.preview_title")}>
          <p data-testid="fyc-net">{preview.isProfit ? t("fyc.profit") : preview.netResult === "0.0000" ? t("fyc.no_result") : t("fyc.loss")}: <bdi dir="ltr">{preview.netResult.replace("-", "")}</bdi> {preview.baseCurrency}</p>
          <table className="table" data-testid="fyc-checks">
            <thead><tr><th>{t("fyc.check")}</th><th>{t("common.status")}</th></tr></thead>
            <tbody>
              {preview.checks.map((c) => (
                <tr key={c.code} data-testid={`fyc-check-${c.code}`}>
                  <td>{t(`fyc.check.${c.code}`.replace(/^fyc\.check\.domain:.*$/, "fyc.check.domain"))}{c.code.startsWith("domain:") ? ` (${c.code.slice(7)})` : ""}{c.detail ? ` — ${c.detail}` : ""}</td>
                  <td>{t(STATUS_KEY[c.status] ?? "fyc.check_blocked")}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {preview.closingLines.length > 0 && <><h3>{t("fyc.closing_lines")}</h3>{lineTable(preview.closingLines, "fyc-closing-lines")}</>}
          {preview.openingLines.length > 0 && <><h3>{t("fyc.opening_lines")}</h3>{lineTable(preview.openingLines, "fyc-opening-lines")}</>}
          <p className="hint">{t("fyc.evidence")}: <bdi dir="ltr" data-testid="fyc-hash">{preview.evidenceHash.slice(0, 16)}</bdi></p>
          {can.create && (
            <>
              <button type="button" className="primary" disabled={!preview.canClose || runner.busy} onClick={() => void runner.run(() => auth.api.startYearClosing(preview.yearId), t("fyc.closing_started"))}>{t("fyc.start_closing")}</button>
              <button type="button" disabled={!preview.canCarryForward || runner.busy} onClick={() => void runner.run(() => auth.api.carryForwardYear(preview.yearId), t("fyc.carry_started"))}>{t("fyc.start_carry")}</button>
            </>
          )}
        </Card>
      )}
      {status && (status.closings.length > 0 || status.carryforwards.length > 0) && (
        <Card title={t("fyc.runs_title")}>
          <table className="table" data-testid="fyc-runs">
            <thead><tr><th>{t("fyc.version")}</th><th>{t("common.status")}</th><th>{t("fyc.closing_entry")}</th><th>{t("fyc.opening_entry")}</th><th>{t("common.actions")}</th></tr></thead>
            <tbody>
              {status.closings.map((c) => (
                <tr key={c.id} data-testid={`fyc-run-${c.version}`}>
                  <td>{c.version}</td>
                  <td>{t(`fyc.state.${c.state}`)}{c.stale && <strong data-testid={`fyc-stale-${c.version}`}> — {t("fyc.stale")}</strong>}</td>
                  <td>{c.closingNumber ?? t(`jrn.status.${c.closingStatus}`)}</td>
                  <td>{c.openingEntryId ? (c.openingNumber ?? t(`jrn.status.${c.openingStatus ?? "draft"}`)) : "—"}</td>
                  <td>
                    {can.reverse && c === latest && activeOpening && <button type="button" className="ghost" disabled={runner.busy} onClick={() => void runner.run(() => auth.api.reverseYearOpening(c.yearId), t("fyc.reversal_drafted"))}>{t("fyc.reverse_opening")}</button>}
                    {can.reverse && c === latest && c.closingStatus === "posted" && !activeOpening && <button type="button" className="ghost" disabled={runner.busy} onClick={() => void runner.run(() => auth.api.reverseYearClosing(c.yearId), t("fyc.reversal_drafted"))}>{t("fyc.reverse_closing")}</button>}
                  </td>
                </tr>
              ))}
              {status.carryforwards.filter((k) => k.closingId === null).map((k) => (
                <tr key={k.id} data-testid={`fyc-carry-${k.id}`}>
                  <td>—</td><td>{t("fyc.no_closing_needed")}{k.stale && <strong> — {t("fyc.stale")}</strong>}</td><td>—</td><td>{k.entryNumber ?? t(`jrn.status.${k.entryStatus}`)}</td>
                  <td>{can.reverse && k.entryStatus === "posted" && <button type="button" className="ghost" disabled={runner.busy} onClick={() => void runner.run(() => auth.api.reverseYearOpening(k.fromYearId), t("fyc.reversal_drafted"))}>{t("fyc.reverse_opening")}</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </>
  );
}
