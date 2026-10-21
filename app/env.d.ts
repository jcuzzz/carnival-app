// `tsc -b` type-checks playwright.config.ts and playwright.screenshots.config.ts
// (tsconfig `include`), and they read process.env. This project pins `types` to
// browser globals because src/ is a PWA, so `process` is otherwise unknown.
//
// Honest about the trade-off: with no import/export here this is a GLOBAL
// declaration, so a src/ file could also write process.env, typecheck clean and
// throw in the browser. Adding @types/node would expose the same global plus a
// dependency, so this is the narrower of the two. The scoped fix is a second
// tsconfig covering only the config files — BACKLOG, ADR-0024 follow-ups.
declare const process: { env: Record<string, string | undefined> };
