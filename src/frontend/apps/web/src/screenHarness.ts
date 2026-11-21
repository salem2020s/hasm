import type { ApiClient } from "@hasm/api-client";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { vi } from "vitest";
import { signedIn } from "./test-utils";

/** One route of App.tsx with the permission rule that guards it, read from the source so the tests cannot drift from the real route table. */
export interface RouteRule { path: string; permission?: string; anyOf?: string[]; anyScope?: string; centralAdmin?: boolean }

export function readRoutes(): RouteRule[] {
  const text = readFileSync(join(__dirname, "App.tsx"), "utf8");
  const out: RouteRule[] = [];
  for (const m of text.matchAll(/^\s*\{ path: "(\/[^"]*)"([^\n]*)element:/gm)) {
    const rest = m[2] ?? "";
    if (m[1]!.startsWith("/dev/")) continue;
    const rule: RouteRule = { path: m[1]! };
    const perm = /permission: "([^"]+)"/.exec(rest); if (perm) rule.permission = perm[1];
    const any = /anyOf: \[([^\]]*)\]/.exec(rest); if (any) rule.anyOf = [...any[1]!.matchAll(/"([^"]+)"/g)].map((x) => x[1]!);
    const scope = /anyScope: "([^"]+)"/.exec(rest); if (scope) rule.anyScope = scope[1];
    if (/centralAdmin: true/.test(rest)) rule.centralAdmin = true;
    out.push(rule);
  }
  return out;
}

/** Every permission any route asks for. */
export function allPermissions(routes: RouteRule[]): string[] {
  return [...new Set(routes.flatMap((r) => [r.permission, r.anyScope, ...(r.anyOf ?? [])]).filter((p): p is string => !!p))];
}

/**
 * An "empty" server answer of unknown shape: behaves as an empty list (length 0, map/filter/find/includes...) and every unknown
 * property is again such an empty value, so a screen renders its empty state instead of crashing on an unstubbed field.
 */
export function emptyAnswer(): unknown {
  const target: unknown[] = [];
  return new Proxy(target, {
    get: (t, k) => {
      if (k === Symbol.toPrimitive) return () => "";
      if (k === "then") return undefined;
      if (k in t) { const v = (t as unknown as Record<string | symbol, unknown>)[k]; return typeof v === "function" ? (v as (...a: unknown[]) => unknown).bind(t) : v; }
      if (typeof k === "string" && (k in String.prototype || k in Number.prototype)) return () => "";
      return emptyAnswer();
    },
  });
}

/** A client whose unstubbed methods answer "empty" (see emptyAnswer) so every screen reaches its loaded, empty state. */
export function emptyClient(permissions: string[]) {
  const base = signedIn({}, permissions) as unknown as Record<string, unknown>;
  return new Proxy(base, {
    get: (t, k: string) => (k in t && (t[k] as { getMockImplementation?: () => unknown })?.getMockImplementation?.() ? t[k] : vi.fn().mockResolvedValue(emptyAnswer())),
  }) as unknown as ApiClient;
}

