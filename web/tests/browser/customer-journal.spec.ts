import { expect, Page, test } from '@playwright/test';
import type { CommunicationItem } from '../../src/app/core/customer-journal.models';

const customerId = '71000000-0000-4000-8000-000000000001', actor = '71000000-0000-4000-8000-000000000003';
const entryId = '71000000-0000-4000-8000-000000000004', voidId = '71000000-0000-4000-8000-000000000005', contactId = '71000000-0000-4000-8000-000000000006', projectId = '71000000-0000-4000-8000-000000000007';
const now = '2026-10-22T01:00:00Z', later = '2026-10-22T01:15:00Z';
interface Options { reader?: boolean; archived?: boolean; existing?: boolean; malformed?: boolean; malformedChange?: boolean; conflict?: boolean; delayRead?: boolean; delayWrite?: boolean; revoke?: boolean }

async function fixture(page: Page, options: Options = {}) {
  let customer = { id: customerId, tenantId: 'tenant-a', revision: 2, archived: !!options.archived, name: 'Hausverwaltung Nord', contactName: '', email: '', phone: '', address: '', notes: '' };
  const contact = { id: contactId, name: 'Objektleitung Nord', function: 'Verwaltung', email: 'kontakt@example.test', phone: '0421 123', notes: '', isPrimary: true, archived: false,
    createdBy: actor, updatedBy: actor, createdAt: now, updatedAt: now, archivedBy: null, archivedAt: null };
  const make = (body: any): CommunicationItem => ({ entry: { id: entryId, tenantId: 'tenant-a', revision: 1, archived: false, createdAt: now, updatedAt: now,
    customerId, customerName: customer.name, channel: body.channel, direction: body.direction, occurredAt: body.occurredAt, subject: body.subject, text: body.text,
    contact: body.contactId ? { id: contactId, name: contact.name, function: contact.function, email: contact.email, phone: contact.phone } : null,
    project: body.projectId ? { id: projectId, name: 'Dachsanierung Nord', number: 'P-42' } : null, recordedBy: actor, recordedAt: now, digest: 'a'.repeat(64) }, voiding: null });
  let items: CommunicationItem[] = options.existing ? [make({ channel: 'Telephone', direction: 'Incoming', occurredAt: '2026-10-21T12:00:00Z', subject: 'Dachtermin', text: 'Termin für Mittwoch abgestimmt.' })] : [];
  const calls: { path: string; body: any; headers: Record<string, string> }[] = []; let readonly = !!options.reader;
  let releaseRead!: () => void, releaseWrite!: () => void;
  const readGate = new Promise<void>(resolve => { releaseRead = resolve; }), writeGate = new Promise<void>(resolve => { releaseWrite = resolve; });
  const view = (pageNumber = 1) => ({ customerId, tenantId: 'tenant-a', customerName: customer.name, customerRevision: customer.revision, customerArchived: customer.archived, timeZone: 'Europe/Berlin', items: structuredClone(items), total: items.length, page: pageNumber, pageSize: 20 });
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.slice('/api/v1/'.length), other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: actor, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role: readonly ? 'Accounting' : 'ManagingDirector', modules: ['core'], capabilities: readonly ? ['customers.read', 'projects.read'] : ['*'] })) } });
    if (path === 'customers') return route.fulfill({ json: { items: other ? [] : [customer], total: other ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `customers/${customerId}`) return route.fulfill({ json: customer });
    if (path === `customers/${customerId}/contacts`) return route.fulfill({ json: { customerId, tenantId: 'tenant-a', customerName: customer.name, revision: customer.revision, contacts: [contact] } });
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: projectId, revision: 1, name: 'Dachsanierung Nord', number: 'P-42', customerId }], total: 1, page: 1, pageSize: 25 } });
    if (path === `customers/${customerId}/communications` && request.method() === 'GET') {
      const snapshot = view(Number(url.searchParams.get('page') || 1)); if (options.delayRead) await readGate;
      return route.fulfill({ json: options.malformed ? { ...snapshot, tenantId: 'other-tenant' } : snapshot });
    }
    if (path.startsWith(`customers/${customerId}/communications`) && request.method() === 'POST') {
      const body = request.postDataJSON(); calls.push({ path, body, headers: request.headers() });
      if (options.conflict) return route.fulfill({ status: 409, json: { code: 'revision_conflict' } });
      if (options.revoke) { readonly = true; return route.fulfill({ status: 403, json: { code: 'forbidden' } }); }
      const item = path.endsWith('/void') ? { ...items[0], voiding: { id: voidId, tenantId: 'tenant-a', revision: 1, createdAt: later, updatedAt: later, archived: false as const,
        customerId, communicationId: items[0].entry.id, sourceDigest: body.sourceDigest, reason: body.reason, voidedAt: later, voidedBy: actor, digest: 'b'.repeat(64) } } : make(body);
      items = [item]; customer = { ...customer, revision: customer.revision + 1 };
      const snapshot = { customerId, tenantId: 'tenant-a', customerName: customer.name, customerRevision: customer.revision, ...structuredClone(item) };
      if (options.delayWrite) await writeGate;
      return route.fulfill({ json: options.malformedChange ? { ...snapshot, entry: { ...snapshot.entry, text: 'Abweichender Inhalt' } } : snapshot });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, releaseRead, releaseWrite };
}
async function open(page: Page) { await page.goto(`/customers?selected=${customerId}`); await page.getByRole('button', { name: 'Kommunikationsakte öffnen', exact: true }).click(); }
async function fill(page: Page) {
  await page.getByRole('button', { name: 'Kommunikation dokumentieren', exact: true }).click();
  await page.getByLabel('Datum und Uhrzeit der Kommunikation', { exact: true }).fill('2026-10-21T14:00');
  await page.getByLabel('Betreff der Kommunikation', { exact: false }).fill('Dachtermin');
  await page.getByLabel('Inhalt und Vereinbarungen', { exact: false }).fill('Termin für Mittwoch abgestimmt.');
  await page.getByLabel('Angaben geprüft; der Eintrag wird unveränderlich dokumentiert.', { exact: true }).check();
}

