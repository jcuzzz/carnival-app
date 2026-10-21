import { describe, expect, it } from "vitest";
import { ApiError } from "./client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText, NETWORK_ERROR_TEXT } from "./errorText";

describe("errorText", () => {
  it("shows the server's own message for an ApiError", () => {
    expect(errorText(new ApiError(409, { error: "Event is not Open." }, "/x"))).toBe(
      "Event is not Open.",
    );
  });

  // K1: `detail` is the sentence for a person; `error` is a code for the
  // machine and must never reach a teacher's screen.
  it("shows detail, never the machine code, for an ADR-0011 body", () => {
    const failure = new ApiError(
      409,
      { error: "EVENT_NOT_OPEN", detail: "This event is not open for results yet." },
      "/x",
    );
    expect(errorText(failure)).toBe("This event is not open for results yet.");
    expect(errorText(failure)).not.toContain("EVENT_NOT_OPEN");
  });

  /**
   * ADR-0011 consequences: official-facing copy lives here, with `detail` as
   * the fallback. These two codes carry server details addressed to whoever
   * reads a log; the person reading this one is on a pool deck.
   */
  it("replaces the developer detail for an unreadable body with copy an official can act on", () => {
    const failure = new ApiError(
      400,
      {
        error: "REQUEST_INVALID",
        detail: "Check that the body is valid JSON and matches the documented shape.",
      },
      "/x",
    );
    const text = errorText(failure);
    expect(text).not.toContain("JSON");
    expect(text).not.toContain("REQUEST_INVALID");
    // Says what happened, that the work is not lost, and who fixes it.
    expect(text).toContain("nothing was recorded");
    expect(text).toContain("still held on this device");
    expect(text).toContain("carnival organiser");
  });

  it("tells an official a too-large submission is not theirs to retry", () => {
    const failure = new ApiError(
      413,
      { error: "REQUEST_BODY_TOO_LARGE", detail: "The request body exceeded the maximum accepted size." },
      "/x",
    );
    const text = errorText(failure);
    expect(text).not.toContain("REQUEST_BODY_TOO_LARGE");
    expect(text).toContain("nothing was recorded");
    expect(text).toContain("size limit");
    // The two request-shape codes must not read identically — the actions differ.
    expect(text).not.toBe(
      errorText(new ApiError(400, { error: "REQUEST_INVALID", detail: "d" }, "/x")),
    );
  });

  it("leaves any other code's server detail alone — most details are fine", () => {
    expect(
      errorText(new ApiError(409, { error: "RESULT_EXISTS", detail: "This entry already has a result." }, "/x")),
    ).toBe("This entry already has a result.");
  });

  it("turns a fetch network failure into the offline-first reassurance", () => {
    expect(errorText(new TypeError("Failed to fetch"))).toBe(NETWORK_ERROR_TEXT);
  });

  it("does not promise a save on a screen that has no outbox to make one", () => {
    // The schedule builder writes directly, so the shared reassurance would be
    // false there — the caller supplies the wording that fits its own screen.
    const offline = errorText(new TypeError("Failed to fetch"), DIRECT_WRITE_NETWORK_ERROR_TEXT);
    expect(offline).toBe(DIRECT_WRITE_NETWORK_ERROR_TEXT);
    expect(offline).toContain("nothing was saved");
    expect(DIRECT_WRITE_NETWORK_ERROR_TEXT).not.toContain("saved on this device");
  });

  it("leaves an ApiError alone whichever offline wording the caller passes", () => {
    const refused = new ApiError(409, { detail: "Slot changed." }, "/x");
    expect(errorText(refused, DIRECT_WRITE_NETWORK_ERROR_TEXT)).toBe("Slot changed.");
  });

  it("falls back to the message of any other Error, and to String() otherwise", () => {
    expect(errorText(new Error("Quota exceeded"))).toBe("Quota exceeded");
    expect(errorText("something odd")).toBe("something odd");
  });
});
