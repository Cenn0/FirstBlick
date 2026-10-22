import { expect, Page, test } from '@playwright/test';
import type { VehicleCostItem, VehicleTripItem, VehicleView } from '../../src/app/core/vehicle-ledger.models';

const vehicleId = '72000000-0000-4000-8000-000000000001', actor = '72000000-0000-4000-8000-000000000003', tripId = '72000000-0000-4000-8000-000000000004';
const costId = '72000000-0000-4000-8000-000000000005', reverseId = '72000000-0000-4000-8000-000000000006', driverId = '72000000-0000-4000-8000-000000000007', projectId = '72000000-0000-4000-8000-000000000008';
const now = '2026-10-22T01:00:00Z', today = '2026-10-22';
interface Options { reader?: boolean; existing?: boolean; malformed?: boolean; malformedChange?: boolean; conflict?: boolean; delayRead?: boolean; delayWrite?: boolean; failedRefresh?: boolean }
async function fixture(page: Page, options: Options = {}) {
  let vehicle: VehicleView = { id: vehicleId, tenantId: 'tenant-a', revision: 2, name: 'Transporter Nord', inventoryNumber: 'FB-03', registration: 'TEST-03', kind: 'Vehicle', archived: false, available: true };
  const snapshot = () => ({ id: vehicleId, name: vehicle.name, inventoryNumber: vehicle.inventoryNumber, registration: 'TEST-03', kind: 'Vehicle' as const });
  const common = () => ({ id: tripId, tenantId: 'tenant-a', revision: 1, createdAt: now, updatedAt: now, archived: false as const, vehicleId, vehicleSnapshot: snapshot(), recordedBy: actor, recordedAt: now, digest: 'a'.repeat(64) });
  const makeTrip = (body: any): VehicleTripItem => ({ entry: { ...common(), ...body, driverName: 'Fahrer Nord', projectTitle: body.projectId ? 'Dachprojekt Nord' : null }, distanceKm: String(Number(body.odometerEndKm) - Number(body.odometerStartKm)), reversal: null });
  const makeCost = (body: any): VehicleCostItem => ({ entry: { ...common(), ...body, id: costId, projectTitle: body.projectId ? 'Dachprojekt Nord' : null }, reversal: null });
  let trips: VehicleTripItem[] = options.existing ? [makeTrip({ driverEmployeeId: driverId, projectId: null, start: '2026-10-21T06:00:00Z', end: '2026-10-21T07:00:00Z', odometerStartKm: '1000', odometerEndKm: '1020', purpose: 'Material abholen', origin: 'Betriebshof', destination: 'Lieferant', notes: '' })] : [];
  let costs: VehicleCostItem[] = options.existing ? [makeCost({ projectId: null, spentOn: '2026-10-21', category: 'Fuel', amount: '40.00', currency: 'EUR', reference: 'Tankbeleg-42', notes: '' })] : [];
  const calls: { path: string; body: any; headers: Record<string, string> }[] = []; let mutated = false;
  let releaseRead!: () => void, releaseWrite!: () => void;
  const readGate = new Promise<void>(resolve => { releaseRead = resolve; }), writeGate = new Promise<void>(resolve => { releaseWrite = resolve; });
  function ledger(month: string, tripPage: number, costPage: number) {
    const tripRows = month === '2026-10' ? trips : [], costRows = month === '2026-10' ? costs : [];
    const activeTrips = tripRows.filter(item => !item.reversal), activeCosts = costRows.filter(item => !item.reversal), distance = activeTrips.reduce((sum, item) => sum + Number(item.distanceKm), 0), amount = activeCosts.reduce((sum, item) => sum + Number(item.entry.amount), 0);
    return { vehicle: { ...vehicle }, month, businessDate: today, timeZone: 'Europe/Berlin',
      summary: { tripCount: activeTrips.length, reversedTripCount: tripRows.length - activeTrips.length, distanceKm: String(distance), costCount: activeCosts.length, reversedCostCount: costRows.length - activeCosts.length, costAmount: amount.toFixed(2), costPerKm: distance ? (amount / distance).toFixed(4) : null },
      trips: { items: structuredClone(tripRows), total: tripRows.length, page: tripPage, pageSize: 20 }, costs: { items: structuredClone(costRows), total: costRows.length, page: costPage, pageSize: 20 } };
  }
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.slice('/api/v1/'.length), other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: actor, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role: options.reader ? 'Accounting' : 'ManagingDirector', modules: ['core', 'purchasing', 'field'], capabilities: options.reader ? ['vehicle-trips.read', 'vehicle-costs.read', 'equipment.read', 'projects.read'] : ['*'] })) } });
    if (path === 'vehicle-ledger/vehicles') return route.fulfill({ json: { items: other ? [] : [vehicle], total: other ? 0 : 1, page: 1, pageSize: 25, businessDate: today, timeZone: 'Europe/Berlin' } });
    if (path === `equipment/${vehicleId}/vehicle-ledger`) {
      const value = ledger(url.searchParams.get('month') || '2026-10', Number(url.searchParams.get('tripPage') || 1), Number(url.searchParams.get('costPage') || 1));
      if (options.delayRead) await readGate;
      if (mutated && options.failedRefresh) return route.fulfill({ status: 503, json: { detail: 'Lesedienst vorübergehend nicht erreichbar.' } });
      return route.fulfill({ json: options.malformed ? { ...value, summary: { ...value.summary, distanceKm: '1000' } } : value });
    }
    if (path === 'employees') return route.fulfill({ json: { items: [{ id: driverId, revision: 1, name: 'Fahrer Nord', active: true }], total: 1, page: 1, pageSize: 25 } });
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: projectId, revision: 1, name: 'Dachprojekt Nord', number: 'P-01' }], total: 1, page: 1, pageSize: 25 } });
    if (path.startsWith(`equipment/${vehicleId}/vehicle-`) && request.method() === 'POST') {
      const body = request.postDataJSON(); calls.push({ path, body, headers: request.headers() });
      if (options.conflict) return route.fulfill({ status: 409, json: { code: 'revision_conflict' } });
      const trip = path.includes('/vehicle-trips'), reverse = path.endsWith('/reverse'); let item: VehicleTripItem | VehicleCostItem;
      if (reverse) {
        const original = trip ? trips[0] : costs[0]; item = { ...original, reversal: { id: trip ? reverseId : '72000000-0000-4000-8000-000000000009', tenantId: 'tenant-a', revision: 1, createdAt: now, updatedAt: now, archived: false,
          vehicleId, sourceKind: trip ? 'vehicle-trips' : 'vehicle-costs', sourceId: original.entry.id, sourceDigest: body.sourceDigest, reason: body.reason, recordedBy: actor, recordedAt: now, digest: 'b'.repeat(64) } };
      } else item = trip ? makeTrip(body) : makeCost(body);
      if (trip) trips = [item as VehicleTripItem]; else costs = [item as VehicleCostItem];
      vehicle = { ...vehicle, revision: vehicle.revision + 1 }; mutated = true;
      const value = { vehicle: { ...vehicle }, [trip ? 'trip' : 'cost']: structuredClone(item) };
      if (options.delayWrite) await writeGate;
      return route.fulfill({ json: options.malformedChange ? { ...value, vehicle: { ...vehicle, id: driverId } } : value });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, releaseRead, releaseWrite };
}
async function open(page: Page) { await page.goto('/vehicle-ledger'); await page.getByRole('button', { name: /Transporter Nord FB-03/ }).click(); await expect(page.getByRole('heading', { name: 'Transporter Nord', exact: true })).toBeVisible(); }
async function fillTrip(page: Page) {
  await page.getByRole('button', { name: 'Fahrt erfassen', exact: true }).click(); await page.getByRole('button', { name: 'Fahrer suchen', exact: true }).click(); await page.getByRole('button', { name: 'Fahrer Nord', exact: true }).click();
  await page.getByLabel('Abfahrt am', { exact: false }).fill('2026-10-21T08:00'); await page.getByLabel('Ankunft am', { exact: false }).fill('2026-10-21T09:00');
  await page.getByLabel('Kilometerstand bei Abfahrt', { exact: false }).fill('1000'); await page.getByLabel('Kilometerstand bei Ankunft', { exact: false }).fill('1020');
  await page.getByLabel('Startort', { exact: false }).fill('Betriebshof'); await page.getByLabel('Zielort', { exact: false }).fill('Lieferant'); await page.getByLabel('Fahrtzweck', { exact: false }).fill('Material abholen');
  await page.getByLabel('Fahrt und Kilometerstände geprüft; der Originaleintrag bleibt erhalten.', { exact: true }).check();
}

