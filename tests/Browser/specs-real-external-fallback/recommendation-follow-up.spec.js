import { expect, test } from '@playwright/test';

test('real agent grounds a task recommendation in a follow-up and highlights it', async ({ page, request }) => {
  test.setTimeout(420_000);
  const title = 'Prepare a launch checklist';
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  const status = page.locator('.oc-status__text');
  const textbox = page.getByRole('textbox', { name: /message ellai/i });
  const send = page.getByRole('button', { name: /send to ellai/i });
  await expect(status).toHaveText('Ready');
  await textbox.fill('Show my active tasks.');
  await send.click();
  await expect(page.getByRole('tab', { name: 'Active' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
  await expect(status).toHaveText('Ready', { timeout: 180_000 });
  await textbox.fill('Which one should I do next? Highlight it.');
  await send.click();
  await expect(status).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(status).toHaveText('Ready', { timeout: 180_000 });
  await expect(page.locator('.oc-message-assistant .oc-message-text').last()).toContainText(title);
  await expect(page.locator('p.font-weight-medium').filter({ hasText: title })).toBeVisible();

  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const allEvents = await response.json();
  const accepted = allEvents.filter((event) => event.type === 'task.accepted');
  expect(accepted).toHaveLength(2);
  const events = allEvents.filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  if (process.env.OPENCONNECT_ACCEPTANCE_PLANNER_KIND === 'LocalWorker') {
    expect(events.some((event) => event.sequence > accepted[1].sequence && event.type === 'liveui.response' &&
      event.payload.resultKind === 'plan' && JSON.parse(event.payload.payloadJson).provider === 'LocalWorker')).toBe(true);
  }
  const highlight = events.find((event) => event.sequence > accepted[1].sequence && event.type === 'liveui.request' &&
    event.payload.operation === 'highlight' && event.payload.targetId?.endsWith('-list-task-list') && event.payload.value === title);
  expect(highlight).toBeTruthy();
  const receipt = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === highlight.payload.correlationId);
  expect(receipt).toBeTruthy();
  expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});
