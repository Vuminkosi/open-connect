import { defineConfig } from '@playwright/test';
import connectedConfig from './playwright.liveui-connected.config.js';

const token = process.env.OPENCONNECT_LIVEUI_WORKER_TOKEN;
const container = process.env.OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER;
if (!token || !container || !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(container)) {
  throw new Error('Configure an isolated worker bridge token and container name.');
}
const [fixture, app] = connectedConfig.webServer;
export default defineConfig({
  ...connectedConfig,
  testDir: './specs-worker-samehost',
  outputDir: './test-results-worker-samehost',
  webServer: [
    { ...fixture, reuseExistingServer: false, env: { OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER: container, OPENCONNECT_LIVEUI_WORKER_TOKEN: token } },
    { ...app, reuseExistingServer: false, env: { ...app.env,
      OpenConnect__LiveUI__Inference__Worker__Endpoint: 'http://127.0.0.1:5110/__acceptance/liveui-worker/',
      OpenConnect__LiveUI__Inference__Worker__Token: token } },
  ],
});
