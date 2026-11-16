import { type StockBalanceItem, type StockBalancePage, type StockLedgerPage, type StockReconciliation, type StockWarehouseRef } from "@hasm/api-client";
import { Tabs, useI18n, type TabItem } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { invName } from "./inventoryCommon";

/**
 * Stock balances, item ledger and the reconciliation with the General Ledger (Phase 3.8A): ONE read-only screen for Web and Desktop. It never writes. Every number comes from the costing ledger (the exact,
 * append-only entries); the server applies the warehouse-scoped permission INSIDE the queries, so a warehouse the user may not see contributes to no row, count or total. An as-of date shows the history to that date,
 * never today's stock. Tabs: balances, item ledger, reconciliation; the filters of a tab survive switching tabs.
 */
const PAGE = 25;
/** The user's LOCAL calendar date (the UTC date is another day for part of the day). */
const todayIso = () => { const d = new Date(); const p = (n: number) => String(n).padStart(2, "0"); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };
const num = (n: number) => String(Number(n.toFixed(10)));
const wh = (w: { warehouseCode: string; warehouseNameAr: string | null; warehouseNameEn: string | null }, locale: string) => `${w.warehouseCode} — ${(locale === "en" ? w.warehouseNameEn ?? w.warehouseNameAr : w.warehouseNameAr ?? w.warehouseNameEn) ?? ""}`;

