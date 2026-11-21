import { ApiError } from "@hasm/api-client";
import { describe, expect, it } from "vitest";
import { errorText } from "./components";
import { catalog } from "./messages";

/**
 * A number collision (the counter and the issued numbers disagree; not retryable) is answered 409 number_already_issued by stock-document approval, stocktake approval, stock-adjustment
 * generation and journal posting. Each of those screens shows its own translated message in Arabic and English, never the generic fallback.
 */
describe.each(["ar", "en"] as const)("number_already_issued (%s)", (lang) => {
  const t = (k: string) => catalog[lang][k] ?? k;
  it.each(["stock.error.", "stk.error.", "jrn.error."])("is translated for the %s screens", (prefix) => {
    const text = errorText(t, new ApiError(409, "number_already_issued", {}), prefix);
    expect(text).toBe(catalog[lang][`${prefix}number_already_issued`]);
    expect(text).not.toBe(catalog[lang]["error.generic"]);
    expect(text.trim()).not.toBe("");
  });
});
