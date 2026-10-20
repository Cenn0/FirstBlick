import { expect, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

// Controlled browser HTTP fixtures. Server parsing, transactions and fault injection
// are covered separately by TradeSourceApiTests against real PostgreSQL.
const tenantA = '10000000-0000-4000-8000-000000000001', tenantB = '10000000-0000-4000-8000-000000000002';
const bytes = Buffer.from('\ufeff<synthetic> äöü <!-- original bytes --> </synthetic>\r\n', 'utf8');
const sha = createHash('sha256').update(bytes).digest('hex');
const id = '20000000-0000-4000-8000-000000000002', catalogId = '60000000-0000-4000-8000-000000000006';
const source = (kind = 'gaeb', tenant = tenantA) => ({ contract: 'firstblick.trade-source/1.0.0', id, tenantId: tenant, format: kind,
  documentId: '30000000-0000-4000-8000-000000000003', fileName: kind === 'gaeb' ? 'original.x83' : 'invoice.xml', sha256: sha, size: bytes.length,
  objectVersionId: 'synthetic-version-id', receivedBy: '40000000-0000-4000-8000-000000000004', receivedAt: '2026-10-20T20:00:00+00:00', digest: 'e'.repeat(64) });
const gaeb = () => ({ contract: 'firstblick.gaeb-structural/1.0.0', sourceSha256: sha, exchangePhase: 'X83', schemaVersion: '3.3', schemaEdition: '2021-05',
  scheduleName: 'Synthetische Originalakte', currency: 'EUR', externalSchemaValidationPerformed: false, reviewIssues: ['no_gaeb_xsd_validation'],
  items: [{ positionKey: '01.001', positionId: 'i1', description: 'Dacharbeit', tenderQuantity: '1', unit: 'm²', offeredUnitPrice: null, itemDigest: 'b'.repeat(64), reviewIssues: [] }] });
const invoice = () => ({ contract: 'firstblick.einvoice-preview/1.0.0', sourceSha256: sha, syntax: 'UBL-2.1-Invoice', profileIdentifier: 'synthetic', invoiceNumber: 'ARCHIVE-1', documentKind: 'Invoice', issueDate: '2026-10-20', currency: 'EUR',
  sellerName: 'Synthetischer Lieferant', buyerName: 'Synthetischer Betrieb', netAmount: '50.00', taxAmount: '9.50', grossAmount: '59.50', payableAmount: '59.50',
  lines: [{ reference: '1', description: 'Dacharbeit', quantity: '1', unitCode: 'C62', netAmount: '50.00' }], reviewIssues: ['not_externally_validated'], externalValidationPerformed: false, mayPostAutomatically: false });
async function fixture(page: Page, accounting = false) {
  const state = { duplicate: false, abortFirst: false, wrongTenant: false, corruptBytes: false, denyDetail: false, holdList: false, holdDownload: false, waiting: false };
  const posts: { path: string; body: Record<string, unknown>; headers: Record<string, string> }[] = [], errors: string[] = [];
  let release: (() => void) | undefined, attempts = 0;
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: '40000000-0000-4000-8000-000000000004', email: 'synthetic@example.test', displayName: 'Synthetic operator' }, activeTenantId: tenantA,
      tenants: [tenantA, tenantB].map((id, index) => ({ id, name: `Betrieb ${index + 1}`, role: accounting ? 'Accounting' : 'ManagingDirector', modules: ['core', 'commercial', 'finance'], capabilities: accounting ? ['supplier-invoices.read', 'supplier-invoices.write'] : ['*'] })) } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path.endsWith('/gaeb/preview')) return route.fulfill({ json: gaeb() });
    if (path.endsWith('/e-invoice/preview')) return route.fulfill({ json: invoice() });
    if (path.startsWith('integrations/v1/trade-sources/')) {
      const parts = path.split('/'), kind = parts[3], tenant = request.headers()['x-tenant-id'] || tenantA;
      const original = source(kind, state.wrongTenant ? tenantB : tenant);
      if (request.method() === 'POST') {
        posts.push({ path, body: request.postDataJSON(), headers: request.headers() }); attempts++;
        if (state.abortFirst && attempts === 1) return route.abort('failed');
        return route.fulfill({ json: { source: original, alreadyStored: state.duplicate, contentVerified: true } });
      }
      if (path.endsWith('/original')) {
        if (state.holdDownload) { state.waiting = true; await new Promise<void>(r => { release = r; }); }
        const output = Buffer.from(bytes); if (state.corruptBytes) output[5] ^= 1;
        return route.fulfill({ body: output, headers: { 'Content-Type': 'application/octet-stream', 'Content-Disposition': 'attachment; filename="original.x83"', 'X-Content-SHA256': sha } });
      }
      if (parts.length === 5) {
        if (state.denyDetail) return route.fulfill({ status: 403, json: { code: 'forbidden' } });
        return route.fulfill({ json: { source: original, preview: kind === 'gaeb' ? gaeb() : invoice(), contentVerified: true,
          catalogItems: kind === 'gaeb' ? [{ id: catalogId, positionKey: '01.001', name: 'Zugeordnete Dacharbeit', unit: 'm²', archived: false }] : [] } });
      }
      if (state.holdList) { state.waiting = true; await new Promise<void>(r => { release = r; }); }
      return route.fulfill({ json: { items: [original], total: 1, page: 1, pageSize: 20, contentVerified: false } });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { state, posts, errors, release: () => release?.() };
}
const archive = (page: Page) => page.locator('app-trade-archive');
async function inspect(page: Page, invoiceMode = false) {
  await page.getByLabel('XML-Originaldatei').setInputFiles({ name: invoiceMode ? 'invoice.xml' : 'original.x83', mimeType: 'application/xml', buffer: bytes });
  await page.getByRole('button', { name: 'XML-Datei prüfen', exact: true }).click();
  await expect(archive(page).getByRole('button', { name: 'Geprüftes XML-Original ablegen', exact: true })).toBeEnabled();
}
async function openDetail(page: Page, name = 'original.x83') {
  await archive(page).getByRole('button', { name: 'Originalakten öffnen', exact: true }).click();
  await archive(page).getByRole('button', { name: `Originalakte ${name} prüfen`, exact: true }).click();
  await expect(archive(page).getByRole('heading', { name: `Geprüfte Originalakte: ${name}`, exact: true })).toBeVisible();
}

