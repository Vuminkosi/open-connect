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

test('a click does not dispatch when its postcondition was already true', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<button aria-expanded="false">Details</button>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'details', kind: 'Button', label: 'Details', role: 'button' };
    const button = document.querySelector('button');
    let clicks = 0;
    button.addEventListener('click', () => { clicks++; button.setAttribute('aria-expanded', 'true'); });
    const run = () => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Click', parameters: { stateName: 'expanded', stateValue: 'true' } },
    }, 100, true);
    const changed = await run();
    const alreadyTrue = await run();
    return { changed, alreadyTrue, clicks };
  });
  expect(results.changed).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.alreadyTrue).toMatchObject({ outcome: 'Denied', code: 'postcondition_already_satisfied', postconditionVerified: false });
  expect(results.clicks).toBe(1);
});

test('opening or closing an already matching disclosure does not toggle it', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<button aria-expanded="true">Details</button>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'details', kind: 'Button', label: 'Details', role: 'button' };
    const button = document.querySelector('button');
    let clicks = 0;
    button.addEventListener('click', () => { clicks++; button.setAttribute('aria-expanded', button.getAttribute('aria-expanded') === 'true' ? 'false' : 'true'); });
    const run = (type) => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type, parameters: {} },
    }, 100, true);
    const alreadyOpen = await run('Open');
    const closed = await run('Close');
    const alreadyClosed = await run('Close');
    return { alreadyOpen, closed, alreadyClosed, clicks };
  });
  for (const receipt of [results.alreadyOpen, results.closed, results.alreadyClosed])
    expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.clicks).toBe(1);
});

test('checkbox selection is explicit, verified and idempotent', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<input type="checkbox" aria-label="Complete task">' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute, discoverTargets } = await import('/liveui.js');
    const observed = discoverTargets(20).find((item) => item.kind === 'Checkbox' && item.label === 'Complete task');
    const target = { ...observed, id: 'complete' };
    const input = document.querySelector('input');
    let changes = 0;
    input.addEventListener('change', () => changes++);
    const run = (value) => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Select', parameters: { value } },
    }, 100, true);
    const checked = await run('true');
    const alreadyChecked = await run('true');
    const invalid = await run('yes');
    const unchecked = await run('false');
    const wrongKind = await execute({ manifest: {}, route: { path: '/', targets: [] },
      target: { id: 'wrong-kind', kind: 'Select', label: 'Control', selector: 'input' },
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Select', parameters: { value: 'true' } },
    }, 100, true);
    input.setAttribute('aria-readonly', 'true');
    const readOnly = await run('true');
    return { observed, checked, alreadyChecked, invalid, unchecked, wrongKind, readOnly, changes, final: input.checked };
  });
  expect(results.observed).toMatchObject({ kind: 'Checkbox', label: 'Complete task' });
  for (const receipt of [results.checked, results.alreadyChecked, results.unchecked])
    expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.invalid).toMatchObject({ outcome: 'Failed', code: 'checkbox_value_invalid' });
  expect(results.wrongKind).toMatchObject({ outcome: 'Denied', code: 'control_kind_mismatch' });
  expect(results.readOnly).toMatchObject({ outcome: 'Denied', code: 'control_readonly' });
  expect(results.changes).toBe(2);
  expect(results.final).toBe(false);
});

test('list inspection reports only coarse recognized visible row counts', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<ul aria-label="Results" style="min-height:20px"></ul>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'results', kind: 'List', label: 'Results' };
    const run = () => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Inspect', parameters: {} },
    }, 100, true);
    const list = document.querySelector('ul');
    const empty = await run();
    list.innerHTML = '<li>private content <ul><li>nested</li></ul></li><li hidden>hidden</li>';
    const one = await run();
    list.insertAdjacentHTML('beforeend', '<li>another private value</li>');
    const multiple = await run();
    document.body.innerHTML = '<table aria-label="Results"><thead><tr><th>Header</th></tr></thead><tbody><tr><td>private row</td></tr></tbody></table>';
    target.kind = 'Table';
    const table = await run();
    return { empty, one, multiple, table };
  });
  expect(results.empty).toMatchObject({ outcome: 'Completed', verifiedState: 'no_recognized_visible_items' });
  expect(results.one).toMatchObject({ outcome: 'Completed', verifiedState: 'one_recognized_visible_item' });
  expect(results.multiple).toMatchObject({ outcome: 'Completed', verifiedState: 'multiple_recognized_visible_items' });
  expect(results.table).toMatchObject({ outcome: 'Completed', verifiedState: 'one_recognized_visible_item' });
  expect(JSON.stringify(results)).not.toMatch(/private|nested/);
});

test('ambiguous row matching leaves the page unchanged and can be refined', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<ul aria-label="Results"><li>Quarterly report</li><li>Annual report</li></ul>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'results', kind: 'List', label: 'Results' };
    const run = (value) => execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Highlight', parameters: { value } },
    }, 100, true);
    const ambiguous = await run('report');
    const highlightedAfterAmbiguous = document.querySelectorAll('[data-openconnect-liveui-highlight="true"]').length;
    const unique = await run('quarterly report');
    const highlighted = [...document.querySelectorAll('[data-openconnect-liveui-highlight="true"]')].map((element) => element.textContent);
    return { ambiguous, highlightedAfterAmbiguous, unique, highlighted };
  });
  expect(results.ambiguous).toMatchObject({ outcome: 'Ambiguous', code: 'text_match_ambiguous', postconditionVerified: false });
  expect(results.highlightedAfterAmbiguous).toBe(0);
  expect(results.unique).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.highlighted).toEqual(['Quarterly report']);
});
