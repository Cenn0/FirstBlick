import { createHash, randomUUID } from 'node:crypto';
import { expect, Page, test } from '@playwright/test';
import type { LedgerOriginal, LedgerOriginalKind } from '../../src/app/core/ledger-originals.models';

const parentId = '73000000-0000-4000-8000-000000000001', sourceId = '73000000-0000-4000-8000-000000000002';
const documentId = '73000000-0000-4000-8000-000000000003', actorId = '73000000-0000-4000-8000-000000000004';
const now = '2026-10-22T01:00:00Z', recorded = '2026-10-22T02:00:00Z';
const pdf = Buffer.from('%PDF-1.7\nSynthetic retained ledger original\n%%EOF');
const sha256 = createHash('sha256').update(pdf).digest('hex');
interface Options {
  reader?: boolean; closed?: boolean; state?: 'Staged' | 'Ready' | 'Archived'; badBinding?: boolean;
  badUpload?: boolean; corruptDownload?: boolean; delayList?: boolean; delayUpload?: boolean; loseStageResponse?: boolean; pages?: boolean;
}

async function fixture(page: Page, kind: LedgerOriginalKind, options: Options = {}) {
  const customer = { id: parentId, tenantId: 'tenant-a', revision: 2, archived: !!options.closed, name: 'Hausverwaltung Nord', contactName: '', email: '', phone: '', address: '', notes: '' };
  const vehicle = { id: parentId, tenantId: 'tenant-a', revision: 2, name: 'Transporter Nord', inventoryNumber: 'FB-03', registration: 'TEST-03', kind: 'Vehicle', archived: !!options.closed, available: true };
  const common = { id: sourceId, tenantId: 'tenant-a', revision: 1, archived: false, createdAt: now, updatedAt: now, recordedAt: now, recordedBy: actorId, digest: 'a'.repeat(64) };
  const communication = { ...common, customerId: parentId, customerName: customer.name, channel: 'Email', direction: 'Incoming', occurredAt: '2026-10-21T12:00:00Z',
    subject: 'Dachtermin', text: 'Termin für Mittwoch abgestimmt.', contact: null, project: null };
  const cost = { ...common, vehicleId: parentId, vehicleSnapshot: { id: parentId, name: vehicle.name, inventoryNumber: vehicle.inventoryNumber, registration: vehicle.registration, kind: 'Vehicle' },
    projectId: null, projectTitle: null, spentOn: '2026-10-21', category: 'Fuel', amount: '40.00', currency: 'EUR', reference: 'Tankbeleg-42', notes: '' };
  const original = (state: 'Staged' | 'Ready' | 'Archived'): LedgerOriginal => ({ id: documentId, tenantId: 'tenant-a', name: 'Original.pdf', contentType: 'application/pdf',
    size: pdf.length, sha256, versionId: state === 'Staged' ? null : 'retained-version-1', state, revision: state === 'Staged' ? 1 : 2,
    projectId: null, sourceKind: kind, sourceId, sourceDigest: common.digest,
    rendererVersion: kind === 'customer-communications' ? 'customer-communication-original-v1' : 'vehicle-cost-original-v1', createdAt: recorded });
  let documents = options.state ? [original(options.state)] : [];
  if (options.pages) documents = Array.from({ length: 21 }, (_, i) => ({ ...original('Ready'), id: i === 0 ? documentId : randomUUID(), name: `Original-${i + 1}.pdf` }));
  const writes: { path: string; headers: Record<string, string>; body: any }[] = [], stageReceipts = new Map<string, LedgerOriginal>();
  let lostStageResponse = false, release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  const base = `${kind}/${sourceId}/documents`;
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.slice('/api/v1/'.length), other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: actorId, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a', tenants: ['a', 'b'].map(id => ({
      id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role: options.reader ? 'Accounting' : 'ManagingDirector', modules: ['core', 'purchasing', 'field'],
      capabilities: options.reader ? ['customers.read', 'projects.read', 'vehicle-trips.read', 'vehicle-costs.read', 'equipment.read', 'documents.read'] : ['*'] })) } });
    if (path === 'customers') return route.fulfill({ json: { items: other || options.closed ? [] : [customer], total: other || options.closed ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `customers/${parentId}`) return route.fulfill({ json: customer });
    if (path === `customers/${parentId}/contacts`) return route.fulfill({ json: { customerId: parentId, tenantId: 'tenant-a', customerName: customer.name, revision: customer.revision, contacts: [] } });
    if (path === `customers/${parentId}/communications`) return route.fulfill({ json: { customerId: parentId, tenantId: 'tenant-a', customerName: customer.name, customerRevision: 2,
      customerArchived: customer.archived, timeZone: 'Europe/Berlin', items: [{ entry: communication, voiding: null }], total: 1, page: 1, pageSize: 20 } });
    if (path === 'vehicle-ledger/vehicles') return route.fulfill({ json: { items: other || options.closed ? [] : [vehicle], total: other || options.closed ? 0 : 1, page: 1, pageSize: 25, businessDate: '2026-10-22', timeZone: 'Europe/Berlin' } });
    if (path === `equipment/${parentId}/vehicle-ledger`) return route.fulfill({ json: { vehicle, businessDate: '2026-10-22', timeZone: 'Europe/Berlin', month: '2026-10',
      summary: { tripCount: 0, reversedTripCount: 0, distanceKm: '0', costCount: 1, reversedCostCount: 0, costAmount: '40.00', costPerKm: null },
      trips: { items: [], total: 0, page: 1, pageSize: 20 }, costs: { items: [{ entry: cost, reversal: null }], total: 1, page: 1, pageSize: 20 } } });
    if (path === 'documents') return route.fulfill({ json: { items: other ? [] : documents, total: other ? 0 : documents.length, page: 1, pageSize: 25 } });
    if (path === `${kind}/${sourceId}/document-context`) return route.fulfill({ json: { sourceKind: kind, source: common,
      title: kind === 'customer-communications' ? communication.subject : cost.reference, parentKind: kind === 'customer-communications' ? 'customers' : 'equipment', parentId } });
    if (path === base) {
      if (request.method() === 'POST') {
        const body = request.postDataJSON(), key = request.headers()['idempotency-key']; writes.push({ path, headers: request.headers(), body });
        let staged = stageReceipts.get(key);
        if (!staged) {
          staged = { ...original('Staged'), id: documents.length ? randomUUID() : documentId, name: body.name, contentType: body.contentType, size: body.size, sha256: body.sha256 };
          stageReceipts.set(key, staged); documents = [staged, ...documents];
        }
        if (options.loseStageResponse && !lostStageResponse) { lostStageResponse = true; return route.abort('failed'); }
        return route.fulfill({ json: staged });
      }
      const pageNumber = Number(url.searchParams.get('page') || 1);
      const snapshot = documents.slice((pageNumber - 1) * 20, pageNumber * 20);
      if (options.delayList) await gate;
      return route.fulfill({ json: { sourceKind: kind, sourceId, sourceDigest: common.digest, canAttach: !options.reader && !options.closed,
        items: snapshot.map(doc => options.badBinding ? { ...doc, sourceDigest: 'b'.repeat(64) } : doc), total: documents.length, page: pageNumber, pageSize: 20 } });
    }
    const document = documents.find(doc => path.startsWith(`${base}/${doc.id}/`));
    if (document && path.endsWith('/content')) {
      writes.push({ path, headers: request.headers(), body: request.postDataBuffer() });
      const ready: LedgerOriginal = { ...document, state: 'Ready', revision: 2, versionId: 'retained-version-1' };
      documents = documents.map(doc => doc.id === ready.id ? ready : doc);
      if (options.delayUpload) await gate;
      return route.fulfill({ json: options.badUpload ? { ...ready, sourceDigest: 'b'.repeat(64) } : ready });
    }
    if (document && path.endsWith('/download')) return route.fulfill({ body: options.corruptDownload ? Buffer.from('%PDF-1.7\nCorrupted') : pdf,
      headers: { 'Content-Type': 'application/pdf', 'X-Content-SHA256': sha256 } });
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { writes, release, count: () => documents.length };
}

async function open(page: Page, kind: LedgerOriginalKind) {
  if (kind === 'customer-communications') {
    await page.goto(`/customers?selected=${parentId}`);
    await page.getByRole('button', { name: 'Kommunikationsakte öffnen', exact: true }).click();
  } else {
    await page.goto(`/vehicle-ledger?equipmentId=${parentId}&month=2026-10`);
    await page.getByRole('button', { name: 'Fahrzeugkosten', exact: true }).click();
  }
  await page.getByRole('button', { name: 'Originaldateien öffnen', exact: true }).click();
}
const select = (page: Page, buffer: Buffer = pdf) => page.getByLabel('Originaldatei auswählen', { exact: true })
  .setInputFiles({ name: 'Original.pdf', mimeType: 'application/pdf', buffer });

for (const kind of ['customer-communications', 'vehicle-costs'] as const) {
  test(`${kind}: uploads, verifies and downloads an original without posting another ledger entry`, async ({ page }) => {
    const api = await fixture(page, kind); await open(page, kind); await select(page);
    await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Originaldatei vollständig gespeichert und geprüft.' })).toBeVisible();
    expect(api.writes).toHaveLength(2); expect(api.writes[0].body).toMatchObject({ sha256, size: pdf.length, projectId: null });
    expect(api.writes[0].headers['if-match']).toBe('"1"'); expect(api.writes[1].headers['if-match']).toBe('"1"'); expect(api.writes[1].body).toEqual(pdf);
    const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click();
    expect((await download).suggestedFilename()).toBe('Original.pdf');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (kind === 'vehicle-costs') await expect(page.locator('.vehicle-summary')).toContainText('40,00 €');
  });

  test(`${kind}: accounting reads archived files without upload controls`, async ({ page }) => {
    const api = await fixture(page, kind, { reader: true, state: 'Archived' }); await open(page, kind);
    await expect(page.getByText('Archiviert · Original erhalten', { exact: false })).toBeVisible();
    await expect(page.getByLabel('Originaldatei auswählen', { exact: true })).toHaveCount(0);
    const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click(); await download;
    expect(api.writes).toHaveLength(0);
  });

  test(`${kind}: original files on archived parents remain readable without new transfers`, async ({ page }) => {
    const api = await fixture(page, kind, { closed: true, state: 'Ready' }); await open(page, kind);
    await expect(page.getByLabel('Originaldatei auswählen', { exact: true })).toHaveCount(0);
    const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click(); await download;
    expect(api.writes).toHaveLength(0);
  });

  test(`${kind}: interrupted transfers accept only matching original bytes`, async ({ page }) => {
    const api = await fixture(page, kind, { state: 'Staged' }); await open(page, kind);
    await page.getByRole('button', { name: 'Übertragung fortsetzen', exact: true }).click(); await select(page, Buffer.from('%PDF-1.7\nChanged original'));
    await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'entspricht nicht der begonnenen' })).toBeVisible(); expect(api.writes).toHaveLength(0);
    await select(page); await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Originaldatei vollständig gespeichert und geprüft.' })).toBeVisible();
    expect(api.writes).toHaveLength(1); expect(api.writes[0].path).toMatch(/\/content$/);
  });

  test(`${kind}: an uncertain stage response retries with its original key and no duplicate file`, async ({ page }) => {
    const api = await fixture(page, kind, { loseStageResponse: true }); await open(page, kind); await select(page);
    await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click();
    await expect(page.locator('app-ledger-originals').getByRole('alert')).toBeVisible(); await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Originaldatei vollständig gespeichert und geprüft.' })).toBeVisible();
    expect(api.count()).toBe(1); expect(api.writes[0].headers['idempotency-key']).toBe(api.writes[1].headers['idempotency-key']);
  });

  test(`${kind}: substituted original bindings do not become visible or usable`, async ({ page }) => {
    await fixture(page, kind, { state: 'Ready', badBinding: true }); await open(page, kind);
    await expect(page.locator('app-ledger-originals').getByRole('alert').filter({ hasText: 'nicht eindeutig' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Original herunterladen', exact: true })).toHaveCount(0);
  });

  test(`${kind}: a changed upload receipt cannot announce a saved original`, async ({ page }) => {
    await fixture(page, kind, { badUpload: true }); await open(page, kind); await select(page);
    await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click();
    await expect(page.locator('app-ledger-originals').getByRole('alert').filter({ hasText: 'nicht eindeutig' })).toBeVisible();
    await expect(page.getByText('Originaldatei vollständig gespeichert und geprüft.', { exact: true })).toHaveCount(0);
  });

  test(`${kind}: corrupted download bytes never create a browser download`, async ({ page }) => {
    await fixture(page, kind, { state: 'Ready', corruptDownload: true }); await open(page, kind);
    const downloads: string[] = []; page.on('download', value => downloads.push(value.suggestedFilename()));
    await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'beschädigt' })).toBeVisible(); expect(downloads).toHaveLength(0);
  });

  test(`${kind}: late list responses cannot reopen files after switching business`, async ({ page }) => {
    const api = await fixture(page, kind, { state: 'Ready', delayList: true }); await open(page, kind);
    await expect(page.getByText('Originaldateien werden geladen …', { exact: true })).toBeVisible();
    await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
    await expect(page.locator('app-ledger-originals')).toHaveCount(0);
  });

  test(`${kind}: late upload responses are discarded after switching business`, async ({ page }) => {
    const api = await fixture(page, kind, { delayUpload: true }); await open(page, kind); await select(page);
    await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click(); await expect.poll(() => api.writes.length).toBe(2);
    await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
    await expect(page.locator('app-ledger-originals')).toHaveCount(0);
    await expect(page.getByText('Originaldatei vollständig gespeichert und geprüft.', { exact: true })).toHaveCount(0);
  });

  test(`${kind}: paginated original lists retain the source and show the requested page`, async ({ page }) => {
    await fixture(page, kind, { pages: true }); await open(page, kind);
    await expect(page.locator('app-ledger-originals .original-list li')).toHaveCount(20);
    await page.getByRole('button', { name: 'Weitere Originale', exact: true }).click();
    await expect(page.locator('app-ledger-originals .original-list li')).toHaveCount(1);
    await expect(page.locator('app-ledger-originals')).toContainText('Original-21.pdf');
    await expect(page.getByRole('button', { name: 'Weitere Originale', exact: true })).toBeDisabled();
  });

  test(`${kind}: the general document list resumes through its verified original source`, async ({ page }) => {
    const api = await fixture(page, kind, { state: 'Staged' }); await page.goto('/documents');
    await expect(page.getByRole('button', { name: 'Upload fortsetzen', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'In der Originalakte fortsetzen', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Verknüpfte Originalakte', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Originaldateien öffnen', exact: true }).click();
    await page.getByRole('button', { name: 'Übertragung fortsetzen', exact: true }).click(); await select(page);
    await page.getByRole('button', { name: 'Originaldatei speichern', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Originaldatei vollständig gespeichert und geprüft.' })).toBeVisible();
    expect(api.writes).toHaveLength(1); expect(api.writes[0].path).toBe(`${kind}/${sourceId}/documents/${documentId}/content`);
    await expect(page.getByRole('button', { name: 'Originalakte öffnen', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'In der Originalakte fortsetzen', exact: true })).toHaveCount(0);
    await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
    await expect(page.getByRole('region', { name: 'Verknüpfte Originalakte', exact: true })).toHaveCount(0);
  });
}
