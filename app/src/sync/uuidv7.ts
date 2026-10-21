/**
 * Within one millisecond `rand_a` is a sequence counter (RFC 9562 §6.2 method 1), and a clock
 * going backwards reuses the last timestamp, so ids on one device sort in generation order.
 */

let lastMillis = -1;
let sequence = 0;

function randomBytes(length: number): Uint8Array {
  const bytes = new Uint8Array(length);
  crypto.getRandomValues(bytes);
  return bytes;
}

export function uuidv7(now: () => number = Date.now): string {
  let millis = now();
  if (millis <= lastMillis) {
    millis = lastMillis;
    sequence += 1;
    if (sequence > 0xfff) {
      millis += 1;
      sequence = 0;
    }
  } else {
    // Starts at 0 for maximal headroom; cross-device uniqueness comes from the 62 random bits.
    sequence = 0;
  }
  lastMillis = millis;

  const bytes = new Uint8Array(16);
  bytes[0] = (millis / 2 ** 40) & 0xff;
  bytes[1] = (millis / 2 ** 32) & 0xff;
  bytes[2] = (millis / 2 ** 24) & 0xff;
  bytes[3] = (millis / 2 ** 16) & 0xff;
  bytes[4] = (millis / 2 ** 8) & 0xff;
  bytes[5] = millis & 0xff;

  bytes[6] = 0x70 | ((sequence >> 8) & 0x0f);
  bytes[7] = sequence & 0xff;

  const rand = randomBytes(8);
  bytes[8] = 0x80 | (rand[0]! & 0x3f);
  for (let i = 1; i < 8; i++) bytes[8 + i] = rand[i]!;

  const hex: string[] = [];
  for (const b of bytes) hex.push(b.toString(16).padStart(2, "0"));
  const h = hex.join("");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

export function _resetUuidv7StateForTests(): void {
  lastMillis = -1;
  sequence = 0;
}
