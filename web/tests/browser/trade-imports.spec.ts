import { expect, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';

const bytes = Buffer.from('\ufeff<?xml version="1.0" encoding="UTF-8"?>\r\n<synthetic>Original: äöü &amp; 12</synthetic>\r\n', 'utf8');
const hash = createHash('sha256').update(bytes).digest('hex'), itemDigest = 'b'.repeat(64), previewDigest = 'c'.repeat(64);
const catalogId = '40000000-0000-4000-8000-000000000004';
// These are controlled HTTP fixtures, distinct from real GAEB parser/PostgreSQL integration tests.
function gaebPreview(count = 1) { return {
  contract: 'firstblick.gaeb-structural/1.0.0', sourceSha256: hash, exchangePhase: 'X83', schemaVersion: '3.3', schemaEdition: '2021-05', scheduleName: 'Synthetisches Leistungsverzeichnis', currency: 'EUR', externalSchemaValidationPerformed: false,
  reviewIssues: ['no_gaeb_xsd_validation_or_exchange_roundtrip_performed'],
  items: Array.from({ length: count }, (_, i) => ({ positionKey: `01.${String(i + 1).padStart(3, '0')}`, positionId: `item-${i + 1}`, description: `Synthetische Leistung ${i + 1}`, tenderQuantity: '12', unit: 'm²', offeredUnitPrice: '99.00', itemDigest, reviewIssues: [] }))
}; }
function invoicePreview() { return {
  contract: 'firstblick.einvoice-preview/1.0.0', sourceSha256: hash, syntax: 'UBL-2.1-Invoice', profileIdentifier: 'synthetic-profile-not-validated', invoiceNumber: 'SYN-2026-01', documentKind: 'Invoice', issueDate: '2026-10-20', currency: 'EUR', sellerName: 'Synthetischer Lieferant', buyerName: 'Synthetischer Kunde',
  netAmount: '9007199254740993.01', taxAmount: '0.00', grossAmount: '9007199254740993.01', payableAmount: '9007199254740993.01',
  lines: [{ reference: '1', description: '<img src=x onerror=alert(1)> literal XML description', quantity: '1', unitCode: 'C62', netAmount: '9007199254740993.01' }],
  reviewIssues: ['external_xsd_schematron_and_tax_profile_validation_not_performed', 'no_accounting_import_or_supplier_matching'], externalValidationPerformed: false, mayPostAutomatically: false
}; }
async function fixture(page: Page, role = 'ManagingDirector') {
  const state = { wrongHash: false, hold: false, itemCount: 1, rejectPreview: false, corruptReceipt: false, abortFirstImport: false };
  const posts: { path: string; bytes: Buffer | null; body: Record<string, unknown> | null; headers: Record<string, string> }[] = [], errors: string[] = [];
  let release: (() => void) | undefined, importAttempts = 0;
  page.on('pageerror', e => errors.push(e.message));
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: 'synthetic-user', email: 'synthetic@example.test', displayName: 'Synthetic operator' }, activeTenantId: 'tenant-a', tenants: ['a', 'b'].map(k => ({ id: `tenant-${k}`, name: `Betrieb ${k.toUpperCase()}`, role, modules: ['core', 'commercial', 'finance'], capabilities: role === 'ManagingDirector' ? ['*'] : role === 'Accounting' ? ['supplier-invoices.read'] : ['projects.read'] })) } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path.startsWith('integrations/v1/') && request.method() === 'POST') {
      const xml = path.endsWith('/preview');
      const body = xml ? null : request.postDataJSON() as Record<string, unknown>;
      posts.push({ path, body, bytes: request.postDataBuffer(), headers: request.headers() });
      if (xml) {
        if (state.hold) await new Promise<void>(resolve => { release = resolve; });
        if (state.rejectPreview) return route.fulfill({ status: 403, json: { code: 'forbidden' } });
        const preview = path.includes('/e-invoice/') ? invoicePreview() : gaebPreview(state.itemCount);
        return route.fulfill({ json: { ...preview, sourceSha256: state.wrongHash ? 'd'.repeat(64) : hash } });
      }
      if (path.endsWith('/catalog-preview')) {
        const selections = body!['selections'] as Record<string, string>[];
        return route.fulfill({ json: { contract: 'firstblick.gaeb-catalog-preview/1.0.0', sourceSha256: hash, phase: 'X83', digest: previewDigest, schemaValidated: false, reviewIssues: ['reviewed_internal_catalogue_mapping_only'], candidates: selections.map(s => { const item = gaebPreview(state.itemCount).items.find(i => i.positionKey === s['positionKey'])!; const { priceEvidence, ...costs } = s; return { ...costs, description: item.description, unit: item.unit, tenderQuantity: item.tenderQuantity, offeredUnitPrice: item.offeredUnitPrice, origin: { sourceSha256: hash, positionKey: item.positionKey, itemDigest, exchangePhase: 'X83', priceEvidence } }; }) } });
      }
      if (path.endsWith('/catalog-import')) {
        importAttempts++;
        if (state.abortFirstImport && importAttempts === 1) return route.abort('failed');
        const selections = body!['selections'] as Record<string, string>[];
        return route.fulfill({ json: { contract: 'firstblick.gaeb-catalog-import/1.0.0', sourceSha256: hash, reviewedPreviewDigest: previewDigest, items: state.corruptReceipt ? [] : selections.map(s => { const item = gaebPreview(state.itemCount).items.find(i => i.positionKey === s['positionKey'])!; return { positionKey: s['positionKey'], catalogItemId: catalogId, name: item.description, unit: item.unit }; }) } });
      }
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { state, posts, errors, release: () => release?.() };
}
async function inspect(page: Page) { await page.getByLabel('XML-Originaldatei').setInputFiles({ name: 'synthetic.x83', mimeType: 'application/xml', buffer: bytes }); await page.getByRole('button', { name: 'XML-Datei prüfen', exact: true }).click(); }
async function prepareMapping(page: Page) {
  await inspect(page); await page.getByRole('checkbox', { name: 'Position 01.001 auswählen', exact: true }).check();
  await expect(page.getByLabel('Eigene Materialkosten je Einheit (€) · 01.001', { exact: true })).toHaveValue('');
  for (const [label, value] of [['Eigene Materialkosten je Einheit (€)', '12,30'], ['Eigene Arbeitsstunden je Einheit', '0'], ['Eigener Stundensatz (€)', '50'], ['Zuschlag (%)', '10'], ['Umsatzsteuer (%)', '19']]) await page.getByLabel(`${label} · 01.001`, { exact: true }).fill(value);
  await page.getByLabel('Preisgrundlage · 01.001', { exact: true }).fill('Eigene synthetische Kalkulation 2026-10-20');
  await page.getByRole('button', { name: 'Katalogzuordnung prüfen', exact: true }).click();
  await expect(page.getByRole('heading', { name: '4 · Geprüfte Zuordnung bestätigen', exact: true })).toBeVisible();
}
const consent = (page: Page) => page.getByRole('checkbox', { name: 'Ich habe Zuordnung, eigene Kosten und Preisgrundlagen', exact: false });

