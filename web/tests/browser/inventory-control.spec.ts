import { expect, Page, test } from '@playwright/test';

const materialId = '11111111-1111-4111-8111-111111111111';
const receiptId = '22222222-2222-4222-8222-222222222222';
const consumptionId = '33333333-3333-4333-8333-333333333333';

async function fixture(page: Page, role = 'ManagingDirector') {
  const requests: { path: string; body: Record<string, unknown>; headers: Record<string, string> }[] = [];
  const errors: string[] = [];
  let onHand = 10, reserved = 4;
  let nextId = 10;
  const movements = [
    { id: receiptId, revision: 1, materialId, kind: 'Receipt', quantity: '10', location: 'Hauptlager', occurredAt: '2026-10-21T08:00:00Z', reference: 'BE-001', reason: '', unitCost: '2' },
    { id: consumptionId, revision: 1, materialId, kind: 'Consumption', quantity: '-2', location: 'Hauptlager', occurredAt: '2026-10-21T09:00:00Z', reference: '', reason: '', unitCost: '2' }
  ];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/**', async route => {
    const req = route.request(), url = new URL(req.url()), path = url.pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: 'synthetic-user', email: 'warehouse@example.test', displayName: 'Warehouse inspector' },
      activeTenantId: 'tenant-a', tenants: [{ id: 'tenant-a', name: 'Synthetic warehouse', role,
        modules: ['core', 'purchasing'], capabilities: ['*'] }] } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'materials') return route.fulfill({ json: { items: [{ id: materialId, revision: 1, name: 'Synthetic tiles', unit: 'Stk' }], total: 1, page: 1, pageSize: 25 } });
    if (path === `materials/${materialId}/stock`) return route.fulfill({ json: { onHand: String(onHand), reserved: String(reserved), available: String(onHand-reserved) } });
    if (path === `materials/${materialId}/movements`) return route.fulfill({ json: {
      items: movements.filter(m => m.location === (url.searchParams.get('location') || 'Hauptlager')),
      total: movements.length, page: 1, pageSize: 25
    } });
    if (req.method() === 'POST') {
      const body = req.postDataJSON() as Record<string, unknown>;
      requests.push({ path, body, headers: req.headers() });
      if (path.endsWith('/inventory-count')) onHand = Number(body['countedOnHand']);
      else if (path.endsWith('/transfer')) onHand -= Number(body['quantity']);
      else if (path.endsWith('/supplier-return')) onHand -= Number(body['quantity']);
      else if (path.endsWith('/project-return')) onHand += Number(body['quantity']);
      nextId++;
      return route.fulfill({ json: { id: String(nextId), result: 'synthetic-only' } });
    }
    return route.fulfill({ status: 404, json: { code: 'not_found' } });
  });
  return { requests, errors };
}

test('warehouse count, transfer and linked returns are explicit and discard stale views', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/inventory-control');
  const select = page.getByLabel('Material auswählen');
  await expect(select.locator('option')).toHaveCount(2);
  await select.selectOption(materialId);
  await expect(page.getByText('Gebucht', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' })).toBeDisabled();
  await page.getByLabel('Tatsächlich gezählter Bestand').fill('9');
  await page.getByLabel('Bewertung je Einheit (€)').fill('2');
  await page.getByLabel('Begründung / Belegreferenz').fill('Signed count evidence');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect(page.getByText('Bestandsbewegung mit Buchungsbeleg erfasst.', { exact: false })).toBeVisible();
  expect(f.requests[0].path).toBe(`materials/${materialId}/inventory-count`);
  expect(f.requests[0].body).toMatchObject({ expectedOnHand: '10', countedOnHand: '9', location: 'Hauptlager' });
  expect(f.requests[0].headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/i);
  await page.getByRole('tab', { name: 'Lagerumbuchung' }).click();
  await page.getByLabel('Ziellagerort').fill('Aussenlager');
  await page.getByLabel('Menge', { exact: true }).fill('2');
  await page.getByLabel('Bewertung je Einheit (€)').fill('2');
  await page.getByLabel('Begründung / Belegreferenz').fill('Relocation record');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect.poll(() => f.requests.length).toBe(2);
  expect(f.requests[1].body).toMatchObject({ fromLocation: 'Hauptlager', toLocation: 'Aussenlager', quantity: '2' });
  await page.getByRole('tab', { name: 'Lieferantenrückgabe' }).click();
  await page.getByLabel('Ursprüngliche gebuchte Bewegung').selectOption(receiptId);
  await page.getByLabel('Menge', { exact: true }).fill('1');
  await page.getByLabel('Begründung / Belegreferenz').fill('Damaged roll returned');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect.poll(() => f.requests.length).toBe(3);
  expect(f.requests[2].path).toBe(`stock-movements/${receiptId}/supplier-return`);
  expect(f.requests[2].headers['if-match']).toBe('"1"');
  await page.getByRole('tab', { name: 'Baustellenrückgabe' }).click();
  await page.getByLabel('Ursprüngliche gebuchte Bewegung').selectOption(consumptionId);
  await page.getByLabel('Menge', { exact: true }).fill('1');
  await page.getByLabel('Begründung / Belegreferenz').fill('Unused stock from site');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect.poll(() => f.requests.length).toBe(4);
  expect(f.requests[3].path).toBe(`stock-movements/${consumptionId}/project-return`);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(f.errors).toEqual([]);
});

test('accounting role cannot trigger privileged manual warehouse changes', async ({ page }) => {
  const f = await fixture(page, 'Accounting');
  await page.goto('/inventory-control');
  const select = page.getByLabel('Material auswählen');
  await expect(select.locator('option')).toHaveCount(2);
  await select.selectOption(materialId);
  await expect(page.getByText('Für diesen Buchungstyp fehlt die erforderliche Rolle.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' })).toHaveCount(0);
  expect(f.requests).toEqual([]);
  expect(f.errors).toEqual([]);
});
