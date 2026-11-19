import { defineConfig, devices } from '@playwright/test';

// Dedicated synthetic-provider lane; never inherit the live/manual spec configuration.
export default defineConfig({
  testDir: './specs-voice-local',
  timeout: 90_000,
  expect: { timeout: 20_000 },
  workers: 1,
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  reporter: [['list']],
  outputDir: './test-results-voice-local',
  webServer: {
    command: 'dotnet VoiceFixture/bin/Release/net10.0/VoiceFixture.dll --sample ../../samples/Blazor/bin/Release/net10.0/OpenConnect.Samples.Blazor.dll --content-root ../../samples/Blazor',
    url: 'http://127.0.0.1:5188/_fixture/ready',
    reuseExistingServer: false,
    timeout: 90_000,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 10_000 },
  },
  use: {
    ...devices['Desktop Chrome'],
    baseURL: 'http://127.0.0.1:5187',
    serviceWorkers: 'block',
    trace: 'off',
    screenshot: 'off',
    video: 'off',
    launchOptions: { args: ['--disable-background-networking'] },
  },
});
