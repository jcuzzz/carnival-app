import type { ApiFile } from "../../api/client";

/** Long enough for any browser to have started the save; exported for the test. */
export const REVOKE_DELAY_MS = 60_000;

/**
 * Hand a downloaded file to the browser. An object URL rather than a data URL:
 * an export is arbitrarily large and a data URL would have to be built as one
 * string in memory first.
 */
export function saveFile(file: ApiFile): void {
  const url = URL.createObjectURL(file.blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = file.filename;
  document.body.append(link);
  link.click();
  link.remove();
  // WebKit and Firefox read the blob AFTER the click returns, so revoking
  // synchronously cancels the download (iPad is a pilot device).
  setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS);
}
