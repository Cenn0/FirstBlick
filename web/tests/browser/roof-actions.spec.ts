import { expect, Page, test } from '@playwright/test';
import { RoofAction, roofActions } from '../../src/app/core/roof-actions.models';
import { actionInputs, actionResponse } from '../roof-actions.fixture';

const projectId = '10000000-0000-4000-8000-000000000001';
async function fixture(page: Page, modify?: (mode: RoofAction, result: unknown) => unknown) {
  const calls: { path: string; body: Record<string, unknown> }[] = [], errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: {
      user: { id: 'operator', email: 'operator@example.test', displayName: 'Operator' }, activeTenantId: 'tenant',
      tenants: [{ id: 'tenant', name: 'Synthetic', role: 'ManagingDirector', modules: ['core', 'technical'], capabilities: ['*'] }]
    } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: projectId, revision: 1, name: 'Synthetic roof project' }], total: 1, page: 1, pageSize: 25 } });
    if (path.startsWith('technical/roof/')) {
      const mode = path.split('/').at(-1) as RoofAction;
      const body = route.request().postDataJSON() as Record<string, unknown>;
      calls.push({ path, body });
      const result = actionResponse(mode);
      return route.fulfill({ json: modify ? modify(mode, result) : result });
    }
    if (path === 'technical/runs') {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      calls.push({ path, body });
      return route.fulfill({ json: { ...body, id: '20000000-0000-4000-8000-000000000001',
        methodVersion: '1.0.0', digest: 'b'.repeat(64), isNormative: false,
        result: actionResponse((body['method'] as string).split('.')[1] as RoofAction) } });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, errors };
}
async function fill(page: Page, mode: RoofAction) {
  await page.locator('#roof-action').selectOption(mode);
  const def = roofActions[mode], input = actionInputs[mode] as unknown as Record<string, unknown>;
  await page.locator('#action-source').fill(input[def.sourceKey] as string);
  for (const field of def.globals) await page.locator('#' + field.key).fill(String(input[field.key]));
  if (mode === 'drainage') await page.locator('#emergency-scenario').selectOption('PrimaryUnavailable');
  const row = (input[def.rowsKey] as Record<string, unknown>[])[0];
  await page.locator('#action-name-0').fill(row['name'] as string);
  for (const f of def.fields) await page.locator('#' + f.key + '-0').fill(String(row[f.key]));
  for (const source of def.sources) await page.locator('#' + source.key + '-0').fill(row[source.key] as string);
}
for (const mode of ['drainage', 'wind', 'snow'] as RoofAction[]) {
  test(mode + ' explicit preview, verified export and project evidence', async ({ page }) => {
    const f = await fixture(page);
    await page.goto('/technical/actions'); await fill(page, mode);
    await page.getByRole('button', { name: 'Fall berechnen' }).click();
    const result = page.getByRole('region', { name: 'Geprüftes Rechenergebnis' });
    await expect(result).toContainText('West');
    await expect(result).toContainText('Keine konstruktive oder hydraulische Freigabe');
    expect(f.calls[0].body).toEqual(actionInputs[mode]);
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'JSON herunterladen' }).click();
    expect((await download).suggestedFilename()).toContain(mode);
    await page.getByRole('button', { name: 'Projekte suchen' }).click();
    await page.locator('#action-project').selectOption(projectId);
    await page.locator('#action-title').fill('Synthetic reviewed ' + mode);
    await page.getByRole('button', { name: 'Im Projekt speichern' }).click();
    await expect(page.getByRole('status')).toContainText('gespeichert');
    expect(f.calls.at(-1)?.body).toMatchObject({ projectId, method: 'roof.' + mode, input: actionInputs[mode] });
    expect(f.errors).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('#action-name-0').fill('Changed');
    await expect(result).toHaveCount(0);
  });
}
test('a contradictory capacity comparison is rejected without a save option', async ({ page }) => {
  await fixture(page, (_mode, raw) => ({ ...(raw as object), hydraulicDesignApproved: true }));
  await page.goto('/technical/actions'); await fill(page, 'drainage');
  await page.getByRole('button', { name: 'Fall berechnen' }).click();
  await expect(page.getByRole('alert')).toContainText('freigegeben');
  await expect(page.getByRole('button', { name: 'Im Projekt speichern' })).toHaveCount(0);
});
test('switching methods while a response is in flight discards the old result', async ({ page }) => {
  await fixture(page);
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/technical/roof/wind', async route => {
    await pending; await route.fulfill({ json: actionResponse('wind') });
  });
  await page.goto('/technical/actions'); await fill(page, 'wind');
  const request = page.waitForRequest('**/api/v1/technical/roof/wind');
  await page.getByRole('button', { name: 'Fall berechnen' }).click(); await request;
  await page.locator('#roof-action').selectOption('snow');
  const response = page.waitForResponse('**/api/v1/technical/roof/wind'); release(); await response;
  await expect(page.getByRole('region', { name: 'Geprüftes Rechenergebnis' })).toHaveCount(0);
  await expect(page.locator('#action-source')).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Fall berechnen' })).toBeDisabled();
});
