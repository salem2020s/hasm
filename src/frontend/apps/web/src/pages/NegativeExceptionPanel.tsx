import { ApiError, type NegativeException, type StockDocument } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, errorText, Field } from "../components";
import { invName } from "./inventoryCommon";

/**
 * The negative-stock exception of an approved, not executed ISSUE document (Phase 3.8B-3), shown in the posting tab of the stock document screen. ONE implementation for Web and Windows Desktop. It shows the plan the
 * server computes (quantity on hand, covered and uncovered parts, provisional cost and its source) and lets a user holding the warehouse-scoped permission record an approval with a reason (and a justified manual
 * provisional value for a line that has no cost source). The server decides everything: eligibility, segregation (never the creator or the poster), staleness, and whether posting may use the approval.
 */
const AMOUNT = /^\d+(\.\d{1,6})?$/;
const num = (n: number | null | undefined) => (n === null || n === undefined ? "—" : String(Number(n.toFixed(10))));

export function NegativeExceptionPanel({ doc, onApproved }: { doc: StockDocument; onApproved: (message: string) => void }) {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<{ exception: NegativeException; canApprove: boolean } | null>(null);
  const [reason, setReason] = useState("");
  const [manual, setManual] = useState<Record<number, { amount: string; justification: string }>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try { setData(await auth.api.getNegativeException(doc.id)); }
    catch (e) { if (!auth.handleError(e)) setError(errorText(tRef.current, e, "neg.error.")); }
  }, [auth.api, auth.handleError, doc.id]);
  useEffect(() => { void load(); }, [load]);

  if (!data) return error ? <Banner kind="error" testId="neg-panel-error">{error}</Banner> : null;
  const x = data.exception;
  const plan = x.plan;
  if (!plan.needed && x.approvals.length === 0) return null;                          // nothing exceeds the stock: no exception, no panel
  const lineOf = (no: number) => doc.lines.find((l) => l.lineNo === no);
  const needsManual = plan.lines.filter((l) => l.uncovered > 0 && (l.source === 3 || l.problem === "provisional_cost_unavailable"));
  const problemText = (code: string) => (t(`neg.problem.${code}`) === `neg.problem.${code}` ? t("field.invalid") : t(`neg.problem.${code}`));

  async function approve() {
    const v: Record<string, string> = {};
    if (!reason.trim()) v.reason = t("field.required");
    for (const l of needsManual) {
      const m = manual[l.lineNo];
      if (l.source === 3 && !m) continue;                                                 // the approved manual value is confirmed as it stands
      if (!m || !AMOUNT.test(m.amount.trim()) || Number(m.amount) <= 0) v[`amount-${l.lineNo}`] = t("field.invalid");
      if (!m || !m.justification.trim()) v[`just-${l.lineNo}`] = t("field.required");
    }
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true); setError(null);
    try {
      const manualCosts = needsManual.filter((l) => manual[l.lineNo]).map((l) => ({ lineNo: l.lineNo, amount: Number(manual[l.lineNo].amount.trim()), justification: manual[l.lineNo].justification.trim() }));
      await auth.api.approveNegativeException(doc.id, doc.revision, reason.trim(), manualCosts);
      setReason(""); setManual({});
      await load();
      onApproved(t("neg.approved"));
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 400) {
        const out: Record<string, string> = {};
        for (const f of err.details.fields ?? []) out[f.field] = t(`field.${f.code}`) === `field.${f.code}` ? t("field.invalid") : t(`field.${f.code}`);
        setErrors(out); setError(t("form.fix_errors"));
      } else setError(errorText(t, err, "neg.error."));
    } finally { setBusy(false); }
  }

  return (
    <section className="neg-exception" aria-labelledby="neg-exception-title" data-testid="neg-exception">
      <h3 id="neg-exception-title">{t("neg.exception")}</h3>
      <p className="hint">{t("neg.exception_hint")}</p>
      {!x.enabled && <Banner kind="warning" testId="neg-disabled">{t("neg.exception_disabled")}</Banner>}
      {!plan.needed && <Banner kind="info" testId="neg-not-needed">{t("neg.not_needed")}</Banner>}
      {x.approvals.length > 0 && x.validApprovals === 0 && plan.needed && <Banner kind="warning" testId="neg-stale">{t("neg.stale_approval")}</Banner>}
      {error && <Banner kind="error" testId="neg-error">{error}</Banner>}
      <div className="table-wrap">
        <table className="data-table" data-testid="neg-plan">
          <caption className="visually-hidden">{t("neg.exception")}</caption>
          <thead><tr><th scope="col">#</th><th scope="col">{t("stock.item")}</th><th scope="col">{t("neg.on_hand")}</th><th scope="col">{t("neg.covered")}</th><th scope="col">{t("neg.uncovered")}</th><th scope="col">{t("neg.source")}</th><th scope="col">{t("neg.provisional_total")}</th><th scope="col">{t("neg.unit_cost")}</th></tr></thead>
          <tbody>
            {plan.lines.map((l) => {
              const d = lineOf(l.lineNo);
              return (
                <tr key={l.lineNo} data-testid={`neg-line-${l.lineNo}`}>
                  <td>{l.lineNo}</td>
                  <td>{d ? <><bdi dir="ltr">{d.itemCode}</bdi> — <bdi>{invName({ code: d.itemCode, nameAr: d.itemNameAr, nameEn: d.itemNameEn }, locale)}</bdi></> : "—"}</td>
                  <td><bdi dir="ltr">{num(l.onHand)}</bdi></td><td><bdi dir="ltr">{num(l.covered)}</bdi></td><td><bdi dir="ltr" data-testid={`neg-uncovered-${l.lineNo}`}>{num(l.uncovered)}</bdi></td>
                  <td>{l.source ? t(`neg.source.${l.source}`) : "—"}{l.problem && <span className="field-error" data-testid={`neg-problem-${l.lineNo}`}> {problemText(l.problem)}</span>}</td>
                  <td><bdi dir="ltr" data-testid={`neg-total-${l.lineNo}`}>{num(l.provisionalTotal)}</bdi></td><td><bdi dir="ltr">{num(l.provisionalUnitCost)}</bdi></td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p data-testid="neg-approvals">{t("neg.approvals")}: {t("neg.approvals_n", { n: String(x.validApprovals), m: String(x.approvalsRequired) })}</p>
      {data.canApprove && x.enabled && plan.needed && (
        <div className="fields" data-testid="neg-approve-form">
          {needsManual.map((l) => (
            <div key={l.lineNo} className="fields">
              <Field id={`neg-amount-${l.lineNo}`} label={`${t("neg.manual_total")} #${l.lineNo}`} error={errors[`amount-${l.lineNo}`]}>
                {(aria) => <input {...aria} inputMode="decimal" dir="ltr" value={manual[l.lineNo]?.amount ?? ""} onChange={(e) => setManual((m) => ({ ...m, [l.lineNo]: { amount: e.target.value, justification: m[l.lineNo]?.justification ?? "" } }))} />}
              </Field>
              <Field id={`neg-just-${l.lineNo}`} label={`${t("neg.manual_justification")} #${l.lineNo}`} error={errors[`just-${l.lineNo}`]}>
                {(aria) => <input {...aria} maxLength={500} value={manual[l.lineNo]?.justification ?? ""} onChange={(e) => setManual((m) => ({ ...m, [l.lineNo]: { amount: m[l.lineNo]?.amount ?? "", justification: e.target.value } }))} />}
              </Field>
            </div>
          ))}
          <Field id="neg-reason" label={t("neg.reason")} error={errors.reason}>{(aria) => <input {...aria} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />}</Field>
          <div className="actions"><button type="button" className="primary" disabled={busy} onClick={() => void approve()}>{busy ? t("loading") : t("neg.approve")}</button></div>
        </div>
      )}
    </section>
  );
}
