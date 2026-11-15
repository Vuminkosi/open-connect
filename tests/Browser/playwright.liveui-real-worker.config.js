import { defineConfig } from '@playwright/test';
import connectedConfig from './playwright.liveui-connected.config.js';

const endpointText = process.env.OPENCONNECT_LIVEUI_WORKER_ENDPOINT;
const bearerToken = process.env.OPENCONNECT_LIVEUI_WORKER_TOKEN;
if (process.env.OPENCONNECT_LIVEUI_REAL_WORKER_RUN !== '1') {
  throw new Error('Set OPENCONNECT_LIVEUI_REAL_WORKER_RUN=1 to enable local worker inference acceptance.');
}
if (!endpointText || !bearerToken) {
  throw new Error('Set the private worker endpoint and token in the test environment.');
}

const endpoint = new URL(endpointText);
if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname) ||
    endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
  throw new Error('Real worker browser acceptance accepts only a credential-free loopback HTTP endpoint.');
}

const [fixtureServer, appServer] = connectedConfig.webServer;
const appEnvironment = {
  ...appServer.env,
  OpenConnect__LiveUI__Inference__Worker__Endpoint: endpointText,
  OpenConnect__LiveUI__Inference__Worker__Token: bearerToken,
};

export default defineConfig({
  ...connectedConfig,
  testDir: './specs-real-worker',
  outputDir: './test-results-real-worker',
  webServer: [
    { ...fixtureServer, reuseExistingServer: false },
    { ...appServer, env: appEnvironment, reuseExistingServer: false },
  ],
});
