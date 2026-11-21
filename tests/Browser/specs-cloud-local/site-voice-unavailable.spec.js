import { expect, test } from '@playwright/test';

test.use({
  ignoreHTTPSErrors: true,
  permissions: ['microphone'],
  launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] },
});

test('an unavailable local transcription worker leaves the text assistant usable', async ({ page }) => {
  test.skip(process.env.OPENCONNECT_SITE_VOICE_UNAVAILABLE_ACCEPTANCE_ENABLED !== '1' || !process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL,
    'Explicit local unavailable-voice acceptance configuration is required.');
  const portal = new URL(process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL);
  if (portal.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(portal.hostname) ||
      portal.username || portal.password || portal.pathname !== '/' || portal.search || portal.hash) {
    throw new Error('Site acceptance requires a loopback HTTPS origin.');
  }

  await page.goto(`${portal.origin}/docs`);
  await page.getByRole('button', { name: /open openconnect assistant/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('button.oc-speech-item[title="Turn on microphone"]').click();
  await page.getByRole('button', { name: 'Voice input', exact: true }).click();
  await expect(page.getByText(/Voice transcription is unavailable|Microphone access or voice transcription is unavailable/).first())
    .toBeVisible({ timeout: 30_000 });
  await expect(page.locator('button.oc-speech-item[title="Turn on microphone"]')).toHaveAttribute('aria-pressed', 'false');

  await page.getByRole('textbox', { name: /message/i }).fill('Search documentation for privacy');
  await page.getByRole('button', { name: /send/i }).click();
  await expect(page.locator('#docs-search')).toHaveValue(/privacy/i, { timeout: 120_000 });
});
