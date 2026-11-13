import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const stored = {
  nameAr: "شركة الأمل", nameEn: "Amal Co", legalName: null, commercialRegistrationNo: null, taxRegistrationNo: null, countryCode: "SA",
  addressLine1: null, addressLine2: null, city: "Riyadh", region: null, postalCode: null, phone: null, email: null, website: null,
};
const profileResp = (rev = 1, p = stored) => ({ profile: p, revision: rev, updatedAtUtc: null, projection: "synced" });
const settingsResp = (rev = 1, over = {}) => ({
  settings: { defaultLanguage: "ar", dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: "UTC", ...over }, revision: rev, exists: true, setupComplete: true,
  updatedAtUtc: null, options: { languages: ["ar", "en"], dateFormats: ["yyyy-MM-dd", "dd/MM/yyyy"], numeralSystems: ["latin", "arabic-indic"] },
});
const PROFILE_ONLY = ["company.profile.view", "company.profile.update"];
const SETTINGS_ONLY = ["company.settings.view", "company.settings.update"];

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

const dirtyClient = (extra = {}) => signedIn({ getProfile: vi.fn().mockResolvedValue(profileResp()), getSettings: vi.fn().mockResolvedValue(settingsResp()), ...extra });
const editCity = async (text = "Secret City") => { const c = await screen.findByLabelText("المدينة"); await userEvent.clear(c); await userEvent.type(c, text); };

