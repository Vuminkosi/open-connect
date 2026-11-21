import { expect, test } from '@playwright/test';

test.use({
  ignoreHTTPSErrors: true,
  permissions: ['microphone'],
  launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] },
});

function portalOrigin() {
  const portal = new URL(process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL);
  if (portal.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(portal.hostname) ||
      portal.username || portal.password || portal.pathname !== '/' || portal.search || portal.hash) {
    throw new Error('Site acceptance requires a loopback HTTPS origin.');
  }
  return portal.origin;
}

test('assistant launcher appears after the interactive circuit starts', async ({ page }) => {
  test.skip(process.env.OPENCONNECT_SITE_VOICE_UNAVAILABLE_ACCEPTANCE_ENABLED !== '1' || !process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL,
    'Explicit local unavailable-voice acceptance configuration is required.');
  const origin = portalOrigin();
  const initial = await page.request.get(`${origin}/docs`);
  expect(initial.ok()).toBeTruthy();
  const initialHtml = await initial.text();
  expect(initialHtml).toContain('<h1>Documentation</h1>');
  expect(initialHtml).not.toContain('Open OpenConnect Assistant');
  await page.goto(`${origin}/docs`);
  await page.getByRole('button', { name: /open openconnect assistant/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
});

test('documentation examples accept input when their controls appear', async ({ page }) => {
  test.skip(process.env.OPENCONNECT_SITE_VOICE_UNAVAILABLE_ACCEPTANCE_ENABLED !== '1' || !process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL,
    'Explicit local documentation acceptance configuration is required.');
  const origin = portalOrigin();
  const docsHtml = await (await page.request.get(`${origin}/docs`)).text();
  expect(docsHtml).not.toContain('id="docs-show-guides"');
  await page.goto(`${origin}/docs`);
  await page.getByRole('button', { name: 'Show integration guides' }).click();
  await expect(page.getByRole('region', { name: 'OpenConnect integration guides' })).toBeVisible();

  const privacyHtml = await (await page.request.get(`${origin}/docs/v1/privacy`)).text();
  expect(privacyHtml).not.toContain('id="privacy-demo-run"');
  await page.goto(`${origin}/docs/v1/privacy`);
  await page.getByLabel('Example').selectOption('email');
  await page.getByRole('button', { name: 'Check example' }).click();
  await expect(page.getByText('Detected categories:', { exact: false })).toContainText('EMAIL');
});

test('unavailable local transcription disables microphone and leaves text usable', async ({ page }) => {
  test.skip(process.env.OPENCONNECT_SITE_VOICE_UNAVAILABLE_ACCEPTANCE_ENABLED !== '1' || !process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL,
    'Explicit local unavailable-voice acceptance configuration is required.');
  await page.goto(`${portalOrigin()}/docs`);
  await page.getByRole('button', { name: /open openconnect assistant/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await expect(page.getByText('Voice input is unavailable. You can continue using text; voice output is configured separately.'))
    .toBeVisible({ timeout: 30_000 });
  const microphone = page.locator('button.oc-speech-item[title="Turn on microphone"]');
  await expect(microphone).toBeDisabled();
  await expect(microphone).toHaveAttribute('aria-pressed', 'false');

  await page.getByRole('textbox', { name: /message/i }).fill('Search documentation for privacy');
  await page.getByRole('button', { name: /send/i }).click();
  await expect(page.locator('#docs-search')).toHaveValue(/privacy/i, { timeout: 120_000 });
});
