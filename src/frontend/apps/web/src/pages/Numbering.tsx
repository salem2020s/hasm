import type { NumberingResponse, NumberingRule } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { toLatinDigits, useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/** Document numbering (Phase 3.2B S1). The official number is issued by the server inside the posting transaction; this screen only configures the format. */

function sample(f: { prefix: string; separator: string; width: number; perBranch: boolean; perFiscalYear: boolean }): string {
  const parts: string[] = [];
  if (f.prefix) parts.push(f.prefix);
  if (f.perFiscalYear) parts.push("2026");
  if (f.perBranch) parts.push("BR1");
  parts.push("1".padStart(Math.min(Math.max(f.width, 1), 12), "0"));
  return parts.join(f.separator);
}

function RuleForm({ rule, canConfigure, onSaved }: { rule: NumberingRule; canConfigure: boolean; onSaved: () => Promise<void> }) {
  const { t } = useI18n();
  const auth = useAuth();
  const runner = useRunner("numbering.error.", onSaved);
  const initial = { prefix: rule.prefix, separator: rule.separator, width: String(rule.width), perBranch: rule.perBranch, perFiscalYear: rule.perFiscalYear };
  const [f, setF] = useState(initial);
  useEffect(() => setF(initial), [rule.revision]); // eslint-disable-line react-hooks/exhaustive-deps
  const dirty = JSON.stringify(f) !== JSON.stringify(initial);
  useReportUnsaved(`numbering-${rule.docType}`, dirty, t("numbering.title"));
  const width = Number(toLatinDigits(f.width));
  const valid = /^[A-Za-z0-9._-]{0,20}$/.test(f.prefix.trim()) && /^[A-Za-z0-9._/-]{0,3}$/.test(f.separator.trim()) && Number.isInteger(width) && width >= 1 && width <= 12;

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!valid) return;
    await runner.run(() => auth.api.putNumberingRule(rule.docType, { prefix: f.prefix.trim(), separator: f.separator.trim(), width, perBranch: f.perBranch, perFiscalYear: f.perFiscalYear }, rule.revision), t("numbering.saved"));
  }

  return (
    <Card title={t(`numbering.doc.${rule.docType}`)} hint={rule.configured ? undefined : t("numbering.defaults_hint")}>
      <form onSubmit={save} noValidate data-testid={`numbering-form-${rule.docType}`}>
        <fieldset disabled={!canConfigure || runner.busy}>
          <Field id={`np-${rule.docType}`} label={t("numbering.prefix")} error={runner.fields.prefix}>
            {(a) => <input {...a} value={f.prefix} maxLength={20} onChange={(e) => setF({ ...f, prefix: e.target.value })} />}
          </Field>
          <Field id={`ns-${rule.docType}`} label={t("numbering.separator")} hint={t("numbering.separator_hint")} error={runner.fields.separator}>
            {(a) => <input {...a} value={f.separator} maxLength={3} onChange={(e) => setF({ ...f, separator: e.target.value })} />}
          </Field>
          <Field id={`nw-${rule.docType}`} label={t("numbering.width")} hint={t("numbering.width_hint")} error={runner.fields.width}>
            {(a) => <input {...a} inputMode="numeric" value={f.width} onChange={(e) => setF({ ...f, width: e.target.value })} />}
          </Field>
          <div className="field">
            <label><input type="checkbox" checked={f.perFiscalYear} disabled={rule.numbersIssued} onChange={(e) => setF({ ...f, perFiscalYear: e.target.checked })} /> {t("numbering.per_year")}</label>
            <label><input type="checkbox" checked={f.perBranch} disabled={rule.numbersIssued} onChange={(e) => setF({ ...f, perBranch: e.target.checked })} /> {t("numbering.per_branch")}</label>
            {rule.numbersIssued && <p className="hint" data-testid="numbering-scope-locked">{t("numbering.scope_locked")}</p>}
          </div>
          <p className="hint" data-testid={`numbering-sample-${rule.docType}`}>{t("numbering.sample")}: <bdi dir="ltr">{valid ? sample({ ...f, width }) : "—"}</bdi></p>
          {canConfigure && <button type="submit" className="primary" disabled={!dirty || !valid}>{t("common.save")}</button>}
        </fieldset>
        {runner.fields.body && <Banner kind="error">{runner.fields.body}</Banner>}
        {runner.error && <Banner kind="error" testId="numbering-error">{runner.error}</Banner>}
        {runner.conflict && <Banner kind="warning" testId="numbering-conflict">{t("common.conflict")}</Banner>}
        {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      </form>
    </Card>
  );
}

export function NumberingPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<NumberingResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { setData(await auth.api.getNumbering()); setLoadError(null); }
    catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "numbering.error.")); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);
  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!data) return <Loading />;
  return (
    <>
      <PageHeader title={t("numbering.title")} description={t("numbering.subtitle")} />
      <Banner kind="info">{t("numbering.void_note")}</Banner>
      {data.rules.map((r) => <RuleForm key={r.docType} rule={r} canConfigure={data.can.configure} onSaved={load} />)}
    </>
  );
}
