import { expect, test } from '@playwright/test';

test('real agent finds the newest Todo and reveals the exact record in the UI', async ({ page, request }) => {
  test.setTimeout(360_000);
  const title = 'Prepare a launch checklist';
  await page.goto('/');
  await page.getByRole('tab', { name: 'Completed' }).click();
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  await page.locator('.oc-anchored-fab').click();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Pull up my tasks to find the most recent one.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.getByRole('tab', { name: 'All' })).toHaveAttribute('aria-selected', 'true', { timeout: 180_000 });
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue(title, { timeout: 180_000 });
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 180_000 });
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(title);

  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  if (process.env.OPENCONNECT_ACCEPTANCE_PLANNER_KIND === 'LocalWorker') {
    const plans = events.filter((event) => event.type === 'liveui.response' && event.payload.resultKind === 'plan')
      .map((event) => JSON.parse(event.payload.payloadJson))
      .filter((plan) => plan.provider === 'LocalWorker' && plan.outcome === 'completed');
    expect(plans.some((plan) => plan.plan.actions.some((action) => action.type === 'setValue' && action.value === title))).toBe(true);
  }
  for (const action of [
    { action: 'SelectTab', targetSuffix: '-tab-all' },
    { action: 'SetValue', targetSuffix: '-input-search-tasks-by-title-or-description', value: title },
  ]) {
    const execution = events.find((event) => event.type === 'liveui.request' && event.payload.operation === 'execute' &&
      event.payload.action === action.action && event.payload.targetId?.endsWith(action.targetSuffix) &&
      (action.value === undefined || event.payload.value === action.value));
    expect(execution).toBeTruthy();
    const receipt = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === execution.payload.correlationId);
    expect(receipt).toBeTruthy();
    expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  }
});
