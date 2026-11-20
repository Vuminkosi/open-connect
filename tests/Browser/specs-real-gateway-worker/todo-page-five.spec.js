import { approvalDetails } from '../scripts/approval-details.mjs';
import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';

test('T17: finds a task on the fifth page through the assistant', async ({ page, request }, testInfo) => {
  const prefix = 'Page five task';
  const title = `${prefix} 01`;
  await page.goto('/');
  const input = page.getByRole('textbox', { name: 'Task Title' });
  const add = page.getByRole('button', { name: 'Add Task' });
  for (let number = 1; number <= 41; number++) {
    await input.fill(`${prefix} ${String(number).padStart(2, '0')}`);
    await input.blur();
    await expect(add).toBeEnabled();
    await add.click();
    await expect(input).toBeEmpty();
  }
  await page.getByRole('textbox', { name: 'Search tasks by title or description...' }).fill(prefix);
  const pages = page.getByRole('navigation', { name: 'Task pages' });
  const selector = pages.getByRole('combobox', { name: 'Task page' });
  await expect(pages).toContainText('Page 1 of 5');
  await selector.selectOption('5');
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  await selector.selectOption('1');
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);

  let rejected = false;
  const confirmations = [];
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    const pageSelection = /^Allow Select on 'Task page'\?\s*\nValue: [1-5](?:\s|$)/.test(details);
    const pageButton = /^Allow (?:Click|Activate) on '(?:Next task page|Previous task page)'\?/i.test(details);
    const showAll = /^Allow SelectTab on 'All'\?/.test(details);
    const read = /^Allow (Inspect|Focus|WaitForState|Highlight) on /.test(details);
    confirmations.push({ category: pageSelection ? 'page-selection' : pageButton ? 'page-button' : showAll ? 'show-all' : read ? 'read' : 'other',
      action: details.match(/^Allow (\w+) on /)?.[1] ?? null,
      pageControl: /task page|next page|previous page|pagination/i.test(details),
      searchControl: /search/i.test(details),
      allowed: pageSelection || pageButton || showAll || read });
    if (pageSelection || pageButton || showAll || read) {
      await dialog.getByRole('button', { name: 'Allow' }).click();
    } else {
      rejected = true;
      await dialog.getByRole('button', { name: 'Cancel' }).click();
    }
  });
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  await page.locator('.mud-overlay').last().click({ position: { x: 5, y: 5 } });
  await page.getByRole('textbox', { name: /message ellai/i }).fill(`Find the task called ${title} by navigating the task pages.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  let responseTimedOut = false;
  try { await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 }); }
  catch { responseTimedOut = true; }
  const answer = responseTimedOut ? '' : await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  const response = await request.get(`${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    const pageSelector = payload.operation === 'get_current_page' && Array.isArray(receipt?.targets)
      ? receipt.targets.find((target) => target.label === 'Task page') : null;
    return { operation: payload.operation, action: payload.action ?? null, selectedPage: payload.action === 'Select' && payload.value === '5',
      pageSelectorDiscovered: Boolean(pageSelector), pageSelectorSelectable: Boolean(pageSelector?.capabilities?.includes('Select')),
      outcome: receipt?.outcome ?? null,
      code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null };
  });
  await fs.writeFile(testInfo.outputPath('sanitized-liveui-trace.json'), JSON.stringify({ scenario: 'T17', responseTimedOut, rejected, confirmations, trace }, null, 2));
  expect(responseTimedOut).toBe(false);
  expect(rejected).toBe(false);
  expect(answer).toContain(title);
  await expect(selector).toHaveValue('5');
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  expect(trace.some((item) => item.operation === 'execute' &&
    (item.action === 'Select' && item.selectedPage && item.outcome === 'Completed' && item.verified ||
      confirmations.some((confirmation) => confirmation.category === 'page-button' && confirmation.allowed) &&
      ['Click', 'Activate'].includes(item.action) && ['Completed', 'Uncertain'].includes(item.outcome)))).toBe(true);
});
