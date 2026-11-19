import { expect, test } from '@playwright/test';

test('T23: finds an active task hidden by the completed filter', async ({ page, request }) => {
  const title = 'Prepare a launch checklist';
  await page.goto('/');
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Completed' }).click();
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);

  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill(`Find the task ${title}.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'false');

  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const gatewayUrl = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL ?? 'http://127.0.0.1:5120';
  const response = await request.get(`${gatewayUrl}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const change = events.find((event) => event.type === 'liveui.request' && event.payload.operation === 'execute' &&
    event.payload.action === 'SelectTab');
  expect(change).toBeTruthy();
  const receipt = events.find((event) => event.type === 'liveui.response' &&
    event.payload.correlationId === change.payload.correlationId);
  expect(receipt).toBeTruthy();
  expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});
