import { expect, Page, test } from '@playwright/test';

const materialId = '11111111-1111-4111-8111-111111111111';
const reservationId = '22222222-2222-4222-8222-222222222222';
const consumptionId = '33333333-3333-4333-8333-333333333333';
const purchaseId = '44444444-4444-4444-8444-444444444444';
const lineId = '55555555-5555-4555-8555-555555555555';
const movementId = '66666666-6666-4666-8666-666666666666';
const outboundId = '77777777-7777-4777-8777-777777777777';
const inboundId = '88888888-8888-4888-8888-888888888888';

async function fixture(page: Page, role = 'ManagingDirector') {
  const errors: string[] = [], commands: { path: string; body: Record<string, unknown>; headers: Record<string, string> }[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname.split('/api/v1/')[1];
    if (path === 'auth/me') return route.fulfill({ json: {
      user: { id: 'synthetic-user', email: 'operator@example.test', displayName: 'Synthetic inventory operator' },
      activeTenantId: 'tenant-a',
      tenants: [{ id: 'tenant-a', name: 'Synthetic roofing company', role,
        modules: ['core', 'purchasing'], capabilities: role === 'ManagingDirector' ? ['*'] : ['projects.read'] }]
    } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: {
      'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict'
    } });
    if (path === 'materials') return route.fulfill({ json: {
      items: [{ id: materialId, revision: 1, name: 'Synthetic tiles', sku: 'TILE-01' }],
      page: 1, pageSize: 25, total: 1
    } });
    if (path === `materials/${materialId}/stock`) return route.fulfill({ json: {
      onHand: '10', reserved: '2', available: '8'
    } });
    if (path === `material-reservations/${reservationId}`) return route.fulfill({ json: {
      id: reservationId, revision: 1, materialId, projectId: '99999999-9999-4999-8999-999999999999',
      location: 'Hauptlager', quantity: '5', consumedQuantity: '3', releasedQuantity: '1'
    } });
    if (path === `purchase-orders/${purchaseId}`) return route.fulfill({ json: {
      id: purchaseId, revision: 2, number: 'BE-2026-01', title: 'Tiles',
      status: 'Received', lines: [{ id: lineId, materialId, description: 'Roof tiles', quantity: '10' }]
    } });
    if (request.method() === 'POST') {
      commands.push({ path, body: request.postDataJSON(), headers: request.headers() });
      if (path === `materials/${materialId}/stock-count`) return route.fulfill({ json: { id: movementId } });
      if (path === `materials/${materialId}/transfer`) return route.fulfill({ json: {
        outbound: { id: outboundId }, inbound: { id: inboundId }
      } });
      if (path === `material-reservations/${reservationId}/return`) return route.fulfill({ json: {
        reservation: { id: reservationId, revision: 2, materialId, projectId: '99999999-9999-4999-8999-999999999999',
          location: 'Hauptlager', quantity: '5', consumedQuantity: '1', releasedQuantity: '3' },
        correction: { id: movementId }
      } });
      if (path === `purchase-orders/${purchaseId}/supplier-return`) return route.fulfill({ json: {
        order: { id: purchaseId, revision: 3, number: 'BE-2026-01', title: 'Tiles',
          status: 'PartiallyReceived', lines: [{ id: lineId, materialId, description: 'Roof tiles', quantity: '10' }] },
        correction: { id: movementId }
      } });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 25, total: 0 } });
  });
  return { commands, errors };
}
async function selectMaterial(page: Page) {
  await page.getByRole('button', { name: 'Materialstamm durchsuchen' }).click();
  await page.getByLabel('Material für die Korrektur').selectOption(materialId);
  await page.getByRole('button', { name: 'Lagerbilanz abrufen' }).click();
  await expect(page.getByText('Buchbestand', { exact: true })).toBeVisible();
}
test('a signed stock-count request uses the observed ledger balance and explicit source evidence', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/inventory-controls');
  await selectMaterial(page);
  await page.getByLabel('Physisch gezählter Bestand').fill('8');
  await page.getByLabel('Bewerteter Einzelpreis (€)').fill('2');
  await page.getByLabel('Inventurbeleg / Referenz').fill('INVENTUR-001');
  await page.getByLabel('Grund der Abweichung').fill('Independent roof-tile count');
  await page.getByRole('button', { name: 'Geprüfte Inventurdifferenz buchen' }).click();
  await expect(page.getByText('Inventurkorrektur gebucht.', { exact: false })).toBeVisible();
  const posted = f.commands.find(x => x.path.endsWith('/stock-count'))!;
  expect(posted.body).toMatchObject({ location: 'Hauptlager', expectedOnHand: 10,
    countedOnHand: 8, unitCost: 2, evidenceReference: 'INVENTUR-001' });
  expect(posted.headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/);
  await expect(page.getByRole('button', { name: 'Geprüfte Inventurdifferenz buchen' })).toBeDisabled();
  expect(f.errors).toEqual([]);
});
test('stock transfer creates exactly one paired command with its checked source balance', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/inventory-controls');
  await page.getByRole('tab', { name: 'Lagerumlagerung' }).click();
  await selectMaterial(page);
  await page.getByLabel('Ziellagerort').fill('Baustelle-1');
  await page.getByLabel('Menge').fill('3');
  await page.getByLabel('Bewerteter Einzelpreis (€)').fill('2');
  await page.getByLabel('Lieferschein / Umlagerungsgrund').fill('Reviewed delivery note');
  await page.getByRole('button', { name: 'Ausgang und Eingang gemeinsam buchen' }).click();
  await expect(page.getByText('Beide Lagerbewegungen wurden gebucht.', { exact: false })).toBeVisible();
  expect(f.commands.find(x => x.path.endsWith('/transfer'))?.body).toMatchObject({
    fromLocation: 'Hauptlager', toLocation: 'Baustelle-1',
    quantity: 3, expectedFromOnHand: 10
  });
  expect(f.errors).toEqual([]);
});
test('project returns reference the original consumption and preserve reservation revisions', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/inventory-controls');
  await page.getByRole('tab', { name: 'Projekt-Rückgabe' }).click();
  await page.getByLabel('Reservierungs-ID').fill(reservationId);
  await page.getByRole('button', { name: 'Reservierung laden' }).click();
  await expect(page.getByText('verbracht:', { exact: false })).toHaveCount(0);
  await page.getByLabel('ID der ursprünglichen Verbrauchsbuchung').fill(consumptionId);
  await page.getByLabel('Rückgabemenge').fill('2');
  await page.getByLabel('Rückgabebeleg und Begründung').fill('Project material returned');
  await page.getByRole('button', { name: 'Projekt-Rückgabe buchen' }).click();
  await expect(page.getByText('Projektmaterial zum Originalkostenwert zurückgebucht.')).toBeVisible();
  const posted = f.commands.find(x => x.path.endsWith('/return'))!;
  expect(posted.body).toMatchObject({ consumptionMovementId: consumptionId, quantity: 2 });
  expect(posted.headers['if-match']).toBe('"1"');
  expect(f.errors).toEqual([]);
});
test('supplier return selects a real purchase line and keeps the credit workflow separate', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/inventory-controls');
  await page.getByRole('tab', { name: 'Lieferantenretoure' }).click();
  await page.getByLabel('Bestellungs-ID').fill(purchaseId);
  await page.getByRole('button', { name: 'Bestellung laden' }).click();
  await page.getByLabel('Bestellposition').selectOption(lineId);
  await page.getByLabel('Retourenmenge').fill('2');
  await page.getByLabel('RMA / Gutschriftbezug').fill('RMA-001');
  await page.getByLabel('Retourengrund').fill('Tiles damaged in shipment');
  await page.getByRole('button', { name: 'Lieferantenretoure buchen' }).click();
  await expect(page.getByText('Lieferantenretoure im Warenbestand erfasst.', { exact: false })).toBeVisible();
  const posted = f.commands.find(x => x.path.endsWith('/supplier-return'))!;
  expect(posted.body).toMatchObject({ lineId, quantity: 2, supplierCreditReference: 'RMA-001' });
  expect(posted.headers['if-match']).toBe('"2"');
  expect(f.errors).toEqual([]);
});
test('workers cannot access inventory write controls', async ({ page }) => {
  const f = await fixture(page, 'Worker');
  await page.goto('/inventory-controls');
  await expect(page.getByRole('heading', { name: 'Zugriff eingeschränkt' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Geprüfte Inventurdifferenz buchen' })).toHaveCount(0);
  expect(f.commands).toEqual([]);
  expect(f.errors).toEqual([]);
});
