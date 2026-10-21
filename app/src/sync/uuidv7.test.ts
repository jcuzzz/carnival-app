import { beforeEach, describe, expect, it } from "vitest";
import { _resetUuidv7StateForTests, uuidv7 } from "./uuidv7";

const UUIDV7_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe("uuidv7", () => {
  beforeEach(() => _resetUuidv7StateForTests());

  it("produces RFC 9562 v7 format (version and variant bits)", () => {
    for (let i = 0; i < 100; i++) {
      expect(uuidv7()).toMatch(UUIDV7_RE);
    }
  });

  it("encodes the capture timestamp in the first 48 bits", () => {
    const fixed = 1_753_600_000_123;
    const id = uuidv7(() => fixed);
    const millis = parseInt(id.slice(0, 8) + id.slice(9, 13), 16);
    expect(millis).toBe(fixed);
  });

  it("is strictly monotonic within the same millisecond", () => {
    const ids = Array.from({ length: 500 }, () => uuidv7(() => 1_753_600_000_000));
    const sorted = [...ids].sort();
    expect(sorted).toEqual(ids);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("remains monotonic when the clock goes backwards", () => {
    const a = uuidv7(() => 2_000_000_000_000);
    const b = uuidv7(() => 1_999_999_999_000); // clock regression
    expect(b > a).toBe(true);
  });

  it("is monotonic across many real-time generations", () => {
    const ids = Array.from({ length: 2000 }, () => uuidv7());
    const sorted = [...ids].sort();
    expect(sorted).toEqual(ids);
    expect(new Set(ids).size).toBe(ids.length);
  });
});
