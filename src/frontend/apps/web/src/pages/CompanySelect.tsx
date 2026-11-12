import { AddCompanyIcon, CompanyIcon, useI18n } from "@hasm/ui";
import { useState } from "react";
import { displayCompanyName, useAuth } from "../auth";
import { Banner, errorText } from "../components";

/**
 * "Add New Company": VISIBLE so the capability and its license limits are communicated, but NOT operational. Company creation is not
 * implemented (no endpoint, no provisioning), so this control does nothing and says so. It is aria-disabled (not disabled) so keyboard and
 * screen-reader users can reach it and hear why. Real creation must enforce license limits, authorization and safe provisioning on the
 * backend (docs/COMPANY-CREATION-REQUIREMENTS.md); this UI is never the enforcement.
 * License information is company-scoped in the API and no company is selected here, so no edition or limit is read or assumed beyond the
 * documented Trial rule stated in the text.
 */
function AddCompanyAction({ existingCount }: { existingCount: number }) {
  const { t } = useI18n();
  return (
    <div className="add-company" data-testid="add-company">
      <button type="button" className="add-company-btn" aria-disabled="true" aria-describedby="add-company-note" onClick={(e) => e.preventDefault()}>
        <span className="company-badge" aria-hidden="true"><AddCompanyIcon /></span>
        <span className="add-company-label">{t("companies.add_new")}</span>
        <span className="chip chip-off">{t("companies.add_unavailable_tag")}</span>
      </button>
      <div id="add-company-note" className="add-company-note">
        <p className="hint">{t("companies.add_not_implemented")}</p>
        <p className="hint">{existingCount > 0 ? t("companies.add_license_trial") : t("companies.add_license_general")}</p>
      </div>
    </div>
  );
}

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
    <section aria-labelledby="page-title">
      <h1 id="page-title" className="auth-title">{t("companies.title")}</h1>
      <p className="page-description auth-sub">{t("companies.subtitle")}</p>
      {error && <Banner kind="error">{error}</Banner>}
      {auth.companies.length === 0 ? (
        <Banner kind="info" testId="no-companies">{t("companies.none")}</Banner>
      ) : (
        <ul className="company-list" aria-label={t("companies.title")}>
          {auth.companies.map((c, i) => (
            <li key={c.id}>
              <button type="button" className="company-choice" disabled={busyId !== null} onClick={() => choose(c.id)}>
                {/* Only the member's own companies are listed; a company whose name is not projected yet gets a neutral label, never an identifier. */}
                <span className="company-badge" aria-hidden="true"><CompanyIcon /></span>
                <bdi className="company-label">{displayCompanyName(c, locale) ?? t("companies.unnamed", { n: i + 1 })}</bdi>
              </button>
            </li>
          ))}
        </ul>
      )}
      <AddCompanyAction existingCount={auth.companies.length} />
      <button type="button" className="ghost block" onClick={() => auth.logout()}>{t("nav.logout")}</button>
    </section>
  );
}
