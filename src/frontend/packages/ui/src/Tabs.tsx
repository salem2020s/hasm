import { useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { useI18n } from "./i18n";

/**
 * The one shared Tabs component (HASM UI standard for tabs): used by Web and Tauri alike. Sections of ONE screen only (never separate modules or sequential steps).
 * - Accessible tab semantics (tablist / tab / tabpanel, roving tabindex, aria-selected, aria-controls / aria-labelledby).
 * - Keyboard: Left/Right (mirrored in RTL), Home, End move AND activate; disabled tabs are skipped; Tab leaves the tablist into the active panel.
 * - Every panel stays MOUNTED; inactive panels are `hidden` and `inert`, so form state is never reset by switching and hidden fields are unreachable.
 * - Indicators: `invalid` (error mark + text for assistive technology) and `dirty` (unsaved marker) per tab.
 * - Narrow windows: the tablist scrolls horizontally instead of wrapping or overflowing the page.
 * Controlled (`value` + `onChange`) or uncontrolled (`defaultValue`).
 */
export interface TabItem {
  id: string;
  label: string;
  content: ReactNode;
  disabled?: boolean;
  /** The tab contains invalid fields: shows an error indicator. */
  invalid?: boolean;
  /** The tab contains unsaved edits: shows a marker. */
  dirty?: boolean;
}

export interface TabsProps {
  tabs: TabItem[];
  label: string;
  value?: string;
  defaultValue?: string;
  onChange?: (id: string) => void;
  testId?: string;
}

export function Tabs({ tabs, label, value, defaultValue, onChange, testId }: TabsProps) {
  const { t, direction } = useI18n();
  const base = useId();
  const [inner, setInner] = useState(defaultValue ?? tabs.find((x) => !x.disabled)?.id ?? tabs[0]?.id ?? "");
  const listRef = useRef<HTMLDivElement>(null);
  const active = value ?? inner;
  const enabled = tabs.filter((x) => !x.disabled);

  const select = (id: string, focus: boolean) => {
    // Keyboard moves focus NOW, in the key event: every tab button is always mounted, and a button with tabindex -1 is focusable by script.
    // Never defer this (requestAnimationFrame/timeout): a late frame would pull focus out of a field the user entered meanwhile, and their typing or Enter would land on a tab.
    if (focus) listRef.current?.querySelector<HTMLElement>(`[data-tab="${CSS.escape(id)}"]`)?.focus();
    if (value === undefined) setInner(id);
    onChange?.(id);
  };

  const onKey = (e: KeyboardEvent<HTMLButtonElement>) => {
    const i = enabled.findIndex((x) => x.id === active);
    const next = direction === "rtl" ? "ArrowLeft" : "ArrowRight";
    const prev = direction === "rtl" ? "ArrowRight" : "ArrowLeft";
    let target: TabItem | undefined;
    if (e.key === next) target = enabled[(i + 1) % enabled.length];
    else if (e.key === prev) target = enabled[(i - 1 + enabled.length) % enabled.length];
    else if (e.key === "Home") target = enabled[0];
    else if (e.key === "End") target = enabled[enabled.length - 1];
    if (!target) return;
    e.preventDefault();
    select(target.id, true);
  };

  return (
    <div className="tabs-root" data-testid={testId}>
      <div role="tablist" aria-label={label} className="tabs" ref={listRef}>
        {tabs.map((x) => (
          <button key={x.id} type="button" role="tab" id={`${base}-tab-${x.id}`} data-tab={x.id} aria-selected={active === x.id} aria-controls={`${base}-panel-${x.id}`}
            tabIndex={active === x.id ? 0 : -1} disabled={x.disabled} className={`tab${active === x.id ? " is-active" : ""}${x.invalid ? " has-error" : ""}`}
            onClick={() => select(x.id, false)} onKeyDown={onKey}>
            {x.label}
            {x.invalid && <><span className="tab-error" aria-hidden="true">!</span><span className="visually-hidden"> ({t("tabs.has_errors")})</span></>}
            {x.dirty && <><span className="tab-dot" aria-hidden="true" /><span className="visually-hidden"> ({t("tabs.unsaved")})</span></>}
          </button>
        ))}
      </div>
      {tabs.map((x) => (
        <div key={x.id} role="tabpanel" id={`${base}-panel-${x.id}`} aria-labelledby={`${base}-tab-${x.id}`} hidden={active !== x.id} inert={active !== x.id} className="tab-panel">
          {x.content}
        </div>
      ))}
    </div>
  );
}
