import { ApiError } from "@hasm/api-client";
import type { StatementMappingAccount, StatementMappingResponse, StatementMappingVersionInfo } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../auth";
import { Banner, Card, errorText, Loading, PageHeader } from "../components";
import { localName } from "../ledgerCommon";

/**
 * Statement classification (Phase 3.3 S3): which section of the financial statements each account is presented in. It is separate from the Chart of Accounts, versioned (every save creates a new immutable
 * version), audited, and needs its own permission. A group's section applies to everything below it unless a descendant has its own; an account with none is UNCLASSIFIED and is shown clearly (never dropped).
 * The server validates every entry (and PostgreSQL enforces which sections fit an account type); this screen only offers the allowed choices.
 */

const INHERIT = "";

function mappingError(t: (k: string) => string, e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 412) return t("sm.error.version_conflict");
    const f = e.details.fields?.[0]?.code;
    if (f) { const k = `sm.error.${f}`; if (t(k) !== k) return t(k); }
  }
  return errorText(t, e, "sm.error.");
}

export function StatementMappingPage() {
  const { t, locale } = useI18n();
  const tRef = useRef(t);
  tRef.current = t;
  const auth = useAuth();
  const canConfigure = auth.can("company.statementmapping.configure");
  const [data, setData] = useState<StatementMappingResponse | null>(null);
  const [versions, setVersions] = useState<StatementMappingVersionInfo[]>([]);
  const [own, setOwn] = useState<Record<string, string>>({});
  const [original, setOriginal] = useState<Record<string, string>>({});
  const [viewing, setViewing] = useState<number | null>(null);
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async (version?: number) => {
    try {
      const [m, v] = await Promise.all([auth.api.getStatementMapping(version), auth.api.listStatementMappingVersions()]);
      const map: Record<string, string> = {};
      for (const e of m.entries) map[e.accountId] = e.section;
      setData(m); setVersions(v.versions); setOwn(map); setOriginal(map); setViewing(version ?? null); setLoadError(null);
    } catch (e) { if (!auth.handleError(e)) setLoadError(mappingError(tRef.current, e)); }
  }, [auth.api, auth.handleError]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, [load]);

  const accounts = data?.accounts ?? [];
  const byId = useMemo(() => new Map(accounts.map((a) => [a.id, a])), [accounts]);
  const allowed = useMemo(() => {
    const out: Record<string, string[]> = {};
    for (const s of data?.sections ?? []) for (const ty of s.types) (out[ty] ??= []).push(s.key);
    return out;
  }, [data]);
  const depth = (a: StatementMappingAccount) => { let d = 0; let cur = a; while (cur.parentId && byId.has(cur.parentId) && d < 20) { cur = byId.get(cur.parentId)!; d++; } return d; };
  /** The section an account ends up in with the CURRENT (possibly unsaved) choices: its own entry or the nearest ancestor's that fits its type. */
  const effective = (a: StatementMappingAccount): string | null => {
    let cur: StatementMappingAccount | undefined = a; let guard = 0;
    while (cur && guard++ < 50) { const s = own[cur.id]; if (s && (allowed[a.type] ?? []).includes(s)) return s; cur = cur.parentId ? byId.get(cur.parentId) : undefined; }
    return null;
  };
  const dirty = JSON.stringify(Object.entries(own).sort()) !== JSON.stringify(Object.entries(original).sort());
  const unclassified = accounts.filter((a) => !a.isGroup && effective(a) === null).length;
  const current = viewing === null || viewing === versions[0]?.versionNo;
  const expected = versions[0]?.versionNo ?? 0;

  const run = async (action: () => Promise<void>) => {
    setBusy(true); setError(null); setNotice(null);
    try { await action(); } catch (e) { if (!auth.handleError(e)) { setError(mappingError(tRef.current, e)); if (e instanceof ApiError && e.status === 412) await load().catch(() => undefined); } } finally { setBusy(false); }
  };
  const loadTemplate = () => run(async () => {
    const s = await auth.api.getStatementMappingSuggestion();
    const next = { ...own };
    for (const e of s.entries) next[e.accountId] = e.section;
    setOwn(next);
    setNotice(t("sm.template_loaded", { n: String(s.entries.length) }));
  });
  const save = () => run(async () => {
    const entries = Object.entries(own).filter(([, s]) => s).map(([accountId, section]) => ({ accountId, section }));
    const saved = await auth.api.saveStatementMapping(entries, expected, note.trim() || null);
    setNote("");
    await load();
    setNotice(t("sm.saved", { n: String(saved.version?.versionNo ?? 0) }));
  });

  if (loadError) return <Banner kind="error" testId="sm-load-error">{loadError}</Banner>;
  if (!data) return <Loading />;
  return (
    <>
      <PageHeader title={t("sm.title")} description={t("sm.subtitle")} />
      {error && <Banner kind="error" testId="sm-error">{error}</Banner>}
      {notice && <Banner kind="success" testId="sm-notice">{notice}</Banner>}
      {!data.version && <Banner kind="info" testId="sm-none">{t("sm.none")}</Banner>}
      {unclassified > 0 && <Banner kind="warning" testId="sm-unclassified">{t("sm.unclassified_note", { n: String(unclassified) })}</Banner>}
      {!current && <Banner kind="info" testId="sm-old-version">{t("sm.old_version", { n: String(viewing) })}</Banner>}
      <Card title={t("sm.accounts_title")}>
        <p className="hint" data-testid="sm-version">{data.version ? t("sm.current_version", { n: String(data.version.versionNo), date: data.version.createdAtUtc.slice(0, 10) }) : t("sm.no_version")}</p>
        <div className="actions">
          {canConfigure && current && <button type="button" disabled={busy} data-testid="sm-template" onClick={() => void loadTemplate()}>{t("sm.load_template")}</button>}
          {canConfigure && current && (
            <>
              <input aria-label={t("sm.note")} placeholder={t("sm.note")} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
              <button type="button" className="primary" disabled={busy || !dirty} data-testid="sm-save" onClick={() => void save()}>{t("sm.save")}</button>
            </>
          )}
        </div>
        <div className="table-scroll">
          <table className="table" data-testid="sm-table">
            <thead><tr><th>{t("cc.code")}</th><th>{t("cc.name")}</th><th>{t("sm.type")}</th><th>{t("sm.section")}</th><th>{t("sm.effective")}</th></tr></thead>
            <tbody>
              {accounts.map((a) => {
                const eff = effective(a);
                return (
                  <tr key={a.id} data-testid={`sm-row-${a.code}`} className={!a.isGroup && eff === null ? "row-warning" : a.isGroup ? "row-group" : undefined}>
                    <td><bdi dir="ltr" style={{ paddingInlineStart: `${depth(a) * 1.25}rem` }}>{a.code}</bdi></td>
                    <td>{localName(a, locale)}</td>
                    <td>{t(`sm.type.${a.type}`)}</td>
                    <td>
                      <select aria-label={`${a.code} — ${t("sm.section")}`} disabled={!canConfigure || !current || busy} value={own[a.id] ?? INHERIT}
                        onChange={(e) => setOwn((prev) => { const next = { ...prev }; if (e.target.value === INHERIT) delete next[a.id]; else next[a.id] = e.target.value; return next; })}>
                        <option value={INHERIT}>{a.parentId ? t("sm.inherit") : t("sm.unclassified")}</option>
                        {(allowed[a.type] ?? []).map((s) => <option key={s} value={s}>{t(`sm.section.${s}`)}</option>)}
                      </select>
                    </td>
                    <td>{eff ? t(`sm.section.${eff}`) : <strong>{t("sm.unclassified")}</strong>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>
      <Card title={t("sm.versions_title")}>
        {versions.length === 0 ? <p>{t("sm.no_version")}</p> : (
          <ul data-testid="sm-versions">
            {versions.map((v) => (
              <li key={v.id} data-testid={`sm-version-${v.versionNo}`}>
                <strong>{t("st.mapping_version_n", { n: String(v.versionNo) })}</strong> — <bdi dir="ltr">{v.createdAtUtc.slice(0, 16).replace("T", " ")}</bdi> — {t(`sm.source.${v.source}`)} — {t("sm.entries", { n: String(v.entryCount) })}{v.note ? <> — <bdi dir="auto">{v.note}</bdi></> : null}
                {" "}<button type="button" onClick={() => void load(v.versionNo === versions[0]?.versionNo ? undefined : v.versionNo)}>{t("sm.view")}</button>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </>
  );
}
