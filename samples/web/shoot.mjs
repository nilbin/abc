// Screenshot tour of the running ERP app (used for docs/screenshots).
import { chromium } from 'playwright';
import fs from 'node:fs';

const BASE = process.env.BASE ?? 'http://localhost:5100';
const OUT = process.env.OUT ?? '../../docs/screenshots';
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({
  executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome',
});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

async function shot(name) {
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}

async function pick(label, option) {
  await page.getByRole('textbox', { name: label }).click();
  await page.getByRole('option', { name: option }).click();
}

await page.goto(BASE, { waitUntil: 'networkidle' });
await shot('01-orders-grid');

// New order: project type reveals conditional required field; customer picks load options.
await page.getByRole('button', { name: 'Ny order' }).click();
await page.waitForTimeout(400);
await pick('Kund', 'Acme Industri AB');
await page.waitForTimeout(1000); // debounce + server resolve (suggestion arrives)
await pick('Ordertyp', 'Projekt');
await page.waitForTimeout(1000);
await shot('02-create-order-project');

await page.getByRole('textbox', { name: 'Projekt', exact: true }).click();
await page.waitForTimeout(400);
await shot('03-project-options');
await page.getByRole('option', { name: 'Pumprenovering 2026' }).click();

await page.getByRole('textbox', { name: 'Arbetsbeskrivning' }).fill('Etapp 2: byte av pumphjul och tätningar');
await page.getByRole('textbox', { name: 'Maskinserienummer' }).fill('PX-9917');
await page.waitForTimeout(300);
await shot('04-create-order-filled');
const submit = page.locator('.mantine-Modal-content').getByRole('button', { name: 'Ny order' });
await submit.click();
await page.waitForTimeout(1500);
await shot('05-orders-after-create');

// Edit an order (row click) → change-set form including the tenant custom field with value
await page.getByText('2026-01416').first().click();
await page.waitForTimeout(900);
await shot('06-edit-order');
await page.keyboard.press('Escape');
await page.waitForTimeout(300);

await page.getByText('Kunder', { exact: true }).click();
await page.waitForTimeout(900);
await shot('07-customers');

await page.getByText('Anpassade fält', { exact: true }).first().click();
await page.waitForTimeout(900);
await shot('08-extensions');

await page.getByText('English', { exact: true }).click();
await page.waitForTimeout(500);
await page.getByText('Orders', { exact: true }).click();
await page.waitForTimeout(900);
await shot('09-orders-english');

await browser.close();
console.log('done');
