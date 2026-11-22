import { expect, test } from '@playwright/test';

test.use({ ignoreHTTPSErrors: true });

test('anonymous visitor analyzes the synthetic documentation example', async ({ page }) => {
  const configured = process.env.OPENCONNECT_SITE_ANALYTICS_URL;
  test.skip(!configured, 'Set OPENCONNECT_SITE_ANALYTICS_URL to a disposable configured site.');
  const origin = new URL(configured);
  if (origin.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname) ||
      origin.username || origin.password || origin.pathname !== '/' || origin.search || origin.hash)
    throw new Error('Analytics acceptance requires a loopback HTTPS origin.');

  await page.goto(`${origin.origin}/docs/v1/supported-services`);
  await expect(page.getByRole('heading', { name: 'Try analytics' })).toBeVisible();
  await page.getByRole('button', { name: 'Analyze example' }).click();
  await expect(page.getByRole('status')).toHaveText('Analysis complete.');
  await expect(page.getByText('Anomalies found: 1.')).toBeVisible();
});
