import { expect, Page, test } from '@playwright/test';

const materialId = '11111111-1111-4111-8111-111111111111';
const receiptId = '22222222-2222-4222-8222-222222222222';
const consumptionId = '33333333-3333-4333-8333-333333333333';
const transferId = '44444444-4444-4444-8444-444444444444';
const intermediateTransferId = '66666666-6666-4666-8666-666666666666';

async function fixture(page: Page, role = 'ManagingDirector', linkedTransfers = false) {
  const requests: { path: string; body: Record<string, unknown>; headers: Record<string, string> }[] = [];
  const errors: string[] = [];
  let onHand = 10, reserved = 4;
  let nextId = 10;
  const movements = [
    { id: receiptId, revision: 1, materialId, kind: 'Receipt', quantity: '10', location: 'Hauptlager', occurredAt: '2026-10-21T08:00:00Z', reference: 'BE-001', reason: '', unitCost: '2', purchaseOrderId: '77777777-7777-4777-8777-777777777777' },
    { id: consumptionId, revision: 1, materialId, kind: 'Consumption', quantity: '-2', location: 'Hauptlager', occurredAt: '2026-10-21T09:00:00Z', reference: '', reason: '', unitCost: '2' },
    { id: transferId, revision: 1, materialId, kind: 'TransferIn', quantity: '2', location: 'Aussenlager', occurredAt: '2026-10-21T10:00:00Z', reference: '55555555-5555-4555-8555-555555555555', reason: 'Recorded transfer', unitCost: '2', ...(linkedTransfers ? { transferSourceId: intermediateTransferId } : {}) }
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
    if (path === `materials/${materialId}/movements`) {
      const items = movements.filter(m => (!url.searchParams.get('location') || m.location === url.searchParams.get('location'))
        && (!url.searchParams.get('kind') || m.kind === url.searchParams.get('kind')));
      return route.fulfill({ json: { items, total: items.length, page: 1, pageSize: 25 } });
    }
    if (path === `stock-movements/${receiptId}/transfer-chain` || path === `stock-movements/${transferId}/transfer-chain`) {
      const transferred = path.includes(transferId);
      return route.fulfill({ json: { sourceMovementId: transferred ? transferId : receiptId,
        originalReceiptId: receiptId, originalLocation: 'Hauptlager', sourceLocation: transferred ? 'Aussenlager' : 'Hauptlager',
        unitCost: '2', evidenceRemaining: transferred ? '1' : '6', transfers: transferred ? [
          { incomingId: intermediateTransferId, sourceId: receiptId, fromLocation: 'Hauptlager', toLocation: 'Zwischenlager', quantity: '4', occurredAt: '2026-10-21T09:30:00Z' },
          { incomingId: transferId, sourceId: intermediateTransferId, fromLocation: 'Zwischenlager', toLocation: 'Aussenlager', quantity: '2', occurredAt: '2026-10-21T10:00:00Z' }
        ] : [] } });
    }
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
  await page.getByLabel('Inventurbeleg', { exact: true }).fill('COUNT-2026-001');
  await page.getByLabel('Bewertung je Einheit (€)').fill('2');
  await page.getByLabel('Begründung / Belegreferenz').fill('Signed count evidence');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect(page.getByText('Bestandsbewegung mit Buchungsbeleg erfasst.', { exact: false })).toBeVisible();
  expect(f.requests[0].path).toBe(`materials/${materialId}/inventory-count`);
  expect(f.requests[0].body).toMatchObject({ expectedOnHand: '10', countedOnHand: '9', location: 'Hauptlager', evidenceReference: 'COUNT-2026-001' });
  expect(f.requests[0].headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/i);
  await page.getByRole('tab', { name: 'Lagerumbuchung' }).click();
  await page.getByLabel('Ziellagerort').fill('Aussenlager');
  await page.getByLabel('Menge', { exact: true }).fill('2');
  await page.getByLabel('Bewertung je Einheit (€)').fill('2');
  await page.getByLabel('Begründung / Belegreferenz').fill('Relocation record');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect.poll(() => f.requests.length).toBe(2);
  expect(f.requests[1].body).toMatchObject({ fromLocation: 'Hauptlager', toLocation: 'Aussenlager', quantity: '2', expectedFromOnHand: '9' });
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

test('changing a warehouse during a pending request enables refresh and discards the old response', async ({ page }) => {
  const f = await fixture(page);
  let release!: () => void, started!: () => void;
  const blocked = new Promise<void>(resolve => { release = resolve; });
  const requested = new Promise<void>(resolve => { started = resolve; });
  await page.route(`**/api/v1/materials/${materialId}/stock?**`, async route => {
    if (new URL(route.request().url()).searchParams.get('location') === 'Hauptlager') {
      started(); await blocked;
      return route.fulfill({ json: { onHand: '999', reserved: '0', available: '999' } });
    }
    return route.fulfill({ json: { onHand: '20', reserved: '0', available: '20' } });
  });
  await page.goto('/inventory-control');
  const select = page.getByLabel('Material auswählen');
  await expect(select.locator('option')).toHaveCount(2);
  await select.selectOption(materialId);
  await requested;
  await page.getByLabel('Lagerort (exakter Name)').fill('Aussenlager');
  const refresh = page.getByRole('button', { name: 'Bestand & Bewegungen laden' });
  await expect(refresh).toBeEnabled();
  await refresh.click();
  const onHand = page.locator('dl div').filter({ has: page.getByText('Gebucht', { exact: true }) }).locator('dd');
  await expect(onHand).toHaveText('20');
  release();
  await expect(refresh).toBeEnabled();
  await expect(onHand).toHaveText('20');
  expect(f.errors).toEqual([]);
});

test('a rejected stock command retains the error and entered evidence while refreshing the ledger', async ({ page }) => {
  const f = await fixture(page);
  await page.route(`**/api/v1/materials/${materialId}/inventory-count`, route => route.fulfill({
    status: 400, json: { code: 'stale_stock_count', detail: 'Der Bestand wurde inzwischen geändert. Bitte erneut prüfen.' }
  }));
  await page.goto('/inventory-control');
  const select = page.getByLabel('Material auswählen');
  await expect(select.locator('option')).toHaveCount(2);
  await select.selectOption(materialId);
  await page.getByLabel('Tatsächlich gezählter Bestand').fill('9');
  await page.getByLabel('Inventurbeleg', { exact: true }).fill('COUNT-2026-002');
  await page.getByLabel('Bewertung je Einheit (€)').fill('2');
  await page.getByLabel('Begründung / Belegreferenz').fill('Physical count witness');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect(page.getByRole('button', { name: 'Bestand & Bewegungen laden' })).toBeEnabled();
  await expect(page.getByRole('alert')).toContainText('Der Bestand wurde inzwischen geändert.');
  await expect(page.getByLabel('Inventurbeleg', { exact: true })).toHaveValue('COUNT-2026-002');
  expect(f.requests).toEqual([]);
  expect(f.errors).toEqual([]);
});

test('supplier return can select a matching direct transfer into another warehouse', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/inventory-control');
  const select = page.getByLabel('Material auswählen');
  await expect(select.locator('option')).toHaveCount(2);
  await select.selectOption(materialId);
  await page.getByRole('tab', { name: 'Lieferantenrückgabe' }).click();
  const transfers = page.getByLabel('Rückgabe nach Lagerumbuchung');
  await expect(transfers.locator('option')).toHaveCount(2);
  await page.getByLabel('Ursprüngliche gebuchte Bewegung').selectOption(receiptId);
  await transfers.selectOption(transferId);
  await page.getByLabel('Menge', { exact: true }).fill('1');
  await page.getByLabel('Begründung / Belegreferenz').fill('Supplier accepted the transferred stock');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect(page.getByText('Bestandsbewegung mit Buchungsbeleg erfasst.', { exact: false })).toBeVisible();
  expect(f.requests).toHaveLength(1);
  expect(f.requests[0].body).toMatchObject({ quantity: '1', returnTransferId: transferId });
  expect(f.requests[0].path).toBe(`stock-movements/${receiptId}/supplier-return`);
  expect(f.errors).toEqual([]);
});

test('a linked transfer shows receipt capacity and preserves its inherited unit cost', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/inventory-control');
  await page.getByLabel('Material auswählen').selectOption(materialId);
  await page.getByRole('tab', { name: 'Lagerumbuchung' }).click();
  await page.getByLabel('Herkunftsbeleg der Umbuchung').selectOption(receiptId);
  const chain = page.getByRole('region', { name: 'Herkunftskette' });
  await expect(chain).toContainText('Am ausgewählten Beleg noch nicht zugeordnet: 6');
  await expect(page.getByLabel('Bewertung je Einheit (€)')).toHaveValue('2');
  await expect(page.getByLabel('Bewertung je Einheit (€)')).toHaveAttribute('readonly', '');
  await page.getByLabel('Ziellagerort').fill('Weiteres Lager');
  await page.getByLabel('Menge', { exact: true }).fill('2');
  await page.getByLabel('Begründung / Belegreferenz').fill('Quelle und Umbuchungsbeleg geprüft');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect.poll(() => f.requests.length).toBe(1);
  expect(f.requests[0].body).toMatchObject({ transferSourceId: receiptId, unitCost: '2', quantity: '2', expectedFromOnHand: '10' });
  expect(f.errors).toEqual([]);
});