test('records trips and costs through real selections, then reverses originals without losing their values', async ({ page }) => {
  const api = await fixture(page); await open(page); await fillTrip(page);
  await page.getByRole('button', { name: 'Projekt suchen', exact: true }).click(); await page.getByRole('button', { name: 'Dachprojekt Nord · P-01', exact: true }).click();
  await page.getByRole('button', { name: 'Fahrt speichern', exact: true }).click(); await expect(page.locator('.ledger-record')).toContainText('Material abholen'); await expect(page.locator('.vehicle-summary')).toContainText('20 km');
  expect(api.calls[0].body).toMatchObject({ driverEmployeeId: driverId, projectId, start: '2026-10-21T06:00:00.000Z', end: '2026-10-21T07:00:00.000Z', odometerEndKm: '1020' }); expect(api.calls[0].headers['if-match']).toBe('"2"'); expect(api.calls[0].headers['idempotency-key']).toBeTruthy();
  await page.getByRole('button', { name: 'Fahrzeugkosten', exact: true }).click(); await page.getByRole('button', { name: 'Kosten erfassen', exact: true }).click();
  await page.getByLabel('Belegdatum', { exact: false }).fill('2026-10-21'); await page.getByLabel('Kostenbetrag in EUR', { exact: false }).fill('40,00'); await page.getByLabel('Belegreferenz', { exact: false }).fill('Tankbeleg-42');
  await page.getByLabel('Kostenbetrag und Beleg geprüft; der Originaleintrag bleibt erhalten.', { exact: true }).check(); await page.getByRole('button', { name: 'Kosten speichern', exact: true }).click();
  await expect(page.locator('.ledger-record')).toContainText('Tankbeleg-42'); await expect(page.locator('.vehicle-summary')).toContainText('2,00 €/km'); expect(api.calls[1].body.amount).toBe('40.00'); expect(api.calls[1].headers['if-match']).toBe('"3"');
  await page.getByRole('button', { name: 'Tankbeleg-42 stornieren', exact: true }).click(); await page.getByLabel('Grund der Kostenstornierung', { exact: false }).fill('Doppelt erfasst.'); await page.getByRole('button', { name: 'Kosten mit Grund stornieren', exact: true }).click();
  await expect(page.locator('.ledger-record')).toContainText('Doppelt erfasst.'); await expect(page.locator('.ledger-record')).toContainText('40,00 €'); await expect(page.locator('.vehicle-summary')).toContainText('0,00 €');
  await page.getByRole('button', { name: 'Fahrten', exact: true }).click(); await page.getByRole('button', { name: 'Material abholen stornieren', exact: true }).click(); await page.getByLabel('Grund der Fahrtstornierung', { exact: false }).fill('Fahrzeug verwechselt.'); await page.getByRole('button', { name: 'Fahrt mit Grund stornieren', exact: true }).click();
  await expect(page.locator('.ledger-record')).toContainText('Fahrzeug verwechselt.'); await expect(page.locator('.ledger-record')).toContainText('1.020 km'); await expect(page.locator('.vehicle-summary')).toContainText('Ohne Fahrstrecke');
  expect(api.calls[3].body.sourceDigest).toBe('a'.repeat(64)); expect(api.calls[3].headers['if-match']).toBe('"5"');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('accounting readers see originals and totals without write controls', async ({ page }) => {
  const api = await fixture(page, { reader: true, existing: true }); await open(page); await expect(page.locator('.ledger-record')).toContainText('Material abholen');
  await expect(page.getByRole('button', { name: 'Fahrt erfassen', exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: 'Material abholen stornieren', exact: true })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('contradictory monthly summaries are rejected before displaying the ledger', async ({ page }) => {
  await fixture(page, { existing: true, malformed: true }); await page.goto(`/vehicle-ledger?equipmentId=${vehicleId}`);
  await expect(page.getByRole('alert').filter({ hasText: 'Kilometer-Monatssumme' })).toBeVisible(); await expect(page.locator('.ledger-record')).toHaveCount(0);
});

test('a revision conflict retains the filled trip and disables further commands until reload', async ({ page }) => {
  const api = await fixture(page, { conflict: true }); await open(page); await fillTrip(page); await page.getByRole('button', { name: 'Fahrt speichern', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'zwischenzeitlich geändert' })).toBeVisible(); await expect(page.getByLabel('Fahrtzweck', { exact: false })).toHaveValue('Material abholen'); await expect(page.getByRole('button', { name: 'Fahrt speichern', exact: true })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});

test('wrong-vehicle command replies do not appear as successful saved trips', async ({ page }) => {
  await fixture(page, { malformedChange: true }); await open(page); await fillTrip(page); await page.getByRole('button', { name: 'Fahrt speichern', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'nicht eindeutig' })).toBeVisible(); await expect(page.locator('.ledger-record')).toHaveCount(0);
});

test('late writes cannot display the previous business vehicle ledger', async ({ page }) => {
  const api = await fixture(page, { delayWrite: true }); await open(page); await fillTrip(page); await page.getByRole('button', { name: 'Fahrt speichern', exact: true }).click();
  await expect.poll(() => api.calls.length).toBe(1); await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseWrite();
  await expect(page.locator('.ledger-record')).toHaveCount(0); await expect(page.getByRole('heading', { name: 'Transporter Nord', exact: true })).toHaveCount(0);
});

test('late vehicle reads cannot reappear after switching the business', async ({ page }) => {
  const api = await fixture(page, { existing: true, delayRead: true }); await page.goto(`/vehicle-ledger?equipmentId=${vehicleId}`);
  await expect(page.getByText('Fahrzeugakte wird geladen …', { exact: true })).toBeVisible(); await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseRead();
  await expect(page.locator('.ledger-record')).toHaveCount(0); await expect(page.getByRole('heading', { name: 'Transporter Nord', exact: true })).toHaveCount(0);
});

test('changing month binds the new snapshot and clears previous totals and records', async ({ page }) => {
  await fixture(page, { existing: true }); await open(page); await page.getByLabel('Monat der Fahrzeugakte', { exact: true }).fill('2026-09'); await page.getByRole('button', { name: 'Monat anzeigen', exact: true }).click();
  await expect(page.locator('.ledger-record')).toHaveCount(0); await expect(page.locator('.vehicle-summary')).toContainText('0 km'); await expect(page.getByText('Für diesen Monat sind keine Fahrten dokumentiert.', { exact: true })).toBeVisible();
});

test('a failed refresh preserves the confirmed booking outcome while blocking new writes', async ({ page }) => {
  const api = await fixture(page, { failedRefresh: true }); await open(page); await fillTrip(page); await page.getByRole('button', { name: 'Fahrt speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Fahrt dokumentiert.' })).toBeVisible(); await expect(page.getByRole('alert').filter({ hasText: 'Lesedienst' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Fahrt erfassen', exact: true })).toHaveCount(0); expect(api.calls).toHaveLength(1);
});
