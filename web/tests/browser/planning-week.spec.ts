import { expect, Page, test } from '@playwright/test';
import { resolveBusinessTime } from '../../src/app/core/business-time';

const projectId = '10000000-0000-4000-8000-000000000001';
const employeeId = '10000000-0000-4000-8000-000000000002';
const equipmentId = '10000000-0000-4000-8000-000000000003';
const assignmentId = '10000000-0000-4000-8000-000000000004';
const assignment = { id: assignmentId, revision: 4, projectId, projectName: 'Synthetisches Dachprojekt',
  employeeId, employeeName: 'Testmitarbeiter', title: 'Wartung am Sonntag',
  start: '2026-10-25T01:30:12.345Z', end: '2026-10-25T03:30:45.123Z',
  equipmentIds: [equipmentId], equipment: [{ id: equipmentId, name: 'Prüffahrzeug' }], days: ['2026-10-25'] };

async function fixture(page: Page, options: { role?: string; corrupt?: boolean; disabled?: boolean } = {}) {
  let rows = [{ ...assignment }], revoked = false, rejectWrite = false;
  let deferred: Promise<void> | null = null;
  const mutations: { method: string; body: any; revision?: string; key?: string }[] = [];
  const weeks: string[] = [];
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.slice('/api/v1/'.length);
    const tenant = request.headers()['x-tenant-id'] || 'tenant-a';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: {
      user: { id: 'user-calendar', email: 'calendar@example.test', displayName: 'Disposition' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: 'tenant-' + id, name: 'Testbetrieb ' + id.toUpperCase(), role: options.role ?? 'ManagingDirector',
        modules: options.disabled ? ['core'] : ['core', 'field', 'purchasing'],
        capabilities: revoked ? [] : options.role === 'Worker' ? ['schedule-assignments.read', 'employees.read', 'projects.read', 'equipment.read'] : ['*'] }))
    } });
    if (path === 'planning/week') {
      weeks.push(url.search);
      const monday = url.searchParams.get('weekStart')!, zone = 'Europe/Berlin';
      const days = Array.from({ length: 7 }, (_, i) => {
        const date = new Date(monday + 'T12:00:00Z'); date.setUTCDate(date.getUTCDate() + i);
        const next = new Date(date); next.setUTCDate(next.getUTCDate() + 1);
        const name = date.toISOString().slice(0, 10);
        return { date: name, startUtc: resolveBusinessTime(name + 'T00:00', zone), endUtc: resolveBusinessTime(next.toISOString().slice(0, 10) + 'T00:00', zone) };
      });
      const filters = { employeeId: url.searchParams.get('employeeId'), projectId: url.searchParams.get('projectId'), equipmentId: url.searchParams.get('equipmentId') };
      const visible = (tenant === 'tenant-b' ? [] : rows).filter(row => !row['cancelled' as keyof typeof row]
        && (!filters.employeeId || row.employeeId === filters.employeeId)
        && (!filters.projectId || row.projectId === filters.projectId)
        && (!filters.equipmentId || row.equipmentIds.includes(filters.equipmentId)))
        .map(row => ({ ...row, days: days.filter(day => Date.parse(row.start) < Date.parse(day.endUtc) && Date.parse(row.end) > Date.parse(day.startUtc)).map(day => day.date) }))
        .filter(row => row.days.length);
      const data = { weekStart: monday, timeZone: zone, days, filters, assignments: visible, count: visible.length, maxAssignments: 1000, note: '' };
      if (options.corrupt) data.count++;
      if (tenant === 'tenant-a' && deferred) { const pending = deferred; deferred = null; await pending; }
      return route.fulfill({ json: data });
    }
    if (['projects', 'employees', 'equipment'].includes(path)) {
      const pageNumber = Number(url.searchParams.get('page') || 1);
      const item = path === 'projects' ? { id: projectId, revision: 1, name: assignment.projectName }
        : path === 'employees' ? { id: employeeId, revision: 1, name: assignment.employeeName, active: true }
          : { id: equipmentId, revision: 1, name: 'Prüffahrzeug', available: true };
      return route.fulfill({ json: { items: [item], total: 1, page: pageNumber, pageSize: 25 } });
    }
    if (path.startsWith('schedule-assignments') && ['POST', 'PUT'].includes(request.method())) {
      const body = request.postDataJSON();
      mutations.push({ method: request.method(), body, revision: request.headers()['if-match'], key: request.headers()['idempotency-key'] });
      if (rejectWrite) return route.fulfill({ status: 409, json: { status: 409, code: 'planning_conflict', detail: 'Das Prüfgerät ist bereits eingeplant.' } });
      const saved = { ...assignment, ...body, id: body.id || assignmentId, revision: body.id ? 5 : 1 };
      rows = [saved]; return route.fulfill({ json: saved });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { mutations, weeks, revoke: () => revoked = true, reject: () => rejectWrite = true,
    defer: (pending: Promise<void>) => deferred = pending };
}

