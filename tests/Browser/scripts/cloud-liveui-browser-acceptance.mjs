import { chromium, expect } from '@playwright/test';

const appUrl = process.env.OPENCONNECT_CLOUD_BROWSER_APP_URL;
const fixtureUrl = process.env.OPENCONNECT_CLOUD_BROWSER_FIXTURE_URL;
if (!appUrl || !fixtureUrl) throw new Error('Cloud browser acceptance requires isolated app and fixture URLs.');

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  await page.goto(appUrl);
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Use the server worker to select the Completed tab.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'The local inference worker selected and verified the Completed tab.', { timeout: 90000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  const response = await page.request.get(new URL('/__acceptance/runs', fixtureUrl).href);
  const runs = await response.json();
  expect(runs.error).toBeNull();
  const run = runs.runs.at(-1);
  expect(run.completed).toBe(true);
  expect(run.results.some(result => result.kind === 'worker_plan' && result.evidence?.provider === 'Gateway' &&
    result.evidence?.targetId?.endsWith('-tab-completed'))).toBe(true);
  expect(run.results.some(result => result.evidence?.postconditionVerified === true)).toBe(true);
  process.stdout.write('Cloud LiveUI browser action and receipt verified.\n');
} finally {
  await browser.close();
}
