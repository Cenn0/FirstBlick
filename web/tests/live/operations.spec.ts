import { expect, test } from '@playwright/test';
import { writeFile } from 'node:fs/promises';

test('real director sees the current tenant queue and document snapshot without inferred provider health', async ({ page }, testInfo) => {
  const email = process.env['FIRSTBLICK_LIVE_EMAIL'], password = process.env['FIRSTBLICK_LIVE_PASSWORD'];
  if (!email || !password) throw new Error('Set FIRSTBLICK_LIVE_EMAIL/PASSWORD for the isolated synthetic operator.');
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto('/login');
  await page.getByLabel('E-Mail-Adresse').fill(email); await page.getByLabel('Passwort', { exact: true }).fill(password);
  const login = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/auth/login');
  await page.getByRole('button', { name: 'Anmelden', exact: false }).click(); expect((await login).ok()).toBe(true);
  await expect(page).toHaveURL(/\/overview$/);
  const received = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/operations/snapshot');
  await page.goto('/operations'); const response = await received;
  expect(response.ok()).toBe(true); const snapshot = await response.json();
  expect(snapshot.schema).toBe('firstblick.operations/1.0.0');
  expect(response.headers()['cache-control']).toContain('no-store'); expect(response.headers()['cache-control']).toContain('private');
  await expect(page.getByRole('heading', { name: 'Betriebsübersicht', exact: true })).toBeVisible();
  const pending = page.locator('dl>div').filter({ has: page.getByText('Zur Bearbeitung vorgemerkt', { exact: true }) }).locator('dd');
  await expect(pending).toHaveText(new Intl.NumberFormat('de-DE').format(snapshot.queue.pending));
  await expect(page.getByRole('heading', { name: 'Was diese Ansicht erfasst', exact: true })).toBeVisible();
  expect(snapshot.healthy).toBeUndefined(); expect(snapshot.workerOnline).toBeUndefined();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(errors).toEqual([]);
  await page.screenshot({ path: testInfo.outputPath('operations-live.png'), fullPage: true });
  await writeFile(testInfo.outputPath('operations-live-evidence.json'), JSON.stringify({
    schema: 'firstblick.live-operations/1.0.0', viewport: testInfo.project.name,
    capturedAt: new Date().toISOString(), actualHttp: true, apiInterception: false,
    status: response.status(), cacheControl: response.headers()['cache-control'], snapshot,
    checks: ['Personal login', 'Actual current-tenant SQL snapshot', 'Rendered count equals actual response', 'Private no-store response', 'Explicit provider/worker availability limits', 'No horizontal overflow or page error']
  }, null, 2));
});
