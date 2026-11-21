import type { CurrenciesResponse, EntitlementsResponse, HistoryEntry } from "@hasm/api-client";
import { AccountingIcon, CompanyIcon, ReportsIcon, SalesIcon, SettingsIcon, WarehouseIcon, useI18n } from "@hasm/ui";
import { useEffect, useState } from "react";
import { useAuth } from "../auth";

import { currencyName } from "../countries";
import { formatDateTime, formatNumber } from "../format";
import { navigate } from "../router";
import { useDisplayFormat } from "../useDisplayFormat";
import { DashboardCard, EmptyState, MetricCard, ModuleHero, QuickActionCard } from "../visual";

type Known = boolean | null; // null = not known (the user may not view that part, or it has not loaded)

/**
 * Dashboard. Shows ONLY real company information read from the existing APIs (and only what the user is authorised to see).
 * Future financial / inventory widgets are clearly identified empty states: no totals, quantities or revenue are ever invented here.
 */
export function HomePage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const { format, settings } = useDisplayFormat();
  const [profile, setProfile] = useState<Known>(null);
  const [currencies, setCurrencies] = useState<CurrenciesResponse | null>(null);
  const [entitlements, setEntitlements] = useState<EntitlementsResponse | null>(null);
  /** Recent configuration activity: undefined = not loaded / not permitted, null = unavailable (load failed), array = loaded. */
  const [activity, setActivity] = useState<HistoryEntry[] | null | undefined>(undefined);

  useEffect(() => {
    let live = true;
    (async () => {
      let p: Known = null, c: CurrenciesResponse | null = null, e: EntitlementsResponse | null = null;
      if (auth.can("company.profile.view")) try { p = (await auth.api.getProfile()).profile !== null; } catch (err) { auth.handleError(err); }
      if (auth.can("company.currencies.view")) try { c = await auth.api.getCurrencies(); } catch (err) { auth.handleError(err); }
      if (auth.can("company.settings.view")) try { e = await auth.api.getEntitlements(); } catch (err) { auth.handleError(err); }
      let a: HistoryEntry[] | null | undefined = undefined;
      // Real configuration activity only (the Configuration History API; sensitive fields come back as "changed" with no values).
      if (auth.can("company.config.view")) try { a = (await auth.api.getHistory()).entries.slice(0, 5); } catch (err) { auth.handleError(err); a = null; }
      if (live) { setProfile(p); setCurrencies(c); setEntitlements(e); setActivity(a); }
    })();
    return () => { live = false; };
  }, [auth.api, auth.handleError, auth.can]);

  const steps: { key: string; label: string; path: string; done: Known }[] = [
    { key: "profile", label: t("home.step_profile"), path: "/company/details", done: profile },
    { key: "timeZone", label: t("home.step_time_zone"), path: "/company/details/general", done: settings ? settings.setupComplete : null },
    { key: "baseCurrency", label: t("home.step_currency"), path: "/company/currencies", done: currencies ? currencies.baseCurrencySelected : null },
  ];
  const known = steps.filter((s) => s.done !== null);
  const doneCount = known.filter((s) => s.done).length;
  // The setup checklist is for configuring a new company: it disappears once every (known) step is complete, and is not shown when
  // nothing about setup is visible to this user.
  const setupIncomplete = known.length > 0 && doneCount < known.length;

  const baseName = currencies?.baseCurrency
    ? currencyName(currencies.baseCurrency, currencies.currencies.find((c) => c.isBase)?.nameEn ?? currencies.baseCurrency, locale) : null;
  const editionLabel = entitlements ? (t(`dash.edition.${entitlements.edition}`) === `dash.edition.${entitlements.edition}` ? entitlements.edition : t(`dash.edition.${entitlements.edition}`)) : null;

  const quick = [
    { perm: "company.profile.view|company.settings.view", path: "/company/details", icon: <CompanyIcon />, title: t("nav.details"), desc: t("dash.qa_details") },
    { perm: "company.currencies.view", path: "/company/currencies", icon: <AccountingIcon />, title: t("nav.currencies"), desc: t("dash.qa_currencies") },
    { perm: "company.config.view", path: "/company/history", icon: <ReportsIcon />, title: t("nav.history"), desc: t("dash.qa_history") },
  ].filter((q) => q.perm.split("|").some((p) => auth.can(p)));

  return (
    <section aria-labelledby="page-title">
      <ModuleHero module="dashboard" title={t("dash.hero_title")} description={t("tagline")} />

      <div className="metric-row">
        <MetricCard testId="metric-company" icon={<CompanyIcon />} label={t("dash.company")} value={<bdi>{auth.companyName ?? t("home.welcome")}</bdi>} />
        {setupIncomplete && (
          <MetricCard testId="metric-setup" tone="gold" icon={<SettingsIcon />} label={t("dash.setup_status")}
            value={<span className="ltr-value">{formatNumber(doneCount, format)} / {formatNumber(known.length, format)}</span>}
            hint={t("dash.setup_incomplete")} />
        )}
        {currencies && (
          <MetricCard testId="metric-base-currency" tone={currencies.baseCurrency ? "navy" : "gold"} icon={<AccountingIcon />} label={t("currencies.base")}
            value={currencies.baseCurrency ? <span className="ltr-value">{currencies.baseCurrency}</span> : t("dash.not_set")}
            hint={baseName ? <bdi>{baseName}</bdi> : undefined} />
        )}
        {editionLabel && <MetricCard testId="metric-edition" tone="neutral" icon={<SettingsIcon />} label={t("dash.edition")} value={editionLabel} />}
      </div>

      <div className="dash-grid">
        {setupIncomplete && (
          <DashboardCard title={t("home.setup")}>
            <div className="setup-progress" aria-hidden="true"><span style={{ width: `${Math.round((doneCount / known.length) * 100)}%` }} /></div>
            <ul className="checklist">
              {known.map((s) => (
                <li key={s.key} data-done={s.done}>
                  <span aria-hidden="true">{s.done ? "✓" : "○"}</span>{" "}
                  <button type="button" className="link" onClick={() => navigate(s.path)}>{s.label}</button>{" "}
                  <span className="visually-hidden">{s.done ? t("home.done") : t("home.todo")}</span>
                </li>
              ))}
            </ul>
          </DashboardCard>
        )}

        {/* Replaces the finished setup checklist: real configuration activity, only for users who may view it. */}
        {!setupIncomplete && activity !== undefined && (
          <DashboardCard title={t("dash.recent_activity")} icon={<ReportsIcon />}>
            {activity === null ? <p className="hint" data-testid="activity-unavailable">{t("dash.activity_unavailable")}</p>
              : activity.length === 0 ? <EmptyState title={t("dash.activity_empty")} />
              : (
                <ul className="activity-list" data-testid="recent-activity">
                  {activity.map((a) => (
                    <li key={a.id}>
                      <span className="activity-what">
                        <strong>{t(`history.entity.${a.entity}`)}</strong>{a.key ? <> <bdi className="ltr-value">{a.key}</bdi></> : null}
                        <span className="hint"> · {Object.keys(a.changes).map((f) => t(`history.field.${f}`)).join(t("list.separator"))}</span>
                      </span>
                      <time dateTime={a.changedAtUtc} className="ltr-value hint">{formatDateTime(a.changedAtUtc, format)}</time>
                    </li>
                  ))}
                </ul>
              )}
            {activity && activity.length > 0 && <button type="button" className="link" onClick={() => navigate("/company/history")}>{t("dash.view_all_activity")}</button>}
          </DashboardCard>
        )}

        <DashboardCard title={t("dash.quick_access")}>
          {quick.length === 0 ? <p className="hint">{t("dash.no_access")}</p> : (
            <div className="quick-grid">
              {quick.map((q) => <QuickActionCard key={q.path} icon={q.icon} title={q.title} description={q.desc} onClick={() => navigate(q.path)} />)}
            </div>
          )}
        </DashboardCard>
      </div>

      {/* Future widgets: identified empty states only. Real figures will appear when the modules exist. */}
      <div className="dash-grid" data-testid="future-widgets">
        <DashboardCard title={t("dash.widget_sales")} icon={<SalesIcon />}>
          <EmptyState tag={t("dash.not_available")} title={t("dash.widget_empty_title")} description={t("dash.widget_empty_text")} />
        </DashboardCard>
        <DashboardCard title={t("dash.widget_inventory")} icon={<WarehouseIcon />}>
          <EmptyState tag={t("dash.not_available")} title={t("dash.widget_empty_title")} description={t("dash.widget_empty_text")} />
        </DashboardCard>
      </div>
    </section>
  );
}