test('weekly workplace displays business-local days and equipment without viewport overflow', async ({ page }) => {
  await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await expect(page.locator('.week-day')).toHaveCount(7);
  await expect(page.locator('.dispatch-entry')).toContainText('Prüffahrzeug');
  await expect(page.locator('.dispatch-entry')).toContainText('02:30');
  await expect(page.locator('.dispatch-entry')).toContainText('04:30');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'Nächste Woche', exact: true }).click();
  await expect(page.getByLabel('Datum in der Planungswoche')).toHaveValue('2026-10-26');
  await expect(page.locator('.dispatch-entry')).toHaveCount(0);
});

test('creation resolves the business zone even when the browser is in another zone', async ({ page }) => {
  const api = await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await expect(page.locator('.week-day')).toHaveCount(7);
  await page.getByText('Stammdaten suchen und Wochenplan filtern', { exact: true }).click();
  for (const name of ['Projekte laden', 'Mitarbeiter laden', 'Geräte / Fahrzeuge laden']) await page.getByRole('button', { name, exact: true }).click();
  await page.getByRole('button', { name: 'Einsatz anlegen', exact: true }).click();
  await page.getByLabel('Einsatzbezeichnung', { exact: true }).fill('Neuer Montagseinsatz');
  await page.getByLabel('Projekt', { exact: true }).selectOption(projectId);
  await page.getByLabel('Mitarbeiter', { exact: true }).selectOption(employeeId);
  await page.getByLabel('Prüffahrzeug', { exact: true }).check();
  await page.getByRole('button', { name: 'Einsatz speichern', exact: true }).click();
  await expect(page.locator('.dispatch-entry')).toContainText('Neuer Montagseinsatz');
  expect(api.mutations[0]).toMatchObject({ method: 'POST', body: { start: '2026-10-19T06:00:00.000Z', end: '2026-10-19T14:00:00.000Z', equipmentIds: [equipmentId] } });
  expect(api.mutations[0].key).toBeTruthy();
});

test('editing retains later repeated-hour identity and revision; cancellation updates the journal', async ({ page }) => {
  const api = await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await page.getByRole('button', { name: 'Wartung am Sonntag bearbeiten', exact: true }).click();
  await expect(page.getByLabel('Beginn in Betriebszeit', { exact: true })).toHaveValue('2026-10-25T02:30:12.345');
  await expect(page.getByLabel('Zeitversatz für Beginn', { exact: true })).toHaveValue('+01:00');
  await page.getByRole('button', { name: 'Änderungen speichern', exact: true }).click();
  await expect(page.locator('.editor')).toHaveCount(0);
  expect(api.mutations[0]).toMatchObject({ method: 'PUT', revision: '"4"', body: { start: assignment.start, end: assignment.end, equipmentIds: [equipmentId] } });
  await page.getByRole('button', { name: 'Wartung am Sonntag bearbeiten', exact: true }).click();
  await page.getByLabel('Diesen Einsatz absagen', { exact: true }).check();
  await page.getByRole('button', { name: 'Änderungen speichern', exact: true }).click();
  await expect(page.locator('.dispatch-entry')).toHaveCount(0);
  expect(api.mutations[1]).toMatchObject({ revision: '"5"', body: { cancelled: true } });
});

test('ambiguous times require an offset and nonexistent times cannot be submitted', async ({ page }) => {
  const api = await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await page.getByRole('button', { name: 'Wartung am Sonntag bearbeiten', exact: true }).click();
  await page.getByLabel('Beginn in Betriebszeit', { exact: true }).fill('2026-10-25T02:10');
  await expect(page.getByRole('button', { name: 'Änderungen speichern', exact: true })).toBeDisabled();
  await page.getByLabel('Zeitversatz für Beginn', { exact: true }).selectOption('+02:00');
  await expect(page.getByRole('button', { name: 'Änderungen speichern', exact: true })).toBeEnabled();
  await page.getByLabel('Beginn in Betriebszeit', { exact: true }).fill('2026-03-29T02:30');
  await expect(page.getByRole('button', { name: 'Änderungen speichern', exact: true })).toBeDisabled();
  expect(api.mutations).toEqual([]);
});

