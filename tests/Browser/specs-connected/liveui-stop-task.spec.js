import { expect, test } from '@playwright/test';

test('stopping a reply cancels the admitted server task', async ({ page, request }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Wait for the cancellation test.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect.poll(async () => {
    const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
    return (await response.json()).runs.some((run) => run.scenarioName === 'cancellation');
  }, { timeout: 30_000 }).toBe(true);
  await page.getByRole('button', { name: 'Stop reply' }).first().click();
  await expect.poll(async () => {
    const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
    return (await response.json()).runs.find((run) => run.scenarioName === 'cancellation')?.aborted;
  }, { timeout: 30_000 }).toBe(true);
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
});
