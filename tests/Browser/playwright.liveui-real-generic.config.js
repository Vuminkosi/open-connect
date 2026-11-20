import { defineConfig } from '@playwright/test';
import base from './playwright.liveui-real-authorized.config.js';

const servers = [...base.webServer];
const gateway = servers[1];
servers[1] = {
  ...gateway,
  command: `dotnet run --no-launch-profile --project tests/Fixtures/LiveUIGenericGateway/LiveUIGenericGateway.csproj -c Release${process.env.OPENCONNECT_ACCEPTANCE_NO_BUILD === '1' ? ' --no-build' : ''}`,
  env: { ...gateway.env, LIVEUI_GENERIC_FIXTURE: '1', LIVEUI_GENERIC_BLOCK_MODEL: process.env.LIVEUI_GENERIC_BLOCK_MODEL ?? '0' },
  reuseExistingServer: false,
};

export default defineConfig({ ...base, webServer: servers });
