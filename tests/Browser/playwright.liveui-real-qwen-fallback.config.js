import { defineConfig } from '@playwright/test';
import base from './playwright.liveui-real-external-fallback.config.js';

const workerContainer = process.env.OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER;
const token = process.env.OPENCONNECT_LIVEUI_WORKER_TOKEN;
if (process.env.OPENCONNECT_LIVEUI_TEST_INVALID_PLAN !== '1' || !token ||
    !workerContainer || !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(workerContainer)) {
  throw new Error('Explicit OnFailure acceptance and a private worker container are required.');
}
const [fixture, , gateway, app] = base.webServer;
export default defineConfig({
  ...base,
  testDir: './specs-real-qwen-fallback',
  outputDir: './test-results-real-qwen-fallback',
  webServer: [
    { ...fixture, env: { ...fixture.env, OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER: workerContainer,
      OPENCONNECT_LIVEUI_WORKER_TOKEN: token } },
    { ...gateway, env: { ...gateway.env,
      OpenConnect__LiveUI__Inference__Worker__Endpoint: 'http://127.0.0.1:5110/__acceptance/liveui-worker/',
      OpenConnect__LiveUI__Inference__Worker__Token: token } },
    app,
  ],
});
