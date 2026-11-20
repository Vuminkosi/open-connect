import { expect, test } from '@playwright/test';
import { approvalDetails } from '../scripts/approval-details.mjs';

test('scenario policy reads the visible action and requested value', async ({ page }) => {
  await page.setContent(`<div role="dialog"><div class="mud-dialog-content">
    <p>Change Task Title?</p><span>Requested value</span><div class="mud-paper"><p>Review quotes</p></div>
    <p>Allow this action only if it matches your request.</p></div></div>`);
  expect(await approvalDetails(page.getByRole('dialog')))
    .toBe("Allow SetValue on 'Task Title'?\nValue: Review quotes");
});

test('scenario policy fails closed on unknown wording and accepts the legacy prompt', async ({ page }) => {
  await page.setContent('<div role="dialog"><div class="mud-dialog-content"><p>Do something?</p></div></div>');
  expect(await approvalDetails(page.getByRole('dialog'))).toBe('');
  await page.setContent('<div role="dialog"><p>Allow Click on \'Add Task\'?</p></div>');
  expect(await approvalDetails(page.getByRole('dialog'))).toBe("Allow Click on 'Add Task'?");
});
