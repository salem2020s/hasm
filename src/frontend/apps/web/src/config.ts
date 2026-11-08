/** API base URL comes from the environment (VITE_API_BASE_URL); only a local development default is built in. */
export const apiBaseUrl: string = import.meta.env.VITE_API_BASE_URL ?? "https://localhost:7241";
