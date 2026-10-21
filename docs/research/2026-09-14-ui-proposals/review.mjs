import { chromium } from '../../../app/node_modules/@playwright/test/index.mjs';
import { fileURLToPath } from 'node:url';
import { writeFile } from 'node:fs/promises';

const browser = await chromium.launch();
const page = await browser.newPage();
const errors = [];
const checks = [];
page.on('pageerror', error => errors.push(error.message));
const base = new URL('./index.html', import.meta.url);
try {
  for (const proposal of ['a', 'b']) {
    for (const width of [390, 820, 1440]) {
      for (const screen of ['overview', 'deck', 'setup']) {
        await page.setViewportSize({ width, height: 1000 });
        const url = new URL(base);
        url.search = new URLSearchParams({ proposal, screen }).toString();
        await page.goto(url.href);
        await page.evaluate(() => document.fonts.ready);
        const overflow = await page.evaluate(() => {
          const preview = document.querySelector('#preview');
          return preview.scrollWidth > preview.clientWidth + 1 ||
            document.documentElement.scrollWidth > innerWidth;
        });
        if (overflow) throw new Error(`Horizontal overflow: ${proposal}/${screen}/${width}`);
        checks.push(`${proposal}/${screen}/${width}: no horizontal overflow`);
        if ((width === 1440 && screen === 'overview') || (width === 390 && screen === 'deck')) {
          await page.locator('#preview').screenshot({
            path: fileURLToPath(new URL(`./proposal-${proposal}-${screen}-${width}.png`, import.meta.url)),
          });
        }
      }
    }
  }
  await page.goto(`${base.href}?screen=deck`);
  await page.getByRole('textbox', { name: 'Time for Blake Costa' }).fill('36.42');
  await page.locator('[data-save="2"]').click();
  if (!(await page.locator('.capture').nth(2).innerText()).includes('36.42')) {
    throw new Error('Save preview failed');
  }
  await page.locator('[data-edit="2"]').click();
  await page.getByRole('textbox', { name: 'Time for Blake Costa' }).fill('invalid');
  await page.locator('[data-save="2"]').click();
  if (!(await page.locator('#message').innerText()).includes('positive time')) {
    throw new Error('Invalid time feedback missing');
  }
  await page.locator('#offline').click();
  if (!(await page.locator('.notice').innerText()).includes('Offline')) {
    throw new Error('Offline preview failed');
  }
  await page.locator('[data-proposal="b"]').click();
  await page.locator('[data-size="phone"]').click();
  if ((await page.locator('#preview').boundingBox()).width > 390) {
    throw new Error('Phone preview width failed');
  }
  await page.locator('.account summary').click();
  await page.locator('.account-body [data-outdoor]').click();
  if (await page.locator('html').getAttribute('data-theme') !== 'outdoor') {
    throw new Error('Outdoor preview failed');
  }
  await page.locator('.account summary').click();
  await page.locator('.nav [data-screen="setup"]').click();
  await page.locator('[data-step="3"]').click();
  if (!(await page.locator('.setup-detail').innerText()).includes('Events')) {
    throw new Error('Setup selection failed');
  }
  if (errors.length) throw new Error(errors.join('\n'));
  checks.push('Save/edit, validation, offline, proposal switch, phone sizing, account, outdoor and setup interactions passed.');
  checks.push('No browser JavaScript errors.');
  await writeFile(new URL('./validation.txt', import.meta.url), checks.join('\n') + '\n');
  console.log(checks.join('\n'));
} finally {
  await browser.close();
}
