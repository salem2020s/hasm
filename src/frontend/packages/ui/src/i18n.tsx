import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";

export type Locale = "ar" | "en";
export type Direction = "rtl" | "ltr";

export const directionOf = (locale: Locale): Direction => (locale === "ar" ? "rtl" : "ltr");

export type Messages = Record<string, string>;
export type Catalog = Record<Locale, Messages>;

interface I18nValue {
  locale: Locale;
  direction: Direction;
  setLocale: (locale: Locale) => void;
  t: (key: string, params?: Record<string, string | number>) => string;
}

const I18nContext = createContext<I18nValue | null>(null);
const STORAGE_KEY = "hasm.locale";

function loadInitialLocale(fallback: Locale): Locale {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved === "ar" || saved === "en") return saved;
  } catch {
    /* storage unavailable */
  }
  return fallback;
}

export function I18nProvider({ catalog, defaultLocale = "ar", children }: {
  catalog: Catalog;
  defaultLocale?: Locale;
  children: ReactNode;
}) {
  const [locale, setLocaleState] = useState<Locale>(() => loadInitialLocale(defaultLocale));
  const direction = directionOf(locale);

  // Keep document language and direction in sync so CSS logical properties and native controls follow.
  useEffect(() => {
    document.documentElement.lang = locale;
    document.documentElement.dir = direction;
  }, [locale, direction]);

  const setLocale = useCallback((next: Locale) => {
    setLocaleState(next);
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch {
      /* storage unavailable */
    }
  }, []);

  const value = useMemo<I18nValue>(
    () => ({ locale, direction, setLocale, t: (key, params) => interpolate(catalog[locale][key] ?? catalog.en[key] ?? key, params) }),
    [locale, direction, setLocale, catalog],
  );

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  const ctx = useContext(I18nContext);
  if (!ctx) throw new Error("useI18n must be used inside <I18nProvider>");
  return ctx;
}

function interpolate(text: string, params?: Record<string, string | number>): string {
  return params ? text.replace(/\{(\w+)\}/g, (m, k: string) => (k in params ? String(params[k]) : m)) : text;
}
