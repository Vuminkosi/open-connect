import { defineConfig, devices } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const browserDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(browserDirectory, '../..');
const providerEndpoint = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT;
const providerApiKey = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY;
const providerModel = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL;
const explicitLiveProviderRun = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN === '1';

if (!explicitLiveProviderRun) {
  throw new Error('Set OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN=1 to explicitly enable billable live-provider acceptance.');
}

const missingSettings = [
  ['OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT', providerEndpoint],
  ['OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY', providerApiKey],
  ['OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL', providerModel],
].filter(([, value]) => !value).map(([name]) => name);

if (missingSettings.length > 0) {
  throw new Error(`Missing required real-provider settings: ${missingSettings.join(', ')}`);
}

if (!URL.canParse(providerEndpoint)) {
  throw new Error('OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT must be an absolute URL.');
}

const port = Number.parseInt(process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_PORT ?? '5217', 10);
if (!Number.isInteger(port) || port < 1024 || port > 65535) {
  throw new Error('OPENCONNECT_LIVEUI_REAL_PROVIDER_PORT must be a valid non-privileged TCP port.');
}

const appUrl = `http://127.0.0.1:${port}`;
const isolatedDataDirectory = path.join(os.tmpdir(), `openconnect-liveui-real-${process.pid}-${Date.now()}`);
const isolatedOutputDirectory = path.join(isolatedDataDirectory, 'playwright-results');
const tenantId = '01ARZ3NDEKTSV4RRFFQ69G5FAV';
const internalTodoToken = 'liveui-real-provider-acceptance-local-only';

export default defineConfig({
  testDir: './specs-real-provider',
  testMatch: 'liveui-real-provider.spec.js',
  timeout: 180_000,
  expect: { timeout: 35_000 },
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  workers: 1,
  reporter: [['list']],
  outputDir: isolatedOutputDirectory,
  use: {
    baseURL: appUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    ...devices['Desktop Chrome'],
  },
  webServer: {
    command: 'dotnet run --no-launch-profile --project samples/Blazor/OpenConnect.Samples.Blazor.csproj -c Release --no-build',
    cwd: repositoryRoot,
    url: appUrl,
    timeout: 120_000,
    reuseExistingServer: false,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: appUrl,
      OpenConnect__Gateway__Mode: 'SameHostGateway',
      OpenConnect__Gateway__SameHostBaseUrl: appUrl,
      OpenConnect__Gateway__TenantId: tenantId,
      OpenConnect__ApiUrl: providerEndpoint,
      OpenConnect__ApiKey: providerApiKey,
      OpenConnect__ReasoningModel: providerModel,
      OpenConnect__LiveUI__SameHostGatewayPlanner: process.env.OPENCONNECT_LIVEUI_REAL_GATEWAY_PLANNER_RUN === '1' ? 'true' : 'false',
      OPENCONNECT_BLAZOR_ACCEPTANCE_DATA: 'true',
      OPENCONNECT_BLAZOR_SQLITE_PATH: path.join(isolatedDataDirectory, 'acceptance.db'),
      OPENCONNECT_BLAZOR_DATA_PROTECTION_KEYS_PATH: path.join(isolatedDataDirectory, 'data-protection-keys'),
      TODO_SERVICE_URL: appUrl,
      TODO_INTERNAL_TOKEN: internalTodoToken,
      TODO_INTERNAL_TENANT: tenantId,
    },
  },
});
