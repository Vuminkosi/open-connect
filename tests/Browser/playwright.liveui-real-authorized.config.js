import { defineConfig } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import realGatewayConfig from './playwright.liveui-real-gateway-worker.config.js';

const runId = process.env.OPENCONNECT_LIVEUI_TEST_RUN_ID ?? randomUUID();
if (!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(runId))
  throw new Error('OPENCONNECT_LIVEUI_TEST_RUN_ID must be a version 4 UUID.');
process.env.OPENCONNECT_LIVEUI_TEST_RUN_ID = runId;
const testMatch = process.env.OPENCONNECT_ACCEPTANCE_AUTHORIZED_SPEC ?? 'todo-create.spec.js';
if (!/^[a-z0-9-]+\.spec\.js$/.test(testMatch))
  throw new Error('OPENCONNECT_ACCEPTANCE_AUTHORIZED_SPEC must be a spec filename.');
const servers = [...realGatewayConfig.webServer];
const app = servers[2];
servers[2] = {
  ...app,
  command: `dotnet run --no-launch-profile --project tests/Fixtures/LiveUITestHost/LiveUITestHost.csproj -c Release${process.env.OPENCONNECT_ACCEPTANCE_NO_BUILD === '1' ? ' --no-build' : ''}`,
  env: { ...app.env, OPENCONNECT_LIVEUI_TEST_HOST: '1', OPENCONNECT_LIVEUI_TEST_PORT: new URL(realGatewayConfig.use.baseURL).port },
  reuseExistingServer: false,
};

export default defineConfig({
  ...realGatewayConfig,
  testMatch,
  outputDir: `./test-results-real-authorized/${runId}`,
  use: { ...realGatewayConfig.use, trace: 'off', video: 'off' },
  webServer: servers,
});
