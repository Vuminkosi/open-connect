import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('disabled and readonly controls reject synthetic actions without dispatching events', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <input id="readonly" value="original" readonly>
      <input id="aria-readonly" value="original" aria-readonly="true">
      <input id="native-disabled" value="original" disabled>
      <select id="select-disabled" aria-disabled="true"><option value="one">One</option><option value="two">Two</option></select>
      <button id="aria-disabled" aria-disabled="true">Save</button>
      <div aria-disabled="true"><button id="inherited">Delete</button></div>
      <button id="tab-disabled" role="tab" aria-selected="false" aria-disabled="true">Disabled tab</button>` }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    let events = 0;
    document.addEventListener('input', () => events++);
    document.addEventListener('change', () => events++);
    document.addEventListener('click', () => events++);
    const run = (selector, kind, type, value) => execute({
      manifest: {}, route: { path: '/' }, target: { id: selector, kind, selector },
      action: { schemaVersion: 'openconnect.liveui/v1', type, parameters: { value } },
    }, 100, false);
    const receipts = [await run('#readonly', 'Input', 'SetValue', 'changed'),
      await run('#aria-readonly', 'Input', 'SetValue', 'changed'),
      await run('#native-disabled', 'Input', 'SetValue', 'changed'),
      await run('#select-disabled', 'Select', 'Select', 'two'),
      await run('#aria-disabled', 'Button', 'Click'), await run('#inherited', 'Button', 'Click'),
      await run('#tab-disabled', 'Tab', 'SelectTab')];
    return { receipts, events, values: [...document.querySelectorAll('input')].map((input) => input.value) };
  });
  for (const receipt of results.receipts) expect(receipt).toMatchObject({ outcome: 'Denied', postconditionVerified: false });
  expect(results.events).toBe(0);
  expect(results.values).toEqual(['original', 'original', 'original']);
});
