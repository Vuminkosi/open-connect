import { expect, test } from '@playwright/test';

test('gateway deadline returns an actionable failure to MiniComposer', async ({ page }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show me the current tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText('Connection issue', { timeout: 30_000 });
  await expect(page.getByRole('alert')).toContainText('Some actions may have completed; check the current state before retrying.');
});
