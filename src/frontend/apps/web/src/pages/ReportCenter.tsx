import type { ReportDefinitionInfo } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Loading, PageHeader } from "../components";
import { navigate } from "../router";

/**
 * Report center (Phase 3.3 S6): discovery of the reports the signed-in user may view, from the server's stable report definitions (the same definitions the future visual Report Designer will build on). Each card
 * shows the report, its parameters, the export formats the server offers and whether this user may export; opening it goes to the one shared screen of that report. The list is decided by the server (permissions),
 * never by this screen. The visual Report Designer itself is a later deliverable.
 */

const SCREEN: Record<string, string> = { "trial-balance": "/accounting/reports", "general-ledger": "/accounting/reports", "profit-and-loss": "/accounting/statements", "balance-sheet": "/accounting/statements", "trading-account": "/accounting/statements" };

export function ReportCenterPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const [reports, setReports] = useState<ReportDefinitionInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      try { setReports((await auth.api.getReportDefinitions()).reports); }
      catch (e) { if (!auth.handleError(e)) setError(errorText(tRef.current, e, "rep.error.")); }
    })();
  }, [auth.api]); // eslint-disable-line react-hooks/exhaustive-deps

  if (error) return <Banner kind="error" testId="rc-error">{error}</Banner>;
  if (!reports) return <Loading />;
  return (
    <>
      <PageHeader title={t("rc.title")} description={t("rc.subtitle")} />
      {reports.length === 0 ? <Banner kind="info" testId="rc-none">{t("rc.none")}</Banner> : (
        <div className="cards" data-testid="rc-list">
          {reports.map((r) => (
            <Card key={r.id} title={locale === "en" ? r.titleEn : r.titleAr}>
              <div data-testid={`rc-${r.id}`}>
                <p className="hint">{t("rc.formats")}: {r.formats.map((f) => f.toUpperCase()).join(" · ")} — {r.canExport ? t("rc.can_export") : t("rc.cannot_export")}</p>
                <p className="hint">{t("rc.parameters")}: {r.parameters.map((p) => (locale === "en" ? p.titleEn : p.titleAr) ?? p.name).join("، ")}</p>
                <p className="hint">{[r.supportsComparison ? t("rc.comparison") : null, r.supportsGrouping ? t("rc.grouping") : null].filter(Boolean).join(" · ")}</p>
                <button type="button" className="primary" onClick={() => navigate(SCREEN[r.id] ?? "/accounting/reports")}>{t("rc.open")}</button>
              </div>
            </Card>
          ))}
        </div>
      )}
      <Banner kind="info" testId="rc-designer-note">{t("rc.designer_note")}</Banner>
    </>
  );
}