export function StockBalancesPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [warehouses, setWarehouses] = useState<StockWarehouseRef[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState("balances");
  // balances tab
  const [search, setSearch] = useState("");
  const [warehouse, setWarehouse] = useState("");
  const [asOf, setAsOf] = useState("");
  const [zero, setZero] = useState(false);
  const [offset, setOffset] = useState(0);
  const [page, setPage] = useState<StockBalancePage | null>(null);
  const [balError, setBalError] = useState<string | null>(null);
  // ledger tab
  const [itemTerm, setItemTerm] = useState("");
  const [itemResults, setItemResults] = useState<StockBalanceItem[]>([]);
  const [item, setItem] = useState<StockBalanceItem | null>(null);
  const [ledgerWh, setLedgerWh] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [ledger, setLedger] = useState<StockLedgerPage | null>(null);
  const [ledgerError, setLedgerError] = useState<string | null>(null);
  // reconciliation tab
  const [recDate, setRecDate] = useState(todayIso());
  const [rec, setRec] = useState<StockReconciliation | null>(null);
  const [recError, setRecError] = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      try { setWarehouses((await auth.api.listStockBalanceWarehouses()).warehouses); }
      catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "stockbal.error.")); }
    })();
  }, [auth.api, auth.handleError]);

  const loadBalances = useCallback(async () => {
    try {
      setPage(await auth.api.listStockBalances({ search: search.trim() || undefined, warehouseId: warehouse || undefined, asOf: asOf || undefined, includeZero: zero, offset, limit: PAGE }));
      setBalError(null);
    } catch (e) { if (!auth.handleError(e)) setBalError(errorText(tRef.current, e, "stockbal.error.")); }
  }, [auth.api, auth.handleError, search, warehouse, asOf, zero, offset]);
  useEffect(() => { const h = setTimeout(() => void loadBalances(), 250); return () => clearTimeout(h); }, [loadBalances]);

  useEffect(() => {
    if (!itemTerm.trim()) { setItemResults([]); return; }
    const h = setTimeout(() => { void auth.api.searchStockBalanceItems(itemTerm.trim()).then((r) => setItemResults(r.items)).catch(() => setItemResults([])); }, 250);
    return () => clearTimeout(h);
  }, [itemTerm, auth.api]);

  async function openLedger(itemId: string, warehouseId: string, code: string, nameAr: string | null, nameEn: string | null) {
    setItem({ id: itemId, code, nameAr, nameEn }); setLedgerWh(warehouseId); setTab("ledger");
    await runLedger(itemId, warehouseId);
  }
  async function runLedger(itemId: string, warehouseId: string) {
    try { setLedger(await auth.api.getStockLedger({ itemId, warehouseId, from: from || undefined, to: to || undefined, limit: 200 })); setLedgerError(null); }
    catch (e) { setLedger(null); if (!auth.handleError(e)) setLedgerError(errorText(tRef.current, e, "stockbal.error.")); }
  }
  async function runRec() {
    try { setRec(await auth.api.getStockReconciliation(recDate)); setRecError(null); }
    catch (e) { setRec(null); if (!auth.handleError(e)) setRecError(errorText(tRef.current, e, "stockbal.error.")); }
  }

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!warehouses) return <Loading />;

  const tabs: TabItem[] = [
    {
      id: "balances", label: t("stockbal.tab.balances"),
      content: (
        <div>
          <div className="toolbar">
            <Field id="sb-search" label={t("stockbal.search")} hint={t("stockbal.search_hint")}>{(aria) => <input {...aria} type="search" value={search} onChange={(e) => { setOffset(0); setSearch(e.target.value); }} autoComplete="off" />}</Field>
            <Field id="sb-wh" label={t("stock.warehouse")}>
              {(aria) => <select {...aria} value={warehouse} onChange={(e) => { setOffset(0); setWarehouse(e.target.value); }}><option value="">{t("inv.filter_all")}</option>{warehouses.map((w) => <option key={w.id} value={w.id}>{`${w.code} — ${invName(w, locale)}`}</option>)}</select>}
            </Field>
            <Field id="sb-asof" label={t("stockbal.as_of")} hint={t("stockbal.as_of_hint")}>{(aria) => <input {...aria} type="date" value={asOf} onChange={(e) => { setOffset(0); setAsOf(e.target.value); }} dir="ltr" />}</Field>
            <label className="check"><input type="checkbox" checked={zero} onChange={(e) => { setOffset(0); setZero(e.target.checked); }} /> {t("stockbal.include_zero")}</label>
          </div>
          {balError && <Banner kind="error">{balError}</Banner>}
          {!page ? <Loading /> : (
            <>
              <div className="table-wrap">
                <table className="data-table">
                  <caption className="visually-hidden">{t("stockbal.tab.balances")}</caption>
                  <thead><tr><th scope="col">{t("stock.item")}</th><th scope="col">{t("stock.warehouse")}</th><th scope="col">{t("stockbal.quantity")}</th><th scope="col">{t("stockbal.value")}</th><th scope="col">{t("stockbal.avg_cost")}</th><th scope="col">{t("stockbal.method")}</th><th scope="col">{t("inv.actions")}</th></tr></thead>
                  <tbody>
                    {page.balances.length === 0 && <tr><td colSpan={7}>{t("stockbal.none")}</td></tr>}
                    {page.balances.map((r) => (
                      <tr key={`${r.itemId}-${r.warehouseId}`} data-testid={`bal-${r.itemCode}-${r.warehouseCode}`}>
                        <td><bdi dir="ltr">{r.itemCode}</bdi> — <bdi>{invName({ code: r.itemCode, nameAr: r.itemNameAr, nameEn: r.itemNameEn }, locale)}</bdi></td>
                        <td><bdi>{wh(r, locale)}</bdi></td>
                        <td className="num"><bdi dir="ltr">{num(r.quantity)} {r.baseUnitCode}</bdi></td>
                        <td className="num"><bdi dir="ltr">{num(r.value)}</bdi></td>
                        <td className="num"><bdi dir="ltr">{num(r.averageUnitCost)}</bdi></td>
                        <td>{t(`stockbal.method.${r.method}`)}</td>
                        <td><button type="button" onClick={() => void openLedger(r.itemId, r.warehouseId, r.itemCode, r.itemNameAr, r.itemNameEn)}>{t("stockbal.open_ledger")}</button></td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot><tr><th scope="row" colSpan={3}>{t("stockbal.total_value")}</th><td className="num" data-testid="total-value"><bdi dir="ltr">{num(page.totalValue)}</bdi></td><td colSpan={3} /></tr></tfoot>
                </table>
              </div>
              <div className="toolbar" data-testid="paging">
                <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>{t("inv.items.previous")}</button>
                <span aria-live="polite">{t("inv.items.paging", { from: String(page.total === 0 ? 0 : offset + 1), to: String(offset + page.balances.length), total: String(page.total) })}</span>
                <button type="button" disabled={offset + PAGE >= page.total} onClick={() => setOffset(offset + PAGE)}>{t("inv.items.next")}</button>
              </div>
            </>
          )}
        </div>
      ),
    },
    {
      id: "ledger", label: t("stockbal.tab.ledger"),
      content: (
        <div>
          <p className="hint">{t("stockbal.ledger_hint")}</p>
          <div className="toolbar">
            <Field id="sl-item" label={t("stock.find_item")}>{(aria) => <input {...aria} type="search" value={itemTerm} dir="auto" autoComplete="off" onChange={(e) => setItemTerm(e.target.value)} />}</Field>
            {itemResults.length > 0 && (
              <select aria-label={t("stock.pick_item")} value="" onChange={(e) => { const it = itemResults.find((r) => r.id === e.target.value); if (it) { setItem(it); setItemTerm(""); setItemResults([]); } }}>
                <option value="">{t("form.select")}</option>{itemResults.map((r) => <option key={r.id} value={r.id}>{r.code} — {invName(r, locale)}</option>)}
              </select>
            )}
            <Field id="sl-wh" label={t("stock.warehouse")}>
              {(aria) => <select {...aria} value={ledgerWh} onChange={(e) => setLedgerWh(e.target.value)}><option value="">{t("form.select")}</option>{warehouses.map((w) => <option key={w.id} value={w.id}>{`${w.code} — ${invName(w, locale)}`}</option>)}</select>}
            </Field>
            <Field id="sl-from" label={t("stock.from")}>{(aria) => <input {...aria} type="date" value={from} onChange={(e) => setFrom(e.target.value)} dir="ltr" />}</Field>
            <Field id="sl-to" label={t("stock.to")}>{(aria) => <input {...aria} type="date" value={to} onChange={(e) => setTo(e.target.value)} dir="ltr" />}</Field>
            <button type="button" className="primary" disabled={!item || !ledgerWh} onClick={() => item && void runLedger(item.id, ledgerWh)}>{t("stockbal.show_ledger")}</button>
          </div>
          {item && <p data-testid="ledger-item"><bdi dir="ltr">{item.code}</bdi> — <bdi>{invName(item, locale)}</bdi></p>}
          {ledgerError && <Banner kind="error">{ledgerError}</Banner>}
          {ledger && ledger.total > ledger.rows.length && <Banner kind="error" testId="ledger-truncated">{t("stockbal.ledger_truncated", { shown: String(ledger.rows.length), total: String(ledger.total) })}</Banner>}
          {ledger && (
            <div className="table-wrap">
              <table className="data-table" data-testid="ledger-table">
                <caption className="visually-hidden">{t("stockbal.tab.ledger")}</caption>
                <thead><tr><th scope="col">{t("stock.date")}</th><th scope="col">{t("stockbal.kind")}</th><th scope="col">{t("stock.number")}</th><th scope="col">{t("stockbal.in")}</th><th scope="col">{t("stockbal.out")}</th><th scope="col">{t("stockbal.amount")}</th><th scope="col">{t("stockbal.quantity_after")}</th><th scope="col">{t("stockbal.value_after")}</th></tr></thead>
                <tbody>
                  <tr data-testid="ledger-opening"><th scope="row" colSpan={6}>{t("stockbal.opening")}</th><td className="num"><bdi dir="ltr">{num(ledger.openingQuantity)}</bdi></td><td className="num"><bdi dir="ltr">{num(ledger.openingValue)}</bdi></td></tr>
                  {ledger.rows.map((r) => (
                    <tr key={r.seq} data-testid={`ledger-row-${r.seq}`}>
                      <td><bdi dir="ltr">{r.effectiveDate}</bdi></td><td>{t(`stockbal.kind.${r.kind}`)}</td><td><bdi dir="ltr">{r.sourceNumber ?? ""}</bdi></td>
                      <td className="num"><bdi dir="ltr">{r.direction > 0 ? num(r.quantity) : ""}</bdi></td><td className="num"><bdi dir="ltr">{r.direction < 0 ? num(r.quantity) : ""}</bdi></td>
                      <td className="num"><bdi dir="ltr">{num(r.direction * r.amount)}</bdi></td><td className="num"><bdi dir="ltr">{num(r.quantityAfter)}</bdi></td><td className="num"><bdi dir="ltr">{num(r.valueAfter)}</bdi></td>
                    </tr>
                  ))}
                  <tr data-testid="ledger-closing"><th scope="row" colSpan={6}>{t("stockbal.closing")}</th><td className="num"><bdi dir="ltr">{num(ledger.closingQuantity)}</bdi></td><td className="num"><bdi dir="ltr">{num(ledger.closingValue)}</bdi></td></tr>
                </tbody>
              </table>
            </div>
          )}
        </div>
      ),
    },
    {
      id: "reconciliation", label: t("stockbal.tab.reconciliation"),
      content: (
        <div>
          <p className="hint">{t("stockbal.rec_hint")}</p>
          <div className="toolbar">
            <Field id="sr-date" label={t("stockbal.as_of")}>{(aria) => <input {...aria} type="date" value={recDate} onChange={(e) => setRecDate(e.target.value)} dir="ltr" />}</Field>
            <button type="button" className="primary" disabled={!recDate} onClick={() => void runRec()}>{t("stockbal.run_reconciliation")}</button>
          </div>
          {recError && <Banner kind="error" testId="rec-error">{recError}</Banner>}
          {rec && (
            <div data-testid="reconciliation">
              <dl className="details">
                <dt>{t("stockbal.costing_value")}</dt><dd><bdi dir="ltr" data-testid="rec-costing">{num(rec.costingValue)}</bdi></dd>
                <dt>{t("stockbal.ledger_value")}</dt><dd><bdi dir="ltr" data-testid="rec-ledger">{num(rec.ledgerValue)}</bdi></dd>
                <dt>{t("stockbal.difference")}</dt><dd><bdi dir="ltr" data-testid="rec-difference">{num(rec.difference)}</bdi></dd>
              </dl>
              <Banner kind={rec.difference === 0 ? "success" : "error"} testId="rec-verdict">{rec.difference === 0 ? t("stockbal.rec_ok") : t("stockbal.rec_difference")}</Banner>
              <div className="table-wrap">
                <table className="data-table">
                  <caption className="visually-hidden">{t("stockbal.tab.reconciliation")}</caption>
                  <thead><tr><th scope="col">{t("stockbal.account")}</th><th scope="col">{t("stockbal.ledger_value")}</th></tr></thead>
                  <tbody>{rec.accounts.map((a) => <tr key={a.accountId}><td><bdi dir="ltr">{a.code}</bdi> — <bdi>{invName({ code: a.code, nameAr: a.nameAr, nameEn: a.nameEn }, locale)}</bdi></td><td className="num"><bdi dir="ltr">{num(a.ledgerBalance)}</bdi></td></tr>)}</tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      ),
    },
  ];

  return (
    <section aria-labelledby="page-title" data-testid="stock-balances">
      <PageHeader title={t("stockbal.title")} description={t("stockbal.subtitle")} />
      <Card title={t("stockbal.title")}>
        <Tabs label={t("stockbal.tabs_label")} tabs={tabs} value={tab} onChange={setTab} testId="stockbal-tabs" />
      </Card>
    </section>
  );
}
