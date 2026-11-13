import type { FiscalPeriod, FiscalPeriodStatus, FiscalYear, FiscalYearsResponse } from "@hasm/api-client";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ALL_PERMISSIONS, apiError, renderApp, signedIn } from "./test-utils";

const go = async (hash: string) => {
  await screen.findByTestId("company-name");
  window.location.hash = hash;
  fireEvent(window, new HashChangeEvent("hashchange"));
};

const PERMS = [...ALL_PERMISSIONS, "company.fiscalyears.view"];

const period = (n: number, status: FiscalPeriodStatus = "open", over: Partial<FiscalPeriod> = {}): FiscalPeriod => ({
  id: `p${n}`, yearId: "y1", number: n, startDate: `2026-${String(n).padStart(2, "0")}-01`, endDate: `2026-${String(n).padStart(2, "0")}-28`,
  status, statusChangedAtUtc: null, revision: 1, ...over,
});
const year = (over: Partial<FiscalYear> = {}, periods: FiscalPeriod[] = [period(1), period(2, "soft_closed"), period(3, "final_closed")]): FiscalYear => ({
  id: "y1", name: "FY2026", startDate: "2026-01-01", endDate: "2026-12-31", periodType: "monthly", timeZone: "UTC", closed: false, closedAtUtc: null,
  revision: 1, createdAtUtc: "2026-01-01T00:00:00Z", periods, ...over,
});

const ALL = { create: true, closeYear: true, reopenYear: true, softClose: true, reopen: true, finalClose: true, reopenFinal: true };
const NONE = { create: false, closeYear: false, reopenYear: false, softClose: false, reopen: false, finalClose: false, reopenFinal: false };
const response = (years: FiscalYear[], over: Partial<FiscalYearsResponse> = {}, can: Partial<FiscalYearsResponse["can"]> = {}): FiscalYearsResponse => ({
  years, today: "2026-11-09", closeChecksImplemented: false, can: { ...ALL, ...can }, ...over,
});

const render = (listFiscalYears: unknown, extra: Record<string, unknown> = {}, permissions = PERMS) => {
  const client = signedIn({ listFiscalYears, ...extra }, permissions);
  renderApp(client);
  return client;
};

beforeEach(() => { localStorage.clear(); window.location.hash = ""; });

describe("fiscal calendar screen: list and honesty about close checks", () => {
  it("lists years with their periods, states and the not-implemented close-check notice", async () => {
    render(vi.fn().mockResolvedValue(response([year()])));
    await go("#/company/fiscal-years");
    expect(await screen.findByRole("heading", { name: "التقويم المالي", level: 1 })).toBeInTheDocument();
    const y = screen.getByTestId("year-FY2026");
    expect(within(y).getAllByText("مفتوحة", { selector: ".chip" })).toHaveLength(2);   // the year and its open period
    expect(within(y).getByText("فترات شهرية")).toBeInTheDocument();
    expect(within(screen.getByTestId("period-FY2026-1")).getByText("مفتوحة")).toBeInTheDocument();
    expect(within(screen.getByTestId("period-FY2026-2")).getByText("مقفلة مؤقتاً")).toBeInTheDocument();
    expect(within(screen.getByTestId("period-FY2026-3")).getByText("مقفلة نهائياً")).toBeInTheDocument();
    expect(screen.getByTestId("checks-not-available")).toHaveTextContent("لا يُعد تحققاً محاسبياً");
  });

  it("hides the close-check notice once the server reports the checks as implemented", async () => {
    render(vi.fn().mockResolvedValue(response([year()], { closeChecksImplemented: true })));
    await go("#/company/fiscal-years");
    await screen.findByTestId("year-FY2026");
    expect(screen.queryByTestId("checks-not-available")).not.toBeInTheDocument();
  });

  it("shows an empty state and a missing-time-zone notice that blocks creating a year", async () => {
    render(vi.fn().mockResolvedValue(response([], { today: null })));
    await go("#/company/fiscal-years");
    expect(await screen.findByText("لا توجد سنوات مالية بعد.")).toBeInTheDocument();
    expect(screen.getByTestId("no-time-zone")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "سنة مالية جديدة" })).toBeDisabled();
  });
});

