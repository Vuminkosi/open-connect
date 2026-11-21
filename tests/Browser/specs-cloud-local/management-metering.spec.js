import { execFileSync } from 'node:child_process';
import { expect, test } from '@playwright/test';

const env = process.env;
const required = [
  'OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL', 'OPENCONNECT_CLOUD_ACCEPTANCE_TENANT',
  'OC_LOCAL_OIDC_USERNAME', 'OC_LOCAL_OIDC_PASSWORD',
  'OPENCONNECT_CLOUD_TEST_DB_CONTAINER', 'OPENCONNECT_CLOUD_TEST_DB_USER', 'OPENCONNECT_CLOUD_TEST_DB_NAME',
];

test.use({ ignoreHTTPSErrors: true });

function localOrigin(value) {
  const url = new URL(value);
  if (url.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname) ||
      url.username || url.password || url.pathname !== '/' || url.search || url.hash) {
    throw new Error('Cloud acceptance portal must be a loopback HTTPS origin.');
  }
  return url.origin;
}

function ledgerSnapshot(tenant) {
  // The tenant is validated as a GUID before it is used in this read-only query.
  const query = `SELECT
    (SELECT count(*) FROM cloud.usage_records WHERE "TenantId" = '${tenant}'),
    (SELECT COALESCE(sum("Quantity"), 0) FROM cloud.usage_records WHERE "TenantId" = '${tenant}'),
    (SELECT count(*) FROM cloud.quota_reservations WHERE "TenantId" = '${tenant}'),
    (SELECT COALESCE(sum("Amount"), 0) FROM cloud.quota_reservations WHERE "TenantId" = '${tenant}'),
    (SELECT COALESCE(sum("Actual"), 0) FROM cloud.quota_reservations WHERE "TenantId" = '${tenant}'),
    (SELECT count(*) FROM cloud.execution_consumptions c JOIN cloud.quota_reservations r ON r."Id" = c."ReservationId" WHERE r."TenantId" = '${tenant}'),
    (SELECT count(*) FROM cloud.liveui_metered_consumption_authorizations a JOIN cloud.liveui_token_reservations r ON r."PlanReservationId" = a."PlanReservationId" WHERE r."TenantId" = '${tenant}')`;
  return execFileSync('docker', [
    'exec', env.OPENCONNECT_CLOUD_TEST_DB_CONTAINER, 'psql', '-X', '-v', 'ON_ERROR_STOP=1',
    '-U', env.OPENCONNECT_CLOUD_TEST_DB_USER, '-d', env.OPENCONNECT_CLOUD_TEST_DB_NAME, '-Atc', query,
  ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

test('documentation and authenticated management visits do not consume tenant usage', async ({ page }) => {
  test.skip(env.OPENCONNECT_CLOUD_MANAGEMENT_ACCEPTANCE_ENABLED !== '1' || required.some(name => !env[name]),
    'Explicit local Cloud management acceptance configuration is required.');
  const portal = localOrigin(env.OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL);
  const tenant = env.OPENCONNECT_CLOUD_ACCEPTANCE_TENANT;
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(tenant)) {
    throw new Error('Invalid Cloud acceptance tenant.');
  }

  const before = ledgerSnapshot(tenant);
  await page.goto(`${portal}/docs`);
  await expect(page.getByRole('heading', { name: 'Documentation', exact: true })).toBeVisible();
  await page.goto(`${portal}/cloud/sign-in`);
  await page.getByRole('link', { name: 'Sign in with your identity provider' }).click();
  const identity = new URL(page.url());
  if (identity.protocol !== 'https:' || !['localhost', '127.0.0.1', '[::1]'].includes(identity.hostname)) {
    throw new Error('The local acceptance account may only be sent to a loopback HTTPS identity provider.');
  }
  await page.locator('#username').fill(env.OC_LOCAL_OIDC_USERNAME);
  await page.locator('#password').fill(env.OC_LOCAL_OIDC_PASSWORD);
  await page.locator('#kc-login').click();
  await expect(page.getByText('Authenticated workspace')).toBeVisible();
  await page.goto(`${portal}/cloud/tenants/${tenant}/services`);
  await expect(page.getByRole('heading', { name: 'Service catalog' })).toBeVisible();
  await expect(page.getByText('Loading service catalog...')).not.toBeVisible();
  await expect(page.getByText('The Cloud control plane is not configured.')).not.toBeVisible();
  await page.goto(`${portal}/cloud/tenants/${tenant}/usage`);
  await expect(page.getByRole('heading', { name: 'Recorded usage' })).toBeVisible();
  await expect(page.locator('#usage-from')).toBeEnabled();
  await expect(page.getByText('Loading usage records...')).not.toBeVisible();
  await expect(page.getByText('Usage records are temporarily unavailable.')).not.toBeVisible();
  for (const [route, heading] of [
    ['security', 'Security observations'],
    ['quotas', 'Organization quotas'],
    ['members', 'Organization members'],
    ['applications', 'Applications'],
  ]) {
    await page.goto(`${portal}/cloud/tenants/${tenant}/${route}`);
    await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible();
    await expect(page.getByText(/^Loading/i)).toHaveCount(0);
    await expect(page.getByText(/temporarily unavailable/i)).toHaveCount(0);
  }
  expect(ledgerSnapshot(tenant)).toBe(before);
});
