import { approvalDetails } from '../scripts/approval-details.mjs';
import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';

test('T02: creates three separate tasks and retains each exactly once', async ({ page, request }, testInfo) => {
  const titles = ['Call John', 'Review quotes', 'Send invoices'];
  await page.goto('/');
  for (const title of titles) await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  let unexpectedConfirmation = false;
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    const currentTitle = await page.getByRole('textbox', { name: 'Task Title' }).inputValue();
    const safeInput = details.includes("'Task Title'") && titles.some((title) => details.includes(`Value: ${title}`));
    const safeAdd = details.includes("'Add Task'") && titles.includes(currentTitle) &&
      await page.getByText(currentTitle, { exact: true }).count() === 0;
    if (safeInput || safeAdd) await dialog.getByRole('button', { name: 'Allow' }).click();
    else {
      await dialog.getByRole('button', { name: 'Cancel' }).click();
      unexpectedConfirmation = true;
    }
  });
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  await page.locator('.mud-overlay').last().click({ position: { x: 5, y: 5 } });
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Create three tasks: Call John, Review quotes, Send invoices.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  let responseTimedOut = false;
  try { await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 }); }
  catch { responseTimedOut = true; }
  const answer = responseTimedOut ? '' : await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  const gatewayUrl = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL ?? 'http://127.0.0.1:5120';
  const response = await request.get(`${gatewayUrl}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, outcome: receipt?.outcome ?? null,
      code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.mkdir(path.dirname(tracePath), { recursive: true });
  await fs.writeFile(tracePath, JSON.stringify({ scenario: 'T02', responseTimedOut, trace }, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  await page.reload();
  expect(responseTimedOut).toBe(false);
  expect(unexpectedConfirmation).toBe(false);
  for (const title of titles) {
    await expect(page.getByText(title, { exact: true })).toHaveCount(1);
    expect(answer).toContain(title);
  }
  expect(trace.filter((item) => item.operation === 'execute' && item.action === 'SetValue' && item.verified)).toHaveLength(3);
  expect(trace.filter((item) => item.operation === 'execute' && ['Click', 'Activate'].includes(item.action))).toHaveLength(3);
  expect(answer).not.toMatch(/\b(unable|failed|could not|couldn't)\b/i);
});