test('a pending provenance check prevents competing material and stock refreshes and then unlocks them', async ({ page }) => {
  const f = await fixture(page);
  let release!: () => void, started!: () => void;
  const blocked = new Promise<void>(resolve => { release = resolve; });
  const requested = new Promise<void>(resolve => { started = resolve; });
  let materialReads = 0, stockReads = 0;
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/materials') materialReads++;
    if (path === `/api/v1/materials/${materialId}/stock`) stockReads++;
  });
  await page.route(`**/api/v1/stock-movements/${receiptId}/transfer-chain`, async route => {
    started(); await blocked;
    return route.fulfill({ json: { sourceMovementId: receiptId, originalReceiptId: receiptId,
      originalLocation: 'Hauptlager', sourceLocation: 'Hauptlager', unitCost: '2', evidenceRemaining: '6', transfers: [] } });
  });
  await page.goto('/inventory-control');
  await page.getByLabel('Material auswählen').selectOption(materialId);
  await page.getByRole('tab', { name: 'Lagerumbuchung' }).click();
  await page.getByLabel('Herkunftsbeleg der Umbuchung').selectOption(receiptId);
  await requested;
  const search = page.getByRole('button', { name: 'Materialliste aktualisieren' });
  const refresh = page.getByRole('button', { name: 'Bestand & Bewegungen laden' });
  await expect(search).toBeDisabled();
  await expect(refresh).toBeDisabled();
  await page.getByLabel('Material suchen').press('Enter');
  release();
  await expect(page.getByRole('region', { name: 'Herkunftskette' })).toContainText('noch nicht zugeordnet: 6');
  await expect(search).toBeEnabled();
  await expect(refresh).toBeEnabled();
  expect(materialReads).toBe(1);
  expect(stockReads).toBe(1);
  await page.route(`**/api/v1/materials/${materialId}/stock?**`, route => route.fulfill({
    status: 503, json: { detail: 'Der Lagerstand ist vorübergehend nicht abrufbar.' }
  }));
  await refresh.click();
  await expect(page.getByRole('alert')).toContainText('Der Lagerstand ist vorübergehend nicht abrufbar.');
  await expect(refresh).toBeEnabled();
  await expect(search).toBeEnabled();
  await expect(page.getByText('Herkunftskette wird geprüft …', { exact: true })).toHaveCount(0);
  expect(f.requests).toEqual([]);
  expect(f.errors).toEqual([]);
});

