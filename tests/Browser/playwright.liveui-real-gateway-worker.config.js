import { defineConfig } from '@playwright/test';
import workerConfig from './playwright.liveui-real-worker.config.js';

const endpoint = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT;
const apiKey = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY;
const model = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL;
if (process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN !== '1' || !endpoint || !apiKey || !model) {
  throw new Error('Explicit real-provider acceptance configuration is required.');
}

const servers = [...workerConfig.webServer];
servers[1] = {
  ...servers[1],
  env: {
    ...servers[1].env,
    OpenConnect__ApiUrl: endpoint,
    OpenConnect__ApiKey: apiKey,
    OpenConnect__ReasoningModel: model,
  },
};

export default defineConfig({
  ...workerConfig,
  testDir: './specs-real-gateway-worker',
  outputDir: './test-results-real-gateway-worker',
  timeout: 300_000,
  webServer: servers,
});
