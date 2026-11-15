import { ApiError, type InventoryGroup, type InventoryUnit } from "@hasm/api-client";
import { fieldErrors } from "../components";

/** The name of an inventory record for the current language, falling back to the other language and then the code (same convention as branches and warehouses). */
export function invName(r: { code: string; nameAr: string | null; nameEn: string | null }, locale: string): string {
  const preferred = locale === "en" ? r.nameEn : r.nameAr;
  const other = locale === "en" ? r.nameAr : r.nameEn;
  return preferred?.trim() || other?.trim() || r.code;
}

export const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

/** Server field errors: inventory-specific codes first (`inv.field.<code>`), then the shared ones. */
export function invFieldErrors(t: (k: string) => string, e: unknown): Record<string, string> {
  const out = fieldErrors(t, e);
  if (e instanceof ApiError) for (const f of e.details.fields ?? []) {
    const key = `inv.field.${f.code}`;
    if (t(key) !== key) out[f.field] = t(key);
  }
  return out;
}

/** The ids of a group and all its descendants (a group cannot be moved under itself). */
export function descendantIds(groups: InventoryGroup[], id: string): Set<string> {
  const out = new Set<string>([id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const g of groups) if (g.parentId && out.has(g.parentId) && !out.has(g.id)) { out.add(g.id); grew = true; }
  }
  return out;
}

/** Groups in tree order (parents before children, siblings by code) with their depth, for an indented list or select. */
export function groupTree(groups: InventoryGroup[]): { group: InventoryGroup; depth: number }[] {
  const byParent = new Map<string | null, InventoryGroup[]>();
  const ids = new Set(groups.map((g) => g.id));
  for (const g of groups) {
    const key = g.parentId && ids.has(g.parentId) ? g.parentId : null;
    byParent.set(key, [...(byParent.get(key) ?? []), g]);
  }
  const out: { group: InventoryGroup; depth: number }[] = [];
  const walk = (parent: string | null, depth: number) => {
    for (const g of (byParent.get(parent) ?? []).sort((a, b) => a.code.localeCompare(b.code))) { out.push({ group: g, depth }); walk(g.id, depth + 1); }
  };
  walk(null, 0);
  return out;
}

export const unitLabel = (u: Pick<InventoryUnit, "code" | "nameAr" | "nameEn">, locale: string) => `${u.code} — ${invName(u, locale)}`;
