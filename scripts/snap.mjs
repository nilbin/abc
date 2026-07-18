// snap.mjs — drive the running sample app with Playwright: sign in, navigate, screenshot.
//
// The login flow has three traps this script absorbs so every agent doesn't rediscover them:
//   1. The SPA landing page is a single "Logga in" button — the credential FORM lives on the
//      server-rendered authorize page behind it.
//   2. Seeded usernames ("alva") are not email-shaped, but the email input's type=email
//      validation would block submit — the form gets `novalidate` before filling.
//   3. After credentials, a TENANT PICKER may appear (choose company, then "Fortsätt").
//
// Serve the BUILT app from the API origin (http://localhost:5100) — `npx vite build` outputs
// into samples/erp/wwwroot. The vite dev server only proxies /api, so the OIDC redirect
// breaks there; don't screenshot against :5173.
//
// CLI — flags before steps configure; step flags execute in the order given:
//   node scripts/snap.mjs [--base http://localhost:5100] [--user alva] [--password demo123]
//     [--tenant "Demo AB"] [--path "?mode=work&page=orders"] \
//     --click "Ordrar" --row 0 --tab "Dokument" --wait 800 --shot /tmp/orders.png
//
// Steps: --goto <?query>   navigate (after sign-in) to base + query
//        --click <text>    click the first exact-text match
//        --button <name>   click a button by accessible name
//        --row <n>         click the nth body row of the first table
//        --tab <name>      click a record tab by name
//        --wait <ms>       settle time
//        --shot <file>     screenshot to file
//
// As a library (for bespoke flows — dialogs, asserts):
//   import { launch, signIn } from '../scripts/snap.mjs';
//   const { browser, page } = await launch();
//   try { await signIn(page); /* ... */ } finally { await browser.close(); }
// Always close the browser in `finally` — a hanging chromium keeps node alive past timeouts.

import { chromium } from 'playwright';
import { existsSync, readdirSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

/** Chromium resolution: playwright's own install if present, else the container's
 *  pre-provisioned /opt/pw-browsers build (PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD environments). */
function chromiumPath() {
  if (process.env.SNAP_CHROMIUM) return process.env.SNAP_CHROMIUM;
  const root = process.env.PLAYWRIGHT_BROWSERS_PATH ?? '/opt/pw-browsers';
  if (!existsSync(root)) return undefined;           // let playwright resolve its default
  const dir = readdirSync(root).find(d => /^chromium-\d+$/.test(d));
  return dir ? `${root}/${dir}/chrome-linux/chrome` : undefined;
}

export async function launch(options = {}) {
  const executablePath = chromiumPath();
  const browser = await chromium.launch(executablePath ? { executablePath } : {});
  const page = await browser.newPage({
    viewport: { width: options.width ?? 1440, height: options.height ?? 900 },
  });
  return { browser, page };
}

export async function signIn(page, options = {}) {
  const base = options.base ?? 'http://localhost:5100';
  await page.goto(base, { waitUntil: 'networkidle' }).catch(() => {});
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: 'Logga in' }).click();
  await page.waitForSelector('form', { timeout: 30000 });
  await page.evaluate(() => document.querySelector('form').setAttribute('novalidate', ''));
  await page.getByRole('textbox').first().fill(options.user ?? 'alva');
  await page.locator('input[type=password]').fill(options.password ?? 'demo123');
  await page.getByRole('button').first().click();
  // Tenant picker (multi-company accounts): pick the company, confirm with "Fortsätt".
  const proceed = page.getByRole('button', { name: 'Fortsätt' });
  if (await proceed.isVisible({ timeout: 4000 }).catch(() => false)) {
    await page.getByText(options.tenant ?? 'Demo AB', { exact: true }).click();
    await proceed.click();
  }
  await page.waitForTimeout(1500);
}

async function runCli() {
  const argv = process.argv.slice(2);
  const config = { base: 'http://localhost:5100', user: 'alva', password: 'demo123', tenant: 'Demo AB' };
  const steps = [];
  for (let i = 0; i < argv.length; i += 2) {
    const flag = argv[i].replace(/^--/, '');
    const value = argv[i + 1];
    if (flag in config) config[flag] = value;
    else if (flag === 'path') steps.unshift({ kind: 'goto', value });
    else steps.push({ kind: flag, value });
  }

  const { browser, page } = await launch();
  try {
    await signIn(page, config);
    for (const step of steps) {
      switch (step.kind) {
        case 'goto': await page.goto(config.base + step.value, { waitUntil: 'networkidle' }).catch(() => {});
          await page.waitForTimeout(2000); break;
        case 'click': await page.getByText(step.value, { exact: true }).first().click();
          await page.waitForTimeout(1200); break;
        case 'button': await page.getByRole('button', { name: step.value }).click();
          await page.waitForTimeout(1000); break;
        case 'row': await page.locator('table tbody tr').nth(Number(step.value)).click();
          await page.waitForTimeout(1500); break;
        case 'tab': await page.getByRole('tab', { name: step.value }).click();
          await page.waitForTimeout(800); break;
        case 'wait': await page.waitForTimeout(Number(step.value)); break;
        case 'shot': await page.screenshot({ path: step.value }); console.log(`shot: ${step.value}`); break;
        default: throw new Error(`unknown step --${step.kind}`);
      }
    }
  } finally {
    await browser.close();
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  await runCli();
}
