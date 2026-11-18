import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('infers only unique nearby checkbox text and restricts resolution to confirmed runtime targets', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <div><label><input type="checkbox"></label><div><p>Review report</p><span>Details</span></div></div>
      <div><input type="checkbox"><p>First</p><p>Second</p></div>
      <div><input type="checkbox"><input type="checkbox"><p>Multiple controls</p></div>
      <div><input type="checkbox"><p>Duplicate</p></div><div><input type="checkbox"><p>Duplicate</p></div>` }));
  await page.goto('http://liveui.test/');
  const result = await page.evaluate(async () => {
    const { discoverTargets, execute } = await import('/liveui.js');
    const targets = discoverTargets(50);
    const observed = targets.find((target) => target.kind === 'Checkbox' && target.label === 'Review report');
    const inspect = (id, requiresConfirmation) => {
      const target = { ...observed, id, requiresConfirmation };
      return execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
        action: { schemaVersion: 'openconnect.liveui/v1', type: 'Inspect', parameters: {} },
      }, 100, true);
    };
    return { targets, runtime: await inspect('oc.runtime.example', true),
      unconfirmed: await inspect('oc.runtime.example', false), compiled: await inspect('compiled.example', false) };
  });
  expect(result.targets.filter((target) => target.kind === 'Checkbox')).toEqual([
    expect.objectContaining({ label: 'Review report' }),
  ]);
  expect(result.runtime).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(result.unconfirmed).toMatchObject({ outcome: 'MissingTarget' });
  expect(result.compiled).toMatchObject({ outcome: 'MissingTarget' });
});

test('discovers unique visible semantic controls without exposing input values or code examples', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <h1>Tasks</h1><label>Task title <input value="private task value"></label>
      <button>Add task</button><button>Duplicate</button><button>Duplicate</button>
      <button hidden>Hidden</button><pre><button>Fake action</button></pre>
      <input type="radio" aria-label="Unsupported radio">
      <input type="password" aria-label="Password" value="secret">
      <a href="/docs">Documentation</a><a href="/install"><strong>Installation</strong><span>A longer explanatory summary</span></a>
      <a href="/docs?key=value">Query link</a><a href="https://elsewhere.example/">External</a>` }));
  await page.goto('http://liveui.test/');
  const result = await page.evaluate(async () => (await import('/liveui.js')).discoverTargets(100));
  expect(result).toEqual(expect.arrayContaining([
    expect.objectContaining({ kind: 'Heading', label: 'Tasks' }),
    expect.objectContaining({ kind: 'Input', label: 'Task title' }),
    expect.objectContaining({ kind: 'Button', label: 'Add task' }),
    expect.objectContaining({ kind: 'Link', label: 'Documentation', path: '/docs' }),
    expect.objectContaining({ kind: 'Link', label: 'Installation', path: '/install' }),
  ]));
  const serialized = JSON.stringify(result);
  for (const forbidden of ['private task value', 'secret', 'Password', 'Duplicate', 'Hidden', 'Fake action', 'External', 'Unsupported radio'])
    expect(serialized).not.toContain(forbidden);
  expect(result.find((item) => item.label === 'Query link')).not.toHaveProperty('path');
});

test('discovers duplicate headings in distinct named sections without guessing between them', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <section aria-label="Desktop setup"><h2>Configuration</h2></section>
      <section aria-label="Mobile setup"><h2>Configuration</h2></section>` }));
  await page.goto('http://liveui.test/');
  const { result, highlighted } = await page.evaluate(async () => {
    const { discoverTargets, execute } = await import('/liveui.js');
    const result = discoverTargets(20);
    const targets = result.map((item, index) => ({ ...item, id: `runtime-${index}` }));
    for (const target of targets)
      if (target.parentLabel) target.parentId = targets.find((parent) => parent.kind === 'Region' && parent.label === target.parentLabel)?.id;
    const target = targets.find((item) => item.kind === 'Heading' && item.parentLabel === 'Mobile setup');
    const receipt = await execute({ manifest: {}, route: { path: '/', targets }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Highlight', parameters: {} },
    }, 1000, true);
    return { result, highlighted: { receipt, sections: [...document.querySelectorAll('section')].map((section) =>
      section.querySelector('h2').hasAttribute('data-openconnect-liveui-highlight')) } };
  });
  expect(result).toEqual(expect.arrayContaining([
    expect.objectContaining({ kind: 'Region', label: 'Desktop setup', role: 'region' }),
    expect.objectContaining({ kind: 'Region', label: 'Mobile setup', role: 'region' }),
    expect.objectContaining({ kind: 'Heading', label: 'Configuration', parentLabel: 'Desktop setup' }),
    expect.objectContaining({ kind: 'Heading', label: 'Configuration', parentLabel: 'Mobile setup' }),
  ]));
  expect(highlighted.receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(highlighted.sections).toEqual([false, true]);
});

test('uses a section heading as context when the section has no annotations', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <section><h2>Desktop setup<span hidden> hidden secret</span></h2><h3>Configuration</h3></section>
      <section><h2>Mobile setup</h2><h3>Configuration</h3></section>` }));
  await page.goto('http://liveui.test/');
  const result = await page.evaluate(async () => {
    const { discoverTargets, execute } = await import('/liveui.js');
    const targets = discoverTargets(20).map((item, index) => ({ ...item, id: `runtime-${index}` }));
    for (const target of targets)
      if (target.parentLabel) target.parentId = targets.find((parent) => parent.kind === 'Region' && parent.label === target.parentLabel)?.id;
    const selected = targets.find((target) => target.label === 'Configuration' && target.parentLabel === 'Mobile setup');
    const receipt = await execute({ manifest: {}, route: { path: '/', targets }, target: selected,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Highlight', parameters: {} } }, 1000, true);
    return { targets, receipt, highlighted: [...document.querySelectorAll('h3')].map((heading) =>
      heading.hasAttribute('data-openconnect-liveui-highlight')) };
  });
  expect(result.targets).toEqual(expect.arrayContaining([
    expect.objectContaining({ kind: 'Region', label: 'Desktop setup' }),
    expect.objectContaining({ kind: 'Region', label: 'Mobile setup' }),
    expect.objectContaining({ kind: 'Heading', label: 'Configuration', parentLabel: 'Mobile setup' }),
  ]));
  expect(result.receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(result.highlighted).toEqual([false, true]);
  expect(JSON.stringify(result.targets)).not.toContain('hidden secret');
});
