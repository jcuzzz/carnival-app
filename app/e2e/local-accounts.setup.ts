import { execFileSync } from "node:child_process";
import { ACCOUNTS_API_DIR, ACCOUNTS_API_ENV } from "./local-accounts.env";

/** The install's own path to the first administrator: the host command, against the live database. */
export default function globalSetup(): void {
  const printed = execFileSync(
    "dotnet",
    ["run", "--no-build", "--no-launch-profile", "--", "accounts", "first-admin"],
    { cwd: ACCOUNTS_API_DIR, env: { ...process.env, ...ACCOUNTS_API_ENV }, encoding: "utf8" },
  );
  const token = /#\/setup\/([A-Za-z0-9_-]+)/.exec(printed)?.[1];
  if (!token) throw new Error(`accounts first-admin printed no setup link:\n${printed}`);
  process.env.CARNIVAL_FIRST_ADMIN_TOKEN = token;
}
