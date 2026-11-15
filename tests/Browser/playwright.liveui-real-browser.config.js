import { defineConfig } from '@playwright/test';
import connectedConfig from './playwright.liveui-connected.config.js';

const browserChannel = process.env.LIVEUI_BROWSER_CHANNEL ?? 'msedge';
if (!['chrome', 'chrome-beta', 'chrome-dev', 'chrome-canary', 'msedge', 'msedge-beta', 'msedge-dev', 'msedge-canary'].includes(browserChannel)) {
  throw new Error('LIVEUI_BROWSER_CHANNEL must name an installed Chrome or Edge channel.');
}

export default defineConfig({
  ...connectedConfig,
  testDir: './specs-real',
  outputDir: './test-results-real-browser',
  use: {
    ...connectedConfig.use,
    channel: browserChannel,
  },
});
