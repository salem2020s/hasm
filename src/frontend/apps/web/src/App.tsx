import { createApiClient, type ApiClient } from "@hasm/api-client";
import { LanguageSwitcher, useI18n } from "@hasm/ui";
import { useEffect, useMemo, useState } from "react";
// Official branding assets, used unmodified (see assets/branding/BRANDING.md).
import lockupUrl from "../../../../../assets/branding/hasm-lockup.png";
import markUrl from "../../../../../assets/branding/hasm-mark.png";
import { apiBaseUrl } from "./config";
import "./app.css";

type Status = "checking" | "healthy" | "unreachable";

export function App({ client }: { client?: ApiClient }) {
  const { t } = useI18n();
  const api = useMemo(() => client ?? createApiClient(apiBaseUrl), [client]);
  const [status, setStatus] = useState<Status>("checking");

  useEffect(() => {
    let active = true;
    api.getHealth().then(
      (h) => active && setStatus(h.status === "healthy" ? "healthy" : "unreachable"),
      () => active && setStatus("unreachable"),
    );
    return () => {
      active = false;
    };
  }, [api]);

  return (
    <>
      <header className="app-header">
        <div className="brand">
          {/* Decorative: the adjacent text carries the accessible name. Native 120x136 ratio is preserved. */}
          <img className="brand-mark" src={markUrl} alt="" width={120} height={136} />
          <span className="brand-name">{t("appName")}</span>
        </div>
        <LanguageSwitcher />
      </header>
      <main className="welcome">
        <img className="brand-lockup" src={lockupUrl} alt={t("appName")} width={323} height={145} />
        <p className="tagline">{t("tagline")}</p>
        <p>
          {t("backendStatus")}: <strong data-testid="backend-status">{t(status)}</strong>
        </p>
      </main>
    </>
  );
}
