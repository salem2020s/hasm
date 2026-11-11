import type { HistoryEntry } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../auth";
import { Banner, errorText, Field, Loading } from "../components";

const show = (v: unknown, none: string) => (v === null || v === undefined ? none : String(v));

export function HistoryPage() {
  const { t, locale } = useI18n();
  const auth = useAuth();
  const [entity, setEntity] = useState("");
  const [entries, setEntries] = useState<HistoryEntry[] | null>(null);
  const [more, setMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

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
  const when = (iso: string) => new Date(iso).toLocaleString(locale === "ar" ? "ar-u-nu-latn" : "en");

  return (
    <section aria-labelledby="page-title">
      <h1 id="page-title">{t("history.title")}</h1>
      <p className="hint">{t("history.note")}</p>
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
      {!entries ? <Loading /> : entries.length === 0 ? <Banner kind="info" testId="history-empty">{t("history.empty")}</Banner> : (
        <ol className="history">
          {entries.map((e) => (
            <li key={e.id}>
              <p>
                <strong>{t(`history.entity.${e.entity}`)}{e.key ? ` ${e.key}` : ""}</strong> · {t("history.revision", { n: e.revision })} · <time dateTime={e.changedAtUtc}>{when(e.changedAtUtc)}</time>
              </p>
              <ul>
                {Object.entries(e.changes).map(([field, c]) => (
                  <li key={field}>
                    <span className="change-field">{t(`history.field.${field}`)}</span>:{" "}
                    {c.changed ? t("history.changed") : <>{show(c.old, t("history.empty_value"))} → {show(c.new, t("history.empty_value"))}</>}
                  </li>
                ))}
              </ul>
            </li>
          ))}
        </ol>
      )}
      {more && entries && <button type="button" onClick={() => void load(entries[entries.length - 1].id, true)}>{t("history.more")}</button>}
    </section>
  );
}
