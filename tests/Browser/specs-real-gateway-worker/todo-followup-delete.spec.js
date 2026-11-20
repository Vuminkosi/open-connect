import { approvalDetails } from '../scripts/approval-details.mjs';
import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';

test('T05: deletes the task created in the preceding turn', async ({ page, request }, testInfo) => {
  const title = 'Review follow-up deletion';
  await page.goto('/');
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  let rejectedConfirmation = false;
  const rejectedConfirmationActions = [];
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    const safeInput = details.includes("'Task Title'") && details.includes(`Value: ${title}`);
    const safeAdd = details.includes("'Add Task'") &&
      await page.getByRole('textbox', { name: 'Task Title' }).inputValue() === title &&
      await page.getByText(title, { exact: true }).count() === 0;
    const safeFilter = details.includes("'Search tasks by title or description...'") && details.includes(`Value: ${title}`);
    const safeDelete = /^Allow Click on 'Delete task'\?/.test(details) &&
      await page.getByText(title, { exact: true }).count() === 1 &&
      await page.getByRole('button', { name: 'Delete task' }).count() === 1;
    const safeRead = /^Allow (Inspect|Focus|WaitForState) on /.test(details);
    if (safeInput || safeAdd || safeFilter || safeDelete || safeRead || /^Allow SelectTab on '(All|Active|Completed)'\?/.test(details))
      await dialog.getByRole('button', { name: 'Allow' }).click();
    else {
      rejectedConfirmation = true;
      rejectedConfirmationActions.push(details.match(/^Allow ([A-Za-z]+) on /)?.[1] ?? 'unknown');
      await dialog.getByRole('button', { name: 'Cancel' }).click();
    }
  });
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  const message = page.getByRole('textbox', { name: /message ellai/i });
  const send = page.getByRole('button', { name: /send to ellai/i });
  await message.fill(`Create a task called ${title}.`);
  await send.click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  await expect(page.getByText(title, { exact: true })).toHaveCount(1);
  await message.fill('Delete the task I just created.');
  await send.click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  const answer = await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const gatewayUrl = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL ?? 'http://127.0.0.1:5120';
  const response = await request.get(`${gatewayUrl}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = await response.json();
  const accepted = events.filter((event) => event.type === 'task.accepted').length;
  const interactions = events.filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = interactions.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = interactions.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, outcome: receipt?.outcome ?? null,
      code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null,
      targetsDeleteControl: typeof payload.targetId === 'string' && /delete/i.test(payload.targetId) };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.mkdir(path.dirname(tracePath), { recursive: true });
  await fs.writeFile(tracePath, JSON.stringify({ scenario: 'T05', accepted, rejectedConfirmation, rejectedConfirmationActions, trace }, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  await page.reload();
  expect(rejectedConfirmation).toBe(false);
  expect(accepted).toBe(2);
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  expect(trace.some((item) => item.operation === 'execute' && ['Click', 'Activate'].includes(item.action) &&
    item.targetsDeleteControl && item.outcome === 'Completed' && item.verified === true)).toBe(true);
  expect(answer).toMatch(/\b(deleted|removed)\b/i);
  expect(answer).not.toMatch(/\b(unable|failed|could not|couldn't|not deleted)\b/i);
});