test('unchanged original XML is mapped with explicit own costs and imported only after confirmed review', async ({ page }, info) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await prepareMapping(page);
  const importButton = page.getByRole('button', { name: 'Geprüfte Leistungen übernehmen', exact: true }); await expect(importButton).toBeDisabled(); await consent(page).check(); await importButton.click();
  await expect(page.getByRole('heading', { name: 'Importbeleg: 1 Leistungen gespeichert', exact: true })).toBeVisible();
  const xml = f.posts[0]; expect(xml.bytes).toEqual(bytes); expect(xml.headers['content-type']).toContain('application/xml'); expect(xml.headers['x-tenant-id']).toBe('tenant-a'); expect(xml.headers['x-xsrf-token']).toBe('synthetic');
  const imported = f.posts.find(p => p.path.endsWith('/catalog-import'))!; expect(imported.body?.['sourceBase64']).toBe(bytes.toString('base64')); expect(imported.body?.['previewDigest']).toBe(previewDigest); expect(imported.body?.['selections']).toEqual([{ positionKey: '01.001', unitMaterialCost: '12.3', unitLaborHours: '0', hourlyRate: '50', markupPercent: '10', vatPercent: '19', priceEvidence: 'Eigene synthetische Kalkulation 2026-10-20' }]);
  expect(imported.headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/i); await expect(page.getByRole('link', { name: '01.001 · Synthetische Leistung 1', exact: true })).toHaveAttribute('href', `/catalog-items?selected=${catalogId}`);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(f.errors).toEqual([]); await page.screenshot({ path: info.outputPath('gaeb-catalogue.png'), fullPage: true });
});

test('editing a reviewed cost invalidates the digest and consent before any write', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await prepareMapping(page); await consent(page).check(); await page.getByLabel('Eigene Materialkosten je Einheit (€) · 01.001', { exact: true }).fill('13');
  await expect(page.getByRole('button', { name: 'Geprüfte Leistungen übernehmen', exact: true })).toHaveCount(0); expect(f.posts.filter(p => p.path.endsWith('/catalog-import'))).toHaveLength(0); expect(f.errors).toEqual([]);
});

