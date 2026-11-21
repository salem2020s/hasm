// @vitest-environment jsdom
// MFA step-up in the shared api client (Phase 4.1), identical on both transports: a 401 `mfa_step_up_required` on an authenticated call is NOT a session end; the client asks once, repeats the identical request once, and
// reports a cancelled step-up as `mfa_step_up_cancelled` (never as a session end). Every other 401 still ends the session.
import { describe, expect, it, vi } from "vitest";
import { ApiError, createApiClient, type ClientKind } from "./index";

const json = (body: unknown, init: ResponseInit = {}) => new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" }, ...init });
const STALE = () => json({ error: "mfa_step_up_required" }, { status: 401 });
const ACCOUNT = { id: "a1" };
const INPUT = { code: null, nameAr: "x", type: "asset" } as never;

function make(kind: ClientKind, replies: (() => Response)[], stepUp?: () => Promise<boolean>) {
  const queue = [...replies];
  const fetchMock = vi.fn().mockImplementation(async () => { const r = queue.shift(); if (!r) throw new Error("no more answers"); return r(); });
  const unauth = vi.fn();
  const client = createApiClient("https://x", fetchMock as unknown as typeof fetch, { client: kind, onUnauthenticated: unauth, stepUp });
  return { client, fetchMock, unauth };
}
const sent = (fetchMock: ReturnType<typeof vi.fn>, n: number) => fetchMock.mock.calls[n]![1] as RequestInit & { headers: Record<string, string> };

describe.each<ClientKind>(["web", "desktop"])("%s transport: step-up", (kind) => {
  it("asks once on a stale proof, repeats the IDENTICAL request once, returns the result and never ends the session", async () => {
    const stepUp = vi.fn().mockResolvedValue(true);
    const { client, fetchMock, unauth } = make(kind, [STALE, () => json(ACCOUNT, { status: 201 })], stepUp);
    const created = await client.createAccount(INPUT);
    expect(created).toEqual(ACCOUNT);
    expect(stepUp).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1]![0]).toBe(fetchMock.mock.calls[0]![0]);
    expect(sent(fetchMock, 1)).toEqual(sent(fetchMock, 0));                                            // method, headers, credentials, body: unchanged
    expect(unauth).not.toHaveBeenCalled();
  });

  it("a cancelled or failed step-up leaves the request undone: ApiError mfa_step_up_cancelled, no repeat, no session end", async () => {
    const { client, fetchMock, unauth } = make(kind, [STALE], vi.fn().mockResolvedValue(false));
    const error = await client.createAccount(INPUT).catch((e) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(error.code).toBe("mfa_step_up_cancelled");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(unauth).not.toHaveBeenCalled();
  });

  it("without a step-up handler the caller gets mfa_step_up_required itself, and the session is not ended", async () => {
    const { client, unauth } = make(kind, [STALE]);
    const error = await client.createAccount(INPUT).catch((e) => e);
    expect([error.status, error.code]).toEqual([401, "mfa_step_up_required"]);
    expect(unauth).not.toHaveBeenCalled();
  });

  it("a server that is still stale after a good step-up is asked once more at most: no loop", async () => {
    const stepUp = vi.fn().mockResolvedValue(true);
    const { client, fetchMock, unauth } = make(kind, [STALE, STALE], stepUp);
    const error = await client.createAccount(INPUT).catch((e) => e);
    expect(error.code).toBe("mfa_step_up_required");
    expect(stepUp).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(unauth).not.toHaveBeenCalled();
  });

  it("any OTHER 401 still ends the session and does not ask for a step-up", async () => {
    const stepUp = vi.fn().mockResolvedValue(true);
    for (const body of [{ error: "unauthenticated" }, { error: "something_else" }, {}]) {
      const { client, unauth } = make(kind, [() => json(body, { status: 401 })], stepUp);
      await client.createAccount(INPUT).catch(() => undefined);
      expect(unauth).toHaveBeenCalledTimes(1);
    }
    expect(stepUp).not.toHaveBeenCalled();
  });

  it("the step-up call: success sends only the code with the anti-CSRF header; a wrong code (401 invalid_code) does NOT end the session; a lost session (401 unauthenticated) does; the throttle is a 429", async () => {
    const ok = make(kind, [() => json({ verifiedAtUtc: "2026-11-21T10:00:00Z" })]);
    expect(await ok.client.mfaStepUp("123456")).toEqual({ verifiedAtUtc: "2026-11-21T10:00:00Z" });
    expect(ok.fetchMock.mock.calls[0]![0]).toBe("https://x/api/v1/auth/mfa/step-up");
    expect(sent(ok.fetchMock, 0).method).toBe("POST");
    expect(sent(ok.fetchMock, 0).headers["X-Hasm-Request"]).toBe("1");
    expect(JSON.parse(sent(ok.fetchMock, 0).body as string)).toEqual({ code: "123456" });
    expect(sent(ok.fetchMock, 0).credentials).toBe(kind === "web" ? "include" : "omit");

    const wrong = make(kind, [() => json({ error: "invalid_code" }, { status: 401 })]);
    expect(await wrong.client.mfaStepUp("000000").catch((e) => e)).toMatchObject({ status: 401, code: "invalid_code" });
    expect(wrong.unauth).not.toHaveBeenCalled();

    const gone = make(kind, [() => json({ error: "unauthenticated" }, { status: 401 })]);
    await gone.client.mfaStepUp("123456").catch(() => undefined);
    expect(gone.unauth).toHaveBeenCalledTimes(1);

    const throttled = make(kind, [() => json({ error: "too_many_attempts" }, { status: 429, headers: { "Retry-After": "600" } })]);
    expect(await throttled.client.mfaStepUp("123456").catch((e) => e)).toMatchObject({ status: 429, code: "too_many_attempts", details: { retryAfterSeconds: 600 } });
    expect(throttled.unauth).not.toHaveBeenCalled();
  });
});

describe("the two transports behave identically for step-up", () => {
  it("the same script gives the same outcome and the same request sequence apart from the credential", async () => {
    const outcomes = await Promise.all((["web", "desktop"] as ClientKind[]).map(async (kind) => {
      const { client, fetchMock, unauth } = make(kind, [STALE, () => json(ACCOUNT, { status: 201 })], vi.fn().mockResolvedValue(true));
      const result = await client.createAccount(INPUT);
      return { result, calls: fetchMock.mock.calls.map((c) => [c[0], (c[1] as RequestInit).method, (c[1] as RequestInit).body]), unauth: unauth.mock.calls.length };
    }));
    expect(outcomes[1]).toEqual(outcomes[0]);
  });
});
