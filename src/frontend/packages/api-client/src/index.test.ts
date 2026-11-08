import { describe, expect, it, vi } from "vitest";
import { ApiError, createApiClient } from "./index";

describe("api client", () => {
  it("requests the health endpoint under the base url", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ status: "healthy", service: "hasm-api", version: "1.0.0" })),
    );
    const client = createApiClient("https://localhost:7241/", fetchMock as unknown as typeof fetch);

    const health = await client.getHealth();

    expect(fetchMock.mock.calls[0][0]).toBe("https://localhost:7241/api/v1/health");
    expect(health.status).toBe("healthy");
  });

  it("throws ApiError on non-success responses", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response("", { status: 503 }));
    const client = createApiClient("https://x", fetchMock as unknown as typeof fetch);

    await expect(client.getHealth()).rejects.toBeInstanceOf(ApiError);
  });
});
