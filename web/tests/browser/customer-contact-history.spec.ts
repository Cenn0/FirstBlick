import { expect, Page, test } from '@playwright/test';
import type { CustomerContact, CustomerContactHistoryEntry } from '../../src/app/core/customer-contacts.models';

const customerId = '72000000-0000-4000-8000-000000000001', userId = '72000000-0000-4000-8000-000000000003';
const contactId = '72000000-0000-4000-8000-000000000004';
const now = '2026-10-22T09:00:00.000Z';
interface Options { reader?: boolean; count?: number; malformed?: boolean; delayed?: boolean; legacy?: boolean }

async function fixture(page: Page, options: Options = {}) {
  const original: CustomerContact = { id: contactId, name: 'Frühere Objektleitung', function: 'Verwaltung', email: 'alt@example.test', phone: '0421 123', notes: '',
    isPrimary: false, archived: false, createdBy: userId, createdAt: now, updatedBy: userId, updatedAt: now, archivedBy: null, archivedAt: null };
  const count = options.legacy ? 0 : options.count ?? 2;
  const history: CustomerContactHistoryEntry[] = []; let current = original;
  for (let i = 0; i < count; i++) {
    const at = new Date(Date.parse(now) + i * 60_000).toISOString();
    const after: CustomerContact = i === 0 ? original : { ...current, name: i === count - 1 ? 'Aktuelle Objektleitung' : `Kontaktstand ${i}`,
      email: i === count - 1 ? 'neu@example.test' : `kontakt${i}@example.test`, updatedAt: at };
    history.push({ id: `72000000-0000-4000-8000-${String(100 + i).padStart(12, '0')}`, tenantId: 'tenant-a', revision: 1, archived: false,
      customerId, contactId, customerRevision: i + 2, changeKind: i === 0 ? 'Created' : 'Updated', before: i === 0 ? null : current,
      after, recordedBy: userId, recordedByName: 'Historische Bearbeiterin', recordedAt: at, createdAt: at, updatedAt: at, digest: 'a'.repeat(64) });
    current = after;
  }
  const customer = { id: customerId, revision: Math.max(1, count + 1), name: 'Hausverwaltung Nord', contactName: '', email: '', phone: '', address: 'Testweg 1', notes: '' };
  const contacts = { customerId, tenantId: 'tenant-a', customerName: customer.name, revision: customer.revision, contacts: [current] };
  let release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; });
  const reads: number[] = [];
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.slice('/api/v1/'.length);
    const other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: userId, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role: options.reader ? 'Accounting' : 'ManagingDirector',
        modules: ['core'], capabilities: options.reader ? ['customers.read'] : ['*'] })) } });
    if (path === 'customers') return route.fulfill({ json: { items: other ? [] : [customer], total: other ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `customers/${customerId}`) return route.fulfill({ json: { ...customer, contacts: [current] } });
    if (path === `customers/${customerId}/contacts`) return route.fulfill({ json: contacts });
    if (path === `customers/${customerId}/contacts/${contactId}/history`) {
      const requestedPage = Number(url.searchParams.get('page') || 1); reads.push(requestedPage);
      const response = { customerId, tenantId: options.malformed ? 'tenant-b' : 'tenant-a', contactId, customerName: customer.name,
        customerRevision: customer.revision, current, items: [...history].reverse().slice((requestedPage - 1) * 20, requestedPage * 20), total: count, page: requestedPage, pageSize: 20 };
      if (options.delayed) await gate;
      return route.fulfill({ json: response });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { release, reads, name: current.name };
}

async function open(page: Page, name: string) {
  await page.goto(`/customers?selected=${customerId}`);
  await page.getByRole('button', { name: `Änderungsverlauf von ${name}`, exact: true }).click();
}

test('read-only users inspect exact prior values and the historical actor without editing controls', async ({ page }) => {
  const api = await fixture(page, { reader: true }); await open(page, api.name);
  const history = page.locator('.contact-history');
  await expect(history.getByText('2 gespeicherte Änderungen', { exact: false })).toBeVisible();
  await expect(history.getByRole('cell', { name: 'alt@example.test', exact: true }).first()).toBeVisible();
  await expect(history.getByRole('cell', { name: 'neu@example.test', exact: true })).toBeVisible();
  await expect(history.getByText('Historische Bearbeiterin', { exact: false }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ansprechpartner hinzufügen' })).toHaveCount(0);
  await expect(history.getByText('Ursprüngliche Anlage dieses Ansprechpartners.')).toBeVisible();
  expect(api.reads).toEqual([1]); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('contact history pages retain their customer revision and show earlier original values', async ({ page }) => {
  const api = await fixture(page, { count: 22 }); await open(page, api.name);
  const history = page.locator('.contact-history');
  await expect(history.getByText('22 gespeicherte Änderungen', { exact: false })).toBeVisible();
  await expect(history.locator('article')).toHaveCount(20);
  await page.getByRole('button', { name: 'Weitere Änderungen', exact: true }).click();
  await expect(history.locator('article')).toHaveCount(2);
  await expect(history.getByText('Ursprüngliche Anlage dieses Ansprechpartners.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Weitere Änderungen', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Vorherige Änderungen', exact: true }).click();
  await expect(history.locator('article')).toHaveCount(20); expect(api.reads).toEqual([1, 2, 1]);
});

test('legacy contacts show an explicit absence of recorded history', async ({ page }) => {
  const api = await fixture(page, { legacy: true }); await open(page, api.name);
  await expect(page.locator('.contact-history').getByText('Für diesen Kontakt ist noch keine Änderungshistorie gespeichert.', { exact: false })).toBeVisible();
  await expect(page.locator('.contact-history article')).toHaveCount(0);
});

test('foreign history responses are rejected without exposing historical values', async ({ page }) => {
  const api = await fixture(page, { malformed: true }); await open(page, api.name);
  await expect(page.locator('.contact-history').getByRole('alert')).toContainText('nicht zum geöffneten Kontaktstand');
  await expect(page.locator('.contact-history article')).toHaveCount(0);
});

test('late history cannot reappear after the current business changes', async ({ page }) => {
  const api = await fixture(page, { delayed: true }); await open(page, api.name);
  await expect(page.getByText('Änderungsverlauf wird geladen …')).toBeVisible();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
  await expect(page.locator('.contact-history')).toHaveCount(0);
  await expect(page.getByText('Historische Bearbeiterin', { exact: false })).toHaveCount(0);
});
