import { expect, test } from '@playwright/test';

test('editing preserves newer changes and reports a deleted task', async ({ page, context }) => {
  const title = 'Concurrent editing task';
  await page.goto('/');
  const newTitle = page.getByRole('textbox', { name: 'Task Title' });
  await newTitle.fill(title);
  await newTitle.blur();
  const add = page.getByRole('button', { name: 'Add Task' });
  await expect(add).toBeEnabled();
  await add.click();
  const row = page.locator('.mud-list-item').filter({ hasText: title });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Edit task' }).click();
  await page.locator('#edit-task-title').fill('');
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Enter a task title' })).toBeVisible();
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  await page.locator('#edit-task-title').fill('Stale title');

  const other = await context.newPage();
  await other.goto('/');
  const otherRow = other.locator('.mud-list-item').filter({ hasText: title });
  await expect(otherRow).toHaveCount(1);
  await otherRow.getByRole('checkbox').check();
  await expect(otherRow.getByRole('checkbox')).toBeChecked();
  await otherRow.getByRole('checkbox').uncheck();
  await expect(otherRow.getByRole('checkbox')).not.toBeChecked();
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'changed while you were editing' })).toBeVisible();
  await expect(page.locator('#edit-task-title')).toHaveValue('Stale title');
  await page.getByRole('button', { name: 'Cancel' }).click();
  await page.reload();
  await expect(row).toHaveCount(1);
  await expect(row.getByRole('checkbox')).not.toBeChecked();
  await row.getByRole('button', { name: 'Edit task' }).click();
  await page.locator('#edit-task-title').fill('Updated task');
  await page.locator('#edit-task-description').fill('Updated description');
  await page.locator('#edit-task-due-date').fill('2030-07-05');
  await page.locator('#edit-task-priority').selectOption('High');
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByText('Updated task', { exact: true })).toBeVisible();
  await page.reload();
  const updated = page.locator('.mud-list-item').filter({ hasText: 'Updated task' });
  await expect(updated).toContainText('Updated description');
  await expect(updated).toContainText('Due 2030-07-05');
  await expect(updated).toContainText('High');
  await expect(updated.getByRole('checkbox')).not.toBeChecked();

  await updated.getByRole('button', { name: 'Edit task' }).click();
  await page.locator('#edit-task-title').fill('Do not restore deleted task');
  await other.reload();
  await other.locator('.mud-list-item').filter({ hasText: 'Updated task' }).getByRole('button', { name: 'Delete task' }).click();
  await other.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(other.getByText('Updated task', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'no longer exists' })).toBeVisible();
  await page.reload();
  await expect(page.getByText('Do not restore deleted task', { exact: true })).toHaveCount(0);
  await other.close();
});

test('internal status changes remain tenant scoped', async ({ page, request }) => {
  const headers = { authorization: 'Bearer local-acceptance-todo-internal-token',
    'X-OpenConnect-Tenant': '01ARZ3NDEKTSV4RRFFQ69G5FAV' };
  const created = await request.post('/internal/openconnect/todos', {
    headers, data: { title: 'Tenant-only task', priority: 'Medium' },
  });
  expect(created.ok()).toBe(true);
  const item = await created.json();
  const denied = await request.put(`/internal/openconnect/todos/${item.id}`, {
    headers: { ...headers, 'X-OpenConnect-Tenant': '01ARZ3NDEKTSV4RRFFQ69G5FAW' }, data: { isCompleted: true },
  });
  expect(denied.status()).toBe(401);
  const updated = await request.put(`/internal/openconnect/todos/${item.id}`, { headers, data: { isCompleted: true } });
  expect(updated.ok()).toBe(true);
  const read = async () => (await (await request.get('/internal/openconnect/todos', { headers })).json()).find((entry) => entry.id === item.id);
  const completed = await read();
  expect(completed).toMatchObject({ isCompleted: true, revision: 1 });
  expect(completed.completedAtUtc).toBeTruthy();
  expect((await request.put(`/internal/openconnect/todos/${item.id}`, { headers, data: { isCompleted: true } })).ok()).toBe(true);
  expect((await read()).completedAtUtc).toBe(completed.completedAtUtc);
  expect((await request.put(`/internal/openconnect/todos/${item.id}`, { headers, data: { isCompleted: false } })).ok()).toBe(true);
  expect(await read()).toMatchObject({ isCompleted: false, completedAtUtc: null });
  await page.goto('/');
  await expect(page.getByText('Tenant-only task', { exact: true })).toHaveCount(0);
});
