import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

// Correctness only. This repo is hand-formatted at ~100 columns, so no
// formatting or stylistic rule may be added here.
export default tseslint.config(
  {
    ignores: [
      "dist/**",
      "dev-dist/**",
      "ui-review/**",
      "test-results/**",
      "playwright-report/**",
    ],
  },
  js.configs.recommended,
  // Non-type-checked: the `tsc -b` in `npm run build` is the type gate, and a
  // second full type-check per lint run buys nothing.
  tseslint.configs.recommended,
  {
    files: ["**/*.{ts,tsx}"],
    plugins: { "react-hooks": reactHooks },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      // Raised from the preset's "warn": the in-tree
      // `eslint-disable-next-line react-hooks/exhaustive-deps` comments are the
      // only sanctioned exceptions, so everything else must fail the run.
      "react-hooks/exhaustive-deps": "error",
      // The rest of the plugin's v7 recommended set (purity, immutability,
      // refs, set-state-in-effect, preserve-manual-memoization, …) checks
      // React Compiler eligibility; this app does not build with the compiler.
    },
  },
  {
    files: ["scripts/**/*.{js,mjs}"],
    languageOptions: { globals: globals.node },
  },
);
