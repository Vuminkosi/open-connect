import { expect, test } from '@playwright/test';

for (const viewport of [{ name: 'desktop', width: 1280, height: 800 }, { name: 'mobile', width: 390, height: 844 }]) {
  test(`task pages remain navigable and reset on filtering: ${viewport.name}`, async ({ page }) => {
    const prefix = `Pagination ${viewport.name}`;
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.emulateMedia({ colorScheme: viewport.name === 'mobile' ? 'dark' : 'light' });
    await page.goto('/');
    const title = page.getByRole('textbox', { name: 'Task Title' });
    const add = page.getByRole('button', { name: 'Add Task' });
    for (let number = 1; number <= 41; number++) {
      await title.fill(`${prefix} ${String(number).padStart(2, '0')}`);
      await title.blur();
      await expect(add).toBeEnabled();
      await add.click();
      await expect(title).toBeEmpty();
    }
    const search = page.getByRole('textbox', { name: 'Search tasks by title or description...' });
    await search.fill(prefix);
    const pages = page.getByRole('navigation', { name: 'Task pages' });
    await expect(pages).toContainText('Page 1 of 5');
    const previous = pages.getByRole('button', { name: 'Previous task page' });
    const next = pages.getByRole('button', { name: 'Next task page' });
    await expect(previous).toBeDisabled();
    for (let number = 2; number <= 5; number++) {
      await next.click();
      await expect(pages).toContainText(`Page ${number} of 5`);
    }
    await expect(next).toBeDisabled();
    await expect(page.locator('.mud-list-item')).toHaveCount(1);
    await previous.focus();
    await page.keyboard.press('Enter');
    await expect(pages).toContainText('Page 4 of 5');
    await pages.getByRole('combobox', { name: 'Task page' }).selectOption('5');
    await expect(pages).toContainText('Page 5 of 5');
    await search.fill(`${prefix} 01`);
    await expect(page.getByText(`${prefix} 01`, { exact: true })).toBeVisible();
    await expect(pages).toHaveCount(0);
    await search.fill(prefix);
    await expect(pages).toContainText('Page 1 of 5');
    await pages.getByRole('combobox', { name: 'Task page' }).selectOption('5');
    await page.getByRole('tab', { name: 'Active' }).click();
    await expect(pages).toContainText('Page 1 of 5');
    await pages.getByRole('combobox', { name: 'Task page' }).selectOption('5');
    await expect(page.locator('.mud-list-item')).toHaveCount(1);
    await page.getByRole('button', { name: 'Delete task' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(pages).toContainText('Page 4 of 4');
    await expect(next).toBeDisabled();
  });
}