describe("unsaved-changes safeguard", () => {
  it("does not interfere when nothing is unsaved (tabs, menu, Change company all proceed at once)", async () => {
    const client = dirtyClient();
    renderApp(client);
    await go("#/company/details");
    await screen.findByLabelText("المدينة");
    await userEvent.click(screen.getByRole("tab", { name: "الإعدادات العامة" })); // tab switching is never "leaving"
    expect(screen.queryByRole("dialog")).toBeNull();
    await userEvent.click(screen.getByRole("menuitem", { name: /إدارة الشركة/ }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "العملات" }));
    await waitFor(() => expect(window.location.hash).toBe("#/company/currencies"));
    expect(screen.queryByRole("dialog")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "تغيير الشركة" }));
    expect(client.clearCompany).toHaveBeenCalled();
  });

  it("warns before leaving through the menu: Stay keeps the page and the edit, Discard leaves; field values are never shown", async () => {
    renderApp(dirtyClient());
    await go("#/company/details");
    await editCity();
    await userEvent.click(screen.getByRole("menuitem", { name: /إدارة الشركة/ }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "العملات" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("بيانات الشركة"); // names the section
    expect(dialog.textContent).not.toContain("Secret City"); // never the value
    expect(window.location.hash).toBe("#/company/details");
    await userEvent.click(within(dialog).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByLabelText("المدينة")).toHaveValue("Secret City");
    await userEvent.click(screen.getByRole("menuitem", { name: /إدارة الشركة/ }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "العملات" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "تجاهل التغييرات" }));
    await waitFor(() => expect(window.location.hash).toBe("#/company/currencies"));
  });

  it("focuses Stay (the safe choice) and Escape also stays", async () => {
    renderApp(dirtyClient());
    await go("#/company/details");
    await editCity();
    await userEvent.click(screen.getByRole("button", { name: "تسجيل الخروج" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("button", { name: "البقاء في الصفحة" })).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("warns before changing company and before signing out; neither happens until Discard", async () => {
    const client = dirtyClient();
    renderApp(client);
    await go("#/company/details");
    await editCity();
    await userEvent.click(screen.getByRole("button", { name: "تغيير الشركة" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(client.clearCompany).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "تسجيل الخروج" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(client.logout).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "تسجيل الخروج" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "تجاهل التغييرات" }));
    await waitFor(() => expect(client.logout).toHaveBeenCalled());
  });

  it("detects the sections independently: only the section with edits is named; saving clears it", async () => {
    const putSettings = vi.fn().mockResolvedValue(settingsResp(2, { dateFormat: "dd/MM/yyyy" }));
    renderApp(dirtyClient({ putSettings }));
    await go("#/company/details");
    await screen.findByLabelText("المدينة");
    await userEvent.click(screen.getByRole("tab", { name: "الإعدادات العامة" }));
    await userEvent.selectOptions(await screen.findByLabelText("صيغة التاريخ"), "dd/MM/yyyy");
    await userEvent.click(screen.getByRole("button", { name: "تسجيل الخروج" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("الإعدادات العامة");
    expect(dialog).not.toHaveTextContent("بيانات الشركة");
    await userEvent.click(within(dialog).getByRole("button", { name: "البقاء في الصفحة" }));
    await userEvent.click(within(screen.getByRole("tabpanel", { name: /الإعدادات العامة/ })).getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(putSettings).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole("tab", { name: /الإعدادات العامة/ })).not.toHaveTextContent("تغييرات غير محفوظة"));
    await userEvent.click(screen.getByRole("button", { name: "تغيير الشركة" })); // saved: no warning any more
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("guards browser Back / typed-address navigation too, restoring the address until the user decides", async () => {
    renderApp(dirtyClient());
    await go("#/company/details");
    await editCity();
    window.location.hash = "#/company/history";
    fireEvent(window, new HashChangeEvent("hashchange"));
    const dialog = await screen.findByRole("dialog");
    expect(window.location.hash).toBe("#/company/details"); // put back
    expect(screen.getByLabelText("المدينة")).toHaveValue("Secret City");
    await userEvent.click(within(dialog).getByRole("button", { name: "تجاهل التغييرات" }));
    await waitFor(() => expect(window.location.hash).toBe("#/company/history"));
  });

  it("asks the browser for its native reload/close confirmation only while something is unsaved", async () => {
    renderApp(dirtyClient());
    await go("#/company/details");
    await screen.findByLabelText("المدينة");
    const clean = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(clean);
    expect(clean.defaultPrevented).toBe(false);
    await editCity();
    const dirty = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(dirty);
    expect(dirty.defaultPrevented).toBe(true);
  });

  it("hidden tab content is inert: its fields cannot be reached with the keyboard", async () => {
    renderApp(dirtyClient());
    await go("#/company/details");
    await screen.findByLabelText("المدينة");
    const hiddenPanel = document.getElementById("panel-settings")!;
    expect(hiddenPanel).toHaveAttribute("hidden");
    expect(hiddenPanel).toHaveAttribute("inert");
    for (let i = 0; i < 60; i++) { await userEvent.tab(); expect(hiddenPanel.contains(document.activeElement)).toBe(false); }
  });

  it("follows the language: the dialog and its choices switch to English", async () => {
    renderApp(dirtyClient());
    await go("#/company/details");
    await editCity();
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    await userEvent.click(await screen.findByRole("button", { name: "Sign out" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Unsaved changes");
    expect(dialog).toHaveTextContent("Company Details");
    expect(within(dialog).getByRole("button", { name: "Stay on this page" })).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Discard changes" })).toBeInTheDocument();
  });
});

describe("unified Company Details & Settings page", () => {
  it("shows two labelled tabs; the old Profile / Settings routes redirect to the matching tab", async () => {
    renderApp(signedIn({ getProfile: vi.fn().mockResolvedValue(profileResp()), getSettings: vi.fn().mockResolvedValue(settingsResp()) }));
    await go("#/company/profile");
    expect(await screen.findByRole("heading", { name: "بيانات وإعدادات الشركة", level: 1 })).toBeInTheDocument();
    await waitFor(() => expect(window.location.hash).toBe("#/company/details"));
    const detailsTab = screen.getByRole("tab", { name: "بيانات الشركة" });
    expect(detailsTab).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "الإعدادات العامة" })).toHaveAttribute("aria-selected", "false");
    expect(screen.getByLabelText("المدينة")).toBeVisible();
    window.location.hash = "#/company/settings";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await waitFor(() => expect(window.location.hash).toBe("#/company/details/general"));
    expect(screen.getByRole("tab", { name: "الإعدادات العامة" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByLabelText("صيغة التاريخ")).toBeVisible();
    expect(screen.getByLabelText("المدينة")).not.toBeVisible(); // hidden, not unmounted
  });

  it("keeps the sidebar entry single, and the menu item opens the unified page", async () => {
    renderApp(signedIn());
    await screen.findByTestId("company-name");
    await userEvent.click(screen.getByRole("menuitem", { name: /إدارة الشركة/ }));
    expect(screen.queryByRole("menuitem", { name: "إعدادات الشركة" })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: "ملف الشركة" })).toBeNull();
    expect(screen.getByRole("menuitem", { name: "العملات" })).toBeInTheDocument(); // Currencies and History stay separate
    expect(screen.getByRole("menuitem", { name: "سجل التغييرات" })).toBeInTheDocument();
  });

  it("never discards unsaved edits when switching tabs, flags the tab, and saves each section independently with its own revision", async () => {
    const putProfile = vi.fn().mockResolvedValue(profileResp(2, { ...stored, city: "Jeddah" }));
    const putSettings = vi.fn().mockResolvedValue(settingsResp(8, { dateFormat: "dd/MM/yyyy" }));
    renderApp(signedIn({ getProfile: vi.fn().mockResolvedValue(profileResp(1)), getSettings: vi.fn().mockResolvedValue(settingsResp(7)), putProfile, putSettings }));
    await go("#/company/details");
    const city = await screen.findByLabelText("المدينة");
    await userEvent.clear(city);
    await userEvent.type(city, "Jeddah");
    expect(screen.getByRole("tab", { name: /بيانات الشركة/ })).toHaveTextContent("تغييرات غير محفوظة"); // unsaved marker (accessible text)
    await userEvent.click(screen.getByRole("tab", { name: "الإعدادات العامة" }));
    await userEvent.selectOptions(await screen.findByLabelText("صيغة التاريخ"), "dd/MM/yyyy");
    await userEvent.click(screen.getByRole("tab", { name: /بيانات الشركة/ }));
    expect(screen.getByLabelText("المدينة")).toHaveValue("Jeddah"); // still there
    await userEvent.click(screen.getByRole("tab", { name: /الإعدادات العامة/ }));
    expect(screen.getByLabelText("صيغة التاريخ")).toHaveValue("dd/MM/yyyy");
    // Save the settings only: the profile is untouched and still unsaved
    const panel = screen.getByRole("tabpanel", { name: /الإعدادات العامة/ });
    await userEvent.click(within(panel).getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(putSettings).toHaveBeenCalledWith(expect.objectContaining({ dateFormat: "dd/MM/yyyy" }), 7));
    expect(putProfile).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("tab", { name: /بيانات الشركة/ }));
    expect(screen.getByLabelText("المدينة")).toHaveValue("Jeddah");
    await userEvent.click(within(screen.getByRole("tabpanel", { name: /بيانات الشركة/ })).getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(putProfile).toHaveBeenCalledWith(expect.objectContaining({ city: "Jeddah" }), 1));
  });

  it("each section keeps its own validation and concurrency handling (a conflict in one does not touch the other)", async () => {
    const putProfile = vi.fn().mockRejectedValue(apiError(412, "revision_mismatch"));
    renderApp(signedIn({ getProfile: vi.fn().mockResolvedValue(profileResp()), getSettings: vi.fn().mockResolvedValue(settingsResp(3)), putProfile }));
    await go("#/company/details");
    await userEvent.click(await screen.findByRole("button", { name: "حفظ" }));
    expect(await screen.findByTestId("conflict")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("tab", { name: /الإعدادات العامة/ }));
    expect(screen.getByLabelText("صيغة التاريخ")).toHaveValue("yyyy-MM-dd");
    expect(screen.queryAllByTestId("conflict")).toHaveLength(1);
  });

  it("with only the profile permission: shows only the details section, no tabs, and never reads the settings", async () => {
    const client = signedIn({ getProfile: vi.fn().mockResolvedValue(profileResp()) }, PROFILE_ONLY);
    renderApp(client);
    await go("#/company/details");
    expect(await screen.findByLabelText("المدينة")).toBeVisible();
    expect(screen.queryByRole("tablist")).toBeNull();
    expect(screen.queryByLabelText("صيغة التاريخ")).toBeNull();
    expect(client.getSettings).not.toHaveBeenCalledWith(); // the dashboard may not read it either: no settings.view
    expect(client.getSettings).not.toHaveBeenCalled();
    // the settings URL does not expose a section the user may not see
    window.location.hash = "#/company/settings";
    fireEvent(window, new HashChangeEvent("hashchange"));
    await waitFor(() => expect(window.location.hash).toBe("#/company/details/general"));
    expect(screen.queryByLabelText("صيغة التاريخ")).toBeNull();
    expect(screen.getByLabelText("المدينة")).toBeVisible();
  });

  it("with only the settings permission: shows only the general settings section and never reads the profile", async () => {
    const client = signedIn({ getSettings: vi.fn().mockResolvedValue(settingsResp()) }, SETTINGS_ONLY);
    renderApp(client);
    await go("#/company/details");
    expect(await screen.findByLabelText("صيغة التاريخ")).toBeVisible();
    expect(screen.queryByRole("tablist")).toBeNull();
    expect(screen.queryByLabelText("المدينة")).toBeNull();
    expect(client.getProfile).not.toHaveBeenCalled();
  });

  it("with neither permission: the page is forbidden and the menu entry is absent", async () => {
    const client = signedIn({}, ["company.currencies.view"]);
    renderApp(client);
    await go("#/company/details");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
    expect(client.getProfile).not.toHaveBeenCalled();
    expect(client.getSettings).not.toHaveBeenCalled();
  });

  it("is keyboard operable (arrow keys move between tabs) and switches to English LTR keeping the route and edits", async () => {
    renderApp(signedIn({ getProfile: vi.fn().mockResolvedValue(profileResp()), getSettings: vi.fn().mockResolvedValue(settingsResp()) }));
    await go("#/company/details");
    const city = await screen.findByLabelText("المدينة");
    await userEvent.type(city, "X");
    const tab = screen.getByRole("tab", { name: /بيانات الشركة/ });
    tab.focus();
    await userEvent.keyboard("{ArrowLeft}");
    await waitFor(() => expect(window.location.hash).toBe("#/company/details/general"));
    expect(screen.getByRole("tab", { name: "الإعدادات العامة" })).toHaveAttribute("aria-selected", "true");
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(await screen.findByRole("heading", { name: "Company Details & Settings", level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(window.location.hash).toBe("#/company/details/general");
    expect(screen.getByRole("tab", { name: "General Settings" })).toHaveAttribute("aria-selected", "true");
    await userEvent.click(screen.getByRole("tab", { name: /Company Details/ }));
    expect(screen.getByLabelText("City")).toHaveValue("RiyadhX"); // the unsaved edit survived the tab changes and the language switch
  });
});
