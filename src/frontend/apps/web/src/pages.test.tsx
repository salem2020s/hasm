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
  nameAr: "شركة الأمل", nameEn: null, legalName: null, commercialRegistrationNo: "CR-1/2", taxRegistrationNo: "VAT 99", countryCode: "SA",
  addressLine1: null, addressLine2: null, city: "Riyadh", region: null, postalCode: null, phone: null, email: null, website: null,
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("company profile screen", () => {
  it("shows an empty new profile and validates names and country client-side without calling the API", async () => {
    const client = signedIn();
    renderApp(client);
    await go("#/company/profile");
    expect(await screen.findByTestId("profile-new")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(screen.getAllByText("أدخل اسم الشركة بالعربية أو بالإنجليزية (واحد على الأقل).")).toHaveLength(2);
    expect(screen.getByLabelText(/الدولة/)).toHaveAttribute("aria-invalid", "true");
    expect(client.putProfile).not.toHaveBeenCalled();
  });

  it("creates the profile with If-Match revision 0 and shows success", async () => {
    const putProfile = vi.fn().mockResolvedValue({ profile: { ...stored }, revision: 1, updatedAtUtc: "2026-01-01T00:00:00Z", projection: "synced" });
    const client = signedIn({ putProfile });
    renderApp(client);
    await go("#/company/profile");
    await userEvent.type(await screen.findByLabelText("الاسم بالعربية"), "شركة الأمل");
    await userEvent.selectOptions(screen.getByLabelText(/الدولة/), "SA");
    await userEvent.type(screen.getByLabelText("رقم السجل التجاري"), "CR-1/2");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(putProfile).toHaveBeenCalled());
    const [body, revision] = putProfile.mock.calls[0];
    expect(revision).toBe(0);
    expect(body).toMatchObject({ nameAr: "شركة الأمل", nameEn: null, countryCode: "SA", commercialRegistrationNo: "CR-1/2" });
    expect(await screen.findByTestId("saved")).toBeInTheDocument();
  });

  it("warns when the display-name projection is pending and offers a retry", async () => {
    const sync = vi.fn().mockResolvedValue({ profile: stored, revision: 1, updatedAtUtc: null, projection: "synced" });
    const client = signedIn({
      putProfile: vi.fn().mockResolvedValue({ profile: stored, revision: 1, updatedAtUtc: null, projection: "pending" }),
      syncDisplayName: sync,
    });
    renderApp(client);
    await go("#/company/profile");
    await userEvent.type(await screen.findByLabelText("الاسم بالعربية"), "x");
    await userEvent.selectOptions(screen.getByLabelText(/الدولة/), "SA");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByTestId("projection-pending")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "إعادة مزامنة الاسم" }));
    await waitFor(() => expect(sync).toHaveBeenCalled());
  });

  it("keeps the user's edits on a concurrency conflict (412) and offers to reload", async () => {
    const getProfile = vi.fn().mockResolvedValue({ profile: stored, revision: 3, updatedAtUtc: null, projection: "synced" });
    const putProfile = vi.fn().mockRejectedValue(apiError(412, "revision_conflict", { currentRevision: 4 }));
    renderApp(signedIn({ getProfile, putProfile }));
    await go("#/company/profile");
    const city = await screen.findByLabelText("المدينة");
    await userEvent.clear(city);
    await userEvent.type(city, "Jeddah");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByTestId("conflict")).toBeInTheDocument();
    expect(putProfile.mock.calls[0][1]).toBe(3);
    expect(screen.getByLabelText("المدينة")).toHaveValue("Jeddah"); // nothing was lost
    getProfile.mockResolvedValue({ profile: { ...stored, city: "Dammam" }, revision: 4, updatedAtUtc: null, projection: "synced" });
    await userEvent.click(within(screen.getByTestId("conflict")).getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(screen.getByLabelText("المدينة")).toHaveValue("Dammam"));
  });

  it("maps server validation errors onto the fields and keeps the form data", async () => {
    const putProfile = vi.fn().mockRejectedValue(apiError(400, "validation_failed", { fields: [{ field: "email", code: "invalid_format" }] }));
    renderApp(signedIn({ getProfile: vi.fn().mockResolvedValue({ profile: stored, revision: 1, updatedAtUtc: null, projection: "synced" }), putProfile }));
    await go("#/company/profile");
    await userEvent.type(await screen.findByLabelText("البريد الإلكتروني"), "a@b.co");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText("الصيغة غير صحيحة.")).toBeInTheDocument();
    expect(screen.getByLabelText("البريد الإلكتروني")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("المدينة")).toHaveValue("Riyadh");
  });

  it("accepts international identifiers client-side (no country-specific format rules)", async () => {
    const putProfile = vi.fn().mockResolvedValue({ profile: stored, revision: 2, updatedAtUtc: null, projection: "synced" });
    renderApp(signedIn({ getProfile: vi.fn().mockResolvedValue({ profile: stored, revision: 1, updatedAtUtc: null, projection: "synced" }), putProfile }));
    await go("#/company/profile");
    const tax = await screen.findByLabelText("الرقم الضريبي");
    await userEvent.clear(tax);
    await userEvent.type(tax, "DE 123.456.789-ÄÖ/x");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(putProfile).toHaveBeenCalled());
    expect(putProfile.mock.calls[0][0].taxRegistrationNo).toBe("DE 123.456.789-ÄÖ/x");
  });

  it("is read-only without the update permission: no save button, fields not editable", async () => {
    renderApp(signedIn({ getProfile: vi.fn().mockResolvedValue({ profile: stored, revision: 1, updatedAtUtc: null, projection: "synced" }) }, ["company.profile.view"]));
    await go("#/company/profile");
    expect(await screen.findByTestId("read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "حفظ" })).toBeNull();
    expect(screen.getByLabelText("المدينة")).toHaveAttribute("readonly");
  });

  it("renders in English LTR", async () => {
    renderApp(signedIn());
    await go("#/company/profile");
    await screen.findByTestId("profile-new");
    fireEvent.click(screen.getByRole("button", { name: "English" }));
    expect(await screen.findByRole("heading", { name: "Company profile" })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
  });
});

describe("company settings screen", () => {
  it("reports incomplete setup when no time zone is selected, and never invents one", async () => {
    const putSettings = vi.fn().mockResolvedValue({
      settings: { defaultLanguage: "ar", dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: null }, revision: 1, exists: true, setupComplete: false,
      updatedAtUtc: null, options: { languages: ["ar", "en"], dateFormats: ["yyyy-MM-dd"], numeralSystems: ["latin"] },
    });
    renderApp(signedIn({ putSettings }));
    await go("#/company/settings");
    expect(await screen.findByTestId("setup-incomplete")).toBeInTheDocument();
    expect(screen.getByLabelText("المنطقة الزمنية للأعمال")).toHaveValue("");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(putSettings).toHaveBeenCalled());
    expect(putSettings.mock.calls[0][0].timeZone).toBeNull();
    expect(putSettings.mock.calls[0][1]).toBe(0);
  });

  it("asks for confirmation before changing an established time zone", async () => {
    const settings = {
      settings: { defaultLanguage: "ar", dateFormat: "yyyy-MM-dd", numeralSystem: "latin", timeZone: "UTC" }, revision: 2, exists: true, setupComplete: true,
      updatedAtUtc: null, options: { languages: ["ar", "en"], dateFormats: ["yyyy-MM-dd"], numeralSystems: ["latin"] },
    };
    const putSettings = vi.fn().mockResolvedValue({ ...settings, revision: 3 });
    renderApp(signedIn({ getSettings: vi.fn().mockResolvedValue(settings), putSettings }));
    await go("#/company/settings");
    const tz = await screen.findByLabelText("المنطقة الزمنية للأعمال");
    const other = Array.from((tz as HTMLSelectElement).options).find((o) => o.value && o.value !== "UTC");
    if (other) {
      await userEvent.selectOptions(tz, other.value);
      await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
      const dialog = await screen.findByRole("dialog");
      expect(putSettings).not.toHaveBeenCalled();
      await userEvent.click(within(dialog).getByRole("button", { name: "إلغاء" }));
      expect(putSettings).not.toHaveBeenCalled();
      await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
      await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "تأكيد" }));
      await waitFor(() => expect(putSettings).toHaveBeenCalled());
    }
  });

  it("handles a settings conflict and an audit failure without losing the form", async () => {
    const putSettings = vi.fn().mockRejectedValueOnce(apiError(412, "revision_conflict")).mockRejectedValueOnce(apiError(503, "audit_unavailable"));
    renderApp(signedIn({ putSettings }));
    await go("#/company/settings");
    await userEvent.selectOptions(await screen.findByLabelText("صيغة التاريخ"), "dd/MM/yyyy");
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByTestId("conflict")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "حفظ" }));
    expect(await screen.findByText(/تعذر تسجيل التدقيق/)).toBeInTheDocument();
    expect(screen.getByLabelText("صيغة التاريخ")).toHaveValue("dd/MM/yyyy");
  });
});

