// The project carries no @types/node; this is the one Node API the e2e setup calls.
declare module "node:child_process" {
  export function execFileSync(
    file: string,
    args: readonly string[],
    options: { cwd?: string; env?: Record<string, string | undefined>; encoding: "utf8" },
  ): string;
}
