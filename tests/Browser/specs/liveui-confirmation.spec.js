import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test.beforeEach(async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<button id="origin">Origin</button>' }));
  await page.goto('http://liveui.test/');
});

test('confirmation uses plain text, defaults focus to cancel, and restores focus after allow', async ({ page }) => {
  await page.locator('#origin').focus();
  await page.evaluate(async () => {
    const runtime = await import('/liveui.js');
    window.confirmation = runtime.confirmAction('Allow <img src=x> ?', 5000, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  });
  const dialog = page.getByRole('dialog', { name: 'Confirm action' });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('img')).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await dialog.getByRole('button', { name: 'Allow' }).click();
  expect(await page.evaluate(() => window.confirmation)).toBe(true);
  await expect(page.locator('#origin')).toBeFocused();
});

test('escape and expiry deny and remove the dialog', async ({ page }) => {
  await page.evaluate(async () => {
    const runtime = await import('/liveui.js');
    window.confirmation = runtime.confirmAction('Allow action?', 5000, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  });
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.keyboard.press('Escape');
  expect(await page.evaluate(() => window.confirmation)).toBe(false);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(await page.evaluate(async () => (await import('/liveui.js')).confirmAction('Expires', 30, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'))).toBe(false);
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('cancellation closes the pending dialog and denies approval', async ({ page }) => {
  await page.evaluate(async () => {
    const runtime = await import('/liveui.js');
    window.confirmation = runtime.confirmAction('Allow action?', 5000, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  });
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.evaluate(async () => (await import('/liveui.js')).cancelConfirmation('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'));
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.evaluate(async () => (await import('/liveui.js')).cancelConfirmation('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'));
  expect(await page.evaluate(() => window.confirmation)).toBe(false);
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('cancellation arriving before the prompt prevents it from opening', async ({ page }) => {
  const result = await page.evaluate(async () => {
    const runtime = await import('/liveui.js');
    const id = 'cccccccccccccccccccccccccccccccc';
    runtime.cancelConfirmation(id);
    return runtime.confirmAction('Expired request', 5000, id);
  });
  expect(result).toBe(false);
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('a host approval dialog blocks discovery and navigation until it has closed', async ({ page }) => {
  const state = await page.evaluate(async () => {
    const runtime = await import('/liveui.js');
    const id = 'dddddddddddddddddddddddddddddddd';
    const began = runtime.beginMudConfirmation(id);
    const nested = runtime.beginMudConfirmation('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee');
    const during = { targets: runtime.discoverTargets(20).length, navigable: runtime.canNavigate() };
    runtime.endMudConfirmation('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee');
    const wrongIdStillBlocked = !runtime.canNavigate();
    const marker = document.createElement('div');
    marker.className = 'oc-liveui-approval';
    marker.textContent = 'Approval';
    document.body.append(marker);
    runtime.endMudConfirmation(id);
    const visibleDialogStillBlocked = !runtime.canNavigate();
    marker.remove();
    return { began, nested, during, wrongIdStillBlocked, visibleDialogStillBlocked, after: runtime.canNavigate() };
  });
  expect(state).toEqual({ began: true, nested: false, during: { targets: 0, navigable: false },
    wrongIdStillBlocked: true, visibleDialogStillBlocked: true, after: true });
});

test('a cancelled host approval cannot begin late', async ({ page }) => {
  const started = await page.evaluate(async () => {
    const runtime = await import('/liveui.js');
    const id = 'ffffffffffffffffffffffffffffffff';
    runtime.endMudConfirmation(id);
    return runtime.beginMudConfirmation(id);
  });
  expect(started).toBe(false);
});
