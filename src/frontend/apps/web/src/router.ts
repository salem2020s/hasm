import { useEffect, useState } from "react";

/**
 * Minimal hash router: `#/company/details`. Hash routing needs no server rewrite rules, so the SAME build works as the shared web
 * application and inside the Tauri window (no separate frontend architecture for desktop).
 */
const current = () => (typeof window === "undefined" ? "/" : window.location.hash.replace(/^#/, "") || "/");

/**
 * Navigation blocker used by the unsaved-changes safeguard. While it reports a destination as blocked, in-app navigation (navigate()) and
 * browser navigation (Back/Forward, typed hash) do NOT leave the current page: the user is asked first, and `request` receives the action
 * that performs the navigation if (and only if) they choose to discard their changes.
 */
export interface NavigationBlocker { isBlocked(to: string): boolean; request(proceed: () => void): void }
let blocker: NavigationBlocker | null = null;
export function setNavigationBlocker(b: NavigationBlocker | null): void { blocker = b; }

let approved = current(); // the last route the user was allowed to be on

export function useRoute(): string {
  const [route, setRoute] = useState(current);
  useEffect(() => {
    const on = () => {
      const to = current();
      if (to !== approved && blocker?.isBlocked(to)) {
        // Put the address back (a replace, so no extra history entry) and ask; the page and its unsaved edits stay untouched.
        window.location.replace(`#${approved}`);
        blocker.request(() => { approved = to; window.location.hash = to; });
        return;
      }
      approved = to;
      setRoute(to);
    };
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  return route;
}

export function navigate(path: string): void {
  if (path !== approved && blocker?.isBlocked(path)) {
    blocker.request(() => { approved = path; window.location.hash = path; });
    return;
  }
  window.location.hash = path;
}
