import { defineConfig, devices } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const browserDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(browserDirectory, '../..');
const acceptanceDirectory = path.join(os.tmpdir(), `openconnect-liveui-${process.pid}`);
function acceptancePort(name, fallback) {
  const value = process.env[name] ?? String(fallback);
  if (!/^\d+$/.test(value) || Number(value) < 1024 || Number(value) > 65535)
    throw new Error(`${name} must be a valid non-privileged port.`);
  return Number(value);
}

const fixtureUrl = `http://127.0.0.1:${acceptancePort('OPENCONNECT_ACCEPTANCE_FIXTURE_PORT', 5110)}`;
const appUrl = `http://127.0.0.1:${acceptancePort('OPENCONNECT_ACCEPTANCE_APP_PORT', 5100)}`;
const noBuild = process.env.OPENCONNECT_ACCEPTANCE_NO_BUILD === '1' ? ' --no-build' : '';

export default defineConfig({
  testDir: './specs-connected',
  timeout: 120_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  outputDir: './test-results-connected',
  use: {
    baseURL: appUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    permissions: ['microphone'],
    ...devices['Desktop Chrome'],
    launchOptions: {
      args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'],
    },
  },
  webServer: [
    {
      command: 'node scripts/liveui-connected-fixture.js',
      cwd: browserDirectory,
      url: `${fixtureUrl}/health`,
      timeout: 30_000,
      reuseExistingServer: !process.env.CI,
      env: { OPENCONNECT_LIVEUI_FIXTURE_PORT: new URL(fixtureUrl).port },
    },
    {
      command: `dotnet run --no-launch-profile --project samples/Blazor/OpenConnect.Samples.Blazor.csproj -c Release${noBuild}`,
      cwd: repositoryRoot,
      url: appUrl,
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: appUrl,
        OPENCONNECT_BLAZOR_SQLITE_PATH: path.join(acceptanceDirectory, 'todo.db'),
        OPENCONNECT_BLAZOR_ACCEPTANCE_DATA: 'true',
        OpenConnect__Gateway__Mode: 'SameHostGateway',
        OpenConnect__Gateway__SameHostBaseUrl: appUrl,
        OpenConnect__ApiUrl: `${fixtureUrl}/v1`,
        OpenConnect__ApiKey: 'local-acceptance-fixture-key',
        OpenConnect__ReasoningModel: 'openconnect-liveui-fixture',
        Workers__Kokoro__Endpoint: fixtureUrl,
        OPENCONNECT_WORKER_TOKEN: 'local-acceptance-kokoro-token',
        TODO_SERVICE_URL: appUrl,
        TODO_INTERNAL_TOKEN: 'local-acceptance-todo-internal-token',
        TODO_INTERNAL_TENANT: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
      },
    },
  ],
});
