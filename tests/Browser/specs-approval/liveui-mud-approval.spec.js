import { expect, test } from '@playwright/test';
import { approvalDetails } from '../scripts/approval-details.mjs';

for (const viewport of [{ name: 'desktop', width: 1280, height: 800 }, { name: 'mobile', width: 390, height: 844 }]) {
  test(`SDK approval is centered and understandable on ${viewport.name}`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await page.locator('.oc-anchored-fab').click();
    await expect(page.locator('.oc-status__text')).toHaveText('Ready');
    await page.getByRole('textbox', { name: /message ellai/i }).fill('Request task deletion for approval test');
    await page.getByRole('button', { name: /send to ellai/i }).click();
    const dialog = page.getByRole('dialog', { name: 'Confirm action' });
    await expect(dialog).toBeVisible({ timeout: 30_000 });
    await expect(dialog).toContainText('Activate Delete task?');
    expect(await approvalDetails(dialog)).toBe("Allow Click on 'Delete task'?");
    await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
    const discovery = await page.evaluate(async () => {
      const sdk = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
      return { targets: sdk.discoverTargets(20).length, navigable: sdk.canNavigate() };
    });
    expect(discovery).toEqual({ targets: 0, navigable: false });
    const bounds = await dialog.boundingBox();
    expect(bounds).not.toBeNull();
    expect(Math.abs(bounds.x + bounds.width / 2 - viewport.width / 2)).toBeLessThan(8);
    expect(Math.abs(bounds.y + bounds.height / 2 - viewport.height / 2)).toBeLessThan(8);
    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
  });
}

test('allowing the SDK dialog performs the authorized action', async ({ page }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Request task deletion for approval test');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  const dialog = page.getByRole('dialog', { name: 'Confirm action' });
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  await dialog.getByRole('button', { name: 'Allow' }).click({ timeout: 5000 });
  await expect(dialog).toHaveCount(0);
  const appDialog = page.locator('.mud-dialog').filter({ hasText: 'Delete task?' });
  await expect(appDialog).toBeVisible();
  await appDialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
});

test('Escape denies the action and restores focus', async ({ page }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  const input = page.getByRole('textbox', { name: /message ellai/i });
  await input.fill('Request task deletion for approval test');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  const dialog = page.getByRole('dialog', { name: 'Confirm action' });
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
  await expect(page.locator('body')).not.toBeFocused();
});
