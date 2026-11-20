import { expect, test } from '@playwright/test';

test('deletion requires confirmation and refuses a changed task', async ({ page, context }) => {
  const title = 'Deletion confirmation task';
  await page.goto('/');
  const input = page.getByRole('textbox', { name: 'Task Title' });
  await input.fill(title);
  await input.blur();
  const add = page.getByRole('button', { name: 'Add Task' });
  await expect(add).toBeEnabled();
  await add.click();
  const row = page.locator('.mud-list-item').filter({ hasText: title });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Delete task' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText(title);
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(row).toHaveCount(1);

  await row.getByRole('button', { name: 'Delete task' }).click();
  await expect(dialog).toBeVisible();
  const other = await context.newPage();
  await other.goto('/');
  const otherRow = other.locator('.mud-list-item').filter({ hasText: title });
  await expect(otherRow).toHaveCount(1);
  await otherRow.getByRole('checkbox').check();
  await expect(otherRow.getByRole('checkbox')).toBeChecked();
  await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByText('The task changed. Review the latest version before deleting it.')).toBeVisible();
  await expect(row).toHaveCount(1);
  await expect(row.getByRole('checkbox')).toBeChecked();

  await row.getByRole('button', { name: 'Delete task' }).click();
  await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(row).toHaveCount(0);
  await page.reload();
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  await other.close();
});
