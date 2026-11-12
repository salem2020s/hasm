import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { ChevronIcon } from "./icons";
import { useI18n } from "./i18n";

export interface MenuItem {
  id: string;
  label: string;
  children?: MenuItem[];
  disabled?: boolean;
  /** Decorative leading icon (top level of a vertical menu). */
  icon?: ReactNode;
  /** Short status text shown beside the label, e.g. "Coming soon". Part of the accessible name. */
  badge?: string;
}

interface Props {
  items: MenuItem[];
  label: string;
  /** The ONLY way to choose a destination: called by a click or Enter/Space on a leaf. Hovering never selects. */
  onSelect: (item: MenuItem) => void;
  currentId?: string;
  /** Hover-intent delay before a submenu expands (ms). */
  hoverDelayMs?: number;
  /** "vertical" is the sidebar layout: submenus fly out toward the content area (inline-end of the sidebar). */
  orientation?: "horizontal" | "vertical";
}

/**
 * Cascading (multi-level) menu. Hover (or focus + ArrowDown/Enter) EXPANDS a submenu; the final selection requires a click or Enter, so
 * moving the pointer across the menu never navigates. Keyboard: Left/Right (mirrored in RTL) move across the top level, Down/Up move in
 * a submenu, Enter/Space opens or selects, Escape closes and restores focus. Works identically in the web app and the Tauri window.
 */
export function CascadingMenu({ items, label, onSelect, currentId, hoverDelayMs = 150, orientation = "horizontal" }: Props) {
  const vertical = orientation === "vertical";
  const { direction } = useI18n();
  const [openPath, setOpenPath] = useState<string[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const root = useRef<HTMLUListElement>(null);

  const clearTimer = () => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
  };
  useEffect(() => clearTimer, []);

  // Clicking or focusing elsewhere closes the menu.
  useEffect(() => {
    const away = (e: Event) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpenPath([]);
    };
    document.addEventListener("pointerdown", away);
    document.addEventListener("focusin", away);
    return () => {
      document.removeEventListener("pointerdown", away);
      document.removeEventListener("focusin", away);
    };
  }, []);

  const expandLater = useCallback((path: string[]) => {
    clearTimer();
    timer.current = setTimeout(() => setOpenPath(path), hoverDelayMs);
  }, [hoverDelayMs]);

  const choose = (item: MenuItem, path: string[]) => {
    if (item.disabled) return;
    clearTimer();
    if (item.children?.length) {
      setOpenPath((cur) => (cur.join("/") === path.join("/") ? path.slice(0, -1) : path)); // click toggles
    } else {
      setOpenPath([]);
      onSelect(item);
    }
  };

  const move = (target: HTMLElement, delta: number) => {
    const list = target.closest("ul");
    if (!list) return;
    const buttons = Array.from(list.querySelectorAll<HTMLElement>(":scope > li > [role='menuitem']:not([aria-disabled='true'])"));
    const at = buttons.indexOf(target);
    if (at >= 0 && buttons.length > 0) buttons[(at + delta + buttons.length) % buttons.length]?.focus();
  };

  const onKeyDown = (e: KeyboardEvent<HTMLElement>, item: MenuItem, path: string[], topLevel: boolean) => {
    const forward = direction === "rtl" ? "ArrowLeft" : "ArrowRight";
    const backward = direction === "rtl" ? "ArrowRight" : "ArrowLeft";
    const target = e.currentTarget;
    const hasChildren = !!item.children?.length;
    if (e.key === "Escape") {
      setOpenPath([]);
      (root.current?.querySelector("[role='menuitem']") as HTMLElement | null)?.focus();
      return;
    }
    if (topLevel && vertical) {
      if (e.key === "ArrowDown") { e.preventDefault(); move(target, 1); }
      else if (e.key === "ArrowUp") { e.preventDefault(); move(target, -1); }
      else if (e.key === forward && hasChildren) {
        e.preventDefault();
        setOpenPath(path);
        requestAnimationFrame(() => target.parentElement?.querySelector<HTMLElement>("ul [role='menuitem']")?.focus());
      }
    } else if (topLevel) {
      if (e.key === forward) { e.preventDefault(); move(target, 1); }
      else if (e.key === backward) { e.preventDefault(); move(target, -1); }
      else if (e.key === "ArrowDown" && hasChildren) {
        e.preventDefault();
        setOpenPath(path);
        requestAnimationFrame(() => target.parentElement?.querySelector<HTMLElement>("ul [role='menuitem']")?.focus());
      }
    } else if (e.key === "ArrowDown") { e.preventDefault(); move(target, 1); }
    else if (e.key === "ArrowUp") { e.preventDefault(); move(target, -1); }
    else if (e.key === forward && hasChildren) {
      e.preventDefault();
      setOpenPath(path);
      requestAnimationFrame(() => target.parentElement?.querySelector<HTMLElement>("ul [role='menuitem']")?.focus());
    } else if (e.key === backward && path.length > 1) {
      e.preventDefault();
      setOpenPath(path.slice(0, -1));
      (target.closest("ul")?.closest("li")?.querySelector(":scope > [role='menuitem']") as HTMLElement | null)?.focus();
    }
  };

  const renderList = (list: MenuItem[], parent: string[], topLevel: boolean) => (
    <ul role={topLevel ? "menubar" : "menu"} aria-label={topLevel ? label : undefined} aria-orientation={topLevel && vertical ? "vertical" : undefined} className={topLevel ? "menu-bar" : "menu-list"} ref={topLevel ? root : undefined}>
      {list.map((item) => {
        const path = [...parent, item.id];
        const hasChildren = !!item.children?.length;
        const open = openPath.length >= path.length && path.every((p, i) => openPath[i] === p);
        return (
          <li
            key={item.id}
            role="none"
            className="menu-entry"
            onMouseEnter={() => hasChildren && !item.disabled && expandLater(path)}
            onMouseLeave={() => { clearTimer(); if (open) expandLater(parent); }}
          >
            <button
              type="button"
              role="menuitem"
              className="menu-item"
              aria-haspopup={hasChildren ? "menu" : undefined}
              aria-expanded={hasChildren ? open : undefined}
              aria-disabled={item.disabled || undefined}
              title={item.disabled ? item.badge : undefined}
              aria-current={currentId === item.id ? "page" : undefined}
              onClick={() => choose(item, path)}
              onKeyDown={(e) => onKeyDown(e, item, path, topLevel)}
            >
              {item.icon && <span className="menu-icon">{item.icon}</span>}
              <span className="menu-label">{item.label}</span>
              {/* The status stays in the accessible name and a hover tooltip, but is not drawn on every row (less clutter, no wrapping). */}
              {item.badge && <> <span className="menu-badge">{item.badge}</span></>}
              {hasChildren && <span aria-hidden="true" className="menu-caret">{topLevel && !vertical ? "▾" : <ChevronIcon />}</span>}
            </button>
            {hasChildren && open && renderList(item.children!, path, false)}
          </li>
        );
      })}
    </ul>
  );

  return <nav className={vertical ? "cascading-menu cascading-menu-vertical" : "cascading-menu"} onMouseLeave={clearTimer}>{renderList(items, [], true)}</nav>;
}
