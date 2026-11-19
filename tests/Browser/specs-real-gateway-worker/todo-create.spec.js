import { expect, test } from '@playwright/test';

test('creates the requested Todo through an authorized live agent and keeps it after reload', async ({ page, request }, testInfo) => {
  await page.goto('/');
  const title = 'Review insurance renewals';
  const requireObservedChange = process.env.OPENCONNECT_LIVEUI_REQUIRE_OBSERVED_CHANGE === '1';
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await dialog.locator('p').innerText();
    if ((details.includes("'Task Title'") && details.includes(`Value: ${title}`)) || details.includes("'Add Task'")) {
      await dialog.getByRole('button', { name: 'Allow' }).click();
    } else {
      await dialog.getByRole('button', { name: 'Cancel' }).click();
      throw new Error('Unexpected action requested confirmation');
    }
  });
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill(requireObservedChange
    ? `Create a task called ${title} and verify that it appears in the list.`
    : `Create a task called ${title}.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  const assistantResponse = await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json())
    .filter((item) => item.type === 'liveui.request' || item.type === 'liveui.response')
    .map((item) => ({ type: item.type, payload: JSON.parse(item.data) }));
  const trace = events.filter((item) => item.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = events.find((item) => item.type === 'liveui.response' && item.payload.correlationId === payload.correlationId);
    const result = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, outcome: result?.outcome ?? null,
      code: result?.code ?? null, verified: result?.postconditionVerified ?? null,
      matchesRequestedTitle: payload.value === title || payload.stateValue === title,
      stateName: payload.stateName ?? null, targetId: payload.targetId ?? null };
  });
  await testInfo.attach('sanitized-liveui-trace', { body: JSON.stringify(trace, null, 2), contentType: 'application/json' });
  console.log(`Sanitized LiveUI trace: ${JSON.stringify(trace)}`);
  await page.reload();
  await expect(page.getByText(title, { exact: true })).toHaveCount(1);
  expect(trace.some((item) => item.operation === 'execute' && item.verified === true)).toBe(true);
  const clickIndex = trace.findIndex((item) => item.operation === 'execute' && item.action === 'Click' && item.code === 'click_dispatched_without_postcondition');
  expect(clickIndex).toBeGreaterThanOrEqual(0);
  const matchedRow = trace.slice(clickIndex + 1).some((item) => item.operation === 'highlight' && item.matchesRequestedTitle &&
    item.outcome === 'Completed' && item.code === 'highlight_active' && item.verified === true);
  const observedChange = trace.slice(clickIndex + 1).some((after) => after.operation === 'await_state' &&
    after.stateName === 'textContains' && after.matchesRequestedTitle && after.verified === true &&
    trace.slice(0, clickIndex).some((before) => before.operation === 'await_state' && before.targetId === after.targetId &&
      before.stateName === 'textNotContains' && before.matchesRequestedTitle && before.verified === true));
  expect(matchedRow || observedChange).toBe(true);
  if (requireObservedChange) expect(observedChange).toBe(true);
  expect(assistantResponse).toMatch(/\b(created|added|visible|showing|appears)\b/i);
  expect(assistantResponse).toContain(title);
  expect(assistantResponse).not.toMatch(/\b(unable|failed|could not|couldn't|not created|not added)\b/i);
});
