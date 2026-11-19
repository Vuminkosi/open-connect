import { expect, test } from '@playwright/test';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const appDirectory = path.join(root, 'samples', 'Blazor');
const assembly = path.join(appDirectory, 'bin', 'Release', 'net10.0', 'OpenConnect.Samples.Blazor.dll');

test('Todo created in the UI survives an application process restart', async ({ page, request }) => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'openconnect-todo-persistence-'));
  const portServer = net.createServer();
  await new Promise((resolve, reject) => portServer.once('error', reject).listen(0, '127.0.0.1', resolve));
  const port = portServer.address().port;
  await new Promise((resolve, reject) => portServer.close((error) => error ? reject(error) : resolve()));
  const origin = `http://127.0.0.1:${port}`;
  let process;
  const start = async () => {
    process = spawn('dotnet', [assembly], { cwd: appDirectory, stdio: 'ignore', env: {
      ...globalThis.process.env,
      ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: origin,
      OPENCONNECT_BLAZOR_SQLITE_PATH: path.join(directory, 'todo.db'),
      OPENCONNECT_BLAZOR_DATA_PROTECTION_KEYS_PATH: path.join(directory, 'keys'),
      OPENCONNECT_BLAZOR_ACCEPTANCE_DATA: 'true',
      OpenConnect__Gateway__Mode: 'SameHostGateway', OpenConnect__Gateway__SameHostBaseUrl: origin,
      OpenConnect__ApiUrl: 'http://127.0.0.1:9/v1', OpenConnect__ApiKey: 'unused-test-key',
      OpenConnect__ReasoningModel: 'unused-test-model',
      TODO_SERVICE_URL: origin, TODO_INTERNAL_TOKEN: 'local-persistence-test-token',
      TODO_INTERNAL_TENANT: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
    } });
    await expect.poll(async () => {
      if (process.exitCode !== null) throw new Error(`Sample exited with code ${process.exitCode}`);
      try { return (await request.get(origin, { timeout: 1000 })).status(); } catch { return 0; }
    }, { timeout: 40_000, intervals: [500] }).toBe(200);
  };
  const stop = async () => {
    if (process && process.exitCode === null) {
      const exited = once(process, 'exit');
      process.kill();
      await exited;
    }
    process = undefined;
  };
  try {
    await start();
    await page.goto(origin);
    const title = page.getByRole('textbox', { name: 'Task Title' });
    await expect(async () => {
      await title.fill('Persistent insurance renewal');
      await title.blur();
      await expect(page.getByRole('button', { name: 'Add Task' })).toBeEnabled({ timeout: 1000 });
    }).toPass({ timeout: 15_000, intervals: [250] });
    await page.getByRole('button', { name: 'Add Task' }).click();
    await expect(page.getByText('Persistent insurance renewal', { exact: true })).toHaveCount(1);

    // Exercise the SDK's browser runtime separately from the agent and host authorization.
    const runtimeTitle = 'Runtime insurance renewal';
    const valueReceipt = await page.evaluate(async (value) => {
      const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
      const found = runtime.discoverTargets(500).filter((item) => item.kind === 'Input' && item.label === 'Task Title');
      if (found.length !== 1) throw new Error('Task title input was not uniquely discovered');
      const target = { ...found[0], id: 'test-runtime-title' };
      return runtime.execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
        action: { schemaVersion: 'openconnect.liveui/v1', type: 'SetValue', parameters: { value } },
      }, 5000, true);
    }, runtimeTitle);
    expect(valueReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
    await expect(page.getByRole('button', { name: 'Add Task' })).toBeEnabled();
    const clickReceipt = await page.evaluate(async () => {
      const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
      const found = runtime.discoverTargets(500).filter((item) => item.kind === 'Button' && /^add task$/i.test(item.label));
      if (found.length !== 1) throw new Error('Add Task button was not uniquely discovered');
      const target = { ...found[0], id: 'test-runtime-add' };
      return runtime.execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
        action: { schemaVersion: 'openconnect.liveui/v1', type: 'Click', parameters: {} },
      }, 5000, true);
    });
    expect(clickReceipt).toMatchObject({ outcome: 'Uncertain', code: 'click_dispatched_without_postcondition', postconditionVerified: false });
    await expect(page.getByText(runtimeTitle, { exact: true })).toHaveCount(1);
    await stop();
    await start();
    await page.goto(origin);
    await expect(page.getByText('Persistent insurance renewal', { exact: true })).toHaveCount(1);
    await expect(page.getByText(runtimeTitle, { exact: true })).toHaveCount(1);
  } finally {
    await stop();
    const resolvedDirectory = await fs.realpath(directory);
    const resolvedTemp = await fs.realpath(os.tmpdir());
    if (path.dirname(resolvedDirectory).toLowerCase() !== resolvedTemp.toLowerCase() ||
        !path.basename(resolvedDirectory).startsWith('openconnect-todo-persistence-'))
      throw new Error('Temporary test directory is outside the expected location.');
    await fs.rm(resolvedDirectory, { recursive: true, force: true });
  }
});
