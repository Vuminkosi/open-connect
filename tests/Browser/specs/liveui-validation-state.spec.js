import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('input validation inspection reports state without exposing data or dispatching validation events', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <label>Title <input id="title" required></label>
      <label>Choice <select id="choice" aria-invalid="true"><option>private value</option></select></label>
      <label>Password <input id="password" type="password" value="private secret"></label>
      <label>Done <input id="done" type="checkbox"></label>` }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    let invalidEvents = 0;
    document.addEventListener('invalid', () => invalidEvents++, true);
    const run = (id, kind, type, parameters = {}) => execute({ manifest: {}, route: { path: '/' },
      target: { id, kind, selector: `#${id}` }, action: { schemaVersion: 'openconnect.liveui/v1', type, parameters } }, 100, false);
    const required = await run('title', 'Input', 'Inspect');
    const waitInvalid = await run('title', 'Input', 'WaitForState', { stateName: 'invalid', stateValue: 'true' });
    document.querySelector('#title').value = 'private input value';
    const valid = await run('title', 'Input', 'Inspect');
    const waitValid = await run('title', 'Input', 'WaitForState', { stateName: 'invalid', stateValue: 'false' });
    const matches = await run('title', 'Input', 'WaitForState', { stateName: 'valueEquals', stateValue: 'private input value' });
    const mismatch = await run('title', 'Input', 'WaitForState', { stateName: 'valueEquals', stateValue: 'other value' });
    const secret = await run('password', 'Input', 'WaitForState', { stateName: 'valueEquals', stateValue: 'private secret' });
    const ariaInvalid = await run('choice', 'Select', 'Inspect');
    const checkbox = await run('done', 'Checkbox', 'Inspect');
    return { required, waitInvalid, valid, waitValid, matches, mismatch, secret, ariaInvalid, checkbox, invalidEvents };
  });
  expect(results.required).toMatchObject({ outcome: 'Completed', verifiedState: 'control_invalid' });
  expect(results.valid).toMatchObject({ outcome: 'Completed', verifiedState: 'control_valid' });
  expect(results.ariaInvalid).toMatchObject({ outcome: 'Completed', verifiedState: 'control_invalid' });
  expect(results.checkbox).toMatchObject({ outcome: 'Completed', verifiedState: 'control_unchecked' });
  expect(results.waitInvalid.postconditionVerified).toBe(true);
  expect(results.waitValid.postconditionVerified).toBe(true);
  expect(results.matches).toMatchObject({ outcome: 'Completed', code: 'state_verified', postconditionVerified: true });
  expect(results.mismatch).toMatchObject({ outcome: 'Uncertain', postconditionVerified: false });
  expect(results.secret).toMatchObject({ outcome: 'Denied', code: 'sensitive_input_blocked', postconditionVerified: false });
  expect(results.invalidEvents).toBe(0);
  expect(JSON.stringify(results)).not.toContain('private');
});