test('a rejected linked transfer preserves its error and evidence after refreshing the provenance', async ({ page }) => {
  const f = await fixture(page);
  let chainReads = 0;
  await page.route(`**/api/v1/stock-movements/${receiptId}/transfer-chain`, route => {
    chainReads++;
    return route.fulfill({ json: { sourceMovementId: receiptId, originalReceiptId: receiptId,
      originalLocation: 'Hauptlager', sourceLocation: 'Hauptlager', unitCost: '2',
      evidenceRemaining: chainReads === 1 ? '6' : '4', transfers: [] } });
  });
  await page.route(`**/api/v1/materials/${materialId}/transfer`, route => route.fulfill({
    status: 400, json: { code: 'stale_stock_count', detail: 'Der Bestand wurde inzwischen geändert. Bitte erneut prüfen.' }
  }));
  await page.goto('/inventory-control');
  await page.getByLabel('Material auswählen').selectOption(materialId);
  await page.getByRole('tab', { name: 'Lagerumbuchung' }).click();
  await page.getByLabel('Herkunftsbeleg der Umbuchung').selectOption(receiptId);
  await expect(page.getByRole('region', { name: 'Herkunftskette' })).toContainText('noch nicht zugeordnet: 6');
  await page.getByLabel('Ziellagerort').fill('Weiteres Lager');
  await page.getByLabel('Menge', { exact: true }).fill('2');
  await page.getByLabel('Begründung / Belegreferenz').fill('Geprüfter Umbuchungsbeleg bleibt erhalten');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect(page.getByRole('region', { name: 'Herkunftskette' })).toContainText('noch nicht zugeordnet: 4');
  await expect(page.getByRole('button', { name: 'Bestand & Bewegungen laden' })).toBeEnabled();
  await expect(page.getByRole('alert')).toContainText('Der Bestand wurde inzwischen geändert.');
  await expect(page.getByLabel('Herkunftsbeleg der Umbuchung')).toHaveValue(receiptId);
  await expect(page.getByLabel('Ziellagerort')).toHaveValue('Weiteres Lager');
  await expect(page.getByLabel('Menge', { exact: true })).toHaveValue('2');
  await expect(page.getByLabel('Begründung / Belegreferenz')).toHaveValue('Geprüfter Umbuchungsbeleg bleibt erhalten');
  expect(chainReads).toBe(2);
  expect(f.requests).toEqual([]);
  expect(f.errors).toEqual([]);
});

