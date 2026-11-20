import { spawn } from 'node:child_process';
import { randomInt, randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { classifyScenario, workerEndpointForFixture } from './liveui-matrix-result.mjs';

const browserRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(browserRoot, '../..');
const cli = path.join(browserRoot, 'node_modules', '@playwright', 'test', 'cli.js');
const implemented = [
  { id: 'T01', spec: 'todo-create.spec.js', authorized: true, observedChange: true },
  { id: 'T02', spec: 'todo-multiple-create.spec.js', authorized: true },
  { id: 'T03', spec: 'todo-completion.spec.js', authorized: true },
  { id: 'T05', spec: 'todo-followup-delete.spec.js', authorized: true },
  { id: 'T06', spec: 'todo-acceptance.spec.js', grep: 'incomplete tasks' },
  { id: 'T09', spec: 'todo-ambiguity.spec.js', authorized: true },
  { id: 'T10', spec: 'todo-bulk-scope.spec.js', authorized: true },
  { id: 'T12', spec: 'todo-safety-cases.spec.js', grep: 'T12:' },
  { id: 'T14', spec: 'todo-safety-cases.spec.js', grep: 'T14:' },
  { id: 'T17', spec: 'todo-page-five.spec.js', authorized: true },
  { id: 'T19', spec: 'todo-modal-create.spec.js', authorized: true },
  { id: 'T20', spec: 'todo-validation-recovery.spec.js', authorized: true },
  { id: 'T23', spec: 'todo-hidden-filter.spec.js' },
  { id: 'T25', spec: 'todo-double-submit.spec.js', authorized: true },
  { id: 'T27', spec: 'todo-concurrent-edit.spec.js', authorized: true },
  { id: 'T30', spec: 'todo-delete-confirmation.spec.js', authorized: true },
  { id: 'T31', spec: 'todo-safety-cases.spec.js', grep: 'T31:' },
  { id: 'T33', spec: 'todo-browser-inference-unavailable.spec.js' },
  { id: 'T36', spec: 'todo-safety-cases.spec.js', grep: 'T36:' },
  { id: 'D01', spec: 'installation.spec.js', docs: true, grep: 'D01:' },
  { id: 'D02', spec: 'installation.spec.js', docs: true, grep: 'D02:' },
  { id: 'D03', spec: 'installation.spec.js', docs: true, grep: 'D03:' },
];
const allIds = ['T', 'D'].flatMap((prefix) => Array.from({ length: 40 }, (_, index) => `${prefix}${String(index + 1).padStart(2, '0')}`));
if (new Set(implemented.map((item) => item.id)).size !== implemented.length ||
    implemented.some((item) => !allIds.includes(item.id)))
  throw new Error('The acceptance matrix contains an invalid or duplicate case ID.');

if (process.argv.slice(2).includes('--inventory')) {
  console.log(JSON.stringify({ total: allIds.length, executable: implemented.map((item) => item.id),
    notImplemented: allIds.filter((id) => !implemented.some((item) => item.id === id)) }, null, 2));
  process.exit(0);
}

const required = ['OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT', 'OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY',
  'OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL', 'OPENCONNECT_LIVEUI_WORKER_ENDPOINT', 'OPENCONNECT_LIVEUI_WORKER_TOKEN',
  'OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER'];
const missing = required.filter((name) => !process.env[name]);
if (process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN !== '1' || process.env.OPENCONNECT_LIVEUI_REAL_WORKER_RUN !== '1' || missing.length)
  throw new Error(`Real provider and worker opt-in and settings are required. Missing: ${missing.join(', ') || 'explicit opt-in'}.`);
const concurrencyText = process.env.OPENCONNECT_ACCEPTANCE_CONCURRENCY ?? String(implemented.length);
const concurrency = Number(concurrencyText);
if (!/^\d+$/.test(concurrencyText) || !Number.isInteger(concurrency) || concurrency < 1 || concurrency > implemented.length)
  throw new Error(`OPENCONNECT_ACCEPTANCE_CONCURRENCY must be between 1 and ${implemented.length}.`);
const basePortText = process.env.OPENCONNECT_ACCEPTANCE_BASE_PORT ?? String(randomInt(30000, 55000));
const basePort = Number(basePortText);
if (!/^\d+$/.test(basePortText) || !Number.isInteger(basePort) || basePort < 1024 || basePort + implemented.length * 3 > 65535)
  throw new Error('OPENCONNECT_ACCEPTANCE_BASE_PORT must leave three valid ports per executable case.');

const root = path.join(browserRoot, 'test-results-liveui-matrix', randomUUID());
await fs.mkdir(root, { recursive: true });
const results = Object.fromEntries(allIds.map((id) => [id, { status: 'NOT_IMPLEMENTED' }]));
for (const scenario of implemented) results[scenario.id] = { status: 'PENDING' };
const reportPath = path.join(root, 'summary.json');
let next = 0;
let stopping = false;
const children = new Set();
let reportWrite = Promise.resolve();
function save() {
  const payload = JSON.stringify({ total: allIds.length, executable: implemented.length, results }, null, 2);
  reportWrite = reportWrite.then(() => fs.writeFile(reportPath, payload, 'utf8'));
  return reportWrite;
}
process.once('SIGINT', () => {
  stopping = true;
  for (const child of children) child.kill();
});
await save();
console.log(`Acceptance results: ${root}`);

// Build once, serially, before parallel cases use --no-build. This ties the run to
// the current sources and avoids shared MSBuild output contention between cases.
for (const project of [
  'samples/Blazor/OpenConnect.Samples.Blazor.csproj',
  'samples/workers/shared/OpenConnect.Samples.Workers.Host/OpenConnect.Samples.Workers.Host.csproj',
  'tests/Fixtures/LiveUITestHost/LiveUITestHost.csproj',
  'site/OpenConnect.Docs/OpenConnect.Docs.csproj',
]) {
  const build = spawn('dotnet', ['build', project, '-c', 'Release', '--no-restore', '-m:1', '-nr:false'],
    { cwd: repositoryRoot, stdio: 'ignore', windowsHide: true });
  children.add(build);
  const code = await new Promise((resolve) => {
    build.once('error', () => resolve(null));
    build.once('close', resolve);
  });
  children.delete(build);
  if (stopping || code !== 0) {
    for (const scenario of implemented)
      results[scenario.id] = { status: stopping ? 'INTERRUPTED' : 'NOT_RUN', reason: 'fixture_build_failed' };
    await save();
    console.error(`Acceptance fixture build did not complete: ${project}`);
    process.exit(1);
  }
}

async function worker() {
  while (!stopping) {
    const index = next++;
    if (index >= implemented.length) return;
    const scenario = implemented[index];
    const port = basePort + index * 3;
    const output = path.join(root, scenario.id);
    const config = scenario.docs ? 'playwright.liveui-real-docs.config.js' :
      scenario.authorized ? 'playwright.liveui-real-authorized.config.js' : 'playwright.liveui-real-gateway-worker.config.js';
    const args = [cli, 'test', `--config=${config}`];
    if (!scenario.authorized) args.push(`${scenario.docs ? 'specs-real-docs' : 'specs-real-gateway-worker'}/${scenario.spec}`);
    if (scenario.grep) args.push('--grep', scenario.grep);
    args.push(`--output=${output}`, '--reporter=json');
    const env = { ...process.env,
      OPENCONNECT_ACCEPTANCE_APP_PORT: String(port), OPENCONNECT_ACCEPTANCE_FIXTURE_PORT: String(port + 1),
      OPENCONNECT_ACCEPTANCE_GATEWAY_PORT: String(port + 2), OPENCONNECT_ACCEPTANCE_NO_BUILD: '1',
      OPENCONNECT_LIVEUI_WORKER_ENDPOINT: workerEndpointForFixture(process.env.OPENCONNECT_LIVEUI_WORKER_ENDPOINT, port + 1),
      OPENCONNECT_ACCEPTANCE_AUTHORIZED_SPEC: scenario.authorized ? scenario.spec : '',
      OPENCONNECT_LIVEUI_REQUIRE_OBSERVED_CHANGE: scenario.observedChange ? '1' : '0' };
    const started = Date.now();
    results[scenario.id] = { status: 'RUNNING', authorizer: scenario.authorized ? 'test-only' : 'sample' };
    await save();
    const child = spawn(process.execPath, args, { cwd: browserRoot, env, stdio: ['ignore', 'pipe', 'ignore'], windowsHide: true });
    const reportChunks = [];
    let reportBytes = 0;
    child.stdout.on('data', (chunk) => {
      reportBytes += chunk.length;
      if (reportBytes <= 8 * 1024 * 1024) reportChunks.push(chunk);
    });
    children.add(child);
    const code = await new Promise((resolve) => {
      child.once('error', () => resolve(null));
      child.once('close', (exitCode) => resolve(exitCode));
    });
    children.delete(child);
    let report = null;
    if (reportBytes <= 8 * 1024 * 1024) {
      try { report = JSON.parse(Buffer.concat(reportChunks).toString('utf8')); }
      catch (error) { if (!(error instanceof SyntaxError)) throw error; }
    }
    const status = classifyScenario(code, report, stopping);
    results[scenario.id] = { status,
      authorizer: scenario.authorized ? 'test-only' : 'sample', durationSeconds: Math.round((Date.now() - started) / 1000),
      resultDirectory: scenario.id };
    await save();
    console.log(`${scenario.id}: ${results[scenario.id].status}`);
  }
}

await Promise.all(Array.from({ length: concurrency }, () => worker()));
await reportWrite;
const counts = Object.groupBy(Object.values(results), (item) => item.status);
console.log(`Results: ${Object.entries(counts).map(([status, entries]) => `${status}=${entries.length}`).join(', ')}`);
if (Object.values(results).some((item) => item.status !== 'PASS')) process.exitCode = 1;
