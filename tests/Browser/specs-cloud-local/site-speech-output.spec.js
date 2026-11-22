import { expect, test } from '@playwright/test';

test.use({
  ignoreHTTPSErrors: true,
  launchOptions: { args: ['--autoplay-policy=no-user-gesture-required'] },
});

test('local documentation assistant synthesizes and plays speech', async ({ page }) => {
  test.skip(process.env.OPENCONNECT_SITE_SPEECH_ACCEPTANCE_ENABLED !== '1' || !process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL,
    'Explicit local site speech acceptance configuration is required.');
  const portal = new URL(process.env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL);
  if (portal.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(portal.hostname) ||
      portal.username || portal.password || portal.pathname !== '/' || portal.search || portal.hash) {
    throw new Error('Site acceptance requires a loopback HTTPS origin.');
  }

  await page.addInitScript(() => {
    window.__speechAcceptance = { started: 0, ended: 0, nonzero: false };
    const start = AudioBufferSourceNode.prototype.start;
    AudioBufferSourceNode.prototype.start = function (...args) {
      window.__speechAcceptance.started++;
      const samples = this.buffer?.getChannelData(0);
      window.__speechAcceptance.nonzero ||= Boolean(samples?.some(sample => sample !== 0));
      this.addEventListener('ended', () => window.__speechAcceptance.ended++, { once: true });
      return start.apply(this, args);
    };
  });

  await page.goto(`${portal.origin}/docs`);
  await page.getByRole('button', { name: /open openconnect assistant/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message/i }).fill('Say hello in one short sentence.');
  await page.getByRole('button', { name: /send/i }).click();
  await expect.poll(() => page.evaluate(() => window.__speechAcceptance.started > 0 &&
    window.__speechAcceptance.ended > 0 && window.__speechAcceptance.nonzero), { timeout: 120_000 }).toBe(true);
  const playback = await page.evaluate(() => window.__speechAcceptance);
  expect(playback.started).toBeGreaterThan(0);
  expect(playback.ended).toBeGreaterThan(0);
});
