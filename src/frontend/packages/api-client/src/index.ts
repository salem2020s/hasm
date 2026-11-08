export interface HealthResponse {
  status: string;
  service: string;
  version: string;
}

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

/** Typed HTTP client for the HASM Backend API. It never talks to the database. */
export function createApiClient(baseUrl: string, fetchImpl: typeof fetch = (...args) => fetch(...args)) {
  const root = baseUrl.replace(/\/+$/, "");

  async function get<T>(path: string): Promise<T> {
    const response = await fetchImpl(`${root}${path}`, { headers: { Accept: "application/json" } });
    if (!response.ok) {
      throw new ApiError(response.status, `Request to ${path} failed with ${response.status}`);
    }
    return (await response.json()) as T;
  }

  return { getHealth: () => get<HealthResponse>("/api/v1/health") };
}

export type ApiClient = ReturnType<typeof createApiClient>;