test('a mismatching original-file SHA blocks structural review and catalogue mapping', async ({ page }) => {
  const f = await fixture(page); f.state.wrongHash = true; await page.goto('/trade-imports'); await inspect(page); await expect(page.getByRole('alert')).toContainText('Dateiprüfsumme'); await expect(page.getByRole('checkbox', { name: 'Position 01.001 auswählen' })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('an uncertain import retries the exact same body and idempotency key without inventing success', async ({ page }) => {
  const f = await fixture(page); f.state.abortFirstImport = true; await page.goto('/trade-imports'); await prepareMapping(page); await consent(page).check(); const button = page.getByRole('button', { name: 'Geprüfte Leistungen übernehmen', exact: true }); await button.click();
  await expect(page.getByRole('alert')).toBeVisible(); await expect(page.getByRole('heading', { name: 'Importbeleg:', exact: false })).toHaveCount(0); await expect(button).toBeEnabled(); await button.click();
  await expect(page.getByRole('heading', { name: 'Importbeleg: 1 Leistungen gespeichert', exact: true })).toBeVisible(); const attempts = f.posts.filter(p => p.path.endsWith('/catalog-import')); expect(attempts).toHaveLength(2); expect(attempts[0].body).toEqual(attempts[1].body); expect(attempts[0].headers['idempotency-key']).toBe(attempts[1].headers['idempotency-key']); expect(f.errors).toEqual([]);
});

test('a malformed successful receipt is not called complete and blocks another import', async ({ page }) => {
  const f = await fixture(page); f.state.corruptReceipt = true; await page.goto('/trade-imports'); await prepareMapping(page); await consent(page).check(); await page.getByRole('button', { name: 'Geprüfte Leistungen übernehmen', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Importbeleg ist unvollständig'); await expect(page.getByRole('button', { name: 'Geprüfte Leistungen übernehmen', exact: true })).toBeDisabled(); await expect(page.getByRole('heading', { name: 'Importbeleg:', exact: false })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('a late preview from a different business cannot restore a file or its positions', async ({ page }) => {
  const f = await fixture(page); f.state.hold = true; await page.goto('/trade-imports'); await inspect(page); await expect.poll(() => f.posts.length).toBe(1); await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); f.release();
  await expect(page.getByRole('button', { name: 'XML-Datei prüfen', exact: true })).toBeDisabled(); await expect(page.getByRole('heading', { name: '2 · Leistungsverzeichnis sichten', exact: true })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('revoked permission discards loaded XML data and leaves no import action', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await prepareMapping(page); f.state.rejectPreview = true; await page.getByRole('button', { name: 'XML-Datei prüfen', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Berechtigung'); await expect(page.getByRole('button', { name: 'XML-Datei prüfen', exact: true })).toBeDisabled(); await expect(page.getByRole('button', { name: 'Geprüfte Leistungen übernehmen', exact: true })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('accounting can inspect literal invoice data with exact decimal digits but cannot import catalogue costs or post an invoice', async ({ page }, info) => {
  const f = await fixture(page, 'Accounting'); await page.goto('/trade-imports'); await expect(page.getByRole('button', { name: 'GAEB-Leistungskatalog', exact: true })).toHaveCount(0); await inspect(page);
  await expect(page.getByRole('heading', { name: 'E-Rechnungsvorschau: SYN-2026-01', exact: true })).toBeVisible(); await expect(page.getByText('<img src=x onerror=alert(1)> literal XML description', { exact: true })).toBeVisible(); await expect(page.locator('.panel img')).toHaveCount(0);
  await expect(page.getByText('9.007.199.254.740.993,01 EUR', { exact: true })).toHaveCount(3); await expect(page.getByText('Keine Buchung.', { exact: true })).toBeVisible(); expect(f.posts.map(p => p.path)).toEqual(['integrations/v1/e-invoice/preview']);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(f.errors).toEqual([]); await page.screenshot({ path: info.outputPath('e-invoice-preview.png'), fullPage: true });
});

test('an oversized XML file is rejected before network transfer', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await page.getByLabel('XML-Originaldatei').setInputFiles({ name: 'too-large.xml', mimeType: 'application/xml', buffer: Buffer.alloc(1024 * 1024 + 1, 32) });
  await expect(page.getByRole('alert')).toContainText('höchstens 1 MiB'); await expect(page.getByRole('button', { name: 'XML-Datei prüfen', exact: true })).toBeDisabled(); expect(f.posts).toEqual([]); expect(f.errors).toEqual([]);
});

test('paged position preview preserves explicit selection without silently importing unseen rows', async ({ page }) => {
  const f = await fixture(page); f.state.itemCount = 26; await page.goto('/trade-imports'); await inspect(page); await page.getByRole('checkbox', { name: 'Position 01.001 auswählen', exact: true }).check();
  await page.getByRole('button', { name: 'Nächste Vorschauseite', exact: true }).click(); await expect(page.getByRole('checkbox', { name: 'Position 01.026 auswählen', exact: true })).toBeVisible(); await expect(page.getByRole('checkbox', { name: 'Position 01.026 auswählen', exact: true })).not.toBeChecked();
  await page.getByRole('button', { name: 'Vorherige Vorschauseite', exact: true }).click(); await expect(page.getByRole('checkbox', { name: 'Position 01.001 auswählen', exact: true })).toBeChecked(); expect(f.posts.filter(p => p.path.endsWith('/catalog-import'))).toHaveLength(0); expect(f.errors).toEqual([]);
});

test('worker access does not load or expose any trade XML controls', async ({ page }) => {
  const f = await fixture(page, 'Worker'); await page.goto('/trade-imports'); await expect(page.getByRole('heading', { name: 'Zugriff eingeschränkt' })).toBeVisible(); await expect(page.getByLabel('XML-Originaldatei')).toHaveCount(0); expect(f.posts).toEqual([]); expect(f.errors).toEqual([]);
});
