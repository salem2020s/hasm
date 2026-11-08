import { I18nProvider } from "@hasm/ui";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { App } from "./App";
import { catalog } from "./messages";

const client = (ok: boolean) => ({
  getHealth: () =>
    ok ? Promise.resolve({ status: "healthy", service: "hasm-api", version: "1" }) : Promise.reject(new Error("down")),
});

const renderApp = (ok: boolean) =>
  render(<I18nProvider catalog={catalog}><App client={client(ok)} /></I18nProvider>);

describe("App", () => {
  beforeEach(() => localStorage.clear());

  it("defaults to Arabic RTL and shows backend health", async () => {
    renderApp(true);
    expect(document.documentElement.dir).toBe("rtl");
    await waitFor(() => expect(screen.getByTestId("backend-status")).toHaveTextContent("يعمل"));
  });

  it("switches to English LTR", async () => {
    renderApp(true);
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(document.documentElement.dir).toBe("ltr");
    await waitFor(() => expect(screen.getByTestId("backend-status")).toHaveTextContent("Healthy"));
  });

  it("reports an unreachable backend", async () => {
    renderApp(false);
    await waitFor(() => expect(screen.getByTestId("backend-status")).toHaveTextContent("غير متاح"));
  });
});
