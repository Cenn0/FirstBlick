import { expect, Page, test } from '@playwright/test';
import type { EquipmentInspection, EquipmentRecord } from '../../src/app/core/equipment-workflow.models';

const equipmentId = '20000000-0000-4000-8000-000000000001';
const inspectionId = '20000000-0000-4000-8000-000000000002';
const userId = '20000000-0000-4000-8000-000000000003';
const today = '2026-10-21';
interface Options { role?: string; planned?: boolean; completed?: boolean; unavailable?: boolean; nullOptionalTexts?: boolean; stale?: boolean; malformedHistory?: boolean; malformedMutation?: boolean; differentEvidence?: boolean; delayDetail?: boolean; delayCompletion?: boolean; failedRefresh?: boolean }

async function fixture(page: Page, options: Options = {}) {
  const calls: { path: string; body: any; headers: Record<string, string> }[] = [];
  let role = options.role || 'ManagingDirector'; let allowed = true;
  let equipment: EquipmentRecord = { id: equipmentId, revision: 2, name: 'Transporter Nord', inventoryNumber: 'FB-FZ-03',
    kind: 'Vehicle', registration: options.nullOptionalTexts ? null : 'TEST-FZ-03', location: options.nullOptionalTexts ? null : 'Betriebshof', available: !options.unavailable,
    nextInspectionOn: '2026-10-21', inspectionBlocked: false, pendingInspectionId: options.planned ? inspectionId : null, lastInspectionId: null };
  const pending = (): EquipmentInspection => ({ id: inspectionId, revision: 1, equipmentId, kind: 'Inspection', title: 'Jahresprüfung Transporter',
    scheduledOn: today, notes: 'Prüfbericht mitbringen', status: 'Scheduled', scopeDigest: 'a'.repeat(64), plannedBy: userId,
    equipmentSnapshot: { id: equipmentId, name: equipment.name, inventoryNumber: equipment.inventoryNumber, kind: 'Vehicle', registration: equipment.registration },
    completion: null, cancellation: null });
  let inspections: EquipmentInspection[] = options.planned ? [pending()] : [];
  if (options.completed) {
    equipment = { ...equipment, nextInspectionOn: '2027-10-21', lastInspectionId: inspectionId, lastInspectionOn: today };
    inspections = [{ ...pending(), status: 'Completed', completion: { performedOn: today, result: 'Passed', inspector: 'Prüfstelle Nord',
      evidenceReference: 'Prüfbericht-2026-014', notes: '', nextInspectionOn: '2027-10-21', makeAvailable: false,
      recordedBy: userId, recordedAt: `${today}T10:00:00Z`, digest: 'd'.repeat(64) } }];
  }
  let released = !options.delayDetail; let releaseDetail: (() => void) | undefined; const detailGate = new Promise<void>(resolve => { releaseDetail = resolve; });
  let completionReleased = !options.delayCompletion; let releaseCompletion: (() => void) | undefined; const completionGate = new Promise<void>(resolve => { releaseCompletion = resolve; });
  let mutated = false;
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.slice('/api/v1/'.length), other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: userId, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role, modules: ['core', 'purchasing', 'field'], capabilities: allowed ? ['*'] : [] })) } });
    if (['POST', 'PUT'].includes(request.method())) calls.push({ path, body: request.postDataJSON(), headers: request.headers() });
    if (path === 'equipment') return route.fulfill({ json: { items: other ? [] : [equipment], total: other ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `equipment/${equipmentId}`) {
      if (request.method() === 'PUT') equipment = { ...equipment, ...request.postDataJSON(), revision: equipment.revision + 1 };
      return route.fulfill({ json: equipment });
    }
    if (path === 'equipment/inspection-worklist') {
      const dueStatus = !equipment.nextInspectionOn ? 'Unscheduled' : equipment.nextInspectionOn < today ? 'Overdue' : equipment.nextInspectionOn === today ? 'Due' : 'Current';
      const filter = url.searchParams.get('status');
      const visible = !other && (filter === 'all' || filter === 'due' && ['Due', 'Overdue'].includes(dueStatus)
        || filter === 'blocked' && equipment.inspectionBlocked
        || filter === 'planned' && !!equipment.pendingInspectionId);
      return route.fulfill({ json: { businessDate: today, timeZone: 'Europe/Berlin', items: visible ? [{ ...equipment, dueStatus }] : [], total: visible ? 1 : 0, page: Number(url.searchParams.get('page') || 1), pageSize: 25 } });
    }
    if (path === `equipment/${equipmentId}/inspection-history`) {
      const data = { equipment: { ...equipment }, pendingInspection: inspections.find(x => x.id === equipment.pendingInspectionId) || null, items: inspections.map(x => structuredClone(x)),
        total: inspections.length, page: Number(url.searchParams.get('page') || 1), pageSize: 20, businessDate: today, timeZone: 'Europe/Berlin', affectedAssignmentCount: equipment.inspectionBlocked ? 2 : 0 };
      if (!released) await detailGate;
      if (mutated && options.failedRefresh) return route.fulfill({ status: 503, json: { detail: 'Lesedienst vorübergehend nicht erreichbar.' } });
      return route.fulfill({ json: options.malformedHistory ? { ...data, pendingInspection: { ...pending(), equipmentId: userId } } : data });
    }
    if (path === `equipment/${equipmentId}/inspection-plan`) {
      if (options.stale) return route.fulfill({ status: 409, json: { code: 'revision_conflict' } });
      const body = request.postDataJSON(), inspection = { ...pending(), ...body };
      inspections = [inspection, ...inspections]; equipment = { ...equipment, revision: equipment.revision + 1, pendingInspectionId: inspection.id }; mutated = true;
      return route.fulfill({ json: { equipment, inspection, affectedAssignmentCount: 0 } });
    }
    if (path === `equipment/${equipmentId}/inspection-complete`) {
      if (options.stale) return route.fulfill({ status: 409, json: { code: 'revision_conflict' } });
      const body = request.postDataJSON();
      const inspection: EquipmentInspection = { ...inspections[0], revision: inspections[0].revision + 1, status: 'Completed', completion: {
        performedOn: body.performedOn, result: body.result, inspector: body.inspector, evidenceReference: options.differentEvidence ? 'Anderer-Bericht' : body.evidenceReference,
        notes: body.notes, nextInspectionOn: body.nextInspectionOn, makeAvailable: body.makeAvailable, recordedBy: userId, recordedAt: `${today}T10:30:00Z`, digest: 'b'.repeat(64) } };
      inspections = [inspection]; equipment = { ...equipment, revision: equipment.revision + 1, pendingInspectionId: null, lastInspectionId: inspection.id,
        lastInspectionOn: body.performedOn, available: body.result === 'Failed' ? false : equipment.available || body.makeAvailable,
        nextInspectionOn: body.result === 'Passed' ? body.nextInspectionOn : equipment.nextInspectionOn, inspectionBlocked: body.result === 'Failed' }; mutated = true;
      const data = { equipment: { ...equipment }, inspection, affectedAssignmentCount: body.result === 'Failed' ? 2 : 0 };
      if (!completionReleased) await completionGate;
      return route.fulfill({ json: options.malformedMutation ? { ...data, equipment: { ...equipment, id: userId } } : data });
    }
    if (path === `equipment/${equipmentId}/inspection-cancel`) {
      const body = request.postDataJSON(), inspection: EquipmentInspection = { ...inspections[0], revision: inspections[0].revision + 1, status: 'Cancelled',
        cancellation: { reason: body.reason, cancelledBy: userId, cancelledAt: `${today}T10:00:00Z`, digest: 'c'.repeat(64) } };
      inspections = [inspection]; equipment = { ...equipment, revision: equipment.revision + 1, pendingInspectionId: null }; mutated = true;
      return route.fulfill({ json: { equipment, inspection, affectedAssignmentCount: 0 } });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, releaseDetail: () => { released = true; releaseDetail?.(); }, releaseCompletion: () => { completionReleased = true; releaseCompletion?.(); },
    revoke: () => { allowed = false; role = 'Worker'; } };
}

async function openEquipment(page: Page) {
  await page.goto('/equipment-inspections');
  await page.getByRole('button', { name: /Transporter Nord FB-FZ-03/ }).click();
  await expect(page.getByRole('heading', { name: 'Transporter Nord', exact: true })).toBeVisible();
}
async function completeFields(page: Page) {
  await page.getByLabel('Durchgeführt am', { exact: true }).fill(today);
  await page.getByLabel('Prüfer / ausführende Stelle', { exact: true }).fill('Prüfstelle Nord');
  await page.getByLabel('Prüfbericht / Belegreferenz', { exact: true }).fill('Prüfbericht-2026-014');
  await page.getByLabel('Nächster Prüftermin', { exact: true }).fill('2027-10-21');
  await page.getByLabel('Ich habe die Angaben mit dem genannten Beleg abgeglichen.', { exact: true }).check();
}

test('planning and completing a documented inspection preserves evidence and explicitly releases unavailable equipment', async ({ page }) => {
  const api = await fixture(page, { unavailable: true }); await openEquipment(page);
  await page.getByLabel('Bezeichnung des Vorgangs', { exact: true }).fill('Jahresprüfung Transporter');
  await page.getByLabel('Arbeitsumfang und Hinweise', { exact: true }).fill('Prüfbericht mitbringen');
  await page.getByRole('button', { name: 'Prüfvorgang planen', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Geplant: Jahresprüfung Transporter' })).toBeVisible();
  await completeFields(page); await page.getByLabel('Gerät ausdrücklich als verfügbar kennzeichnen', { exact: true }).check();
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Verfügbarkeit und nächster Prüftermin wurden übernommen' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Geplant: Jahresprüfung Transporter' })).toHaveCount(0);
  await page.locator('.history-record summary').click();
  await expect(page.getByText('Prüfbericht-2026-014', { exact: true })).toBeVisible();
  await expect(page.getByText('Prüfstelle Nord', { exact: true })).toBeVisible();
  expect(api.calls.map(x => x.path)).toEqual([`equipment/${equipmentId}/inspection-plan`, `equipment/${equipmentId}/inspection-complete`]);
  expect(api.calls[0].headers['if-match']).toBe('"2"');
  expect(api.calls[1].headers).toMatchObject({ 'if-match': '"3"', 'x-tenant-id': 'tenant-a', 'x-xsrf-token': 'csrf-test' });
  expect(api.calls[1].headers['idempotency-key']).toBeTruthy();
  expect(api.calls[1].body).toMatchObject({ inspectionId, inspectionRevision: 1, nextInspectionOn: '2027-10-21', makeAvailable: true, result: 'Passed' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('nullable optional equipment text keeps the inspection history readable and completable', async ({ page }) => {
  const api = await fixture(page, { planned: true, nullOptionalTexts: true }); await openEquipment(page);
  await expect(page.getByText('Nicht angegeben', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Geplant: Jahresprüfung Transporter' })).toBeVisible();
  await page.locator('.history-record summary').click();
  await expect(page.getByText('Gerät bei Planung: Transporter Nord · FB-FZ-03', { exact: true })).toBeVisible();
  await completeFields(page);
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Prüfergebnis gespeichert' })).toBeVisible();
  await expect(page.locator('.history-record summary')).toContainText('Abgeschlossen');
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(api.calls).toHaveLength(1);
  expect(api.calls[0].body).toMatchObject({ inspectionId, result: 'Passed', nextInspectionOn: '2027-10-21' });
});

test('a failed inspection requires findings, clears release inputs and exposes affected scheduled assignments', async ({ page }) => {
  const api = await fixture(page, { planned: true }); await openEquipment(page); await completeFields(page);
  await page.getByLabel('Gerät ausdrücklich als verfügbar kennzeichnen', { exact: true }).check();
  await page.getByLabel('Ergebnis', { exact: true }).selectOption('Failed');
  await expect(page.getByLabel('Nächster Prüftermin', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toBeDisabled();
  await page.getByLabel('Ergebnis und Feststellungen', { exact: true }).fill('Mangel an der Beleuchtung. Nachprüfung erforderlich.');
  await page.getByLabel('Ich habe die Angaben mit dem genannten Beleg abgeglichen.', { exact: true }).check();
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'für neue Einsätze gesperrt' })).toBeVisible();
  await expect(page.getByText('2 bereits geplante Einsätze betroffen', { exact: true })).toBeVisible();
  expect(api.calls[0].body).toMatchObject({ result: 'Failed', makeAvailable: false, nextInspectionOn: null });
});

test('passing without an explicit availability choice retains an existing manual unavailability', async ({ page }) => {
  const api = await fixture(page, { planned: true, unavailable: true }); await openEquipment(page); await completeFields(page);
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'bisherige Verfügbarkeit bleibt erhalten' })).toBeVisible();
  await expect(page.locator('.equipment-facts dd').filter({ hasText: 'Nicht verfügbar' })).toBeVisible();
  expect(api.calls[0].body.makeAvailable).toBe(false);
});

test('cancellation requires a reason and preserves an immutable history entry', async ({ page }) => {
  const api = await fixture(page, { planned: true }); await openEquipment(page);
  await page.getByText('Geplanten Vorgang absagen', { exact: true }).click();
  await expect(page.getByRole('button', { name: 'Vorgang mit Begründung absagen' })).toBeDisabled();
  await page.getByLabel('Grund der Absage', { exact: true }).fill('Termin wird mit anderer Prüfstelle vereinbart.');
  await page.getByRole('button', { name: 'Vorgang mit Begründung absagen' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'mit Begründung abgesagt' })).toBeVisible();
  await page.locator('.history-record summary').click();
  await expect(page.getByText('Absage: Termin wird mit anderer Prüfstelle vereinbart.', { exact: false })).toBeVisible();
  expect(api.calls[0].body).toEqual({ inspectionId, inspectionRevision: 1, reason: 'Termin wird mit anderer Prüfstelle vereinbart.' });
});

test('accounting can read the evidence but cannot plan, release or cancel equipment', async ({ page }) => {
  const api = await fixture(page, { role: 'Accounting', planned: true }); await openEquipment(page);
  await expect(page.getByRole('heading', { name: 'Geplant: Jahresprüfung Transporter' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toHaveCount(0);
  await expect(page.getByText('Geplanten Vorgang absagen', { exact: true })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('workers cannot load the inspection evidence workspace even with an overbroad mocked capability list', async ({ page }) => {
  const api = await fixture(page, { role: 'Worker', planned: true }); await page.goto('/equipment-inspections');
  await expect(page.getByRole('heading', { name: 'Zugriff eingeschränkt' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Transporter Nord/ })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('list filters select the requested due or planned worklist without creating records', async ({ page }) => {
  const api = await fixture(page); await page.goto('/equipment-inspections');
  await page.getByLabel('Anzeige', { exact: true }).selectOption('planned'); await page.getByRole('button', { name: 'Filter anwenden' }).click();
  await expect(page.getByText('Keine Geräte für diese Auswahl gefunden.', { exact: true })).toBeVisible();
  await page.getByLabel('Anzeige', { exact: true }).selectOption('due'); await page.getByRole('button', { name: 'Filter anwenden' }).click();
  await expect(page.getByRole('button', { name: /Transporter Nord FB-FZ-03/ })).toBeVisible(); expect(api.calls).toHaveLength(0);
});

test('a conflicting equipment revision retains draft evidence and requires an explicit reload', async ({ page }) => {
  const api = await fixture(page, { planned: true, stale: true }); await openEquipment(page); await completeFields(page);
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('alert')).toContainText('zwischenzeitlich geändert');
  await expect(page.getByLabel('Prüfbericht / Belegreferenz', { exact: true })).toHaveValue('Prüfbericht-2026-014');
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});

test('malformed cross-equipment history never exposes a completion command', async ({ page }) => {
  const api = await fixture(page, { planned: true, malformedHistory: true }); await page.goto('/equipment-inspections');
  await page.getByRole('button', { name: /Transporter Nord FB-FZ-03/ }).click();
  await expect(page.getByRole('alert')).toContainText('unpassende Nachweise');
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('a malformed command response cannot report a confirmed completion or offer a second write', async ({ page }) => {
  const api = await fixture(page, { planned: true, malformedMutation: true }); await openEquipment(page); await completeFields(page);
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('alert')).toContainText('Geräteakte passt nicht');
  await expect(page.getByRole('status').filter({ hasText: 'Prüfergebnis gespeichert' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});

test('a late device response after a business change cannot restore old evidence or targets', async ({ page }) => {
  const api = await fixture(page, { planned: true, delayDetail: true }); await page.goto('/equipment-inspections');
  await page.getByRole('button', { name: /Transporter Nord FB-FZ-03/ }).click();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseDetail();
  await expect(page.getByText('Keine Geräte für diese Auswahl gefunden.', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Transporter Nord', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('a delayed confirmed completion stays in its originating business after switching context', async ({ page }) => {
  const api = await fixture(page, { planned: true, delayCompletion: true }); await openEquipment(page); await completeFields(page);
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect.poll(() => api.calls.length).toBe(1);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseCompletion();
  await expect(page.getByText('Keine Geräte für diese Auswahl gefunden.', { exact: true })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Prüfergebnis gespeichert' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Transporter Nord', exact: true })).toHaveCount(0);
});

test('a confirmed completion remains visible if the subsequent history refresh fails', async ({ page }) => {
  const api = await fixture(page, { planned: true, failedRefresh: true }); await openEquipment(page); await completeFields(page);
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Prüfergebnis gespeichert' })).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Lesedienst');
  await expect(page.locator('.history-record summary')).toContainText('Abgeschlossen');
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Prüfvorgang planen', exact: true })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});

test('freshly revoked access clears open evidence and disables all targets', async ({ page }) => {
  const api = await fixture(page, { planned: true }); await openEquipment(page); await completeFields(page);
  api.revoke(); await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await expect(page.getByRole('heading', { name: 'Zugriff eingeschränkt' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('equipment master-data edits retain inspection evidence and protect the proven next date', async ({ page }) => {
  const api = await fixture(page, { completed: true }); await page.goto(`/equipment?selected=${equipmentId}`);
  await expect(page.getByRole('heading', { name: 'Transporter Nord', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Bearbeiten', exact: true }).click();
  await expect(page.getByRole('dialog').getByLabel('Nächste Prüfung', { exact: true })).toHaveCount(0);
  await page.getByRole('dialog').getByLabel('Bezeichnung', { exact: false }).fill('Transporter Werkstatt');
  await page.getByRole('dialog').getByRole('button', { name: 'Speichern', exact: true }).click();
  await expect(page.getByText('Eintrag gespeichert.', { exact: true })).toBeVisible();
  expect(api.calls).toHaveLength(1);
  expect(api.calls[0].body).toMatchObject({ name: 'Transporter Werkstatt', lastInspectionId: inspectionId, lastInspectionOn: today,
    nextInspectionOn: '2027-10-21', inspectionBlocked: false });
});

test('the master-data link opens the selected equipment inspection record directly', async ({ page }) => {
  const api = await fixture(page, { planned: true }); await page.goto(`/equipment?selected=${equipmentId}`);
  await page.getByRole('link', { name: 'Prüf- und Wartungsakte öffnen', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/equipment-inspections\\?equipmentId=${equipmentId}`));
  await expect(page.getByRole('heading', { name: 'Geplant: Jahresprüfung Transporter' })).toBeVisible(); expect(api.calls).toHaveLength(0);
});

test('a completion response with different evidence cannot silently replace the submitted proof', async ({ page }) => {
  const api = await fixture(page, { planned: true, differentEvidence: true }); await openEquipment(page); await completeFields(page);
  await page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' }).click();
  await expect(page.getByRole('alert')).toContainText('andere Angaben als die gesendeten Daten');
  await expect(page.getByRole('status').filter({ hasText: 'Prüfergebnis gespeichert' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Prüfergebnis verbindlich speichern' })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});
