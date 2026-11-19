import { defineConfig } from '@playwright/test';
import realGatewayConfig from './playwright.liveui-real-gateway-worker.config.js';

const servers = [...realGatewayConfig.webServer];
const app = servers[2];
servers[2] = {
  ...app,
  command: 'dotnet run --no-launch-profile --project tests/Fixtures/LiveUITestHost/LiveUITestHost.csproj -c Release',
  env: { ...app.env, OPENCONNECT_LIVEUI_TEST_HOST: '1', OPENCONNECT_LIVEUI_TEST_PORT: '5100' },
  reuseExistingServer: false,
};

export default defineConfig({
  ...realGatewayConfig,
  testMatch: 'todo-create.spec.js',
  outputDir: './test-results-real-authorized',
  webServer: servers,
});
