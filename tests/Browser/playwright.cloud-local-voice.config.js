import { defineConfig } from '@playwright/test';

// This suite uses real credentials and model execution. Do not capture browser
// traces, screenshots or video containing the one-time credential.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
export default defineConfig({
  testDir: './specs-cloud-local',
  timeout: 180_000,
  expect: { timeout: 30_000 },
  workers: 1,
  retries: 0,
  reporter: 'list',
  outputDir: './test-results-cloud-local',
  use: {
    viewport: { width: 1280, height: 800 },
    trace: 'off',
    screenshot: 'off',
    video: 'off',
    launchOptions: process.env.OPENCONNECT_CLOUD_ACCEPTANCE_CHROME
      ? { executablePath: process.env.OPENCONNECT_CLOUD_ACCEPTANCE_CHROME }
      : {},
  },
});
