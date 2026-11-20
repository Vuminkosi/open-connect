import { expect, test } from '@playwright/test';

test('due dates persist and tasks sort by earliest deadline', async ({ page }) => {
  await page.goto('/');
  const title = page.getByRole('textbox', { name: 'Task Title' });
  const dueDate = page.getByLabel('Due date (optional)');
  const add = page.getByRole('button', { name: 'Add Task' });
  for (const [name, date] of [['Deadline later', '2030-06-15'], ['Deadline earlier', '2030-06-05']]) {
    await title.fill(name);
    await title.blur();
    await dueDate.fill(date);
    await expect(add).toBeEnabled();
    await add.click();
    await expect(title).toBeEmpty();
    await expect(dueDate).toHaveValue('');
  }
  await page.reload();
  const rows = page.locator('.mud-list-item');
  await expect(async () => {
    await page.getByRole('textbox', { name: 'Search tasks by title or description...' }).fill('Deadline');
    await expect(rows).toHaveCount(2, { timeout: 1_000 });
  }).toPass({ timeout: 15_000, intervals: [250] });
  await page.getByLabel('Sort tasks').selectOption('deadline');
  await expect(rows.nth(0)).toContainText('Deadline earlier');
  await expect(rows.nth(0)).toContainText('Due 2030-06-05');
  await expect(rows.nth(1)).toContainText('Deadline later');
  await expect(rows.nth(1)).toContainText('Due 2030-06-15');
});
