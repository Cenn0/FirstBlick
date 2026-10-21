import { expect, Page, test } from '@playwright/test';
import type { CustomerContact } from '../../src/app/core/customer-contacts.models';

const customerId = '70000000-0000-4000-8000-000000000001';
const userId = '70000000-0000-4000-8000-000000000003';
const firstId = '70000000-0000-4000-8000-000000000004';
const secondId = '70000000-0000-4000-8000-000000000005';
const now = '2026-10-21T12:00:00Z';
const later = '2026-10-21T13:00:00Z';
interface Options { reader?: boolean; existing?: boolean; malformed?: boolean; malformedChange?: boolean; conflict?: boolean; delayRead?: boolean; delayWrite?: boolean }

async function fixture(page: Page, options: Options = {}) {
  let customer = { id: customerId, revision: 1, name: 'Hausverwaltung Nord', contactName: 'Bisheriger Kontakt', email: 'zentrale@example.test', phone: '0421 987', address: 'Testweg 1', notes: '' };
  let contacts: CustomerContact[] = options.existing ? [{ id: firstId, name: 'Hausmeisterin Nord', function: 'Objektbetreuung', email: 'kontakt@example.test', phone: '0421 123', notes: 'Werktags', isPrimary: true,
    archived: false, createdBy: userId, createdAt: now, updatedBy: userId, updatedAt: now, archivedBy: null, archivedAt: null }] : [];
  const calls: { path: string; body: any; headers: Record<string, string> }[] = [];
  let releaseRead!: () => void, releaseWrite!: () => void;
  const readGate = new Promise<void>(resolve => { releaseRead = resolve; }), writeGate = new Promise<void>(resolve => { releaseWrite = resolve; });
  const view = () => ({ customerId, tenantId: 'tenant-a', customerName: customer.name, revision: customer.revision, contacts: structuredClone(contacts) });
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length), other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: userId, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role: options.reader ? 'Accounting' : 'ManagingDirector', modules: ['core'], capabilities: options.reader ? ['customers.read'] : ['*'] })) } });
    if (path === 'customers') return route.fulfill({ json: { items: other ? [] : [customer], total: other ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `customers/${customerId}`) return route.fulfill({ json: { ...customer, contacts } });
    if (path === `customers/${customerId}/contacts` && request.method() === 'GET') {
      const snapshot = view(); if (options.delayRead) await readGate;
      return route.fulfill({ json: options.malformed ? { ...snapshot, contacts: null } : snapshot });
    }
    if (path.startsWith(`customers/${customerId}/contacts`) && request.method() === 'POST') {
      const body = request.postDataJSON(); calls.push({ path, body, headers: request.headers() });
      if (options.conflict) return route.fulfill({ status: 409, json: { code: 'revision_conflict', detail: 'Der Kunde wurde geändert. Bitte neu laden.' } });
      const parts = path.split('/'), id = parts[3], action = parts[4] || 'add';
      if (action === 'add' || action === 'update') {
        if (body.isPrimary) contacts = contacts.map(x => x.isPrimary ? { ...x, isPrimary: false, updatedAt: later } : x);
        if (action === 'add') contacts.push({ id: contacts.length ? secondId : firstId, ...body, archived: false,
          createdBy: userId, createdAt: later, updatedBy: userId, updatedAt: later, archivedBy: null, archivedAt: null });
        else contacts = contacts.map(x => x.id === id ? { ...x, ...body, updatedAt: later } : x);
      } else contacts = contacts.map(x => x.id === id ? { ...x, archived: action === 'archive', isPrimary: false, updatedAt: later,
        archivedAt: action === 'archive' ? later : null, archivedBy: action === 'archive' ? userId : null } : x);
      customer = { ...customer, revision: customer.revision + 1 };
      const snapshot = view(); if (options.delayWrite) await writeGate;
      return route.fulfill({ json: options.malformedChange ? { ...snapshot, customerId: firstId } : snapshot });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, releaseRead, releaseWrite };
}

async function open(page: Page): Promise<void> { await page.goto(`/customers?selected=${customerId}`); await expect(page.getByRole('heading', { name: 'Ansprechpartner', exact: true })).toBeVisible(); }
async function add(page: Page, name: string, primary = false): Promise<void> {
  await page.getByRole('button', { name: 'Ansprechpartner hinzufügen', exact: true }).click();
  await page.getByLabel('Name des Ansprechpartners', { exact: false }).fill(name);
  await page.getByLabel('Funktion / Zuständigkeit', { exact: true }).fill('Verwaltung');
  await page.getByLabel('E-Mail des Ansprechpartners', { exact: true }).fill('kontakt@example.test');
  if (primary) await page.getByLabel('Als Hauptkontakt festlegen', { exact: true }).check();
  await page.getByRole('button', { name: 'Ansprechpartner speichern', exact: true }).click();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
}

test('multiple contacts support explicit primary choice, edit, retained archive and restore', async ({ page }) => {
  const api = await fixture(page); await open(page);
  await expect(page.getByRole('definition').filter({ hasText: /^Bisheriger Kontakt$/ })).toBeVisible();
  await add(page, 'Objektleitung', true); await add(page, 'Buchhaltung', true);
  await expect(page.locator('article').filter({ has: page.getByRole('heading', { name: 'Buchhaltung', exact: true }) }).getByText('Hauptkontakt', { exact: true })).toBeVisible();
  await expect(page.locator('article').filter({ has: page.getByRole('heading', { name: 'Objektleitung', exact: true }) }).getByText('Hauptkontakt', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Objektleitung bearbeiten', exact: true }).click();
  await page.getByLabel('Telefon des Ansprechpartners', { exact: true }).fill('0421 111');
  await page.getByRole('button', { name: 'Ansprechpartner speichern', exact: true }).click();
  await expect(page.getByText('Telefon: 0421 111', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Objektleitung archivieren', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Objektleitung', exact: true })).toHaveCount(0);
  await page.getByLabel('Archivierte anzeigen', { exact: true }).check();
  await expect(page.getByText('Telefon: 0421 111', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Objektleitung wiederherstellen', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Objektleitung bearbeiten', exact: true })).toBeVisible();
  await page.getByLabel('Kontakte filtern', { exact: false }).fill('Buchhaltung');
  await expect(page.getByRole('heading', { name: 'Objektleitung', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Buchhaltung', exact: true })).toBeVisible();
  expect(api.calls.map(x => x.headers['if-match'])).toEqual(['"1"', '"2"', '"3"', '"4"', '"5"']);
  expect(api.calls.every(x => x.headers['idempotency-key'] && x.headers['x-xsrf-token'] === 'csrf-test' && x.headers['x-tenant-id'] === 'tenant-a')).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('read-only users can inspect contacts and have no contact mutation controls', async ({ page }) => {
  const api = await fixture(page, { reader: true, existing: true }); await open(page);
  await expect(page.getByRole('heading', { name: 'Hausmeisterin Nord' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ansprechpartner hinzufügen' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Hausmeisterin Nord bearbeiten' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('malformed contact books are rejected without opening a mutation form', async ({ page }) => {
  const api = await fixture(page, { malformed: true }); await open(page);
  await expect(page.getByRole('alert').filter({ hasText: 'nicht eindeutig' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ansprechpartner hinzufügen' })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('revision conflicts retain the unsaved draft and do not announce success', async ({ page }) => {
  const api = await fixture(page, { conflict: true, existing: true }); await open(page);
  await page.getByRole('button', { name: 'Hausmeisterin Nord bearbeiten' }).click();
  await page.getByLabel('Name des Ansprechpartners', { exact: false }).fill('Neuer Name');
  await page.getByRole('button', { name: 'Ansprechpartner speichern' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'zwischenzeitlich geändert' })).toBeVisible();
  await expect(page.getByLabel('Name des Ansprechpartners', { exact: false })).toHaveValue('Neuer Name');
  await expect(page.getByRole('heading', { name: 'Hausmeisterin Nord' })).toBeVisible(); expect(api.calls).toHaveLength(1);
});

test('unexpected mutation identity is not accepted as saved contact data', async ({ page }) => {
  await fixture(page, { existing: true, malformedChange: true }); await open(page);
  await page.getByRole('button', { name: 'Hausmeisterin Nord bearbeiten' }).click();
  await page.getByLabel('Telefon des Ansprechpartners', { exact: true }).fill('777');
  await page.getByRole('button', { name: 'Ansprechpartner speichern' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'nicht eindeutig' })).toBeVisible();
  await expect(page.getByText('Telefon: 777', { exact: true })).toHaveCount(0);
});

test('late customer contacts cannot reappear after switching the business', async ({ page }) => {
  const api = await fixture(page, { existing: true, delayRead: true }); await open(page);
  await expect(page.getByText('Ansprechpartner werden geladen …')).toBeVisible();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseRead();
  await expect(page.getByRole('heading', { name: 'Hausmeisterin Nord' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Ansprechpartner', exact: true })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('late successful contact changes do not reopen a former business or display its success', async ({ page }) => {
  const api = await fixture(page, { existing: true, delayWrite: true }); await open(page);
  await page.getByRole('button', { name: 'Hausmeisterin Nord archivieren' }).click();
  await expect.poll(() => api.calls.length).toBe(1);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseWrite();
  await expect(page.getByRole('heading', { name: 'Ansprechpartner', exact: true })).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: 'Ansprechpartner archiviert' })).toHaveCount(0);
});
