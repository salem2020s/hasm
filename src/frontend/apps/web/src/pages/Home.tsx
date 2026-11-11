import { useI18n } from "@hasm/ui";
import { useEffect, useState } from "react";
import { useAuth } from "../auth";
import { Banner } from "../components";
import { navigate } from "../router";

interface Setup { profile: boolean | null; timeZone: boolean | null; baseCurrency: boolean | null }

/** Landing page with the company setup state read from the real APIs (a check the user may not view simply stays unknown). */
export function HomePage() {
  const { t } = useI18n();
  const auth = useAuth();
  const [setup, setSetup] = useState<Setup>({ profile: null, timeZone: null, baseCurrency: null });

  useEffect(() => {
    let live = true;
    (async () => {
      const next: Setup = { profile: null, timeZone: null, baseCurrency: null };
      if (auth.can("company.profile.view")) try { next.profile = (await auth.api.getProfile()).profile !== null; } catch (e) { auth.handleError(e); }
      if (auth.can("company.settings.view")) try { next.timeZone = (await auth.api.getSettings()).setupComplete; } catch (e) { auth.handleError(e); }
      if (auth.can("company.currencies.view")) try { next.baseCurrency = (await auth.api.getCurrencies()).baseCurrencySelected; } catch (e) { auth.handleError(e); }
      if (live) setSetup(next);
    })();
    return () => { live = false; };
  }, [auth.api, auth.handleError, auth.can]);

  const items: { key: keyof Setup; label: string; path: string }[] = [
    { key: "profile", label: t("home.step_profile"), path: "/company/profile" },
    { key: "timeZone", label: t("home.step_time_zone"), path: "/company/settings" },
    { key: "baseCurrency", label: t("home.step_currency"), path: "/company/currencies" },
  ];

  return (
    <section aria-labelledby="page-title">
      <h1 id="page-title">{auth.companyName ?? t("home.welcome")}</h1>
      <p>{t("tagline")}</p>
      <h2>{t("home.setup")}</h2>
      {Object.values(setup).every((v) => v === true) ? <Banner kind="success">{t("home.setup_done")}</Banner> : null}
      <ul className="checklist">
        {items.map((i) => setup[i.key] === null ? null : (
          <li key={i.key} data-done={setup[i.key]}>
            <span aria-hidden="true">{setup[i.key] ? "✓" : "○"}</span>{" "}
            <button type="button" className="link" onClick={() => navigate(i.path)}>{i.label}</button>{" "}
            <span className="visually-hidden">{setup[i.key] ? t("home.done") : t("home.todo")}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}