test('a supplier return shows every warehouse hop and sends the final transfer with the original receipt', async ({ page }) => {
  const f = await fixture(page, 'ManagingDirector', true);
  await page.goto('/inventory-control');
  await page.getByLabel('Material auswählen').selectOption(materialId);
  await page.getByRole('tab', { name: 'Lieferantenrückgabe' }).click();
  await page.getByLabel('Ursprüngliche gebuchte Bewegung').selectOption(receiptId);
  await page.getByLabel('Rückgabe nach Lagerumbuchung').selectOption(transferId);
  const chain = page.getByRole('region', { name: 'Herkunftskette' });
  await expect(chain.getByRole('listitem')).toHaveCount(2);
  await expect(chain).toContainText('Hauptlager → Zwischenlager');
  await expect(chain).toContainText('Zwischenlager → Aussenlager');
  await page.getByLabel('Menge', { exact: true }).fill('1');
  await page.getByLabel('Begründung / Belegreferenz').fill('Mehrstufige Herkunft vor Rückgabe geprüft');
  await page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' }).click();
  await expect.poll(() => f.requests.length).toBe(1);
  expect(f.requests[0].path).toBe(`stock-movements/${receiptId}/supplier-return`);
  expect(f.requests[0].body).toMatchObject({ quantity: '1', returnTransferId: transferId });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(f.errors).toEqual([]);
});

