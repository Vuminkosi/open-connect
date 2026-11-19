import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './specs-real-persistence',
  timeout: 120_000,
  workers: 1,
  reporter: [['list']],
  outputDir: './test-results-real-persistence',
  use: { ...devices['Desktop Chrome'], trace: 'retain-on-failure', screenshot: 'only-on-failure' },
});
