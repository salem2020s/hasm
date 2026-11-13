import type { HistoryEntry } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Field, Loading, PageHeader } from "../components";
import { formatDateTime } from "../format";
import { useDisplayFormat } from "../useDisplayFormat";

const show = (v: unknown, none: string) => (v === null || v === undefined ? none : String(v));

export function HistoryPage() {
  const { t } = useI18n();
  const auth = useAuth();
  const [entity, setEntity] = useState("");
  const [entries, setEntries] = useState<HistoryEntry[] | null>(null);
  const [more, setMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Dates follow the company settings (date format, numerals, business time zone); defaults apply without settings.view. Display only.
  const { format } = useDisplayFormat();

  const load = useCallback(async (before?: number, append = false) => {
    try {
      const r = await auth.api.getHistory(entity || undefined, before);
      setEntries((cur) => (append && cur ? [...cur, ...r.entries] : r.entries));
      setMore(r.entries.length >= 50);
      setError(null);
    } catch (e) {
      if (!auth.handleError(e)) setError(errorText(t, e));
    }
  }, [auth.api, auth.handleError, entity, t]);
  useEffect(() => { setEntries(null); void load(); }, [load]);

  if (error) return <Banner kind="error">{error}</Banner>;
  const when = (iso: string) => formatDateTime(iso, format);

  return (
    <section aria-labelledby="page-title">
      <PageHeader title={t("history.title")} description={t("history.note")} />
      <Card>
      <div className="toolbar">
      <Field id="h-entity" label={t("history.filter")}>
        {(aria) => (
          <select {...aria} value={entity} onChange={(e) => setEntity(e.target.value)}>
            <option value="">{t("history.all")}</option>
            <option value="profile">{t("history.entity.profile")}</option>
            <option value="settings">{t("history.entity.settings")}</option>
            <option value="currency">{t("history.entity.currency")}</option>
          </select>
        )}
      </Field>
      <p className="hint toolbar-note">{t("history.time_note", { zone: format.timeZone ?? "UTC" })}</p>
      </div>
      {!entries ? <Loading /> : entries.length === 0 ? <Banner kind="info" testId="history-empty">{t("history.empty")}</Banner> : (
        <div className="table-wrap">
        <table className="data-table history-table">
          <caption className="visually-hidden">{t("history.title")}</caption>
          <thead><tr>
            <th scope="col">{t("history.col_when")}</th><th scope="col">{t("history.col_item")}</th><th scope="col">{t("history.col_changes")}</th>
          </tr></thead>
          <tbody>
            {entries.map((e) => (
              <tr key={e.id}>
                <td className="nowrap"><time dateTime={e.changedAtUtc} className="ltr-value">{when(e.changedAtUtc)}</time></td>
                <td>
                  <strong>{t(`history.entity.${e.entity}`)}</strong>{e.key ? <> <bdi className="ltr-value">{e.key}</bdi></> : null}
                  <div className="hint">{t("history.revision", { n: e.revision })}</div>
                </td>
                <td>
                  <ul className="change-list">
                    {Object.entries(e.changes).map(([field, c]) => (
                      <li key={field}>
                        <span className="change-field">{t(`history.field.${field}`)}</span>:{" "}
                        {c.changed ? <span className="hint">{t("history.changed")}</span> : <><bdi>{show(c.old, t("history.empty_value"))}</bdi> → <bdi>{show(c.new, t("history.empty_value"))}</bdi></>}
                      </li>
                    ))}
                  </ul>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        </div>
      )}
      {more && entries && <div className="actions"><button type="button" onClick={() => void load(entries[entries.length - 1].id, true)}>{t("history.more")}</button></div>}
      </Card>
    </section>
  );
}
