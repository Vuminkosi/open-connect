import { defineConfig } from '@playwright/test';
import base from './playwright.liveui-real-gateway-worker.config.js';

const servers = [...base.webServer];
const app = servers[2];
const noBuild = process.env.OPENCONNECT_ACCEPTANCE_NO_BUILD === '1' ? ' --no-build' : '';
servers[2] = {
  ...app,
  command: `dotnet run --no-launch-profile --project site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release${noBuild}`,
  env: { ...app.env, OpenConnect__Assistant__Enabled: 'true',
    OpenConnect__Assistant__GatewayUrl: `${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/` },
  reuseExistingServer: false,
};

export default defineConfig({ ...base, testDir: './specs-real-docs', webServer: servers });
