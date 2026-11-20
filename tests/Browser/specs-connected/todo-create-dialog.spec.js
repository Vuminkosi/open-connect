import { expect, test } from '@playwright/test';

for (const viewport of [{ name: 'desktop', width: 1280, height: 800 }, { name: 'mobile', width: 390, height: 844 }]) {
  test(`create task in a dialog: ${viewport.name}`, async ({ page }) => {
    const title = `Dialog task ${viewport.name}`;
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    const open = page.getByRole('button', { name: 'New task dialog' });
    await open.focus();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog', { name: 'Create task' });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Add Task' }).click();
    await expect(dialog.getByText('Enter a task title.')).toBeVisible();
    const invalid = await page.evaluate(async () => {
      const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
      const target = runtime.discoverTargets(500).find((item) => item.kind === 'Input' && item.label === 'Task Title');
      return runtime.execute({ manifest: {}, route: { path: '/', targets: [] }, target: { ...target, id: 'test-title' },
        action: { schemaVersion: 'openconnect.liveui/v1', type: 'Inspect', parameters: {} } }, 1000, true);
    });
    expect(invalid).toMatchObject({ outcome: 'Completed', verifiedState: 'control_invalid' });
    await dialog.getByRole('textbox', { name: 'Task Title' }).fill(title);
    await expect(dialog.getByText('Enter a task title.')).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true);
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(open).toBeFocused();
    await open.click();
    await expect(dialog).toBeVisible();
    const discovered = await page.evaluate(async () => {
      const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
      return runtime.discoverTargets(500);
    });
    expect(discovered).toEqual(expect.arrayContaining([expect.objectContaining({ kind: 'Input', label: 'Task Title' })]));
    await dialog.getByRole('button', { name: 'Cancel task creation' }).click();
    await expect(dialog).toHaveCount(0);
    await expect(open).toBeFocused();
    await expect(page.getByText(title, { exact: true })).toHaveCount(0);

    await open.click();
    await expect(dialog).toBeVisible();
    const input = dialog.getByRole('textbox', { name: 'Task Title' });
    await input.fill(title);
    await input.blur();
    await dialog.getByLabel('Due date (optional)').fill('2030-08-14');
    const add = dialog.getByRole('button', { name: 'Add Task' });
    await expect(add).toBeEnabled();
    await add.dblclick();
    await expect(dialog).toHaveCount(0);
    await expect(page.getByText(title, { exact: true })).toHaveCount(1);
    await page.reload();
    const row = page.locator('.mud-list-item').filter({ hasText: title });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Due 2030-08-14');
  });
}

test('SDK verifies removal of the modal save control without matching the background editor', async ({ page }) => {
  const title = 'Modal scoped save proof';
  await page.goto('/');
  await page.getByRole('button', { name: 'New task dialog' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create task' });
  await dialog.getByRole('textbox', { name: 'Task Title' }).fill(title);
  const result = await page.evaluate(async () => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    const targets = runtime.discoverTargets(500);
    const target = targets.find((item) => item.kind === 'Button' && /^add task$/i.test(item.label));
    if (!target) return { targets };
    const receipt = await runtime.execute({ manifest: {}, route: { path: '/', targets: [] },
      target: { ...target, id: 'oc.runtime.test-save', requiresConfirmation: true },
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Click', parameters: { stateName: 'exists', stateValue: 'false' } } }, 5000, true);
    return { receipt, targets };
  });
  expect(result.targets).toEqual(expect.arrayContaining([expect.objectContaining({ kind: 'Button', label: expect.stringMatching(/^add task$/i) })]));
  expect(result.receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  await expect(dialog).toHaveCount(0);
  await page.reload();
  await expect(page.getByText(title, { exact: true })).toHaveCount(1);
});
