import { expect, test } from '@playwright/test';

test('discovers task creation controls as they become available in the existing UI', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  const targets = await page.evaluate(async () => {
    const { discoverTargets } = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return discoverTargets(500);
  });
  expect(targets).toContainEqual(expect.objectContaining({ kind: 'Input', label: 'Task Title' }));
  expect(targets).not.toContainEqual(expect.objectContaining({ kind: 'Button', label: expect.stringMatching(/^add task$/i) }));

  await expect(async () => {
    await page.getByRole('textbox', { name: 'Task Title' }).fill('Discovery check');
    await page.getByRole('textbox', { name: 'Task Title' }).blur();
    await expect(page.getByRole('button', { name: 'Add Task' })).toBeEnabled({ timeout: 1000 });
  }).toPass({ timeout: 15000, intervals: [250] });
  const updatedTargets = await page.evaluate(async () => {
    const { discoverTargets } = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return discoverTargets(500);
  });
  expect(updatedTargets).toContainEqual(expect.objectContaining({ kind: 'Button', label: expect.stringMatching(/^add task$/i) }));
  expect(JSON.stringify(updatedTargets)).not.toContain('Discovery check');
});

test('SDK value interaction reaches the existing Todo component', async ({ page }) => {
  await page.goto('/');
  const title = page.getByRole('textbox', { name: 'Task Title' });
  const add = page.getByRole('button', { name: 'Add Task' });
  await expect(async () => {
    await title.fill('Ready');
    await title.blur();
    await expect(add).toBeEnabled({ timeout: 1000 });
  }).toPass({ timeout: 15000, intervals: [250] });
  await title.fill('');
  await title.blur();
  await expect(add).toBeDisabled();

  const receipt = await page.evaluate(async () => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    const target = runtime.discoverTargets(500).find((item) => item.kind === 'Input' && item.label === 'Task Title');
    return runtime.execute({
      manifest: {}, route: { path: '/', targets: [] }, target: { ...target, id: 'test-title' },
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'SetValue', parameters: { value: 'SDK discovery check' } },
    }, 5000, true);
  });
  expect(receipt.outcome).toBe('Completed');
  await expect(add).toBeEnabled();
});