test('a warehouse chain belonging to another receipt cannot be submitted', async ({ page }) => {
  const f = await fixture(page, 'ManagingDirector', true);
  await page.route(`**/api/v1/stock-movements/${transferId}/transfer-chain`, route => route.fulfill({ json: {
    sourceMovementId: transferId, originalReceiptId: '88888888-8888-4888-8888-888888888888', originalLocation: 'Hauptlager',
    sourceLocation: 'Aussenlager', unitCost: '2', evidenceRemaining: '2', transfers: [
      { incomingId: transferId, sourceId: '88888888-8888-4888-8888-888888888888', fromLocation: 'Hauptlager',
        toLocation: 'Aussenlager', quantity: '2', occurredAt: '2026-10-21T10:00:00Z' }
    ]
  } }));
  await page.goto('/inventory-control');
  await page.getByLabel('Material auswählen').selectOption(materialId);
  await page.getByRole('tab', { name: 'Lieferantenrückgabe' }).click();
  await page.getByLabel('Ursprüngliche gebuchte Bewegung').selectOption(receiptId);
  await page.getByLabel('Rückgabe nach Lagerumbuchung').selectOption(transferId);
  await expect(page.getByRole('alert')).toContainText('anderen Originalwareneingang');
  await page.getByLabel('Menge', { exact: true }).fill('1');
  await page.getByLabel('Begründung / Belegreferenz').fill('Darf keine fremde Quelle belasten');
  await expect(page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' })).toBeDisabled();
  expect(f.requests).toEqual([]);
  expect(f.errors).toEqual([]);
});

test('late provenance responses cannot restore a chain after changing the warehouse', async ({ page }) => {
  const f = await fixture(page);
  let release!: () => void, started!: () => void;
  const blocked = new Promise<void>(resolve => { release = resolve; });
  const requested = new Promise<void>(resolve => { started = resolve; });
  await page.route(`**/api/v1/stock-movements/${receiptId}/transfer-chain`, async route => {
    started(); await blocked;
    return route.fulfill({ json: { sourceMovementId: receiptId, originalReceiptId: receiptId,
      originalLocation: 'Hauptlager', sourceLocation: 'Hauptlager', unitCost: '2', evidenceRemaining: '999', transfers: [] } });
  });
  await page.goto('/inventory-control');
  await page.getByLabel('Material auswählen').selectOption(materialId);
  await page.getByRole('tab', { name: 'Lagerumbuchung' }).click();
  await page.getByLabel('Herkunftsbeleg der Umbuchung').selectOption(receiptId);
  await requested;
  await page.getByLabel('Lagerort (exakter Name)').fill('Aussenlager');
  await page.getByRole('button', { name: 'Bestand & Bewegungen laden' }).click();
  release();
  await expect(page.getByRole('button', { name: 'Bestand & Bewegungen laden' })).toBeEnabled();
  await expect(page.getByRole('region', { name: 'Herkunftskette' })).toHaveCount(0);
  await expect(page.getByLabel('Herkunftsbeleg der Umbuchung')).toHaveValue('');
  expect(f.requests).toEqual([]);
  expect(f.errors).toEqual([]);
});

test('an inconsistent chain response blocks the linked transfer without dropping the users evidence', async ({ page }) => {
  const f = await fixture(page);
  await page.route(`**/api/v1/stock-movements/${receiptId}/transfer-chain`, route => route.fulfill({ json: {
    sourceMovementId: receiptId, originalReceiptId: receiptId, originalLocation: 'Hauptlager',
    sourceLocation: 'Hauptlager', unitCost: '999', evidenceRemaining: '6', transfers: []
  } }));
  await page.goto('/inventory-control');
  await page.getByLabel('Material auswählen').selectOption(materialId);
  await page.getByRole('tab', { name: 'Lagerumbuchung' }).click();
  await page.getByLabel('Ziellagerort').fill('Weiteres Lager');
  await page.getByLabel('Menge', { exact: true }).fill('1');
  await page.getByLabel('Begründung / Belegreferenz').fill('Vorliegender Umlagerungsbeleg');
  await page.getByLabel('Herkunftsbeleg der Umbuchung').selectOption(receiptId);
  await expect(page.getByRole('alert')).toContainText('Herkunftskette konnte nicht eindeutig geprüft werden');
  await expect(page.getByRole('button', { name: 'Beleggebundene Bewegung buchen' })).toBeDisabled();
  await expect(page.getByLabel('Begründung / Belegreferenz')).toHaveValue('Vorliegender Umlagerungsbeleg');
  expect(f.requests).toEqual([]);
  expect(f.errors).toEqual([]);
});
