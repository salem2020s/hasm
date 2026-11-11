import { useI18n } from "@hasm/ui";
import { useState } from "react";
import { displayCompanyName, useAuth } from "../auth";
import { Banner, errorText } from "../components";

export function CompanySelectPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function choose(id: string) {
    setBusyId(id);
    setError(null);
    try {
      await auth.chooseCompany(id);
    } catch (e) {
      if (!auth.handleError(e)) setError(errorText(t, e));
    } finally {
      setBusyId(null);
    }
  }

  return (
    <main className="auth-card">
      <h1>{t("companies.title")}</h1>
      {error && <Banner kind="error">{error}</Banner>}
      {auth.companies.length === 0 ? (
        <Banner kind="info" testId="no-companies">{t("companies.none")}</Banner>
      ) : (
        <ul className="company-list" aria-label={t("companies.title")}>
          {auth.companies.map((c, i) => (
            <li key={c.id}>
              <button type="button" className="company-choice" disabled={busyId !== null} onClick={() => choose(c.id)}>
                {/* Only the member's own companies are listed; a company whose name is not projected yet gets a neutral label, never an identifier. */}
                {displayCompanyName(c, locale) ?? t("companies.unnamed", { n: i + 1 })}
              </button>
            </li>
          ))}
        </ul>
      )}
      <button type="button" onClick={() => auth.logout()}>{t("nav.logout")}</button>
    </main>
  );
}
