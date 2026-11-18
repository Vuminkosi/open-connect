import { defineConfig } from '@playwright/test';
import connectedConfig from './playwright.liveui-connected.config.js';

export default defineConfig({
  ...connectedConfig,
  testDir: './specs-real',
  testMatch: ['liveui-real-browser-model.spec.js'],
  timeout: 15 * 60 * 1000,
  fullyParallel: false,
  workers: 1,
});
