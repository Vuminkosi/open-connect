import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('selects a unique enabled option and rejects ambiguous or disabled options', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => {
    if (route.request().url().endsWith('/liveui.js')) {
      await route.fulfill({ contentType: 'text/javascript', body: runtime });
    } else {
      await route.fulfill({ contentType: 'text/html', body: '<select id="priority"><option value="normal">Normal</option><option value="urgent">Urgent</option><option value="locked" disabled>Locked</option><optgroup disabled><option value="group-locked">Group locked</option></optgroup><option value="one">Duplicate</option><option value="two">Duplicate</option></select>' });
    }
  });
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const select = document.querySelector('#priority');
    let changes = 0;
    select.addEventListener('change', () => changes++);
    const run = (value) => execute({
      manifest: {}, route: { path: '/' },
      target: { id: 'priority', kind: 'Select', selector: '#priority' },
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Select', parameters: { value } },
    }, 5000, false);
    const selected = await run('Urgent');
    const selectedValue = select.value;
    const disabled = await run('locked');
    const groupDisabled = await run('group-locked');
    const ambiguous = await run('Duplicate');
    select.disabled = true;
    const disabledControl = await run('normal');
    return { selected, selectedValue, disabled, groupDisabled, ambiguous, disabledControl, changes, finalValue: select.value };
  });
  expect(results.selected).toMatchObject({ outcome: 'Completed', code: 'selection_verified' });
  expect(results.selectedValue).toBe('urgent');
  expect(results.disabled).toMatchObject({ outcome: 'Denied', code: 'option_disabled' });
  expect(results.groupDisabled).toMatchObject({ outcome: 'Denied', code: 'option_disabled' });
  expect(results.ambiguous).toMatchObject({ outcome: 'Ambiguous', code: 'option_ambiguous' });
  expect(results.disabledControl).toMatchObject({ outcome: 'Denied', code: 'select_disabled' });
  expect(results.changes).toBe(1);
  expect(results.finalValue).toBe('urgent');
});
