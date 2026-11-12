import { ApiError, type CurrenciesResponse, type CurrencyReference, type EntitlementsResponse } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, ConfirmDialog, errorText, Field, Loading, PageHeader } from "../components";
import { currencyName } from "../countries";

type Pending = { code: string; enabled: boolean; isBase: boolean; revision: number; kind: "base" | "disable" };

export function CurrenciesPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const canEdit = auth.can("company.currencies.update");
  const [data, setData] = useState<CurrenciesResponse | null>(null);
  const [reference, setReference] = useState<CurrencyReference[]>([]);
  const [entitlements, setEntitlements] = useState<EntitlementsResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const [baseChoice, setBaseChoice] = useState("");
  const [extraChoice, setExtraChoice] = useState("");
  const [pending, setPending] = useState<Pending | null>(null);

  const load = useCallback(async () => {
    try {
      const [c, r] = await Promise.all([auth.api.getCurrencies(), auth.api.getCurrencyReference()]);
      setData(c);
      setReference(r.currencies.filter((x) => x.active));
      setLoadError(null);
      try { setEntitlements(await auth.api.getEntitlements()); } catch { /* informational only (needs settings.view) */ }
    } catch (e) {
      if (!auth.handleError(e)) setLoadError(errorText(t, e));
    }
  }, [auth.api, auth.handleError, t]);
  useEffect(() => { void load(); }, [load]);

  if (loadError) return <Banner kind="error">{loadError}</Banner>;
  if (!data) return <Loading />;

  const nameOf = (code: string, nameEn: string) => `${currencyName(code, nameEn, locale)} (${code})`;
  const revisionOf = (code: string) => data.currencies.find((c) => c.code === code)?.revision ?? 0;
  const trialSingle = entitlements?.restrictions.find((r) => r.key === "multi_currency" && r.enforced)?.limit === 1;

  async function apply(p: Pending) {
    setBusy(true);
    setError(null);
    setConflict(false);
    setSaved(false);
    try {
      setData(await auth.api.setCurrency(p.code, { enabled: p.enabled, isBase: p.isBase }, p.revision));
      setSaved(true);
      setBaseChoice("");
      setExtraChoice("");
    } catch (err) {
      if (auth.handleError(err)) return;
      if (err instanceof ApiError && err.status === 412) setConflict(true);
      else setError(errorText(t, err));
    } finally {
      setBusy(false);
    }
  }

  function requestBase() {
    if (!baseChoice) return;
    const p: Pending = { code: baseChoice, enabled: true, isBase: true, revision: revisionOf(baseChoice), kind: "base" };
    if (data?.baseCurrencySelected) setPending(p); // replacing an established base currency is sensitive
    else void apply(p);
  }

  const enabledCodes = new Set(data.currencies.map((c) => c.code));
  const candidates = reference.filter((c) => !enabledCodes.has(c.code) || !data.currencies.find((x) => x.code === c.code)?.enabled);

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("currencies.title")} description={t("currencies.subtitle")} />
      {!data.baseCurrencySelected && <Banner kind="warning" testId="no-base">{t("currencies.base_required")}</Banner>}
      {/* Informational notices side by side (one row on wide windows). Each keeps its own live region and test id; nothing is hidden or collapsed. */}
      {(trialSingle || !data.baseCurrencyLock.checksImplemented) && (
        <div className="notices">
          {trialSingle && <Banner kind="info" testId="trial-note"><strong>{t("currencies.notice_trial")}</strong> {t("currencies.trial_note")}</Banner>}
          {!data.baseCurrencyLock.checksImplemented && <Banner kind="warning" testId="lock-note"><strong>{t("currencies.notice_lock")}</strong> {t("currencies.lock_unimplemented")}</Banner>}
        </div>
      )}
      {!canEdit && <Banner kind="info" testId="read-only">{t("form.read_only")}</Banner>}
      {conflict && (
        <Banner kind="error" testId="conflict">
          {t("form.conflict")} <button type="button" onClick={() => { setConflict(false); void load(); }}>{t("form.reload_latest")}</button>
        </Banner>
      )}
      {error && <Banner kind="error">{error}</Banner>}
      {saved && <Banner kind="success" testId="saved">{t("form.saved")}</Banner>}

      <Card title={t("currencies.base")}>
        <div className="base-current">
          <span className="base-label">{t("currencies.current_base")}</span>
          <p data-testid="base-currency" className="base-value">
            <bdi>
              {data.baseCurrency
                ? nameOf(data.baseCurrency, data.currencies.find((c) => c.isBase)?.nameEn ?? data.baseCurrency)
                : t("currencies.base_none")}
            </bdi>
          </p>
          {data.baseCurrency && <span className="chip chip-base">{t("currencies.is_base")}</span>}
        </div>
        {canEdit && (
          <div className="change-base">
            {/* A separate, explicitly labelled action: the control never reads as the current value. */}
            {data.baseCurrencySelected && (
              <>
                <h3 className="subtitle">{t("currencies.change_base_heading")}</h3>
                <p className="hint">{t("currencies.change_base_hint")}</p>
              </>
            )}
            <div className="inline-form">
              <Field id="c-base" label={data.baseCurrencySelected ? t("currencies.change_base_to") : t("currencies.choose_base")}>
                {(aria) => (
                  <select {...aria} value={baseChoice} onChange={(e) => setBaseChoice(e.target.value)}>
                    <option value="">{data.baseCurrencySelected ? t("currencies.change_placeholder") : t("form.select")}</option>
                    {reference.filter((c) => c.code !== data.baseCurrency).map((c) => <option key={c.code} value={c.code}>{nameOf(c.code, c.nameEn)}</option>)}
                  </select>
                )}
              </Field>
              <button type="button" className={data.baseCurrencySelected ? "" : "primary"} disabled={!baseChoice || busy} onClick={requestBase}>
                {data.baseCurrencySelected ? t("currencies.change_base") : t("currencies.set_base")}
              </button>
            </div>
          </div>
        )}
      </Card>

      <Card title={t("currencies.enabled")}>
      <div className="table-wrap">
      <table className="data-table">
        <caption className="visually-hidden">{t("currencies.enabled")}</caption>
        <thead><tr><th scope="col">{t("currencies.currency")}</th><th scope="col">{t("currencies.status")}</th><th scope="col">{t("currencies.actions")}</th></tr></thead>
        <tbody>
          {data.currencies.length === 0 && <tr><td colSpan={3}>{t("currencies.none")}</td></tr>}
          {data.currencies.map((c) => (
            <tr key={c.code}>
              <td><bdi>{nameOf(c.code, c.nameEn)}</bdi></td>
              <td><span className={`chip ${c.isBase ? "chip-base" : c.enabled ? "chip-ok" : "chip-off"}`}>{c.isBase ? t("currencies.is_base") : c.enabled ? t("currencies.enabled_status") : t("currencies.disabled_status")}</span></td>
              <td>
                {canEdit && !c.isBase && c.enabled && (
                  <button type="button" disabled={busy} onClick={() => setPending({ code: c.code, enabled: false, isBase: false, revision: c.revision, kind: "disable" })}>{t("currencies.disable")}</button>
                )}
                {canEdit && !c.isBase && !c.enabled && (
                  <button type="button" disabled={busy} onClick={() => void apply({ code: c.code, enabled: true, isBase: false, revision: c.revision, kind: "base" })}>{t("currencies.enable")}</button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>

      {canEdit && data.baseCurrencySelected && (
        <div className="inline-form">
          <Field id="c-extra" label={t("currencies.add")}>
            {(aria) => (
              <select {...aria} value={extraChoice} onChange={(e) => setExtraChoice(e.target.value)}>
                <option value="">{t("form.select")}</option>
                {candidates.map((c) => <option key={c.code} value={c.code}>{nameOf(c.code, c.nameEn)}</option>)}
              </select>
            )}
          </Field>
          <button type="button" disabled={!extraChoice || busy} onClick={() => void apply({ code: extraChoice, enabled: true, isBase: false, revision: revisionOf(extraChoice), kind: "base" })}>{t("currencies.enable")}</button>
        </div>
      )}
      </Card>

      {pending && (
        <ConfirmDialog
          title={pending.kind === "base" ? t("currencies.confirm_base_title") : t("currencies.confirm_disable_title")}
          message={pending.kind === "base" ? t("currencies.confirm_base_message", { code: pending.code }) : t("currencies.confirm_disable_message", { code: pending.code })}
          confirmLabel={t("form.confirm")}
          onCancel={() => setPending(null)}
          onConfirm={() => { const p = pending; setPending(null); void apply(p); }}
        />
      )}
    </section>
  );
}
