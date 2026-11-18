import { expect, test } from '@playwright/test';

test('real browser Prompt API creates a plan through the server-initiated Blazor session bridge', async ({ page, request }) => {
  test.setTimeout(availabilityMayDownloadModel() ? 15 * 60_000 : 120_000);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();

  const availability = await page.evaluate(async () => {
    if (typeof globalThis.LanguageModel?.availability !== 'function') return 'prompt_api_unavailable';
    try {
      return await globalThis.LanguageModel.availability();
    } catch {
      return 'availability_check_failed';
    }
  });
  if (availability === 'downloadable' && process.env.OPENCONNECT_LIVEUI_ALLOW_BROWSER_MODEL_DOWNLOAD === '1') {
    await page.getByRole('button', { name: 'Enable on-device AI' }).click();
    await expect(page.getByText('On-device AI ready')).toBeVisible({ timeout: 14 * 60_000 });
  } else {
    test.skip(availability !== 'available',
      `Real local Prompt API model is not available (${availability}); set OPENCONNECT_LIVEUI_ALLOW_BROWSER_MODEL_DOWNLOAD=1 to explicitly allow its browser-managed download.`);
  }

  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('section.oc-bubble')).toBeVisible();
  await page.getByRole('textbox', { name: /message ellai/i })
    .fill('Use the on-device model to show my completed tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();

  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'The on-device model selected and verified the Completed tab.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');

  const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
  expect(response.ok()).toBeTruthy();
  const payload = await response.json();
  expect(payload.error).toBeNull();
  const run = payload.runs.at(-1);
  expect(run).toMatchObject({ completed: true, scenarioName: 'local_inference' });
  expect(run.results.map(result => result.name)).toEqual([
    'liveui_infer_plan', 'liveui_execute', 'liveui_await_state',
  ]);
  expect(run.results[0].evidence).toMatchObject({
    provider: 'Browser',
    targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
    action: 'activate',
  });
  expect(run.results.at(-1).evidence).toMatchObject({
    outcome: 'Completed',
    postconditionVerified: true,
    verifiedState: 'state_verified',
  });
});

function availabilityMayDownloadModel() {
  return process.env.OPENCONNECT_LIVEUI_ALLOW_BROWSER_MODEL_DOWNLOAD === '1';
}
