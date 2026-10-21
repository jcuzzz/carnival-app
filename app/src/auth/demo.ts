import { ApiError, apiBase } from "../api/client";
import type { ApiErrorBody } from "../api/types";
import { fetchMe } from "./entra";
import { signIn } from "./session";

/**
 * Starts (or, with a live cookie, resumes) this visitor's sandbox and adopts it (ADR-0061 §3).
 * Raw fetch: the demo routes exist only on a demo host, so they are not in the generated client.
 */
export async function startDemo(): Promise<void> {
  const url = `${apiBase()}/api/v1/demo/sessions`;
  const response = await fetch(url, { method: "POST", credentials: "include" });
  if (!response.ok) {
    let body: ApiErrorBody | undefined;
    try {
      body = (await response.json()) as ApiErrorBody;
    } catch {
      body = undefined;
    }
    throw new ApiError(response.status, body, url);
  }
  const session = await fetchMe();
  if (!session) throw new ApiError(401, undefined, url);
  signIn(session);
}