describe("fiscal calendar screen: permissions", () => {
  it("shows no action to a view-only user", async () => {
    render(vi.fn().mockResolvedValue(response([year()], {}, NONE)));
    await go("#/company/fiscal-years");
    expect(await screen.findByTestId("read-only")).toBeInTheDocument();
    for (const name of ["سنة مالية جديدة", "إقفال مؤقت", "إعادة فتح", "إقفال نهائي", "إعادة فتح استثنائية", "إقفال السنة"])
      expect(screen.queryByRole("button", { name })).not.toBeInTheDocument();
  });

  it("offers each action only for the matching state and permission", async () => {
    render(vi.fn().mockResolvedValue(response([year()], {}, { reopenFinal: false })));
    await go("#/company/fiscal-years");
    await screen.findByTestId("year-FY2026");
    expect(within(screen.getByTestId("period-FY2026-1")).getByRole("button", { name: "إقفال مؤقت" })).toBeInTheDocument();
    expect(within(screen.getByTestId("period-FY2026-1")).queryByRole("button", { name: "إقفال نهائي" })).not.toBeInTheDocument();   // Open never jumps to Final
    const soft = screen.getByTestId("period-FY2026-2");
    expect(within(soft).getByRole("button", { name: "إعادة فتح" })).toBeInTheDocument();
    expect(within(soft).getByRole("button", { name: "إقفال نهائي" })).toBeInTheDocument();
    const final = screen.getByTestId("period-FY2026-3");
    expect(within(final).queryByRole("button")).not.toBeInTheDocument();                                                        // exceptional reopening needs its own permission
    expect(screen.getByRole("button", { name: "إقفال السنة" })).toBeInTheDocument();
  });

  it("separates the permissions: a user who may only soft close sees no final close or reopen", async () => {
    render(vi.fn().mockResolvedValue(response([year()], {}, { ...NONE, softClose: true })));
    await go("#/company/fiscal-years");
    await screen.findByTestId("year-FY2026");
    expect(screen.getAllByRole("button", { name: "إقفال مؤقت" })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "إقفال نهائي" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "إعادة فتح" })).not.toBeInTheDocument();
  });

  it("refuses the route without the fiscal view permission", async () => {
    render(vi.fn(), {}, ALL_PERMISSIONS);
    await go("#/company/fiscal-years");
    expect(await screen.findByTestId("forbidden")).toBeInTheDocument();
  });

  it("offers no period action in a closed year (its periods are frozen) but offers reopening the year to a holder", async () => {
    const closed = year({ closed: true, closedAtUtc: "2027-01-05T00:00:00Z" }, [period(1, "final_closed")]);
    render(vi.fn().mockResolvedValue(response([closed])));
    await go("#/company/fiscal-years");
    await screen.findByTestId("year-FY2026");
    expect(within(screen.getByTestId("period-FY2026-1")).queryByRole("button")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "إقفال السنة" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "إعادة فتح السنة" })).toBeInTheDocument();
  });
});

