/** True inside the Tauri (Windows desktop) window. The shared web build runs in both; only the session transport and the window-close hook differ. */
export const isTauri = () => typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;
