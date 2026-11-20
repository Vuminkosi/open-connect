import { approvalDetails } from '../scripts/approval-details.mjs';
import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import { createHmac, randomBytes } from 'node:crypto';

test('T19: creates a task through the task creation dialog', async ({ page, request }, testInfo) => {
  const title = 'Modal workflow proof';
  const traceKey = randomBytes(32);
  await page.addInitScript(() => {
    window.__sawTaskDialog = false;
    const observe = () => {
      if (document.querySelector('[role="dialog"][aria-modal="true"] #create-task-dialog-title'))
        window.__sawTaskDialog = true;
    };
    new MutationObserver(observe).observe(document, { childList: true, subtree: true });
  });
  await page.goto('/');
  let rejected = false;
  const confirmations = [];
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    const modal = page.getByRole('dialog', { name: 'Create task' });
    const open = /^Allow Click on 'New task dialog'\?/i.test(details);
    const input = /'Task Title'/i.test(details) && details.includes(`Value: ${title}`) && await modal.isVisible();
    const submit = /^Allow Click on 'Add Task'\?/i.test(details) && await modal.isVisible() &&
      await modal.getByRole('textbox', { name: 'Task Title' }).inputValue() === title;
    const read = /^Allow (Inspect|Focus|WaitForState|Highlight) on /i.test(details);
    const action = details.match(/^Allow (\w+) on /i)?.[1] ?? 'unknown';
    const category = open ? 'open-dialog' : input ? 'enter-title' : submit ? 'submit-dialog' : read ? 'read' : 'other';
    confirmations.push({ action, category, allowed: open || input || submit || read });
    if (open || input || submit || read) await dialog.getByRole('button', { name: 'Allow' }).click();
    else {
      rejected = true;
      await dialog.getByRole('button', { name: 'Cancel' }).click();
    }
  });
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  await page.locator('.mud-overlay').last().click({ position: { x: 5, y: 5 }, timeout: 5_000 });
  await expect(page.locator('.mud-overlay').last()).toBeHidden({ timeout: 5_000 });
  await page.getByRole('textbox', { name: /message ellai/i }).fill(`Open the task creation dialog and create a task called ${title}.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  let responseFailure;
  try { await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 }); }
  catch (error) { responseFailure = error; }
  const answer = responseFailure ? '' : await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  const sawDialog = await page.evaluate(() => window.__sawTaskDialog);
  const response = await request.get(`${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, stateName: payload.stateName ?? null, stateValue: payload.stateValue ?? null,
      targetHash: typeof payload.targetId === 'string' ? createHmac('sha256', traceKey).update(payload.targetId).digest('hex') : null,
      outcome: receipt?.outcome ?? null, code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null,
      verifiedState: receipt?.verifiedState ?? null };
  });
  let persistedCount = null;
  if (!responseFailure) {
    await page.reload();
    await expect.poll(() => page.getByText(title, { exact: true }).count(), { timeout: 10_000 }).toBe(1).catch(() => {});
    persistedCount = await page.getByText(title, { exact: true }).count();
  }
  await fs.writeFile(testInfo.outputPath('sanitized-liveui-trace.json'), JSON.stringify({ scenario: 'T19', sawDialog, rejected, persistedCount, confirmations, trace }, null, 2));
  if (responseFailure) throw responseFailure;
  expect(rejected).toBe(false);
  expect(sawDialog).toBe(true);
  expect(persistedCount).toBe(1);
  expect(trace).toContainEqual(expect.objectContaining({ operation: 'execute', action: 'SetValue', outcome: 'Completed', verified: true }));
  expect(trace.some((item) => item.operation === 'execute' && ['Click', 'Activate'].includes(item.action) &&
    item.stateName === 'exists' && item.stateValue === 'false' && item.outcome === 'Completed' && item.verified)).toBe(true);
  expect(answer.trim().length).toBeGreaterThan(0);
  expect(answer).not.toMatch(/\b(unable|failed|could not|couldn't|not created)\b/i);
});