describe("fiscal calendar screen: closing and reopening", () => {
  it("soft closes a period after confirmation, sending its revision", async () => {
    const softCloseFiscalPeriod = vi.fn().mockResolvedValue(period(1, "soft_closed"));
    const list = vi.fn().mockResolvedValue(response([year()]));
    render(list, { softCloseFiscalPeriod });
    await go("#/company/fiscal-years");
    await userEvent.click(within(await screen.findByTestId("period-FY2026-1")).getByRole("button", { name: "إقفال مؤقت" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(softCloseFiscalPeriod).toHaveBeenCalledWith("p1", 1));
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم تحديث التقويم المالي.");
    expect(list.mock.calls.length).toBeGreaterThan(1);                                                                           // reloaded from the server
  });

  it("final closes after a warning that explains the exceptional reopening", async () => {
    const finalCloseFiscalPeriod = vi.fn().mockResolvedValue(period(2, "final_closed"));
    render(vi.fn().mockResolvedValue(response([year()])), { finalCloseFiscalPeriod });
    await go("#/company/fiscal-years");
    await userEvent.click(within(await screen.findByTestId("period-FY2026-2")).getByRole("button", { name: "إقفال نهائي" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("مستخدماً آخر غير من أقفلها");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(finalCloseFiscalPeriod).toHaveBeenCalledWith("p2", 1));
  });

  it("reopens a soft closed period", async () => {
    const reopenFiscalPeriod = vi.fn().mockResolvedValue(period(2));
    render(vi.fn().mockResolvedValue(response([year()])), { reopenFiscalPeriod });
    await go("#/company/fiscal-years");
    await userEvent.click(within(await screen.findByTestId("period-FY2026-2")).getByRole("button", { name: "إعادة فتح" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(reopenFiscalPeriod).toHaveBeenCalledWith("p2", 1));
  });

  it("closes the year after a message that says only the state is recorded", async () => {
    const closeFiscalYear = vi.fn().mockResolvedValue(year({ closed: true }));
    render(vi.fn().mockResolvedValue(response([year()])), { closeFiscalYear });
    await go("#/company/fiscal-years");
    await userEvent.click(await screen.findByRole("button", { name: "إقفال السنة" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("لا تُنشأ قيود إقفال ولا أرصدة مرحّلة");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    await waitFor(() => expect(closeFiscalYear).toHaveBeenCalledWith("y1", 1));
  });

  it("requires a reason of at least 10 characters for the exceptional reopening and sends it", async () => {
    const reopenFinalFiscalPeriod = vi.fn().mockResolvedValue(period(3, "soft_closed"));
    render(vi.fn().mockResolvedValue(response([year()])), { reopenFinalFiscalPeriod });
    await go("#/company/fiscal-years");
    await userEvent.click(within(await screen.findByTestId("period-FY2026-3")).getByRole("button", { name: "إعادة فتح استثنائية" }));
    const dialog = screen.getByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "إعادة فتح استثنائية" }));
    expect(within(dialog).getByText("اكتب سبباً من 10 أحرف على الأقل.")).toBeInTheDocument();
    expect(reopenFinalFiscalPeriod).not.toHaveBeenCalled();
    await userEvent.type(within(dialog).getByLabelText(/سبب إعادة الفتح/), "short");
    await userEvent.click(within(dialog).getByRole("button", { name: "إعادة فتح استثنائية" }));
    expect(reopenFinalFiscalPeriod).not.toHaveBeenCalled();
    await userEvent.clear(within(dialog).getByLabelText(/سبب إعادة الفتح/));
    await userEvent.type(within(dialog).getByLabelText(/سبب إعادة الفتح/), "Correction of an error found in audit");
    await userEvent.click(within(dialog).getByRole("button", { name: "إعادة فتح استثنائية" }));
    await waitFor(() => expect(reopenFinalFiscalPeriod).toHaveBeenCalledWith("p3", 1, "Correction of an error found in audit"));
  });

  it("reopens a closed year with a recorded reason", async () => {
    const reopenFiscalYear = vi.fn().mockResolvedValue(year());
    render(vi.fn().mockResolvedValue(response([year({ closed: true, revision: 4 }, [period(1, "final_closed")])])), { reopenFiscalYear });
    await go("#/company/fiscal-years");
    await userEvent.click(await screen.findByRole("button", { name: "إعادة فتح السنة" }));
    const dialog = screen.getByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/سبب إعادة الفتح/), "Documented correction after year close");
    await userEvent.click(within(dialog).getByRole("button", { name: "إعادة فتح السنة" }));
    await waitFor(() => expect(reopenFiscalYear).toHaveBeenCalledWith("y1", 4, "Documented correction after year close"));
  });

  it("shows the server's refusals in words and a stale-revision conflict banner that reloads", async () => {
    const softCloseFiscalPeriod = vi.fn()
      .mockRejectedValueOnce(apiError(409, "period_close_blocked"))
      .mockRejectedValueOnce(apiError(412, "revision_conflict", { currentRevision: 5 }));
    const finalCloseFiscalPeriod = vi.fn().mockRejectedValue(apiError(403, "segregation_of_duties"));
    const list = vi.fn().mockResolvedValue(response([year()]));
    render(list, { softCloseFiscalPeriod, finalCloseFiscalPeriod });
    await go("#/company/fiscal-years");
    await userEvent.click(within(await screen.findByTestId("period-FY2026-1")).getByRole("button", { name: "إقفال مؤقت" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByText("توجد فروقات أو بنود معلّقة تمنع الإقفال.")).toBeInTheDocument();
    await userEvent.click(within(screen.getByTestId("period-FY2026-1")).getByRole("button", { name: "إقفال مؤقت" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByTestId("conflict")).toBeInTheDocument();
    const before = list.mock.calls.length;
    await userEvent.click(screen.getByRole("button", { name: "تحميل أحدث نسخة" }));
    await waitFor(() => expect(list.mock.calls.length).toBeGreaterThan(before));
    await userEvent.click(within(screen.getByTestId("period-FY2026-2")).getByRole("button", { name: "إقفال نهائي" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "تأكيد" }));
    expect(await screen.findByText("لا يجوز لمن أقفل الفترة نهائياً أن يعيد فتحها (فصل المهام).")).toBeInTheDocument();
  });
});

describe("fiscal calendar screen: creating a year", () => {
  it("suggests the first year for the current calendar year and sends first-of-month dates", async () => {
    const created = year({ id: "y9", name: "FY 2026" });
    const createFiscalYear = vi.fn().mockResolvedValue(created);
    const list = vi.fn().mockResolvedValueOnce(response([])).mockResolvedValue(response([created]));
    render(list, { createFiscalYear });
    await go("#/company/fiscal-years");
    await userEvent.click(await screen.findByRole("button", { name: "سنة مالية جديدة" }));
    expect(screen.getByLabelText(/اسم السنة/)).toHaveValue("FY 2026");
    await userEvent.selectOptions(screen.getByLabelText("تقسيم السنة"), "quarterly");
    await userEvent.selectOptions(screen.getByLabelText("شهر البداية"), "7");
    await userEvent.click(screen.getByRole("button", { name: "إنشاء السنة" }));
    await waitFor(() => expect(createFiscalYear).toHaveBeenCalledWith({ name: "FY 2026", startDate: "2026-07-01", periodType: "quarterly" }));
    expect(await screen.findByTestId("saved")).toHaveTextContent("تم إنشاء السنة المالية وفتراتها.");
    expect(await screen.findByTestId("year-FY 2026")).toBeInTheDocument();
  });

  it("fixes the start of a following year to the day after the latest year", async () => {
    render(vi.fn().mockResolvedValue(response([year()])));
    await go("#/company/fiscal-years");
    await userEvent.click(await screen.findByRole("button", { name: "سنة مالية جديدة" }));
    expect(screen.getByLabelText(/سنة البداية/)).toHaveValue("2027");
    expect(screen.getByLabelText(/سنة البداية/)).toHaveAttribute("readonly");
    expect(screen.getByLabelText("شهر البداية")).toBeDisabled();
    expect(screen.getByLabelText(/اسم السنة/)).toHaveValue("FY 2027");
  });

  it("validates client-side and maps server refusals to the form without losing input", async () => {
    const createFiscalYear = vi.fn()
      .mockRejectedValueOnce(apiError(409, "fiscal_year_name_taken"))
      .mockRejectedValueOnce(apiError(409, "company_time_zone_not_configured"));
    const client = render(vi.fn().mockResolvedValue(response([])), { createFiscalYear });
    await go("#/company/fiscal-years");
    await userEvent.click(await screen.findByRole("button", { name: "سنة مالية جديدة" }));
    await userEvent.clear(screen.getByLabelText(/اسم السنة/));
    await userEvent.click(screen.getByRole("button", { name: "إنشاء السنة" }));
    expect(screen.getByLabelText(/اسم السنة/)).toHaveAttribute("aria-invalid", "true");
    expect(client.createFiscalYear).not.toHaveBeenCalled();
    await userEvent.type(screen.getByLabelText(/اسم السنة/), "Mine");
    await userEvent.click(screen.getByRole("button", { name: "إنشاء السنة" }));
    expect(await screen.findByText("اسم السنة المالية مستخدم بالفعل.")).toBeInTheDocument();
    expect(screen.getByLabelText(/اسم السنة/)).toHaveValue("Mine");
    await userEvent.click(screen.getByRole("button", { name: "إنشاء السنة" }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent("لم تُحدَّد المنطقة الزمنية للشركة بعد.");
  });

  it("protects an edited form against leaving the page", async () => {
    render(vi.fn().mockResolvedValue(response([])));
    await go("#/company/fiscal-years");
    await userEvent.click(await screen.findByRole("button", { name: "سنة مالية جديدة" }));
    await userEvent.type(screen.getByLabelText(/اسم السنة/), " draft");
    await go("#/company/currencies");
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "البقاء في الصفحة" }));
    expect(screen.getByLabelText(/اسم السنة/)).toHaveValue("FY 2026 draft");
  });
});

describe("fiscal calendar screen: languages", () => {
  it("renders Arabic right-to-left and English left-to-right with the same screen", async () => {
    render(vi.fn().mockResolvedValue(response([year()])));
    await go("#/company/fiscal-years");
    await screen.findByTestId("year-FY2026");
    expect(document.documentElement.dir).toBe("rtl");
    await userEvent.click(screen.getByRole("button", { name: /English/ }));
    expect(await screen.findByRole("heading", { name: "Fiscal calendar", level: 1 })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("ltr");
    expect(within(screen.getByTestId("period-FY2026-2")).getByText("Soft closed")).toBeInTheDocument();
    expect(within(screen.getByTestId("period-FY2026-3")).getByText("Final closed")).toBeInTheDocument();
    expect(screen.getByTestId("checks-not-available")).toHaveTextContent("not an accounting verification");
    await userEvent.click(screen.getByRole("button", { name: "New fiscal year" }));
    expect(screen.getByLabelText(/Year name/)).toBeInTheDocument();
  });
});