test('records customer communication with selected contacts and projects and keeps the original after voiding', async ({ page }) => {
  const api = await fixture(page); await open(page); await fill(page);
  await page.getByLabel('Ansprechpartner zur Kommunikation', { exact: true }).selectOption(contactId);
  await page.getByRole('button', { name: 'Projekt suchen', exact: true }).click();
  await page.getByRole('button', { name: 'Dachsanierung Nord · P-42', exact: true }).click();
  await page.getByRole('button', { name: 'Kommunikation speichern', exact: true }).click();
  const record = page.locator('.journal-entry'); await expect(record.getByRole('heading', { name: 'Dachtermin', exact: true })).toBeVisible();
  await expect(record).toContainText('Objektleitung Nord'); await expect(record).toContainText('Dachsanierung Nord');
  expect(api.calls[0].body).toMatchObject({ occurredAt: '2026-10-21T12:00:00.000Z', contactId, projectId }); expect(api.calls[0].headers['if-match']).toBe('"2"'); expect(api.calls[0].headers['idempotency-key']).toBeTruthy();
  await page.getByRole('button', { name: 'Dachtermin stornieren', exact: true }).click(); await page.getByLabel('Grund der Stornierung', { exact: false }).fill('Termin war doppelt erfasst.');
  await page.getByRole('button', { name: 'Eintrag mit Grund stornieren', exact: true }).click();
  await expect(record).toContainText('Termin war doppelt erfasst.'); await expect(record).toContainText('Termin für Mittwoch abgestimmt.');
  expect(api.calls[1].body.sourceDigest).toBe('a'.repeat(64)); expect(api.calls[1].headers['if-match']).toBe('"3"');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('read-only access retains history without journal write controls', async ({ page }) => {
  const api = await fixture(page, { reader: true, existing: true }); await open(page);
  await expect(page.locator('.journal-entry')).toContainText('Dachtermin'); await expect(page.getByRole('button', { name: 'Kommunikation dokumentieren', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Dachtermin stornieren', exact: true })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('archived customer history remains readable without write controls even for a managing director', async ({ page }) => {
  const api = await fixture(page, { archived: true, existing: true }); await open(page);
  await expect(page.locator('.journal-entry')).toContainText('Dachtermin');
  await expect(page.getByText('Dieser Kunde ist archiviert. Die historische Kommunikationsakte bleibt lesbar; neue Einträge und Stornierungen sind gesperrt.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Kommunikation dokumentieren', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Dachtermin stornieren', exact: true })).toHaveCount(0);
  await expect(page.locator('app-customer-contacts')).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('wrong-tenant journal replies are rejected without displaying their entries', async ({ page }) => {
  await fixture(page, { existing: true, malformed: true }); await open(page);
  await expect(page.getByRole('alert').filter({ hasText: 'nicht eindeutig' })).toBeVisible(); await expect(page.locator('.journal-entry')).toHaveCount(0);
});

test('revision conflicts preserve the draft and require an explicit reload', async ({ page }) => {
  const api = await fixture(page, { conflict: true }); await open(page); await fill(page); await page.getByRole('button', { name: 'Kommunikation speichern', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'zwischenzeitlich geändert' })).toBeVisible();
  await expect(page.getByLabel('Inhalt und Vereinbarungen', { exact: false })).toHaveValue('Termin für Mittwoch abgestimmt.');
  await expect(page.getByRole('button', { name: 'Kommunikation speichern', exact: true })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});

test('a changed command response cannot be presented as the requested saved communication', async ({ page }) => {
  await fixture(page, { malformedChange: true }); await open(page); await fill(page); await page.getByRole('button', { name: 'Kommunikation speichern', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'andere Angaben' })).toBeVisible(); await expect(page.locator('.journal-entry')).toHaveCount(0);
});

test('late journal reads cannot reappear after a business switch', async ({ page }) => {
  const api = await fixture(page, { existing: true, delayRead: true }); await open(page); await expect(page.getByText('Kommunikationsakte wird geladen …')).toBeVisible();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseRead();
  await expect(page.locator('.journal-entry')).toHaveCount(0); await expect(page.getByRole('heading', { name: 'Kommunikationsakte', exact: true })).toHaveCount(0);
});

test('revoked journal rights clear the editing form after refreshing the current session', async ({ page }) => {
  const api = await fixture(page, { revoke: true }); await open(page); await fill(page); await page.getByRole('button', { name: 'Kommunikation speichern', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Kommunikation dokumentieren', exact: true })).toHaveCount(0); await expect(page.getByLabel('Inhalt und Vereinbarungen', { exact: false })).toHaveCount(0); expect(api.calls).toHaveLength(1);
});

test('late journal writes cannot replace the new business screen', async ({ page }) => {
  const api = await fixture(page, { delayWrite: true }); await open(page); await fill(page); await page.getByRole('button', { name: 'Kommunikation speichern', exact: true }).click();
  await expect.poll(() => api.calls.length).toBe(1); await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.releaseWrite();
  await expect(page.locator('.journal-entry')).toHaveCount(0); await expect(page.getByRole('heading', { name: 'Kommunikationsakte', exact: true })).toHaveCount(0);
});

test('nonexistent business-local DST time is rejected before submitting a journal entry', async ({ page }) => {
  const api = await fixture(page); await open(page); await fill(page); await page.getByLabel('Datum und Uhrzeit der Kommunikation', { exact: true }).fill('2026-03-29T02:30');
  // Editing the time clears the previous confirmation; wait for that state before confirming again.
  await expect(page.getByLabel('Angaben geprüft; der Eintrag wird unveränderlich dokumentiert.', { exact: true })).not.toBeChecked();
  await page.getByLabel('Angaben geprüft; der Eintrag wird unveränderlich dokumentiert.', { exact: true }).check(); await page.getByRole('button', { name: 'Kommunikation speichern', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'existiert in der Betriebszeitzone nicht' })).toBeVisible(); expect(api.calls).toHaveLength(0);
});

test('repeated business-local DST time requires and retains the explicit occurrence', async ({ page }) => {
  const api = await fixture(page); await open(page); await fill(page); await page.getByLabel('Datum und Uhrzeit der Kommunikation', { exact: true }).fill('2025-10-26T02:30');
  // Editing the time clears the previous confirmation; wait for that state before confirming again.
  await expect(page.getByLabel('Angaben geprüft; der Eintrag wird unveränderlich dokumentiert.', { exact: true })).not.toBeChecked();
  await expect(page.getByLabel('Zeitversatz der Kommunikation', { exact: true })).toBeVisible(); await page.getByLabel('Zeitversatz der Kommunikation', { exact: true }).selectOption('+01:00');
  await page.getByLabel('Angaben geprüft; der Eintrag wird unveränderlich dokumentiert.', { exact: true }).check(); await page.getByRole('button', { name: 'Kommunikation speichern', exact: true }).click();
  await expect(page.locator('.journal-entry')).toContainText('Dachtermin'); expect(api.calls[0].body.occurredAt).toBe('2025-10-26T01:30:00.000Z');
});
