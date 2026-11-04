import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";

/**
 * ADR-0065 end to end, through the production build and real local accounts: the first
 * administrator from the host command, sign-out and sign-in, adding a person, an authenticator
 * enrolled with codes the test computes, the require-authenticator setting, reset and disable.
 * One journey: every step depends on the accounts the steps before it made.
 */

const ADMIN = {
  username: "backup-admin",
  displayName: "Backup Admin",
  password: "lantern orchard quietly ticking",
};
const HELPER = {
  username: "helper1",
  displayName: "Helper One",
  password: "copper kettle under the jacaranda",
  resetPassword: "seven paper boats on the dam",
};

const STEP_SECONDS = 30;

function base32(secret: string): Uint8Array<ArrayBuffer> {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const char of secret.replace(/[\s=]/g, "").toUpperCase()) {
    const value = alphabet.indexOf(char);
    if (value < 0) throw new Error(`Not base32: ${char}`);
    bits += value.toString(2).padStart(5, "0");
  }
  return Uint8Array.from((bits.match(/.{8}/g) ?? []).map((byte) => parseInt(byte, 2)));
}

/** RFC 6238: SHA-1, 30-second steps, six digits. WebCrypto, which Node has built in. */
async function totp(secret: string, step: number): Promise<string> {
  const counter = new DataView(new ArrayBuffer(8));
  counter.setBigUint64(0, BigInt(step));
  const key = await crypto.subtle.importKey(
    "raw",
    base32(secret),
    { name: "HMAC", hash: "SHA-1" },
    false,
    ["sign"],
  );
  const mac = new DataView(await crypto.subtle.sign("HMAC", key, counter.buffer));
  const offset = mac.getUint8(mac.byteLength - 1) & 0x0f;
  const value = (mac.getUint32(offset) & 0x7fffffff) % 1_000_000;
  return value.toString().padStart(6, "0");
}

const currentStep = () => Math.floor(Date.now() / 1000 / STEP_SECONDS);

/** The server refuses a time step it has already accepted, so a second code waits for the next. */
async function codeAfter(page: Page, secret: string, usedStep: number): Promise<[string, number]> {
  while (currentStep() <= usedStep) await page.waitForTimeout(1_000);
  const step = currentStep();
  return [await totp(secret, step), step];
}

async function freshContext(browser: Browser): Promise<BrowserContext> {
  return browser.newContext({
    baseURL: test.info().project.use.baseURL,
    serviceWorkers: "block",
  });
}

async function signedInAs(page: Page, displayName: string): Promise<void> {
  await expect(page.getByLabel(`Signed in as ${displayName}`)).toBeVisible();
}

async function choosePassword(page: Page, password: string): Promise<void> {
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByLabel("Type it again").fill(password);
  await page.getByRole("button", { name: "Save password" }).click();
}

