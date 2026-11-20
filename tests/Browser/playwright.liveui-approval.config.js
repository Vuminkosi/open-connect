import { defineConfig } from '@playwright/test';
import connected from './playwright.liveui-connected.config.js';

const servers = [...connected.webServer];
const app = servers[1];
const port = new URL(connected.use.baseURL).port;
servers[1] = {
  ...app,
  command: 'dotnet run --no-launch-profile --project tests/Fixtures/LiveUITestHost/LiveUITestHost.csproj -c Release',
  env: { ...app.env, OPENCONNECT_LIVEUI_TEST_HOST: '1', OPENCONNECT_LIVEUI_TEST_PORT: port },
  reuseExistingServer: false,
};

export default defineConfig({ ...connected, testDir: './specs-approval', webServer: servers });
