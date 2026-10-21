// Regenerate src/api/generated.d.ts from the API's OpenAPI document
// (ADR-0012 / assessment finding K6).
//
//   1. start the API:  pwsh scripts\dev-run.ps1 -ApiOnly     (from the repo root)
//   2. then:           npm run generate:types                (from app/)
//   3. commit the diff — the generated file is TRACKED, so `npm ci && npm run
//      build` never needs a live API, and a server contract change shows up as
//      a reviewable diff plus a type error instead of a runtime `undefined`.
//
// The document is Development-only (Program.cs maps it under IsDevelopment), so
// there is nothing to generate from in a deployment — by design.

import { writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import openapiTS, { astToString } from "openapi-typescript";

const url = process.env.OPENAPI_URL ?? "http://localhost:5080/openapi/v1.json";
const out = fileURLToPath(new URL("../src/api/generated.d.ts", import.meta.url));

let document;
try {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  document = await response.json();
} catch (err) {
  console.error(
    `Could not read the OpenAPI document from ${url} (${err.message}).\n` +
      "Start the dev API first:  pwsh scripts\\dev-run.ps1 -ApiOnly",
  );
  process.exit(1);
}

const banner = `/**
 * GENERATED FILE — do not edit by hand.
 *
 * \`npm run generate:types\` rewrites this from the API's OpenAPI document
 * (ADR-0012). Wire types are re-exported with app-facing names from
 * ./types.ts — import from there, not from here.
 */
`;

// enum: false keeps string enums as unions ("Ok" | "Dns" | …), which is what the
// app compares against; a TS enum would need a value import at every call site.
const ast = await openapiTS(document, { enum: false, alphabetize: true });
await writeFile(out, `${banner}${astToString(ast)}`, "utf8");
console.log(`Wrote ${out}`);