test('explicit capture, verified original download and catalogue history preserve exact bytes', async ({ page }, info) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page);
  await archive(page).getByRole('button', { name: 'Geprüftes XML-Original ablegen', exact: true }).click();
  await expect(archive(page).getByRole('status')).toContainText('unverändert abgelegt');
  expect(f.posts).toHaveLength(1); expect(f.posts[0].body).toEqual({ fileName: 'original.x83', sourceBase64: bytes.toString('base64'), expectedSha256: sha });
  expect(f.posts[0].headers['x-tenant-id']).toBe(tenantA); expect(f.posts[0].headers['x-xsrf-token']).toBe('synthetic');
  await archive(page).getByRole('button', { name: 'Gespeicherte Originalakte prüfen', exact: true }).click();
  await expect(archive(page).getByRole('link', { name: '01.001 · Zugeordnete Dacharbeit', exact: true })).toHaveAttribute('href', `/catalog-items?selected=${catalogId}`);
  const downloadEvent = page.waitForEvent('download'); await archive(page).getByRole('button', { name: 'Originaldatei herunterladen', exact: true }).click();
  const download = await downloadEvent; expect(await readFile((await download.path())!)).toEqual(bytes);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(f.errors).toEqual([]); await page.screenshot({ path: info.outputPath('original-archive.png'), fullPage: true });
});

test('a duplicate receipt describes the existing original without claiming another import', async ({ page }) => {
  const f = await fixture(page); f.state.duplicate = true; await page.goto('/trade-imports'); await inspect(page);
  await archive(page).getByRole('button', { name: 'Geprüftes XML-Original ablegen', exact: true }).click();
  await expect(archive(page).getByRole('status')).toContainText('keine zweite Datei'); expect(f.posts).toHaveLength(1); expect(f.errors).toEqual([]);
});

test('an uncertain capture retries the same original and idempotency key', async ({ page }) => {
  const f = await fixture(page); f.state.abortFirst = true; await page.goto('/trade-imports'); await inspect(page);
  const capture = archive(page).getByRole('button', { name: 'Geprüftes XML-Original ablegen', exact: true }); await capture.click();
  await expect(archive(page).getByRole('alert')).toBeVisible(); await expect(archive(page).getByRole('button', { name: 'Gespeicherte Originalakte prüfen' })).toHaveCount(0);
  await capture.click(); await expect(archive(page).getByRole('status')).toContainText('unverändert abgelegt');
  expect(f.posts).toHaveLength(2); expect(f.posts[0].body).toEqual(f.posts[1].body); expect(f.posts[0].headers['idempotency-key']).toBe(f.posts[1].headers['idempotency-key']); expect(f.errors).toEqual([]);
});

