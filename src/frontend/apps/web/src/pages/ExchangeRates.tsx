import type { Account, ExchangeRate, ExchangeRatesOverview, LedgerSettings } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { isDecimal, localName, toLatinDigits, useRunner } from "../ledgerCommon";
import { useReportUnsaved } from "../unsaved";

/**
 * Exchange rates (Phase 3.2B S2). A rate is the number of BASE-currency units for ONE unit of the foreign currency. Rates are entered manually, are append-only (a correction is
 * a new entry for the date; the earlier one stays in history) and a posted journal keeps its own copy of the rate it applied, so nothing here ever changes posted amounts.
 */

const today = () => new Date().toISOString().slice(0, 10);

function RateForm({ overview, onSaved }: { overview: ExchangeRatesOverview; onSaved: () => Promise<void> }) {
  const { t } = useI18n();
  const auth = useAuth();
  const runner = useRunner("fx.error.", onSaved);
  const first = overview.currencies[0]?.currencyCode ?? "";
  const [f, setF] = useState({ currency: first, rate: "", validFrom: today(), note: "" });
  const [touched, setTouched] = useState(false);
  const dirty = touched && (f.rate !== "" || f.note !== "");
  useReportUnsaved("fx-form", dirty, t("fx.enter"));
  const rate = toLatinDigits(f.rate.trim());
  const rateValid = isDecimal(rate, 10) && Number(rate) > 0;
  const current = overview.currencies.find((c) => c.currencyCode === f.currency);
  const set = (patch: Partial<typeof f>) => { setTouched(true); setF({ ...f, ...patch }); };

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!rateValid || !current || !f.validFrom) return;
    const saved = await runner.run(() => auth.api.enterExchangeRate({ currencyCode: f.currency, rate, validFrom: f.validFrom, note: f.note.trim() || null }, current.latestId), t("fx.saved"));
    if (saved) { setF({ ...f, rate: "", note: "" }); setTouched(false); }
  }

  return (
    <Card title={t("fx.enter")}>
      <form onSubmit={submit} noValidate data-testid="fx-form">
        <fieldset disabled={runner.busy}>
          <Field id="fx-currency" label={t("fx.currency")} required>
            {(a) => <select {...a} value={f.currency} onChange={(e) => set({ currency: e.target.value })}>{overview.currencies.map((c) => <option key={c.currencyCode} value={c.currencyCode}>{c.currencyCode}{c.nameEn ? ` — ${c.nameEn}` : ""}</option>)}</select>}
          </Field>
          <Field id="fx-rate" label={t("fx.rate")} required hint={t("fx.rate_hint").replace("{base}", overview.baseCurrency).replace("{foreign}", f.currency)} error={runner.fields.rate ?? (f.rate && !rateValid ? t("fx.rate_invalid") : undefined)}>
            {(a) => <input {...a} inputMode="decimal" dir="ltr" value={f.rate} onChange={(e) => set({ rate: e.target.value })} />}
          </Field>
          <Field id="fx-from" label={t("fx.valid_from")} required error={runner.fields.validFrom}>
            {(a) => <input {...a} type="date" value={f.validFrom} onChange={(e) => set({ validFrom: e.target.value })} />}
          </Field>
          <Field id="fx-note" label={t("fx.note")} error={runner.fields.note}>
            {(a) => <input {...a} maxLength={200} value={f.note} onChange={(e) => set({ note: e.target.value })} />}
          </Field>
          <button type="submit" className="primary" disabled={!rateValid || !f.validFrom || !current}>{t("fx.save")}</button>
        </fieldset>
        {runner.error && <Banner kind="error" testId="fx-error">{runner.error}</Banner>}
        {runner.conflict && <Banner kind="warning" testId="fx-conflict">{t("fx.conflict")}</Banner>}
        {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      </form>
    </Card>
  );
}

