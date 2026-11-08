import { createApiClient, type ApiClient } from "@hasm/api-client";
import { LanguageSwitcher, useI18n } from "@hasm/ui";
import { useEffect, useMemo, useState } from "react";
import logoUrl from "../../../../../assets/branding/hasm-logo.png";
import { apiBaseUrl } from "./config";

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
    <main style={{ padding: "2rem", fontFamily: "system-ui, 'Segoe UI', Tahoma, sans-serif" }}>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <h1 style={{ margin: 0 }}>
          {/* Official approved logo, unmodified; width/height keep its original 449x204 proportions. */}
          <img src={logoUrl} alt={t("appName")} width={449} height={204} style={{ maxWidth: "100%", height: "auto" }} />
        </h1>
        <LanguageSwitcher />
      </header>
      <p>{t("tagline")}</p>
      <p>
        {t("backendStatus")}: <strong data-testid="backend-status">{t(status)}</strong>
      </p>
    </main>
  );
}
