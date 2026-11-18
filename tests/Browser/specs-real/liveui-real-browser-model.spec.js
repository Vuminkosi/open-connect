import { expect, test, chromium } from '@playwright/test';
import { mkdirSync, statfsSync } from 'node:fs';

const minimumFreeBytes = 22 * 1024 ** 3;
const completedTarget = 'route.root.components-todolistcomponent-razor-tab-completed';

test('real Chrome Prompt API plans a LiveUI action through the Blazor Server circuit', async ({ request }) => {
  const profileDirectory = process.env.OPENCONNECT_LIVEUI_CHROME_PROFILE;
  test.skip(!profileDirectory, 'Set OPENCONNECT_LIVEUI_CHROME_PROFILE to an isolated profile on a volume with at least 22 GiB free.');

  mkdirSync(profileDirectory, { recursive: true });
  let freeBytes;
  try {
    const fileSystem = statfsSync(profileDirectory);
    freeBytes = fileSystem.bavail * fileSystem.bsize;
  } catch (error) {
    test.skip(true, `Could not verify browser profile free space; no model initialization was attempted (${error.message}).`);
  }
  test.skip(freeBytes < minimumFreeBytes,
    `Browser model requires 22 GiB free on its profile volume; found ${(freeBytes / 1024 ** 3).toFixed(2)} GiB. No model initialization was attempted.`);

  const context = await chromium.launchPersistentContext(profileDirectory, {
    channel: process.env.OPENCONNECT_LIVEUI_BROWSER_CHANNEL ?? 'chrome',
    headless: process.env.OPENCONNECT_LIVEUI_CHROME_HEADLESS !== '0',
    ignoreDefaultArgs: ['--disable-field-trial-config'],
    viewport: { width: 1280, height: 900 },
    args: ['--no-first-run', '--no-default-browser-check'],
  });

  try {
    const page = context.pages()[0] ?? await context.newPage();
    await page.goto('http://127.0.0.1:5100');
    const availability = await page.evaluate(async () => {
      if (!globalThis.LanguageModel?.availability) {
        return { secureContext: globalThis.isSecureContext, api: false, status: 'unavailable', reason: 'prompt_api_unavailable' };
      }
      try {
        const textOptions = {
          expectedInputs: [{ type: 'text', languages: ['en'] }],
          expectedOutputs: [{ type: 'text', languages: ['en'] }],
        };
        return {
          secureContext: globalThis.isSecureContext,
          api: true,
          status: await LanguageModel.availability(),
          textStatus: await LanguageModel.availability(textOptions),
          browser: navigator.userAgent,
        };
      } catch (error) {
        return { secureContext: globalThis.isSecureContext, api: true, status: 'unavailable', reason: error.name };
      }
    });
    console.log(`Real Chrome Prompt API availability: ${JSON.stringify(availability)}`);

    if (!availability.api || availability.status === 'unavailable') {
      for (const diagnosticUrl of ['chrome://policy', 'chrome://version', 'chrome://on-device-internals']) {
        try {
          const diagnosticsPage = await context.newPage();
          await diagnosticsPage.goto(diagnosticUrl, { waitUntil: 'domcontentloaded', timeout: 15_000 });
          const diagnostics = await diagnosticsPage.locator('body').innerText({ timeout: 5_000 });
          console.log(`Chrome diagnostics (${diagnosticUrl}): ${diagnostics.slice(0, 8_000)}`);
          await diagnosticsPage.close();
        } catch (error) {
          console.log(`Chrome diagnostics unavailable (${diagnosticUrl}): ${error.name}`);
        }
      }
      test.skip(true, `Chrome reports the Prompt API unavailable (${availability.reason ?? 'device_or_model_unavailable'}); model download was not attempted.`);
    }

    if (availability.status === 'downloadable') {
      const enableButton = page.getByRole('button', { name: 'Enable on-device AI' });
      await expect(enableButton).toBeVisible();
      await enableButton.click();
    } else if (availability.status === 'downloading') {
      await expect(page.getByText('Preparing on-device AI')).toBeVisible();
    } else if (availability.status !== 'available') {
      test.skip(true, `Chrome reported unsupported Prompt API status '${availability.status}'; model initialization was not attempted.`);
    }

    await expect(page.getByText('On-device AI ready')).toBeVisible({ timeout: 14 * 60 * 1000 });
    await page.locator('.oc-anchored-fab').click();
    await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
    await page.getByRole('textbox', { name: /message ellai/i }).fill('Use the on-device model to show my completed tasks.');
    await page.getByRole('button', { name: /send to ellai/i }).click();

    await expect(page.getByRole('log', { name: 'Conversation' }))
      .toContainText('The on-device model selected and verified the Completed tab.', { timeout: 90_000 });
    await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');

    const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
    expect(response.ok()).toBeTruthy();
    const run = (await response.json()).runs.at(-1);
    expect(run.results.map((result) => result.name)).toEqual([
      'liveui_infer_plan', 'liveui_execute', 'liveui_await_state',
    ]);
    expect(run.results[0].evidence).toMatchObject({
      provider: 'Browser',
      targetId: completedTarget,
      action: 'activate',
      outcome: 'completed',
    });
  } finally {
    await context.close();
  }
});
