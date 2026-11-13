import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderApp, signedIn } from "./test-utils";

// The official Tauri 2 window API, replaced by a fake that records the close-requested handler and the destroy() call.
const fake = vi.hoisted(() => ({ handler: null as null | ((e: { preventDefault: () => void }) => void), destroy: vi.fn(), unlisten: vi.fn(), onCloseRequested: vi.fn() }));
vi.mock("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => ({
    onCloseRequested: fake.onCloseRequested.mockImplementation(async (h: (e: { preventDefault: () => void }) => void) => { fake.handler = h; return fake.unlisten; }),
    destroy: fake.destroy,
  }),
}));

const stored = { nameAr: "شركة الأمل", nameEn: "Amal Co", legalName: null, commercialRegistrationNo: null, taxRegistrationNo: null, countryCode: "SA",
  addressLine1: null, addressLine2: null, city: "Riyadh", region: null, postalCode: null, phone: null, email: null, website: null };
const client = () => signedIn({ getProfile: vi.fn().mockResolvedValue({ profile: stored, revision: 1, updatedAtUtc: null, projection: "synced" }) });
const go = async (hash: string) => { await screen.findByTestId("company-name"); window.location.hash = hash; fireEvent(window, new HashChangeEvent("hashchange")); };
/** Simulates the native close request; returns whether the app prevented it. */
const pressX = () => { let prevented = false; fake.handler!({ preventDefault: () => { prevented = true; } }); return prevented; };
const edit = async () => { const c = await screen.findByLabelText("المدينة"); await userEvent.clear(c); await userEvent.type(c, "Jeddah"); };

beforeEach(() => { localStorage.clear(); window.location.hash = ""; fake.handler = null; fake.destroy.mockClear(); fake.unlisten.mockClear(); fake.onCloseRequested.mockClear(); });
afterEach(() => { delete (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__; });

describe("Windows desktop: native window-close request", () => {
  beforeEach(() => { (window as unknown as Record<string, unknown>).__TAURI_INTERNALS__ = {}; });

  it("registers for the native close request once the app is running", async () => {
    renderApp(client());
    await waitFor(() => expect(fake.handler).not.toBeNull());
    expect(fake.onCloseRequested).toHaveBeenCalledTimes(1);
  });

  it("closes normally (not prevented, no dialog) when nothing is unsaved", async () => {
    renderApp(client());
    await go("#/company/details");
    await screen.findByLabelText("المدينة");
    await waitFor(() => expect(fake.handler).not.toBeNull());
    expect(pressX()).toBe(false);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(fake.destroy).not.toHaveBeenCalled(); // Tauri itself closes the window when the handler does not prevent it
  });

  it("with unsaved edits: prevents the close and shows the Stay / Discard dialog naming the section only", async () => {
    renderApp(client());
    await go("#/company/details");
    await edit();
    await waitFor(() => expect(fake.handler).not.toBeNull());
    expect(pressX()).toBe(true);
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("بيانات الشركة");
    expect(dialog.textContent).not.toContain("Jeddah");
    expect(fake.destroy).not.toHaveBeenCalled();
  });

  it("Stay keeps the window open and the edit", async () => {
    renderApp(client());
    await go("#/company/details");
    await edit();
    await waitFor(() => expect(fake.handler).not.toBeNull());
    pressX();
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(fake.destroy).not.toHaveBeenCalled();
    expect(screen.getByLabelText("المدينة")).toHaveValue("Jeddah");
    // a later close request asks again (the edit is still unsaved)
    expect(pressX()).toBe(true);
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });

  it("pressing X again while the dialog is open does not stack dialogs or loop; Discard then closes exactly once", async () => {
    renderApp(client());
    await go("#/company/details");
    await edit();
    await waitFor(() => expect(fake.handler).not.toBeNull());
    pressX(); pressX(); pressX();
    expect(await screen.findAllByRole("dialog")).toHaveLength(1);
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تجاهل التغييرات" }));
    await waitFor(() => expect(fake.destroy).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("follows the language (English dialog and choices)", async () => {
    renderApp(client());
    await go("#/company/details");
    await edit();
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    await screen.findByRole("heading", { name: "Company Details & Settings", level: 1 });
    await waitFor(() => expect(fake.handler).not.toBeNull());
    pressX();
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("button", { name: "Stay on this page" })).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Discard changes" }));
    await waitFor(() => expect(fake.destroy).toHaveBeenCalledTimes(1));
  });

  it("stops listening when the app unmounts", async () => {
    const { unmount } = renderApp(client());
    await waitFor(() => expect(fake.handler).not.toBeNull());
    unmount();
    await waitFor(() => expect(fake.unlisten).toHaveBeenCalled());
  });
});

describe("Web: the native window-close hook is not used", () => {
  it("never loads or registers the Tauri window API in a normal browser", async () => {
    renderApp(client());
    await go("#/company/details");
    await edit();
    await new Promise((r) => setTimeout(r, 50));
    expect(fake.onCloseRequested).not.toHaveBeenCalled();
    expect(fake.handler).toBeNull();
    // the browser's own reload/close confirmation still applies
    const ev = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(ev);
    expect(ev.defaultPrevented).toBe(true);
  });
});
