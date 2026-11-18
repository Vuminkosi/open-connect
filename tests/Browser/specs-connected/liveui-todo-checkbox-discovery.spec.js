import { expect, test } from '@playwright/test';

test('discovers a task checkbox by the task shown in the existing UI', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
  const { targets, receipt } = await page.evaluate(async () => {
    const { discoverTargets, execute } = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    const targets = discoverTargets(500);
    const target = targets.find((item) => item.kind === 'Checkbox' && item.label === 'Prepare a launch checklist');
    if (!target) return { targets, receipt: null };
    const discovered = { ...target, id: 'oc.runtime.test', requiresConfirmation: true };
    const receipt = await execute({ manifest: {}, route: { path: '/', targets: [discovered] }, target: discovered,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Inspect', parameters: {} },
    }, 1000, true);
    return { targets, receipt };
  });
  expect(targets).toContainEqual(expect.objectContaining({ kind: 'Checkbox', label: 'Prepare a launch checklist' }));
  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});
