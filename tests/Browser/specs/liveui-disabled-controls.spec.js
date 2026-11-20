import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('invalid postconditions never dispatch an action', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<button id="save">Save</button>' }));
  await page.goto('http://liveui.test/');
  const result = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    let clicks = 0;
    document.querySelector('#save').addEventListener('click', () => clicks++);
    const run = (type, parameters) => execute({
      manifest: {}, route: { path: '/' }, target: { id: 'save', kind: 'Button', selector: '#save' },
      action: { schemaVersion: 'openconnect.liveui/v1', type, parameters },
    }, 100, false);
    const receipts = [await run('Click', { stateName: 'textContains', stateValue: '' }),
      await run('Click', { stateName: 'visible', stateValue: 'perhaps' }),
      await run('Open', { stateName: 'unknown', stateValue: 'true' }),
      await run('Close', { stateValue: 'false' }),
      await run('WaitForState', { stateName: 'exists', stateValue: 'perhaps' })];
    return { receipts, clicks };
  });
  expect(result.receipts.map((receipt) => receipt.code)).toEqual([
    'state_value_invalid', 'state_value_invalid', 'state_name_invalid', 'state_name_required', 'state_value_invalid',
  ]);
  expect(result.receipts.every((receipt) => receipt.outcome === 'Failed' && !receipt.postconditionVerified)).toBe(true);
  expect(result.clicks).toBe(0);
});

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

test('tab activation respects link navigation boundaries', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <a id="external" role="tab" aria-selected="false" href="https://external.example/">External</a>
      <a id="new-window" role="tab" aria-selected="false" href="/local" target="_blank">New window</a>
      <a id="download" role="tab" aria-selected="false" href="/local" download>Download</a>
      <a id="local" role="tab" aria-selected="false" href="/local">Local</a>` }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const clicks = [];
    document.querySelectorAll('a').forEach((link) => link.addEventListener('click', (event) => {
      event.preventDefault();
      clicks.push(link.id);
      link.setAttribute('aria-selected', 'true');
    }));
    const run = (id) => execute({
      manifest: {}, route: { path: '/' }, target: { id, kind: 'Tab', selector: `#${id}` },
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'SelectTab', parameters: {} },
    }, 100, false);
    return { external: await run('external'), newWindow: await run('new-window'),
      download: await run('download'), local: await run('local'), clicks };
  });
  for (const receipt of [results.external, results.newWindow, results.download])
    expect(receipt).toMatchObject({ outcome: 'Denied', code: 'navigation_boundary', postconditionVerified: false });
  expect(results.local).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.clicks).toEqual(['local']);
});
