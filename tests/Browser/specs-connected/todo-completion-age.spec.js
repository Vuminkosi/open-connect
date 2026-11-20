import { expect, test } from '@playwright/test';

test('completion age filter uses completion time and resets when reopened', async ({ page }) => {
  await page.goto('/');
  const title = page.getByRole('textbox', { name: 'Task Title' });
  await title.fill('Recently completed task');
  await title.blur();
  const add = page.getByRole('button', { name: 'Add Task' });
  await expect(add).toBeEnabled();
  await add.click();
  const recent = page.locator('.mud-list-item').filter({ hasText: 'Recently completed task' });
  await expect(recent).toHaveCount(1);
  await recent.getByRole('checkbox').check();
  await expect(recent).toContainText('Completed ');

  const ageFilter = page.getByLabel('Completed more than 30 days ago');
  await ageFilter.check();
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Recently completed task', { exact: true })).toHaveCount(0);
  await ageFilter.uncheck();
  const old = page.locator('.mud-list-item').filter({ hasText: 'Review the quarterly report' });
  await old.getByRole('checkbox').click();
  await expect(old.getByRole('checkbox')).not.toBeChecked();
  await old.getByRole('checkbox').click();
  await expect(old.getByRole('checkbox')).toBeChecked();
  await ageFilter.check();
  await expect(page.getByText('Review the quarterly report', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await page.reload();
  await ageFilter.check();
  await expect(page.getByText('Review the quarterly report', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
});
