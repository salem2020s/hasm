import { ApiError } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useCallback, useRef, useState } from "react";
import { useAuth } from "./auth";
import { errorText, fieldErrors } from "./components";

/**
 * Shared behaviour of the General Ledger screens (numbering, exchange rates, cost centers, approval policies, journal, reports). ONE implementation for the Web app and the
 * Windows Desktop app: every rule (balance, approvals bound to the revision, periods, rates, authorization, audit, concurrency) is enforced by the server and PostgreSQL;
 * these screens validate for convenience only and show or hide controls from what the server says the caller may do.
 */

/** Exact decimal text: digits with an optional fraction, no sign, no exponent. Amounts are NEVER converted to floating point. */
export const isDecimal = (s: string, maxFraction = 4) => new RegExp(`^\\d{1,16}(\\.\\d{1,${maxFraction}})?$`).test(s.trim());

/** Arabic-Indic and Extended Arabic-Indic digits to Latin, so a typed amount or date always reaches the server in one form. */
export const toLatinDigits = (s: string) => s.replace(/[٠-٩]/g, (c) => String(c.charCodeAt(0) - 0x0660)).replace(/[۰-۹]/g, (c) => String(c.charCodeAt(0) - 0x06f0)).replace(/٫/g, ".");

/** The name for the current language, falling back to the other language and then to the code. */
export function localName(x: { nameAr: string | null; nameEn: string | null; code?: string }, locale: string): string {
  const preferred = locale === "en" ? x.nameEn : x.nameAr;
  const other = locale === "en" ? x.nameAr : x.nameEn;
  return preferred?.trim() || other?.trim() || x.code || "";
}

export const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

/** Adds two exact decimal strings (up to 4 fraction digits) without floating point: scaled BigInt arithmetic. */
export function addDecimal(a: string, b: string): string {
  const scale = 10000n;
  const parse = (v: string) => {
    const [i, f = ""] = toLatinDigits(v.trim() || "0").split(".");
    return BigInt(i || "0") * scale + BigInt((f + "0000").slice(0, 4));
  };
  const sum = parse(a) + parse(b);
  const whole = sum / scale;
  const frac = (sum % scale).toString().padStart(4, "0");
  return `${whole}.${frac}`;
}

export function compareDecimal(a: string, b: string): number {
  const x = addDecimal(a, "0"), y = addDecimal(b, "0");
  const [xi, xf] = x.split("."), [yi, yf] = y.split(".");
  const ai = BigInt(xi!), bi = BigInt(yi!);
  if (ai !== bi) return ai < bi ? -1 : 1;
  return xf === yf ? 0 : xf! < yf! ? -1 : 1;
}

/**
 * Runs one server action with the uniform handling: busy flag, translated error, field errors, optimistic-concurrency conflict (412 => the item changed elsewhere; the caller
 * reloads) and session expiry (handled centrally). `prefix` selects the screen's own error texts before the generic ones.
 */
export function useRunner(prefix: string, reload: () => Promise<void>) {
  const { t } = useI18n();
  const auth = useAuth();
  const tRef = useRef(t);
  tRef.current = t;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [fields, setFields] = useState<Record<string, string>>({});
  const clear = useCallback(() => { setError(null); setNotice(null); setConflict(false); setFields({}); }, []);

  const run = useCallback(async <T,>(action: () => Promise<T>, message: string): Promise<T | undefined> => {
    setBusy(true);
    setError(null); setNotice(null); setConflict(false); setFields({});
    try {
      const result = await action();
      await reload();
      setNotice(message);
      return result;
    } catch (e) {
      if (auth.handleError(e)) return undefined;
      if (e instanceof ApiError && e.status === 412) { setConflict(true); await reload().catch(() => undefined); }
      else {
        setError(errorText(tRef.current, e, prefix));
        setFields(fieldErrors(tRef.current, e));
      }
      return undefined;
    } finally {
      setBusy(false);
    }
  }, [auth, prefix, reload]);

  return { busy, error, notice, conflict, fields, run, clear, setError, setNotice };
}
