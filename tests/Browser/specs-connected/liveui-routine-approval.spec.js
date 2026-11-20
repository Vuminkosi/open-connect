import { expect, test } from '@playwright/test';

for (const viewport of [{ name: 'desktop', width: 1280, height: 800 }, { name: 'mobile', width: 390, height: 844 }]) {
  test(`routine tab and search proceed without approval on ${viewport.name}`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await page.locator('.oc-anchored-fab').click();
    await expect(page.locator('.oc-status__text')).toHaveText('Ready');
    let approvalSeen = false;
    await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
      approvalSeen = true;
      await dialog.getByRole('button', { name: 'Cancel' }).click();
    });
    await page.getByRole('textbox', { name: /message ellai/i }).fill('Show completed tasks containing report');
    await page.getByRole('button', { name: /send to ellai/i }).click();
    await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 30_000 });
    await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
    await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 30_000 });
    expect(approvalSeen).toBe(false);
  });
}