async function signInWithPassword(page: Page, username: string, password: string): Promise<void> {
  await page.goto("/#/login");
  await page.getByLabel("Username", { exact: true }).fill(username);
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

/** Enrols from the screen's own secret and returns it with the time step it used. */
async function enrol(page: Page): Promise<[string, number]> {
  const secret = ((await page.locator(".secret-key").textContent()) ?? "").replace(/\s+/g, "");
  expect(secret).not.toBe("");
  const step = currentStep();
  await page.getByLabel("The 6-digit code the app now shows").fill(await totp(secret, step));
  await page.getByRole("button", { name: "Confirm" }).click();
  await expect(page.getByRole("list", { name: "Recovery codes" })).toBeVisible();
  await page.getByRole("button", { name: /I've saved them/ }).click();
  return [secret, step];
}

async function issuedLink(page: Page): Promise<string> {
  const link = await page.getByLabel("Setup link").inputValue();
  expect(link).toMatch(/\/#\/setup\/[A-Za-z0-9_-]+$/);
  return link;
}

async function sessionStatus(context: BrowserContext): Promise<number> {
  return (await context.request.get("/api/v1/me")).status();
}

test("local accounts, from the first administrator to a disabled account", async ({
  page,
  browser,
}) => {
  const token = process.env.CARNIVAL_FIRST_ADMIN_TOKEN;
  expect(token, "global setup's first-admin token").toBeTruthy();

  await test.step("the first administrator sets up from the host's link", async () => {
    await page.goto(`/#/setup/${token}`);
    await expect(page).toHaveURL(/#\/setup$/);
    await expect(page.getByText(/first system administrator/)).toBeVisible();
    await page.getByLabel("Username").fill(ADMIN.username);
    await page.getByLabel("Display name").fill(ADMIN.displayName);
    await choosePassword(page, ADMIN.password);
    await page.getByRole("button", { name: "Skip for now" }).click();
    await signedInAs(page, ADMIN.displayName);

    const stored = await page.evaluate(() =>
      JSON.stringify([{ ...localStorage }, { ...sessionStorage }]),
    );
    expect(stored).not.toContain(token!);
  });

  await test.step("the link is used up", async () => {
    const other = await freshContext(browser);
    const reuse = await other.newPage();
    await reuse.goto(`/#/setup/${token}`);
    await expect(reuse.getByText(/expired or has already been used/)).toBeVisible();
    await other.close();
  });

  await test.step("signing out ends the cookie session", async () => {
    await page.getByRole("link", { name: "Sign out" }).click();
    await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
    expect(await sessionStatus(page.context())).toBe(401);
  });

  await test.step("signing in: one answer for a wrong password, then the right one", async () => {
    await signInWithPassword(page, ADMIN.username, "not the password at all");
    await expect(page.getByText(/don't match, or this account can't sign in/)).toBeVisible();
    await page.getByLabel("Password", { exact: true }).fill(ADMIN.password);
    await page.getByRole("button", { name: "Sign in" }).click();
    await signedInAs(page, ADMIN.displayName);
  });

  let helperLink = "";
  await test.step("the administrator adds a person and gets a one-time link", async () => {
    await page.goto("/#/school/people");
    await expect(page.getByRole("heading", { name: "People", level: 1 })).toBeVisible();
    await page.getByLabel("Username").fill(HELPER.username);
    await page.getByLabel("Display name").fill(HELPER.displayName);
    await page.getByRole("button", { name: "Add person" }).click();
    helperLink = await issuedLink(page);
    await expect(page.getByRole("img", { name: /QR code of the setup link/ })).toBeVisible();
    await page.getByRole("button", { name: "Done" }).click();
    await expect(page.getByText("awaiting setup")).toBeVisible();
  });

  const helper = await freshContext(browser);
  const helperPage = await helper.newPage();
  let enrolled: [string, number] = ["", 0];
  await test.step("the person opens the link on their own device and enrols an authenticator", async () => {
    await helperPage.goto(helperLink);
    await expect(helperPage).toHaveURL(/#\/setup$/);
    await expect(helperPage.getByText(HELPER.displayName)).toBeVisible();
    await choosePassword(helperPage, HELPER.password);
    await helperPage
      .getByRole("button", { name: "Set up an authenticator app (recommended)" })
      .click();
    enrolled = await enrol(helperPage);
    await signedInAs(helperPage, HELPER.displayName);
  });

  await test.step("the next sign-in asks for a code", async () => {
    const again = await freshContext(browser);
    const againPage = await again.newPage();
    await signInWithPassword(againPage, HELPER.username, HELPER.password);
    const [code] = await codeAfter(againPage, enrolled[0], enrolled[1]);
    await againPage.getByLabel("The 6-digit code from your authenticator app").fill(code);
    await againPage.getByRole("button", { name: "Sign in" }).click();
    await signedInAs(againPage, HELPER.displayName);
    await again.close();
  });

  await test.step("the administrator requires an authenticator for everyone", async () => {
    await page.goto("/#/school/settings");
    await page.getByLabel("Require an authenticator app").check();
    await page.getByRole("button", { name: "Save" }).click();
    await expect(page.getByText("School settings saved")).toBeVisible();
  });

  let resetLink = "";
  await test.step("a reset ends the person's session and issues a new link", async () => {
    await page.goto("/#/school/people");
    await page.getByRole("button", { name: `Reset ${HELPER.displayName}` }).click();
    await page.getByRole("button", { name: `Confirm resetting ${HELPER.displayName}` }).click();
    resetLink = await issuedLink(page);
    await expect.poll(() => sessionStatus(helper)).toBe(401);
  });

  await test.step("after the reset, setup insists on an authenticator", async () => {
    await helperPage.goto(resetLink);
    await choosePassword(helperPage, HELPER.resetPassword);
    await expect(helperPage.getByText(/requires an authenticator app/)).toBeVisible();
    await expect(helperPage.getByRole("button", { name: "Skip for now" })).toHaveCount(0);
    await enrol(helperPage);
    await signedInAs(helperPage, HELPER.displayName);
  });

  await test.step("a disabled account is signed out and refused", async () => {
    await page.goto("/#/school/people");
    await page.getByRole("button", { name: `Disable ${HELPER.displayName}` }).click();
    await page.getByRole("button", { name: `Confirm disabling ${HELPER.displayName}` }).click();
    await expect(page.getByText("disabled", { exact: true })).toBeVisible();
    await expect.poll(() => sessionStatus(helper)).toBe(401);

    const refused = await freshContext(browser);
    const refusedPage = await refused.newPage();
    await signInWithPassword(refusedPage, HELPER.username, HELPER.resetPassword);
    await expect(refusedPage.getByText(/don't match, or this account can't sign in/)).toBeVisible();
    await refused.close();
  });

  await helper.close();
});
