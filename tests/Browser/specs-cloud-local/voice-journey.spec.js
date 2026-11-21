import { createHash, randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';

const environment = process.env;
const required = [
  'OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL', 'OPENCONNECT_CLOUD_ACCEPTANCE_GATEWAY',
  'OPENCONNECT_CLOUD_ACCEPTANCE_TENANT', 'OPENCONNECT_CLOUD_ACCEPTANCE_APPLICATION',
  'OC_LOCAL_OIDC_USERNAME', 'OC_LOCAL_OIDC_PASSWORD',
];

function loopbackOrigin(value, protocol) {
  const url = new URL(value);
  if (url.protocol !== protocol || !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname) ||
      url.username || url.password || url.pathname !== '/' || url.search || url.hash) {
    throw new Error('Cloud acceptance origins must use the expected protocol and a loopback host.');
  }
  return url.origin;
}

test('local portal issues and revokes a credential for a real voice request', async ({ page }) => {
  test.skip(environment.OPENCONNECT_CLOUD_ACCEPTANCE_ENABLED !== '1' || required.some(name => !environment[name]),
    'Explicit local Cloud acceptance configuration is required.');

  const portal = loopbackOrigin(environment.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL, 'https:');
  const gateway = loopbackOrigin(environment.OPENCONNECT_CLOUD_ACCEPTANCE_GATEWAY, 'http:');
  const tenant = environment.OPENCONNECT_CLOUD_ACCEPTANCE_TENANT;
  const application = environment.OPENCONNECT_CLOUD_ACCEPTANCE_APPLICATION;
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!guid.test(tenant) || !guid.test(application)) throw new Error('Invalid Cloud acceptance scope.');
  const credentialsUrl = `${portal}/cloud/tenants/${tenant}/applications/${application}/credentials`;
  let secret;
  let fingerprint;
  let revoked = false;

  async function revoke() {
    if (!fingerprint || revoked) return;
    if (!page.url().startsWith(credentialsUrl)) await page.goto(credentialsUrl);
    const row = page.locator('ul.cloud-list li').filter({ has: page.locator('strong', { hasText: fingerprint }) });
    await row.getByRole('button', { name: /Revoke credential/ }).click();
    await expect(row).toContainText('Revoked');
    revoked = true;
  }

  try {
    await page.goto(credentialsUrl);
    await page.getByRole('link', { name: 'Sign in with your identity provider' }).click();
    await page.locator('#username').fill(environment.OC_LOCAL_OIDC_USERNAME);
    await page.locator('#password').fill(environment.OC_LOCAL_OIDC_PASSWORD);
    await page.locator('#kc-login').click();
    await expect(page.getByRole('heading', { name: 'Application credentials' })).toBeVisible();
    await page.getByLabel('Speech synthesis').check();
    await page.locator('#credential-expiry').fill(new Date(Date.now() + 3_600_000).toISOString().slice(0, 16));
    await page.getByRole('button', { name: 'Create credential' }).click();
    await expect(page.getByRole('heading', { name: 'Save this key now' })).toBeVisible();
    secret = await page.locator('section[aria-labelledby="new-key-title"] code').innerText();
    if (secret.length < 20) throw new Error('Credential was not displayed.');
    fingerprint = createHash('sha256').update(secret).digest('hex').slice(0, 12).toUpperCase();
    await page.getByRole('button', { name: 'Done', exact: true }).click();

    const requestId = randomUUID();
    const request = {
      schemaVersion: 'openconnect.cloud.voice-synthesis/v1', requestId, capability: 'voice.synthesize',
      expiresAt: new Date(Date.now() + 180_000).toISOString(),
      input: { text: 'Hello world.', voiceId: 'af_heart', locale: 'en-US', format: 'pcm' },
    };
    const response = await fetch(`${gateway}/cloud/v1/voice/synthesis`, {
      method: 'POST', headers: { authorization: `Bearer ${secret}`, 'content-type': 'application/json' },
      body: JSON.stringify(request), signal: AbortSignal.timeout(150_000),
    });
    expect(response.status).toBe(200);
    const result = await response.json();
    expect(result.requestId).toBe(requestId);
    expect(result.result.sampleRateHz).toBe(24000);
    const audio = Buffer.from(result.result.audioBase64, 'base64');
    expect(audio.length).toBeGreaterThan(0);
    audio.fill(0);

    await revoke();
    const denied = await fetch(`${gateway}/cloud/v1/voice/synthesis`, {
      method: 'POST', headers: { authorization: `Bearer ${secret}`, 'content-type': 'application/json' }, body: '{}',
    });
    expect(denied.status).toBe(401);
    secret = undefined;
    await page.goto(`${portal}/cloud/tenants/${tenant}/usage`);
    await expect(page.getByText(requestId, { exact: true })).toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${portal}/cloud/tenants/${tenant}/security`);
    const observations = page.locator('section[aria-label="Voice privacy observations"]');
    await expect(observations.getByText(requestId, { exact: true }).first()).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  } finally {
    try { await revoke(); }
    finally { secret = undefined; await page.close(); }
  }
});