test('server conflicts preserve the edit and report the rejection', async ({ page }) => {
  const api = await fixture(page); api.reject(); await page.goto('/planning-week?weekStart=2026-10-19');
  await page.getByRole('button', { name: 'Wartung am Sonntag bearbeiten', exact: true }).click();
  await page.getByLabel('Einsatzbezeichnung', { exact: true }).fill('Konflikt bleibt bearbeitbar');
  await page.getByRole('button', { name: 'Änderungen speichern', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('bereits eingeplant');
  await expect(page.getByLabel('Einsatzbezeichnung', { exact: true })).toHaveValue('Konflikt bleibt bearbeitbar');
  await expect(page.getByRole('button', { name: 'Änderungen speichern', exact: true })).toBeEnabled();
});

test('worker can read the assigned week but cannot create or alter assignments', async ({ page }) => {
  const api = await fixture(page, { role: 'Worker' }); await page.goto('/planning-week?weekStart=2026-10-19');
  await expect(page.locator('.dispatch-entry')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Einsatz anlegen', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Wartung am Sonntag bearbeiten', exact: true })).toHaveCount(0);
  expect(api.mutations).toEqual([]);
});

test('disabled module and malformed response fail without exposing a partial editable plan', async ({ page }) => {
  const api = await fixture(page, { disabled: true }); await page.goto('/planning-week');
  await expect(page.getByRole('heading', { name: 'Kein Zugriff', exact: true })).toBeVisible(); expect(api.weeks).toEqual([]);
  await page.unroute('**/api/v1/**'); await fixture(page, { corrupt: true }); await page.reload();
  await expect(page.getByRole('alert')).toContainText('unvollständig');
  await expect(page.locator('.week-day')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Einsatz anlegen', exact: true })).toBeDisabled();
});

test('server-side resource filters are applied to the exact requested week', async ({ page }) => {
  const api = await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await expect(page.locator('.week-day')).toHaveCount(7);
  await page.getByText('Stammdaten suchen und Wochenplan filtern', { exact: true }).click();
  await page.getByRole('button', { name: 'Geräte / Fahrzeuge laden', exact: true }).click();
  await page.getByLabel('Betriebsmittelfilter', { exact: true }).selectOption(equipmentId);
  await page.getByRole('button', { name: 'Filter anwenden', exact: true }).click();
  await expect(page.locator('.dispatch-entry')).toHaveCount(1);
  expect(api.weeks.at(-1)).toContain('equipmentId=' + equipmentId);
});

test('tenant changes discard delayed weekly responses', async ({ page }) => {
  const api = await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await expect(page.locator('.dispatch-entry')).toHaveCount(1);
  let release!: () => void; api.defer(new Promise<void>(resolve => release = resolve));
  await page.getByRole('button', { name: 'Plan aktualisieren', exact: true }).click();
  await expect.poll(() => api.weeks.length).toBe(2);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
  await expect(page.locator('.week-day')).toHaveCount(7);
  await expect(page.locator('.dispatch-entry')).toHaveCount(0);
  const oldResponse = page.waitForResponse(response => response.url().includes('planning/week') && response.request().headers()['x-tenant-id'] === 'tenant-a');
  release(); await oldResponse;
  await expect(page.locator('.dispatch-entry')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Plan aktualisieren', exact: true })).toBeEnabled();
  expect(api.mutations).toEqual([]);
});

test('unsaved edits cannot be overwritten by week navigation and are cleared by a tenant switch', async ({ page }) => {
  const api = await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await page.getByRole('button', { name: 'Wartung am Sonntag bearbeiten', exact: true }).click();
  await page.getByLabel('Einsatzbezeichnung', { exact: true }).fill('Ungespeicherter Entwurf');
  await expect(page.getByRole('button', { name: 'Nächste Woche', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Einsatz anlegen', exact: true })).toBeDisabled();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
  await expect(page.locator('.editor')).toHaveCount(0);
  await expect(page.locator('.week-day')).toHaveCount(7);
  expect(api.mutations).toEqual([]);
});

test('permission withdrawal clears the displayed week and its draft', async ({ page }) => {
  const api = await fixture(page); await page.goto('/planning-week?weekStart=2026-10-19');
  await page.getByRole('button', { name: 'Wartung am Sonntag bearbeiten', exact: true }).click();
  api.revoke();
  const refresh = page.waitForResponse(response => response.url().endsWith('/auth/me'));
  await page.evaluate(() => window.dispatchEvent(new Event('online'))); await refresh;
  await expect(page.getByRole('heading', { name: 'Kein Zugriff', exact: true })).toBeVisible();
  await expect(page.locator('.editor')).toHaveCount(0);
  await expect(page.locator('.dispatch-entry')).toHaveCount(0);
  expect(api.mutations).toEqual([]);
});
