import { defineConfig } from '@playwright/test';
import base from './playwright.liveui-real-external-fallback.config.js';

const servers = [...base.webServer];
const app = servers.pop();
servers.push({
  command: 'node scripts/liveui-receipt-loss-proxy.js',
  url: 'http://127.0.0.1:5130/__receipt-loss/stats',
  timeout: 30_000,
  reuseExistingServer: false,
});
servers.push({ ...app, env: { ...app.env, GATEWAY_URL: 'http://127.0.0.1:5130/openconnect/v1/' } });

export default defineConfig({ ...base, testDir: './specs-real-lost-receipt',
  outputDir: './test-results-real-lost-receipt', webServer: servers });
