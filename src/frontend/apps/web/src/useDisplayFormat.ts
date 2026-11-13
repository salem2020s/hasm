import type { SettingsResponse } from "@hasm/api-client";
import { useEffect, useState } from "react";
import { useAuth } from "./auth";
import { DEFAULT_FORMAT, type DisplayFormat } from "./format";

/**
 * The company's display format (date format, numerals, business time zone). Reading the settings needs `company.settings.view`;
 * without it, or while loading, the documented defaults apply. Display only: nothing here writes or alters stored values.
 */
export function useDisplayFormat(): { format: DisplayFormat; settings: SettingsResponse | null } {
  const auth = useAuth();
  const [settings, setSettings] = useState<SettingsResponse | null>(null);
  useEffect(() => {
    if (!auth.can("company.settings.view")) return;
    let live = true;
    auth.api.getSettings().then((r) => { if (live) setSettings(r); }, (e) => { auth.handleError(e); });
    return () => { live = false; };
  }, [auth.api, auth.can, auth.handleError]);
  const format = settings
    ? { dateFormat: settings.settings.dateFormat, numeralSystem: settings.settings.numeralSystem, timeZone: settings.settings.timeZone }
    : DEFAULT_FORMAT;
  return { format, settings };
}
