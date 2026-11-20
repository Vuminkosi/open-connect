import assert from 'node:assert/strict';
import { test } from 'node:test';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { classifyScenario, workerEndpointForFixture } from './liveui-matrix-result.mjs';

test('only a single executed passing scenario is a pass', () => {
  const stats = { expected: 1, unexpected: 0, skipped: 0, flaky: 0 };
  assert.equal(classifyScenario(0, { stats }), 'PASS');
  assert.equal(classifyScenario(0, { stats: { ...stats, expected: 0, skipped: 1 } }), 'NOT_RUN');
  assert.equal(classifyScenario(0, { stats: { ...stats, expected: 2 } }), 'NOT_RUN');
  assert.equal(classifyScenario(1, { stats }), 'NOT_RUN');
  assert.equal(classifyScenario(1, { stats: { ...stats, expected: 0, unexpected: 1 } }), 'FAIL');
  assert.equal(classifyScenario(0, { stats: { ...stats, expected: 0, flaky: 1 } }), 'FAIL');
  assert.equal(classifyScenario(0, null), 'NOT_RUN');
  assert.equal(classifyScenario(0, { stats }, true), 'INTERRUPTED');
});

test('isolates fixture bridge ports without changing direct worker endpoints', () => {
  assert.equal(workerEndpointForFixture('http://127.0.0.1:5110/__acceptance/liveui-worker/', 32001),
    'http://127.0.0.1:32001/__acceptance/liveui-worker/');
  assert.equal(workerEndpointForFixture('http://127.0.0.1:7000/', 32001), 'http://127.0.0.1:7000/');
});

test('classifies actual Playwright pass, skip and failure reports', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'openconnect-matrix-result-'));
  const browserRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const playwright = pathToFileURL(path.join(browserRoot, 'node_modules', '@playwright', 'test', 'index.mjs')).href;
  const cli = path.join(browserRoot, 'node_modules', '@playwright', 'test', 'cli.js');
  try {
    await fs.writeFile(path.join(root, 'sample.spec.mjs'), `import { test } from '${playwright}';\ntest('pass case', () => {});\ntest.skip('skipped case', () => {});\ntest('failed case', () => { throw new Error('synthetic'); });\n`);
    await fs.writeFile(path.join(root, 'playwright.config.mjs'), `export default { testDir: ${JSON.stringify(root)}, reporter: 'json' };\n`);
    for (const [name, expected] of [['pass case', 'PASS'], ['skipped case', 'NOT_RUN']]) {
      const { stdout } = await promisify(execFile)(process.execPath,
        [cli, 'test', '--config', path.join(root, 'playwright.config.mjs'), '--grep', name], { cwd: browserRoot });
      assert.equal(classifyScenario(0, JSON.parse(stdout)), expected);
    }
    await assert.rejects(promisify(execFile)(process.execPath,
      [cli, 'test', '--config', path.join(root, 'playwright.config.mjs'), '--grep', 'failed case'], { cwd: browserRoot }),
    (error) => classifyScenario(error.code, JSON.parse(error.stdout)) === 'FAIL');
  } finally {
    if (path.dirname(root) === path.resolve(os.tmpdir()) && path.basename(root).startsWith('openconnect-matrix-result-'))
      await fs.rm(root, { recursive: true, force: true });
  }
});