function RoundingSettings() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [settings, setSettings] = useState<LedgerSettings | null>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [value, setValue] = useState("");
  const load = useCallback(async () => {
    try {
      const s = await auth.api.getLedgerSettings();
      setSettings(s); setValue(s.roundingAccountId ?? "");
      if (auth.can("company.accounts.view")) setAccounts((await auth.api.listAccounts()).accounts.filter((a) => a.postable));
    } catch (e) { auth.handleError(e); }
  }, [auth.api]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);
  const runner = useRunner("fx.error.", load);
  if (!settings) return null;
  const canConfigure = auth.can("company.ledgersettings.configure");
  const dirty = value !== (settings.roundingAccountId ?? "");
  return (
    <Card title={t("lset.title")} hint={t("lset.hint")}>
      <form onSubmit={(e) => { e.preventDefault(); void runner.run(() => auth.api.putLedgerSettings(value || null, settings.revision), t("lset.saved")); }} data-testid="lset-form">
        <fieldset disabled={!canConfigure || runner.busy}>
          <Field id="lset-account" label={t("lset.rounding_account")}>
            {(a) => (
              <select {...a} value={value} onChange={(e) => setValue(e.target.value)}>
                <option value="">{t("lset.none")}</option>
                {accounts.map((x) => <option key={x.id} value={x.id}>{x.code} — {localName(x, locale)}</option>)}
              </select>
            )}
          </Field>
          {canConfigure && <button type="submit" className="primary" disabled={!dirty}>{t("lset.save")}</button>}
        </fieldset>
        {runner.error && <Banner kind="error">{runner.error}</Banner>}
        {runner.conflict && <Banner kind="warning">{t("common.conflict")}</Banner>}
        {runner.notice && <Banner kind="success">{runner.notice}</Banner>}
      </form>
    </Card>
  );
}

export function ExchangeRatesPage() {
  const { t } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [data, setData] = useState<ExchangeRatesOverview | null>(null);
  const [history, setHistory] = useState<ExchangeRate[]>([]);
  const [filter, setFilter] = useState("");
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setData(await auth.api.getExchangeRates());
      setHistory((await auth.api.getExchangeRateHistory(filter || undefined)).rates);
      setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(errorText(tRef.current, e, "fx.error.")); }
  }, [auth.api, auth.handleError, filter]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);

  if (loadError) return <Banner kind="error" testId="fx-load-error">{loadError}</Banner>;
  if (!data) return <Loading />;
  return (
    <>
      <PageHeader title={t("fx.title")} description={t("fx.subtitle").replace("{base}", data.baseCurrency)} />
      <Banner kind="info">{t("fx.direction_note").replace("{base}", data.baseCurrency)}</Banner>
      {data.currencies.length === 0 ? <Banner kind="warning" testId="fx-none">{t("fx.no_foreign")}</Banner> : (
        <Card title={t("fx.latest")}>
          <table className="table" data-testid="fx-table">
            <thead><tr><th>{t("fx.currency")}</th><th>{t("fx.rate")}</th><th>{t("fx.valid_from")}</th><th>{t("fx.entered")}</th></tr></thead>
            <tbody>
              {data.currencies.map((c) => (
                <tr key={c.currencyCode} data-testid={`fx-row-${c.currencyCode}`}>
                  <td><bdi dir="ltr">{c.currencyCode}</bdi></td>
                  <td><bdi dir="ltr">{c.latest?.rate ?? "—"}</bdi>{!c.latest && <span className="hint"> {t("fx.missing")}</span>}</td>
                  <td>{c.latest?.validFrom ?? "—"}</td>
                  <td>{c.latest ? new Date(c.latest.enteredAtUtc).toLocaleString() : "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
      {data.can.create && data.currencies.length > 0 && <RateForm overview={data} onSaved={load} />}
      {auth.can("company.ledgersettings.view") && <RoundingSettings />}
      <Card title={t("fx.history")}>
        <Field id="fx-filter" label={t("fx.filter_currency")}>
          {(a) => <select {...a} value={filter} onChange={(e) => setFilter(e.target.value)}><option value="">{t("common.all")}</option>{data.currencies.map((c) => <option key={c.currencyCode} value={c.currencyCode}>{c.currencyCode}</option>)}</select>}
        </Field>
        <table className="table" data-testid="fx-history">
          <thead><tr><th>{t("fx.currency")}</th><th>{t("fx.rate")}</th><th>{t("fx.valid_from")}</th><th>{t("fx.entered")}</th><th>{t("fx.note")}</th></tr></thead>
          <tbody>
            {history.map((r) => (
              <tr key={r.id}>
                <td><bdi dir="ltr">{r.currencyCode}</bdi></td><td><bdi dir="ltr">{r.rate}</bdi></td><td>{r.validFrom}</td>
                <td>{new Date(r.enteredAtUtc).toLocaleString()}</td><td>{r.note ?? ""}</td>
              </tr>
            ))}
            {history.length === 0 && <tr><td colSpan={5}>{t("fx.history_empty")}</td></tr>}
          </tbody>
        </table>
      </Card>
    </>
  );
}
