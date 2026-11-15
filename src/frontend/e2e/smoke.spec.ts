import { expect, test } from "@playwright/test";

test("the production bundle starts in Arabic RTL and shows the unreachable-service state", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/");
  await expect(page.getByTestId("unreachable")).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.locator("html")).toHaveAttribute("lang", "ar");
  expect(errors).toEqual([]);                                           // no uncaught script error in the production bundle
});

test("switching the language flips to English LTR and the choice survives a reload", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("unreachable")).toBeVisible();
  await page.locator(".auth-lang button").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
});

test("the static bundle never talks to a database or to a foreign origin", async ({ page }) => {
  const origins = new Set<string>();
  page.on("request", (r) => origins.add(new URL(r.url()).origin));
  await page.goto("/");
  await expect(page.getByTestId("unreachable")).toBeVisible();
  for (const o of origins) expect(["http://127.0.0.1:4173", "http://127.0.0.1:9"]).toContain(o);
});