test('foreign receipt metadata is rejected rather than presented as successful capture', async ({ page }) => {
  const f = await fixture(page); f.state.wrongTenant = true; await page.goto('/trade-imports'); await inspect(page);
  await archive(page).getByRole('button', { name: 'Geprüftes XML-Original ablegen', exact: true }).click();
  await expect(archive(page).getByRole('alert')).toContainText('aktuellen Betrieb');
  await expect(archive(page).getByRole('button', { name: 'Gespeicherte Originalakte prüfen' })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('corrupted original bytes cannot be downloaded or reopened even with a matching response header', async ({ page }) => {
  const f = await fixture(page); f.state.corruptBytes = true; await page.goto('/trade-imports'); await openDetail(page);
  let downloads = 0; page.on('download', () => downloads++);
  await archive(page).getByRole('button', { name: 'Originaldatei herunterladen', exact: true }).click();
  await expect(archive(page).getByRole('alert')).toContainText('Originalbytes');
  await archive(page).getByRole('button', { name: 'Original im Prüfbereich öffnen', exact: true }).click();
  await expect(archive(page).getByRole('alert')).toContainText('Originalbytes');
  expect(downloads).toBe(0); await expect(page.getByRole('button', { name: 'XML-Datei prüfen', exact: true })).toBeDisabled(); expect(f.errors).toEqual([]);
});

test('late list and download responses cannot survive a business switch', async ({ page }) => {
  const f = await fixture(page); f.state.holdList = true; await page.goto('/trade-imports');
  await archive(page).getByRole('button', { name: 'Originalakten öffnen', exact: true }).click(); await expect.poll(() => f.state.waiting).toBe(true);
  await page.getByLabel('Betrieb', { exact: true }).selectOption(tenantB); f.release(); f.state.holdList = false; f.state.waiting = false;
  await expect(archive(page).getByText('original.x83', { exact: true })).toHaveCount(0);
  await openDetail(page); f.state.holdDownload = true;
  let downloads = 0; page.on('download', () => downloads++);
  await archive(page).getByRole('button', { name: 'Originaldatei herunterladen', exact: true }).click(); await expect.poll(() => f.state.waiting).toBe(true);
  await page.getByLabel('Betrieb', { exact: true }).selectOption(tenantA); f.release();
  await expect(archive(page).getByRole('heading', { name: 'Geprüfte Originalakte:', exact: false })).toHaveCount(0);
  expect(downloads).toBe(0); expect(f.errors).toEqual([]);
});

test('permission denial clears the loaded archive and its download actions', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await openDetail(page); f.state.denyDetail = true;
  await archive(page).getByRole('button', { name: 'Originalakte original.x83 prüfen', exact: true }).click();
  await expect(archive(page).getByRole('alert')).toContainText('Berechtigung');
  await expect(archive(page).getByRole('button', { name: 'Originaldatei herunterladen', exact: true })).toHaveCount(0);
  await expect(archive(page).getByText('original.x83', { exact: true })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('reopening a verified original protects unsaved catalogue edits until discard is confirmed', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page);
  await page.getByRole('checkbox', { name: 'Position 01.001 auswählen', exact: true }).check();
  await page.getByLabel('Eigene Materialkosten je Einheit (€) · 01.001', { exact: true }).fill('12,34'); await openDetail(page);
  page.once('dialog', dialog => dialog.dismiss());
  await archive(page).getByRole('button', { name: 'Original im Prüfbereich öffnen', exact: true }).click();
  await expect(page.getByLabel('Eigene Materialkosten je Einheit (€) · 01.001', { exact: true })).toHaveValue('12,34');
  page.once('dialog', dialog => dialog.accept());
  await archive(page).getByRole('button', { name: 'Original im Prüfbereich öffnen', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: 'Position 01.001 auswählen', exact: true })).not.toBeChecked();
  await expect(page.getByRole('heading', { name: '2 · Leistungsverzeichnis sichten', exact: true })).toBeVisible(); expect(f.errors).toEqual([]);
});

test('accounting can retain and reopen an invoice without any invoice posting or catalogue mutation', async ({ page }) => {
  const f = await fixture(page, true); await page.goto('/trade-imports'); await inspect(page, true);
  await archive(page).getByRole('button', { name: 'Geprüftes XML-Original ablegen', exact: true }).click();
  await archive(page).getByRole('button', { name: 'Gespeicherte Originalakte prüfen', exact: true }).click();
  await expect(archive(page).getByText('E-Rechnung ARCHIVE-1 · Synthetischer Lieferant', { exact: true })).toBeVisible();
  await archive(page).getByRole('button', { name: 'Original im Prüfbereich öffnen', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'E-Rechnungsvorschau: ARCHIVE-1', exact: true })).toBeVisible();
  expect(f.posts.map(p => p.path)).toEqual(['integrations/v1/trade-sources/e-invoice']); expect(f.errors).toEqual([]);
});
