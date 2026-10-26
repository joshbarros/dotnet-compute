import { test, expect } from '@playwright/test';
import { ADMIN, adminToken, signIn } from './support';

test('open a pool, reserve a team, see the lease, cancel it', async ({ page }) => {
  const run = Date.now().toString(36).toUpperCase();
  const code = `E2E-${run}`;
  const reference = `TEAM-${run}`;

  await signIn(page, ADMIN);
  await page.getByLabel('Pool code').fill(code);
  await page.getByLabel('Hour capacity').fill('8');
  await page.getByRole('button', { name: 'Open pool' }).click();

  const remaining = page.getByTestId('selected-remaining');
  await expect(page.getByRole('heading', { name: `Reserve hours in ${code}` })).toBeVisible();
  await expect(remaining).toHaveText('8');

  await page.getByLabel('Team reference').fill(reference);
  await page.getByLabel('Hours', { exact: true }).fill('3');
  await page.getByRole('button', { name: 'Reserve' }).click();

  const row = page.locator(`tr[data-reference="${reference}"]`);
  await expect(row.getByTestId('status')).toHaveText('Leased', { timeout: 20_000 });
  await expect(row.getByTestId('lease')).toHaveText(/LS-[0-9A-F]{12}/);
  await expect(remaining).toHaveText('5');

  // Nothing is kept in the browser: a fresh load reads the same state from the API.
  // Nothing sensitive is stored either: the refresh cookie is HttpOnly, so a reload signs back in through it.
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).toBe(
    '{}',
  );
  await page.reload();
  await expect(page.getByTestId('whoami')).toContainText(ADMIN.email);
  await page.getByRole('button', { name: new RegExp(`^${code}`) }).click();
  const reloaded = page.locator(`tr[data-reference="${reference}"]`);
  await expect(reloaded.getByTestId('status')).toHaveText('Leased');
  await expect(page.getByTestId('selected-remaining')).toHaveText('5');

  await reloaded.getByRole('button', { name: 'Cancel' }).click();
  await expect(reloaded.getByTestId('status')).toHaveText('Cancelled');
  await expect(page.getByTestId('selected-remaining')).toHaveText('8');
  // The issued lease is not rolled back.
  await expect(reloaded.getByTestId('lease')).toHaveText(/LS-[0-9A-F]{12}/);
  const headers = { Authorization: `Bearer ${await adminToken(page.request)}` };
  const reservations = await page.request.get(`/api/reservations?poolCode=${code}`, { headers });
  const [reservation] = await reservations.json();
  const lease = await page.request.get(`/api/leases/${reservation.id}`, { headers });
  expect((await lease.json()).status).toBe('Issued');
});
