import { defineConfig } from '@playwright/test';
import base from './playwright.liveui-real-provider.config.js';

if (process.env.OPENCONNECT_LIVEUI_REAL_GATEWAY_PLANNER_RUN !== '1') {
  throw new Error('Set OPENCONNECT_LIVEUI_REAL_GATEWAY_PLANNER_RUN=1 to enable real gateway fallback.');
}

export default defineConfig({
  ...base,
  testDir: './specs-real-fallback',
  testMatch: 'gateway-fallback.spec.js',
  timeout: 300_000,
  webServer: [
    {
      command: 'node scripts/liveui-unavailable-worker.js',
      url: 'http://127.0.0.1:5311/__stats',
      timeout: 30_000,
      reuseExistingServer: false,
    },
    {
      ...base.webServer,
      env: {
        ...base.webServer.env,
        OpenConnect__LiveUI__Inference__Worker__Endpoint: 'http://127.0.0.1:5311/',
        OpenConnect__LiveUI__Inference__Worker__Token: 'liveui-fallback-acceptance-token',
      },
    },
  ],
});
