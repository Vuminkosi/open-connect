import { expect, test } from '@playwright/test';

test('connected Blazor composer keeps the response readable when Kokoro is unavailable', async ({ page, request }) => {
  const unavailable = await request.post('http://127.0.0.1:5110/__acceptance/speech/unavailable');
  expect(unavailable.ok()).toBeTruthy();
  try {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.oc-bubble__live-speak')).toHaveClass(/--active/);

  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i })
    .fill('Show completed tasks containing report and explain them.');
  await page.getByRole('button', { name: /send to ellai/i }).click();

  await expect(page.getByRole('log', { name: 'Conversation' }))
    .toContainText('The matching completed tasks are Review the quarterly report and Draft the annual report.', { timeout: 90_000 });
  await expect(page.getByRole('status'))
    .toContainText('Voice output is unavailable. The text response is still available.');
  await expect(page.locator('.oc-bubble__live-speak')).not.toHaveClass(/--active/);
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  } finally {
    const available = await request.post('http://127.0.0.1:5110/__acceptance/speech/available');
    expect(available.ok()).toBeTruthy();
  }
});
