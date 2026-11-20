import { useCallback, useEffect, useId, useRef, useState } from "react";
import { useI18n } from "./i18n";

/**
 * Shared searchable, server-limited picker (recorded follow-up of the PR-69 review: pickers silently truncated at 500 items).
 * Works identically on Web and Tauri. It never loads "everything": each search asks the caller's `load` for one bounded page,
 * says when more matches exist than are shown (so the user refines the search), keeps the current selection selectable even when a
 * later search no longer returns it, ignores out-of-order responses, and shows a retryable error instead of an empty list.
 *
 * Messages come from the app catalog (keys `picker.*`): `picker.search`, `picker.none`, `picker.showing` ({shown}, {total}),
 * `picker.refine`, `picker.loading`, `picker.error`, `picker.retry`, `picker.no_results`.
 */
export interface PickerPage<T> { items: T[]; total: number }

export interface SearchPickerProps<T> {
  /** Accessible name of the listbox (the field label). */
  label: string;
  /** Selected id, or "" for none. */
  value: string;
  onChange: (id: string, item: T | null) => void;
  /** One bounded page of matches for the (trimmed) search text; an empty string means "no filter". */
  load: (search: string) => Promise<PickerPage<T>>;
  getId: (item: T) => string;
  getLabel: (item: T) => string;
  /** Label of the current value when it is not (yet) among the loaded items, e.g. an inactive or not-yet-loaded record. */
  selectedLabel?: string;
  /** Allow choosing nothing (default true). */
  allowNone?: boolean;
  disabled?: boolean;
  debounceMs?: number;
  testId?: string;
}

export function SearchPicker<T>({ label, value, onChange, load, getId, getLabel, selectedLabel, allowNone = true, disabled, debounceMs = 250, testId }: SearchPickerProps<T>) {
  const { t } = useI18n();
  const base = useId();
  const [search, setSearch] = useState("");
  const [page, setPage] = useState<PickerPage<T> | null>(null);
  const [failed, setFailed] = useState(false);
  const [loading, setLoading] = useState(false);
  const known = useRef(new Map<string, T>());          // every item seen, so the selection survives a new search
  const seq = useRef(0);
  const loadRef = useRef(load);
  loadRef.current = load;
  const idRef = useRef(getId);
  idRef.current = getId;

  const run = useCallback((text: string) => {
    const mine = ++seq.current;
    setLoading(true); setFailed(false);
    loadRef.current(text.trim()).then(
      (p) => { if (mine !== seq.current) return; for (const i of p.items) known.current.set(idRef.current(i), i); setPage(p); setLoading(false); },
      () => { if (mine !== seq.current) return; setFailed(true); setLoading(false); },
    );
  }, []);

  useEffect(() => {
    const handle = setTimeout(() => run(search), search === "" ? 0 : debounceMs);
    return () => clearTimeout(handle);
  }, [search, run, debounceMs]);
  useEffect(() => () => { seq.current++; }, []);       // unmounted: drop late responses

  const shown = page?.items ?? [];
  const selectedInList = value !== "" && shown.some((i) => getId(i) === value);
  const pinned = value !== "" && !selectedInList ? known.current.get(value) : undefined;
  const pinnedLabel = pinned ? getLabel(pinned) : selectedLabel;
  const more = page !== null && page.total > shown.length;
  const status = loading ? t("picker.loading") : failed ? "" : page !== null && shown.length === 0 ? t("picker.no_results") : more ? `${t("picker.showing", { shown: shown.length, total: page.total })} ${t("picker.refine")}` : "";

  return (
    <div className="search-picker" data-testid={testId}>
      <input type="search" aria-label={t("picker.search")} aria-controls={`${base}-list`} value={search} disabled={disabled}
        onChange={(e) => setSearch(e.target.value)} />
      <select id={`${base}-list`} aria-label={label} value={value} disabled={disabled}
        onChange={(e) => { const id = e.target.value; onChange(id, id === "" ? null : known.current.get(id) ?? null); }}>
        {allowNone && <option value="">{t("picker.none")}</option>}
        {value !== "" && !selectedInList && <option value={value}>{pinnedLabel ?? value}</option>}
        {shown.map((i) => <option key={getId(i)} value={getId(i)}>{getLabel(i)}</option>)}
      </select>
      <p className="hint" role="status" aria-live="polite">{status}</p>
      {failed && (
        <p className="field-error" role="alert">{t("picker.error")} <button type="button" className="ghost" onClick={() => run(search)}>{t("picker.retry")}</button></p>
      )}
    </div>
  );
}
