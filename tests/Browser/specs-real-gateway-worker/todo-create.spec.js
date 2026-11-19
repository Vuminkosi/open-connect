import { expect, test } from '@playwright/test';

import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';

const digest = (value) => typeof value === 'string' && value.length > 0
  ? createHash('sha256').update(value).digest('hex') : null;

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
  await page.getByRole('textbox', { name: /message ellai/i }).fill(`Create a task called ${title}.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  const assistantResponse = await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const gatewayUrl = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL ?? 'http://127.0.0.1:5120';
  const response = await request.get(`${gatewayUrl}/openconnect/v1/sessions/${sessionId}/events`, {
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
      stateName: payload.stateName ?? null, targetId: digest(payload.targetId), correlationId: digest(payload.correlationId),
      provider: ['Browser', 'LocalWorker', 'Gateway'].includes(result?.provider) ? result.provider : null,
      originTool: payload.originTool === 'liveui_click_and_observe_text' ? payload.originTool : null,
      authorizationOutcome: ['Allowed', 'Denied', 'TimedOut'].includes(result?.authorizationOutcome) ? result.authorizationOutcome : null };
  });
  await page.reload();
  await expect.poll(() => page.getByText(title, { exact: true }).count()).toBe(1).catch(() => {});
  const persistedCount = await page.getByText(title, { exact: true }).count();
  const diagnostics = {
    traceId: digest(sessionId), selectedTool: trace.some((item) => item.originTool === 'liveui_click_and_observe_text')
      ? 'liveui_click_and_observe_text' : null, discoveryStrategy: null,
    authorizationOutcome: trace.find((item) => item.originTool === 'liveui_click_and_observe_text' && item.action === 'Click')?.authorizationOutcome ?? null,
    finalResponse: null,
    finalResponseContainsRequestedTitle: assistantResponse.includes(title),
    persistedMatchCount: persistedCount, interactions: trace,
  };
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.mkdir(path.dirname(tracePath), { recursive: true });
  await fs.writeFile(tracePath, JSON.stringify(diagnostics, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  console.log(`Sanitized LiveUI trace: ${JSON.stringify(diagnostics)}`);
  expect(persistedCount).toBe(1);
  expect(trace.some((item) => item.operation === 'execute' && item.verified === true)).toBe(true);
  const clickIndex = trace.findIndex((item) => item.operation === 'execute' &&
    ['click', 'activate'].includes(item.action?.toLowerCase()) && item.code === 'click_dispatched_without_postcondition');
  expect(clickIndex).toBeGreaterThanOrEqual(0);
  const matchedRow = trace.slice(clickIndex + 1).some((item) => item.operation === 'highlight' && item.matchesRequestedTitle &&
    item.outcome === 'Completed' && item.code === 'highlight_active' && item.verified === true);
  const observedChange = trace.slice(clickIndex + 1).some((after) => after.operation === 'await_state' &&
    after.stateName === 'textContains' && after.matchesRequestedTitle && after.verified === true &&
    trace.slice(0, clickIndex).some((before) => before.operation === 'await_state' && before.targetId === after.targetId &&
      before.stateName === 'textNotContains' && before.matchesRequestedTitle && before.verified === true));
  expect(matchedRow || observedChange).toBe(true);
  if (requireObservedChange) {
    expect(observedChange).toBe(true);
    expect(trace[clickIndex].originTool).toBe('liveui_click_and_observe_text');
    expect(trace[clickIndex].authorizationOutcome).toBe('Allowed');
  }
  expect(assistantResponse).toMatch(/\b(created|added|visible|showing|appears)\b/i);
  expect(assistantResponse).toContain(title);
  expect(assistantResponse).not.toMatch(/\b(unable|failed|could not|couldn't|not created|not added)\b/i);
});
