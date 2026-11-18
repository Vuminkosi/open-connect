import { expect, test } from '@playwright/test';

test('fixture reports errors for the current conversation only', async ({ request }) => {
  const url = 'http://127.0.0.1:5110/v1/chat/completions';
  const user = { role: 'user', content: 'Open my tasks' };
  expect((await request.post(url, { data: { messages: [user] } })).status()).toBe(200);
  const invalid = await request.post(url, {
    data: { messages: [user, { role: 'tool', content: '{}' }] },
  });
  expect(invalid.status()).toBe(500);
  const failed = await (await request.get('http://127.0.0.1:5110/__acceptance/runs')).json();
  expect(failed.error).toContain('Current-page discovery');

  expect((await request.post(url, { data: { messages: [{ role: 'user', content: 'Show active tasks' }] } })).status()).toBe(200);
  const later = await (await request.get('http://127.0.0.1:5110/__acceptance/runs')).json();
  expect(later.error).toBeNull();
  expect(later.runs.at(-2).error).toBe(failed.error);
});
