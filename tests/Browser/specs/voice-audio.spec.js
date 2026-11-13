import { test, expect } from '@playwright/test';

test('voice PCM helpers encode little-endian PCM16 and resample', async ({ page }) => {
  await page.addInitScript(() => { window.__OC_VOICE_DIAGNOSTICS__ = true; });
  await page.goto('/');
  const result = await page.evaluate(() => {
    const hooks = window.__ocVoiceTestHooks;
    if (!hooks) throw new Error('voice test hooks were not initialized');
    const encoded = Array.from(new Uint8Array(hooks.pcm16(new Float32Array([-1, -0.5, 0, 0.5, 1]))));
    const resampled = Array.from(hooks.resample(new Float32Array([-1, 0, 1]), 48000, 24000));
    return { encoded, resampled };
  });
  expect(result.encoded).toEqual([0, 128, 0, 192, 0, 0, 255, 63, 255, 127]);
  expect(result.resampled).toEqual([-1, 1]);
});