describe("currencies screen", () => {
  it("warns that a base currency is required and sets it without confirmation the first time", async () => {
    const after = {
      currencies: [{ code: "SAR", nameEn: "Saudi Riyal", minorUnits: 2, isBase: true, enabled: true, revision: 1 }], baseCurrency: "SAR", baseCurrencySelected: true,
      baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: ["general_ledger"] },
    };
    const setCurrency = vi.fn().mockResolvedValue(after);
    renderApp(signedIn({ setCurrency }));
    await go("#/company/currencies");
    expect(await screen.findByTestId("no-base")).toBeInTheDocument();
    expect(screen.getByTestId("trial-note")).toBeInTheDocument();
    expect(screen.getByTestId("lock-note")).toHaveTextContent("لم تُنفَّذ");
    await userEvent.selectOptions(screen.getByLabelText("اختر العملة الأساسية"), "SAR");
    await userEvent.click(screen.getByRole("button", { name: "تعيين كعملة أساسية" }));
    await waitFor(() => expect(setCurrency).toHaveBeenCalledWith("SAR", { enabled: true, isBase: true }, 0));
    expect(await screen.findByTestId("base-currency")).toHaveTextContent("SAR");
  });

  it("confirms before replacing an established base currency", async () => {
    const current = {
      currencies: [{ code: "SAR", nameEn: "Saudi Riyal", minorUnits: 2, isBase: true, enabled: true, revision: 1 }], baseCurrency: "SAR", baseCurrencySelected: true,
      baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] },
    };
    const setCurrency = vi.fn().mockResolvedValue(current);
    renderApp(signedIn({ getCurrencies: vi.fn().mockResolvedValue(current), setCurrency }));
    await go("#/company/currencies");
    await userEvent.selectOptions(await screen.findByLabelText("اختر العملة الأساسية"), "USD");
    await userEvent.click(screen.getByRole("button", { name: "تعيين كعملة أساسية" }));
    expect(setCurrency).not.toHaveBeenCalled();
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(setCurrency).toHaveBeenCalledWith("USD", { enabled: true, isBase: true }, 0));
  });

  it("shows the Trial entitlement denial when enabling an additional currency", async () => {
    const current = {
      currencies: [{ code: "SAR", nameEn: "Saudi Riyal", minorUnits: 2, isBase: true, enabled: true, revision: 1 }], baseCurrency: "SAR", baseCurrencySelected: true,
      baseCurrencyLock: { locked: false, checksImplemented: false, unimplementedDomains: [] },
    };
    const setCurrency = vi.fn().mockRejectedValue(apiError(403, "entitlement_denied"));
    renderApp(signedIn({ getCurrencies: vi.fn().mockResolvedValue(current), setCurrency }));
    await go("#/company/currencies");
    await userEvent.selectOptions(await screen.findByLabelText("إضافة عملة"), "USD");
    await userEvent.click(screen.getByRole("button", { name: "تفعيل" }));
    expect(await screen.findByText(/النسخة التجريبية تسمح بعملة أساسية واحدة فقط/)).toBeInTheDocument();
    expect(screen.getByTestId("base-currency")).toHaveTextContent("SAR"); // unchanged
  });

  it("offers no editing controls without the update permission", async () => {
    renderApp(signedIn({}, ["company.currencies.view"]));
    await go("#/company/currencies");
    expect(await screen.findByTestId("read-only")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "تعيين كعملة أساسية" })).toBeNull();
  });
});

describe("history screen", () => {
  it("lists revisions and never shows values of sensitive fields", async () => {
    const getHistory = vi.fn().mockResolvedValue({
      entries: [{
        id: 2, entity: "profile", key: "", revision: 2, actorUserId: "u1", changedAtUtc: "2026-01-02T10:00:00Z", auditSeq: 5,
        changes: { taxRegistrationNo: { changed: true }, city: { old: "Riyadh", new: "Jeddah" } },
      }],
    });
    renderApp(signedIn({ getHistory }));
    await go("#/company/history");
    await screen.findByText(/تغيّر/);
    expect(document.body.textContent).toContain("Riyadh → Jeddah");
    expect(screen.getByText(/تغيّر \(القيمة غير معروضة\)/)).toBeInTheDocument();
  });

  it("shows an empty state", async () => {
    renderApp(signedIn());
    await go("#/company/history");
    expect(await screen.findByTestId("history-empty")).toBeInTheDocument();
  });
});
