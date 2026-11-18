import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('tab state verification follows rerenders without clicking twice or trusting detached elements', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<button role="tab" aria-selected="false">Completed</button>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'completed', kind: 'Tab', label: 'Completed', role: 'tab' };
    const route = { path: '/', targets: [target] };
    async function run(mode) {
      document.body.innerHTML = '<button role="tab" aria-selected="false">Completed</button>';
      let clicks = 0;
      const original = document.querySelector('button');
      original.addEventListener('click', () => {
        clicks++;
        const replacement = original.cloneNode(true);
        replacement.setAttribute('aria-selected', mode === 'stale' ? 'false' : 'true');
        original.replaceWith(replacement);
        if (mode === 'stale') original.setAttribute('aria-selected', 'true');
        if (mode === 'navigation') history.pushState({}, '', '/other');
      });
      const result = await execute({ manifest: {}, route, target, action: {
        schemaVersion: 'openconnect.liveui/v1', type: 'SelectTab', parameters: {},
      } }, 100, true);
      history.replaceState({}, '', '/');
      return { result, clicks };
    }
    return { replaced: await run('replaced'), stale: await run('stale'), navigation: await run('navigation') };
  });
  expect(results.replaced.result).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.stale.result.outcome).not.toBe('Completed');
  expect(results.navigation.result.outcome).not.toBe('Completed');
  for (const result of Object.values(results)) expect(result.clicks).toBe(1);
});

test('value and selection receipts do not verify detached controls after an input rerender', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<main></main>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    async function run(kind, type) {
      document.querySelector('main').innerHTML = kind === 'Input'
        ? '<input aria-label="Title">'
        : '<select aria-label="Priority"><option value="normal">Normal</option><option value="high">High</option></select>';
      const element = document.querySelector(kind === 'Input' ? 'input' : 'select');
      let changes = 0;
      element.addEventListener('change', () => changes++);
      element.addEventListener('input', () => {
        const replacement = element.cloneNode(true);
        replacement.value = kind === 'Input' ? '' : 'normal';
        element.replaceWith(replacement);
      });
      const target = { id: kind, kind, label: kind === 'Input' ? 'Title' : 'Priority' };
      const result = await execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
        action: { schemaVersion: 'openconnect.liveui/v1', type, parameters: { value: kind === 'Input' ? 'Example' : 'high' } },
      }, 100, true);
      return { result, changes };
    }
    return [await run('Input', 'SetValue'), await run('Select', 'Select')];
  });
  for (const { result, changes } of results) {
    expect(result.outcome).not.toBe('Completed');
    expect(result.postconditionVerified).toBe(false);
    expect(changes).toBe(0);
  }
});

test('text postconditions require meaningful visible text', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<section aria-label="Results" style="min-height:20px"><span hidden>Saved</span></section>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'results', kind: 'Region', label: 'Results', role: 'region' };
    const run = (stateName, stateValue) => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'WaitForState', parameters: { stateName, stateValue } },
    }, 30, true);
    const empty = await run('textContains', '  ');
    const hidden = await run('textContains', 'Saved');
    const absent = await run('textNotContains', 'Saved');
    document.querySelector('span').hidden = false;
    const visible = await run('textContains', 'Saved');
    return { empty, hidden, absent, visible };
  });
  expect(results.empty).toMatchObject({ outcome: 'Failed', postconditionVerified: false });
  expect(results.hidden).toMatchObject({ outcome: 'Uncertain', postconditionVerified: false });
  expect(results.absent).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.visible).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});

test('waits for property-only checkbox changes without accepting a different route', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<input type="checkbox" aria-label="Complete">' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'complete', kind: 'Checkbox', label: 'Complete' };
    const run = () => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'WaitForState', parameters: { stateName: 'checked', stateValue: 'true' } },
    }, 300, true);
    const checkbox = document.querySelector('input');
    setTimeout(() => { checkbox.checked = true; }, 20);
    const checked = await run();
    checkbox.checked = false;
    setTimeout(() => { history.pushState({}, '', '/other'); checkbox.checked = true; }, 20);
    const navigated = await run();
    return { checked, navigated };
  });
  expect(results.checked).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.navigated).toMatchObject({ outcome: 'Uncertain', postconditionVerified: false });
});

test('waits for a known target to render without accepting ambiguous or wrong-route matches', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<main></main>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'result', kind: 'Button', label: 'Open result', role: 'button' };
    const run = () => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'WaitForState', parameters: { stateName: 'visible' } },
    }, 150, true);
    const main = document.querySelector('main');
    setTimeout(() => { main.innerHTML = '<button>Open result</button>'; }, 20);
    const rendered = await run();
    main.innerHTML = '';
    setTimeout(() => { main.innerHTML = '<button>Open result</button><button>Open result</button>'; }, 20);
    const ambiguous = await run();
    main.innerHTML = '';
    setTimeout(() => { history.pushState({}, '', '/other'); main.innerHTML = '<button>Open result</button>'; }, 20);
    const navigated = await run();
    return { rendered, ambiguous, navigated };
  });
  expect(results.rendered).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.ambiguous).toMatchObject({ outcome: 'Uncertain', postconditionVerified: false });
  expect(results.navigated).toMatchObject({ outcome: 'Uncertain', postconditionVerified: false });
});
