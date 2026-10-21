import { expect, Page, test } from '@playwright/test';

async function fixture(page: Page, options: { role?: string; existing?: boolean; stale?: boolean; malformed?: boolean; delayContract?: boolean; legacy?: boolean; invalidContract?: Record<string, unknown> } = {}) {
  const calls: { path: string; body: any; headers: Record<string, string> }[] = [];
  let contract = { id: 'contract-a', revision: 2, title: 'Monatliche Dachkontrolle', scope: 'Abläufe und Anschlüsse kontrollieren', nextDueDate: '2027-01-31', intervalMonths: 1, active: true, automaticPlanning: false, planningLeadDays: 14,
    checklistTemplate: [{ title: 'Abläufe prüfen', instructions: 'Laub entfernen' }, { title: 'Anschlüsse prüfen', instructions: 'Sichtkontrolle' }] };
  let visit = { id: 'visit-a', revision: 1, contractId: 'contract-a', contractRevision: 2, projectId: 'project-a', dueDate: '2027-01-31', terms: { title: contract.title, scope: contract.scope }, scopeDigest: 'a'.repeat(64), status: 'Planned', completionNotes: '', completedAt: null as string | null, nextDueDate: null as string | null,
    checklist: contract.checklistTemplate.map((x, i) => ({ ...x, id: `check-${i}`, state: 'Pending', note: '', recordedBy: null as string | null, recordedAt: null as string | null })) };
  let exists = !!options.existing; let released = !options.delayContract; let unblock: (() => void) | undefined;
  let legacy = !!options.legacy;
  const storedContract = () => {
    if (!legacy) return { ...contract, ...options.invalidContract };
    const { checklistTemplate, automaticPlanning, planningLeadDays, ...stored } = contract;
    return stored;
  };
  const gate = new Promise<void>(resolve => { unblock = resolve; });
  const role = options.role || 'ManagingDirector';
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length), other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: 'user-a', displayName: 'Testmitarbeiter', email: 'test@example.test' }, activeTenantId: 'tenant-a', tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role, modules: ['core', 'maintenance', 'field'], capabilities: ['*'] })) } });
    if (request.method() === 'POST') calls.push({ path, body: request.postDataJSON(), headers: request.headers() });
    if (path === 'maintenance-contracts') return route.fulfill({ json: { items: other ? [] : [storedContract()], total: other ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === 'maintenance-contracts/contract-a') { if (!released) await gate; return route.fulfill({ json: storedContract() }); }
    if (path === 'maintenance-contracts/contract-a/configure') { legacy = false; contract = { ...contract, ...request.postDataJSON(), revision: contract.revision + 1 }; return route.fulfill({ json: contract }); }
    if (path === 'maintenance-contracts/contract-a/schedule') { exists = true; return route.fulfill({ json: visit }); }
    if (path === 'maintenance-visits') return route.fulfill({ json: { items: exists && !other ? [{ ...visit, title: contract.title, totalChecks: visit.checklist.length, completedChecks: visit.checklist.filter(x => x.state !== 'Pending').length }] : [], total: exists && !other ? 1 : 0, page: 1, pageSize: 25 } });
    if (path === 'maintenance-visits/visit-a') return route.fulfill({ json: options.malformed ? { ...visit, checklist: null } : visit });
    if (path === 'maintenance-visits/visit-a/check') {
      if (options.stale) return route.fulfill({ status: 409, json: { code: 'revision_conflict', detail: 'Der Termin wurde geändert. Bitte neu öffnen.' } });
      const body = request.postDataJSON(); visit = { ...visit, revision: visit.revision + 1, checklist: visit.checklist.map(x => x.id === body.checkId ? { ...x, state: body.state, note: body.note, recordedBy: 'user-a', recordedAt: '2027-01-31T09:00:00Z' } : x) }; return route.fulfill({ json: visit });
    }
    if (path === 'maintenance-visits/visit-a/complete') {
      visit = { ...visit, revision: visit.revision + 1, status: 'Completed', completionNotes: request.postDataJSON().notes, completedAt: '2027-01-31T09:30:00Z', nextDueDate: '2027-02-28' };
      contract = { ...contract, revision: contract.revision + 1, nextDueDate: '2027-02-28' }; return route.fulfill({ json: visit });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, release: () => { released = true; unblock?.(); } };
}

async function openContract(page: Page) { await page.goto('/maintenance'); await page.getByRole('button', { name: /Monatliche Dachkontrolle Nächster Termin/ }).click(); await expect(page.getByRole('heading', { name: 'Checkliste für neue Termine' })).toBeVisible(); }
async function openVisit(page: Page) { await page.goto('/maintenance'); await page.getByRole('button', { name: /Monatliche Dachkontrolle · 31.01.2027/ }).click(); await expect(page.getByRole('heading', { name: 'Wartung vom 31.01.2027' })).toBeVisible(); }

test('contract to project checklist and month-end completion is a connected responsive workflow', async ({ page }) => {
  const api = await fixture(page); await openContract(page);
  expect(api.calls).toHaveLength(0);
  await page.getByRole('button', { name: 'Termin 31.01.2027 anlegen oder öffnen' }).click();
  await expect(page.getByRole('heading', { name: 'Wartung vom 31.01.2027' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Wartung und Projekt abschließen', exact: true })).toBeDisabled();
  await page.getByLabel('Ergebnis für Abläufe prüfen', { exact: true }).selectOption('Done');
  await page.getByLabel('Bemerkung zu Abläufe prüfen', { exact: true }).fill('Laub entfernt');
  await page.getByRole('button', { name: 'Ergebnis 1 speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({hasText:'Prüfergebnis gespeichert'})).toBeVisible();
  await page.getByLabel('Ergebnis für Anschlüsse prüfen', { exact: true }).selectOption('NotApplicable');
  await expect(page.getByRole('button', { name: 'Ergebnis 2 speichern', exact: true })).toBeDisabled();
  await page.getByLabel('Bemerkung zu Anschlüsse prüfen', { exact: true }).fill('Keine entsprechenden Anschlüsse vorhanden');
  await page.getByRole('button', { name: 'Ergebnis 2 speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({hasText:'Prüfergebnis gespeichert'})).toBeVisible();
  await page.getByLabel('Abschlussbericht', { exact: true }).fill('Dach und Abläufe kontrolliert.');
  await page.getByRole('button', { name: 'Wartung und Projekt abschließen', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Abschluss', exact: true })).toBeVisible();
  await expect(page.getByText('Nächster Intervalltermin:', { exact: false })).toContainText('28.02.2027');
  await expect(page.getByRole('button', { name: 'Ergebnis 1 speichern' })).toHaveCount(0);
  expect(api.calls.map(x => x.path)).toEqual(['maintenance-contracts/contract-a/schedule', 'maintenance-visits/visit-a/check', 'maintenance-visits/visit-a/check', 'maintenance-visits/visit-a/complete']);
  expect(api.calls[0].body).toEqual({ expectedDueDate: '2027-01-31' }); expect(api.calls[0].headers['if-match']).toBe('"2"');
  expect(api.calls[3].headers).toMatchObject({ 'if-match': '"3"', 'x-tenant-id': 'tenant-a', 'x-xsrf-token': 'csrf-test' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('automatic planning requires saving the explicit contract configuration', async ({ page }) => {
  const api = await fixture(page); await openContract(page);
  await page.getByLabel('Wartungsprojekt und Checkliste automatisch anlegen', { exact: true }).check();
  await page.getByLabel('Vorlauf in Kalendertagen').fill('21');
  await page.getByRole('button', { name: 'Prüfschritt hinzufügen', exact: true }).click();
  await page.getByLabel('Prüfschritt 3', { exact: true }).fill('Dachfläche kontrollieren');
  await expect(page.getByRole('button', { name: 'Termin 31.01.2027 anlegen oder öffnen' })).toBeDisabled(); expect(api.calls).toHaveLength(0);
  await page.getByRole('button', { name: 'Wartungsablauf speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({hasText:'Wartungsablauf gespeichert'})).toBeVisible();
  expect(api.calls).toHaveLength(1); expect(api.calls[0].body.automaticPlanning).toBe(true); expect(api.calls[0].body.planningLeadDays).toBe(21); expect(api.calls[0].body.checklistTemplate).toHaveLength(3);
  await expect(page.getByRole('button', { name: 'Termin 31.01.2027 anlegen oder öffnen' })).toBeEnabled();
});

test('a legacy contract opens with safe defaults and accepts its first explicit checklist', async ({ page }) => {
  const api = await fixture(page, { legacy: true }); await openContract(page);
  await expect(page.getByLabel('Wartungsprojekt und Checkliste automatisch anlegen', { exact: true })).not.toBeChecked();
  await expect(page.getByLabel('Vorlauf in Kalendertagen')).toHaveValue('14');
  await expect(page.getByLabel('Prüfschritt 1', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Termin 31.01.2027 anlegen oder öffnen' })).toBeDisabled(); expect(api.calls).toHaveLength(0);
  await page.getByRole('button', { name: 'Prüfschritt hinzufügen', exact: true }).click();
  await page.getByLabel('Prüfschritt 1', { exact: true }).fill('Bestandsvertrag prüfen');
  await page.getByRole('button', { name: 'Wartungsablauf speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({hasText:'Wartungsablauf gespeichert'})).toBeVisible();
  expect(api.calls).toHaveLength(1); expect(api.calls[0].body).toEqual({ active: true, automaticPlanning: false, planningLeadDays: 14, checklistTemplate: [{ title: 'Bestandsvertrag prüfen', instructions: '' }] });
  await expect(page.getByRole('button', { name: 'Termin 31.01.2027 anlegen oder öffnen' })).toBeEnabled();
});

test('explicit null or invalid configuration types cannot be treated as legacy defaults', async ({ page }) => {
  const api = await fixture(page, { invalidContract: { checklistTemplate: null, automaticPlanning: 'false', planningLeadDays: '14' } });
  await page.goto('/maintenance'); await page.getByRole('button', { name: /Monatliche Dachkontrolle Nächster Termin/ }).click();
  await expect(page.getByRole('alert')).toContainText('Wartungsvertrag passt nicht');
  await expect(page.getByRole('heading', { name: 'Checkliste für neue Termine' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('assigned worker can record a checklist but sees no contract management or final completion', async ({ page }) => {
  const api = await fixture(page, { role: 'Worker', existing: true }); await openVisit(page);
  await expect(page.getByRole('heading', { name: 'Wartungsverträge', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Wartung und Projekt abschließen' })).toHaveCount(0);
  await page.getByLabel('Ergebnis für Abläufe prüfen', { exact: true }).selectOption('Done');
  await page.getByRole('button', { name: 'Ergebnis 1 speichern' }).click(); await expect(page.getByRole('status').filter({hasText:'Prüfergebnis gespeichert'})).toBeVisible();
  expect(api.calls).toHaveLength(1); expect(api.calls[0].path).toBe('maintenance-visits/visit-a/check');
});

test('changing business clears selected contract checks and all action targets', async ({ page }) => {
  const api = await fixture(page, { existing: true }); await openContract(page); await page.getByRole('button', { name: 'Termin 31.01.2027 anlegen oder öffnen' }).click();
  await expect(page.getByRole('heading', { name: 'Wartung vom 31.01.2027' })).toBeVisible();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
  await expect(page.getByRole('heading', { name: 'Wartung vom 31.01.2027' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Checkliste für neue Termine' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Ergebnis 1 speichern' })).toHaveCount(0); expect(api.calls).toHaveLength(1);
});

test('late contract response after business switch cannot restore the old action target', async ({ page }) => {
  const api = await fixture(page, { delayContract: true }); await page.goto('/maintenance'); await page.getByRole('button', { name: /Monatliche Dachkontrolle Nächster Termin/ }).click();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
  await expect(page.getByText('Auf dieser Seite sind keine Wartungsverträge vorhanden.')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Checkliste für neue Termine' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('conflicting revision reports failure and keeps the unsaved result available for review', async ({ page }) => {
  const api = await fixture(page, { existing: true, stale: true }); await openVisit(page);
  await page.getByLabel('Ergebnis für Abläufe prüfen', { exact: true }).selectOption('Done');
  await page.getByLabel('Bemerkung zu Abläufe prüfen', { exact: true }).fill('Noch nicht gespeichert');
  await page.getByRole('button', { name: 'Ergebnis 1 speichern' }).click();
  await expect(page.getByRole('alert')).toContainText('zwischenzeitlich geändert');
  await expect(page.getByLabel('Bemerkung zu Abläufe prüfen', { exact: true })).toHaveValue('Noch nicht gespeichert');
  await expect(page.getByRole('button', { name: 'Wartung und Projekt abschließen' })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});

test('a malformed visit cannot expose checklist or completion commands', async ({ page }) => {
  const api = await fixture(page, { existing: true, malformed: true }); await page.goto('/maintenance'); await page.getByRole('button', { name: /Monatliche Dachkontrolle · 31.01.2027/ }).click();
  await expect(page.getByRole('alert')).toContainText('unvollständige oder unpassende Prüfdaten');
  await expect(page.getByRole('button', { name: 'Ergebnis 1 speichern' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('saving one result preserves other unsaved notes and the drafted completion report', async ({ page }) => {
  await fixture(page, { existing: true }); await openVisit(page);
  await page.getByLabel('Bemerkung zu Anschlüsse prüfen', { exact: true }).fill('Lokaler Entwurf für den zweiten Schritt');
  await page.getByLabel('Abschlussbericht', { exact: true }).fill('Lokaler Abschlussentwurf');
  await page.getByLabel('Ergebnis für Abläufe prüfen', { exact: true }).selectOption('Done');
  await page.getByRole('button', { name: 'Ergebnis 1 speichern' }).click();
  await expect(page.getByRole('status').filter({hasText:'Prüfergebnis gespeichert'})).toBeVisible();
  await expect(page.getByLabel('Bemerkung zu Anschlüsse prüfen', { exact: true })).toHaveValue('Lokaler Entwurf für den zweiten Schritt');
  await expect(page.getByLabel('Abschlussbericht', { exact: true })).toHaveValue('Lokaler Abschlussentwurf');
});
