import { ApiError, type StockAdjustment } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Field, Loading } from "../components";
import { invName } from "./inventoryCommon";

/**
 * The adjustment of an approved stocktake (Phase 3.8B-2), shown in the Adjustment tab of the stocktake screen. ONE implementation for Web and Windows Desktop. The poster enters the value of every SURPLUS line
 * (like a receipt); shortages are costed by the server from stock. Posting and reversal follow the server's rules (warehouse-scoped permissions, open period, exactly once, accounting state); nothing is computed
 * or decided here beyond input validation and display.
 */
const AMOUNT = /^\d+(\.\d{1,6})?$/;
const num = (n: number | null | undefined) => (n === null || n === undefined ? "—" : String(Number(n.toFixed(10))));
const todayIso = () => { const d = new Date(); const p = (n: number) => String(n).padStart(2, "0"); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };

export function StockAdjustmentPanel({ id, canPost, canReverse, onChanged }: { id: string; canPost: boolean; canReverse: boolean; onChanged: (message: string) => void }) {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [adj, setAdj] = useState<StockAdjustment | null>(null);
  const [amounts, setAmounts] = useState<Record<number, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [revDate, setRevDate] = useState(todayIso());
  const [revReason, setRevReason] = useState("");

  const text = useCallback((e: unknown) => {
    if (e instanceof ApiError) {
      for (const k of [`stk.error.${e.code}`, `stock.error.${e.code}`]) if (t(k) !== k) return t(k);
      if (e.status === 403) return t("error.forbidden");
      return t("error.generic");
    }
    return t("error.network");
  }, [t]);
  const field = (code: string) => { for (const k of [`stk.field.${code}`, `stock.field.${code}`, `field.${code}`]) if (t(k) !== k) return t(k); return t("field.invalid"); };

  const load = useCallback(async () => {
    try { setAdj(await auth.api.getStockAdjustment(id)); }
    catch (e) { if (!auth.handleError(e)) setError(text(e)); }
  }, [auth.api, auth.handleError, id, text]);
  useEffect(() => { void load(); }, [load]);

  /** sent = the line numbers in the order they were submitted, so a server error on lines[i].amount or lines[i].lineNo lands on the input of that line. */
  function fail(e: unknown, sent: number[] = []) {
    if (auth.handleError(e)) return;
    if (e instanceof ApiError && e.status === 400) {
      const out: Record<string, string> = {};
      for (const f of e.details.fields ?? []) {
        const m = /^lines\[(\d+)\]\.(amount|lineNo)$/.exec(f.field);
        const lineNo = m ? sent[Number(m[1])] : undefined;
        out[lineNo !== undefined ? `amount-${lineNo}` : f.field] = field(f.code);
      }
      setErrors(out); setError(t("form.fix_errors"));
      return;
    }
    // Phase 3.8B-3 (R15): an inflow refused while a negative-stock obligation is open names the item(s) of the adjustment concerned
    if (e instanceof ApiError && e.status === 409 && e.code === "negative_obligation_open" && adj) {
      const codes = (e.details.fields ?? []).map((f) => /^lines\[(\d+)\]\.itemId$/.exec(f.field)).filter((m): m is RegExpExecArray => m !== null).map((m) => adj.lines[Number(m[1])]?.itemCode).filter((c): c is string => !!c);
      setError(codes.length > 0 ? `${text(e)} (${codes.join(", ")})` : text(e));
      return;
    }
    setError(text(e));
  }

  async function post() {
    if (!adj) return;
    const surplus = adj.lines.filter((l) => l.direction === 1);
    const v: Record<string, string> = {};
    surplus.forEach((l) => { const a = (amounts[l.lineNo] ?? "").trim(); if (!a) v[`amount-${l.lineNo}`] = t("field.required"); else if (!AMOUNT.test(a)) v[`amount-${l.lineNo}`] = t("field.invalid"); });
    setErrors(v);
    if (Object.keys(v).length > 0) { setError(t("form.fix_errors")); return; }
    setBusy(true); setError(null);
    try {
      setAdj(await auth.api.postStockAdjustment(adj.id, surplus.map((l) => ({ lineNo: l.lineNo, amount: Number(amounts[l.lineNo].trim()) }))));
      onChanged(t("stk.adj.posted"));
    } catch (e) { fail(e, surplus.map((l) => l.lineNo)); } finally { setBusy(false); }
  }

  async function reverse() {
    if (!adj) return;
    if (!revDate || !revReason.trim()) { setError(t("stock.reverse_required")); return; }
    setBusy(true); setError(null); setErrors({});
    try { setAdj(await auth.api.reverseStockAdjustment(adj.id, revDate, revReason.trim())); onChanged(t("stock.reversed")); }
    catch (e) { fail(e); } finally { setBusy(false); }
  }

  if (!adj) return error ? <Banner kind="error">{error}</Banner> : <Loading />;
  const p = adj.posting;
  const editable = !p && canPost;
  return (
    <div data-testid="stk-adjustment">
      {error && <Banner kind="error" testId="adj-error">{error}</Banner>}
      <div className="fields">
        <Field id="adj-number" label={t("stk.adj.number")}>{(aria) => <input {...aria} readOnly dir="ltr" value={adj.number} />}</Field>
        <Field id="adj-date" label={t("stk.adj.date")}>{(aria) => <input {...aria} readOnly dir="ltr" value={adj.adjustmentDate} />}</Field>
        <Field id="adj-state" label={t("stock.posting.state")}>{(aria) => <input {...aria} readOnly data-testid="adj-state" value={p ? t(`stock.posting.state.${p.accountingState}`) : t("stock.posting.not_executed")} />}</Field>
        {p && <Field id="adj-total" label={t("stock.posting.total")}>{(aria) => <input {...aria} readOnly dir="ltr" data-testid="adj-total" value={num(p.totalValue)} />}</Field>}
        {p?.journalStatus && <Field id="adj-journal" label={t("stock.posting.journal_status")}>{(aria) => <input {...aria} readOnly value={t(`stock.posting.journal_status.${p.journalStatus}`)} />}</Field>}
      </div>
      <p className="hint">{t("stk.adj.hint")}</p>
      <div className="table-wrap">
        <table className="data-table" data-testid="adj-lines">
          <caption className="visually-hidden">{t("stk.tab.adjustment")}</caption>
          <thead><tr><th scope="col">#</th><th scope="col">{t("stk.item")}</th><th scope="col">{t("stk.adj.type")}</th><th scope="col">{t("stk.adj.quantity")}</th><th scope="col">{t("stk.adj.counted")}</th><th scope="col">{t("stk.adj.book")}</th><th scope="col">{t("stk.reason")}</th><th scope="col">{t("stock.posting.amount")}</th></tr></thead>
          <tbody>
            {adj.lines.map((l) => (
              <tr key={l.lineNo} data-testid={`adj-line-${l.lineNo}`}>
                <td>{l.lineNo}</td>
                <td><bdi dir="ltr">{l.itemCode}</bdi> — <bdi>{invName({ nameAr: l.itemNameAr, nameEn: l.itemNameEn, code: l.itemCode }, locale)}</bdi></td>
                <td><span className={`chip ${l.direction < 0 ? "chip-off" : "chip-ok"}`}>{t(`stk.adj.type.${l.direction}`)}</span></td>
                <td><bdi dir="ltr">{num(l.quantity)} {l.unitCode}</bdi></td>
                <td><bdi dir="ltr">{num(l.countedQuantity)}</bdi></td>
                <td><bdi dir="ltr">{num(l.bookQuantity)}</bdi></td>
                <td><bdi>{l.reason ?? ""}</bdi></td>
                <td>
                  {l.amount !== null ? <bdi dir="ltr" data-testid={`adj-amount-${l.lineNo}`}>{num(l.amount)}</bdi>
                    : l.direction === 1 && editable ? (
                      <Field id={`adj-amt-${l.lineNo}`} label={t("stock.posting.amount")} error={errors[`amount-${l.lineNo}`]}>
                        {(aria) => <input {...aria} value={amounts[l.lineNo] ?? ""} inputMode="decimal" dir="ltr" onChange={(e) => setAmounts((a) => ({ ...a, [l.lineNo]: e.target.value }))} />}
                      </Field>)
                      : <span className="hint">{l.direction < 0 ? t("stock.posting.cost_computed") : "—"}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {errors.lines && <Banner kind="error">{errors.lines}</Banner>}
      {editable && <div className="actions"><button type="button" className="primary" data-testid="adj-post" disabled={busy} onClick={() => void post()}>{busy ? t("loading") : t("stk.adj.post")}</button></div>}
      {p?.reversal && <Banner kind="info" testId="adj-reversed">{`${t("stock.posting.reversed")}: ${p.reversal.effectiveDate} — ${p.reversal.reason}`}</Banner>}
      {p && !p.reversal && canReverse && (
        <div className="fields" data-testid="adj-reverse">
          <Field id="adj-rev-date" label={t("stock.reverse_date")} required error={errors.effectiveDate}>{(aria) => <input {...aria} type="date" dir="ltr" value={revDate} onChange={(e) => setRevDate(e.target.value)} />}</Field>
          <Field id="adj-rev-reason" label={t("stock.reverse_reason")} required error={errors.reason}>{(aria) => <input {...aria} value={revReason} maxLength={500} onChange={(e) => setRevReason(e.target.value)} />}</Field>
          <div className="actions"><button type="button" className="danger" data-testid="adj-reverse-button" disabled={busy} onClick={() => void reverse()}>{t("stock.reverse")}</button></div>
        </div>
      )}
    </div>
  );
}
