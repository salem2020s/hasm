import { useEffect, useState } from "react";

/**
 * Minimal hash router: `#/company/profile`. Hash routing needs no server rewrite rules, so the SAME build works as the shared web
 * application and inside the Tauri window (no separate frontend architecture for desktop).
 */
const current = () => (typeof window === "undefined" ? "/" : window.location.hash.replace(/^#/, "") || "/");

export function useRoute(): string {
  const [route, setRoute] = useState(current);
  useEffect(() => {
    const on = () => setRoute(current());
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  return route;
}

export function navigate(path: string): void {
  window.location.hash = path;
}
