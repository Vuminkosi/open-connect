import { expect, test } from '@playwright/test';

test('greetings and conversational follow-ups do not invoke UI tools', async ({ page, request }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  await page.locator('.mud-overlay').last().click({ position: { x: 5, y: 5 } });

  for (const prompt of ['Hi', 'How are you?']) {
    const answers = page.locator('.oc-message-assistant .oc-message-text');
    const count = await answers.count();
    await page.getByRole('textbox', { name: /message ellai/i }).fill(prompt);
    await page.getByRole('button', { name: /send to ellai/i }).click();
    await expect(answers).toHaveCount(count + 1, { timeout: 120_000 });
    await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
    const answer = await answers.last().innerText();
    if (prompt === 'Hi') expect(answer).toMatch(/\b(hi|hello|hey|greetings)\b/i);
    else expect(answer).toMatch(/\b(good|well|fine|great|doing|thanks|thank you)\b/i);
    expect(answer).not.toMatch(/ask about tasks, navigation, or highlighting/i);
  }

  const response = await request.get(`${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  expect((await response.json()).filter((event) => event.type === 'liveui.request')).toEqual([]);
});
