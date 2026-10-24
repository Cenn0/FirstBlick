import { expect, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { GaebPreview } from '../../src/app/core/trade-import.models';
import { exportGaebPriceWorksheet, GaebWorksheetPrice } from '../../src/app/core/gaeb-price-worksheet';

// Controlled HTTP fixtures: browser interactions are real, XSD/PostgreSQL are not.
const original = Buffer.from('<?xml version="1.0" encoding="UTF-8"?><synthetic>worksheet test</synthetic>');
const sha = createHash('sha256').update(original).digest('hex');
const projectId = '10000000-0000-4000-8000-000000000001';
function source(): GaebPreview {
  return { contract: 'firstblick.gaeb-structural/1.0.0', sourceSha256: sha, exchangePhase: 'X83',
    schemaVersion: '3.3', schemaEdition: '2021-05', currency: 'EUR', scheduleName: 'Synthetic worksheet LV',
    externalSchemaValidationPerformed: false, reviewIssues: [],
    items: [0, 1].map(i => ({ positionKey: `01.000${i + 1}`, positionId: `item-${i}`,
      description: `Synthetische Leistung ${i + 1}`, tenderQuantity: '12.500', unit: 'm²',
      offeredUnitPrice: null, itemDigest: (i ? 'c' : 'b').repeat(64), reviewIssues: [] })) };
}
function sheet(firstPrice = '12.345678'): string {
  const doc = source();
  const values: GaebWorksheetPrice[] = doc.items.map((item, i) => ({ positionKey: item.positionKey,
    expectedItemDigest: item.itemDigest, unitPrice: i ? '' : firstPrice, notOffered: i > 0,
    priceEvidence: 'Synthetische Preisprüfung' }));
  return exportGaebPriceWorksheet(doc, values);
}
async function fixture(page: Page) {
  const posts: { path: string; body: Record<string, unknown> | null }[] = [], errors: string[] = [];
  const state = { schemaValid: true };
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: 'synthetic-user', email: 'synthetic@example.test', displayName: 'Synthetic' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(k => ({ id: `tenant-${k}`, name: `Betrieb ${k.toUpperCase()}`, role: 'ManagingDirector', modules: ['core', 'commercial', 'finance'], capabilities: ['*'] })) } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: projectId, revision: 1, archived: false, name: 'Synthetic project' }], total: 1, page: 1, pageSize: 25 } });
    if (request.method() === 'POST' && path.startsWith('integrations/')) {
      const body = request.headers()['content-type']?.includes('application/json') ? request.postDataJSON() as Record<string, unknown> : null;
      posts.push({ path, body });
      if (path === 'integrations/v1/gaeb/preview') return route.fulfill({ json: source() });
      const rule = { sourceSha256: sha, phase: 'X83', schemaEdition: '2021-05', schemaBundleSha256: 'd'.repeat(64), valid: state.schemaValid, issues: state.schemaValid ? [] : [{ code: 'GAEB-XSD', severity: 'error', message: 'The element is invalid according to its schema.' }] };
      if (path === 'integrations/v2/gaeb/validate') return route.fulfill({ json: { validation: rule, readyForBid: state.schemaValid, originalBytesRetained: false } });
      if (path === 'integrations/v2/gaeb/bid-preview') {
        const mapping = body!['mapping'] as { prices: { positionKey: string; unitPrice: string | null; notOffered: boolean }[] };
        return route.fulfill({ json: { contract: 'firstblick.gaeb-bid-preview/2.0.0', tenantId: 'tenant-a', projectId, projectRevision: 1,
          sourceSha256: sha, originalSchema: rule, exportSchema: { ...rule, phase: 'X84' }, sourcePhase: 'X83', outputPhase: 'X84',
          xmlSha256: 'e'.repeat(64), mappingDigest: 'f'.repeat(64), digest: '9'.repeat(64), canExport: true,
          totalNet: '154.32', lines: mapping.prices.map((p, i) => ({ ...p, originalItemId: `item-${i}`, quantity: '12.500', lineNet: i ? '0.00' : '154.32' })),
          phaseExcludedSourceFields: [], deliveryPerformed: false, independentlyAcceptedByRecipient: false } });
      }
      return route.fulfill({ status: 400, json: { code: 'unexpected_fixture_write' } });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { posts, errors, state };
}
const panel = (page: Page) => page.locator('app-gaeb-bid-exchange');
async function inspect(page: Page) {
  await panel(page).locator('#gb-original').setInputFiles({ name: 'synthetic.x83', mimeType: 'application/xml', buffer: original });
  await panel(page).getByRole('button', { name: 'Originaldatei strukturell und gegen offizielle XSD prüfen', exact: true }).click();
  await expect(panel(page).getByRole('button', { name: 'Aktuelle Preise als CSV herunterladen', exact: true })).toBeVisible();
}
async function importSheet(page: Page, csv = sheet()) {
  await panel(page).locator('#gb-worksheet').setInputFiles({ name: 'preise.csv', mimeType: 'text/csv', buffer: Buffer.from(csv, 'utf8') });
}
async function confirm(page: Page) {
  await panel(page).locator('#gb-worksheet-consent').check();
  await panel(page).getByRole('button', { name: 'Geprüfte Preisarbeitsliste übernehmen', exact: true }).click();
}
async function bidder(page: Page) {
  await panel(page).getByRole('button', { name: 'Projekte anzeigen', exact: true }).click();
  await panel(page).locator('#gb-project').selectOption(projectId);
  for (const [id, value] of [['date', '2026-10-21'], ['name', 'Synthetic Roofing'], ['street', 'Testweg 1'],
    ['postcode', '12345'], ['city', 'Testort'], ['contact', 'Synthetic'], ['email', 'synthetic@example.test'], ['review', 'Synthetische Angebotsprüfung']])
    await panel(page).locator(`#gb-${id}`).fill(value);
}

test('worksheet download is source-bound and import requires separate review before server bid validation', async ({ page }, info) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page);
  const downloadEvent = page.waitForEvent('download');
  await panel(page).getByRole('button', { name: 'Aktuelle Preise als CSV herunterladen', exact: true }).click();
  const downloaded = await downloadEvent, stream = await downloaded.createReadStream();
  expect(downloaded.suggestedFilename()).toBe(`firstblick-gaeb-preise-${sha.slice(0, 12)}.csv`);
  expect(stream).not.toBeNull(); const chunks: Buffer[] = [];
  for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
  const csv = Buffer.concat(chunks).toString('utf8');
  expect(csv).toContain('sha256:' + sha); expect(csv).toContain('"pos:01.0001"'); expect(csv).toContain('"pending"');
  await bidder(page); await importSheet(page);
  await expect(panel(page).getByRole('heading', { name: 'Preisänderungen vor Übernahme prüfen', exact: true })).toBeVisible();
  await expect(panel(page).locator('#gb-price-0')).toHaveValue('');
  const apply = panel(page).getByRole('button', { name: 'Geprüfte Preisarbeitsliste übernehmen', exact: true });
  await expect(apply).toBeDisabled(); expect(f.posts).toHaveLength(2);
  await confirm(page);
  await expect(panel(page).locator('#gb-price-0')).toHaveValue('12.345678');
  await expect(panel(page).locator('#gb-skip-1')).toBeChecked();
  await expect(panel(page).locator('#gb-evidence-0')).toHaveValue('Synthetische Preisprüfung');
  expect(f.posts).toHaveLength(2);
  await panel(page).getByRole('button', { name: 'X84 bilden und nach Original-XSD prüfen', exact: true }).click();
  await expect(panel(page).getByRole('heading', { name: '5 · X84-Ausgabe kontrollieren', exact: true })).toBeVisible();
  const request = f.posts.find(p => p.path.endsWith('/bid-preview'))!.body!;
  expect(request['sourceBase64']).toBe(original.toString('base64'));
  expect((request['mapping'] as { prices: unknown[] }).prices).toEqual([
    { positionKey: '01.0001', expectedItemDigest: 'b'.repeat(64), unitPrice: '12.345678', notOffered: false, priceEvidence: 'Synthetische Preisprüfung' },
    { positionKey: '01.0002', expectedItemDigest: 'c'.repeat(64), unitPrice: null, notOffered: true, priceEvidence: 'Synthetische Preisprüfung' }
  ]);
  expect(f.posts.some(p => p.path.endsWith('/bid-export'))).toBe(false);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(f.errors).toEqual([]); await panel(page).screenshot({ path: info.outputPath('gaeb-worksheet-reviewed.png') });
});

test('wrong original or changed quantities reject the whole import and retain manually entered prices', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page);
  await panel(page).locator('#gb-price-0').fill('7');
  await importSheet(page, sheet().replace('sha256:' + sha, 'sha256:' + '0'.repeat(64)));
  await expect(panel(page).getByRole('alert')).toContainText('Original-SHA-256');
  await expect(panel(page).locator('#gb-price-0')).toHaveValue('7');
  await importSheet(page, sheet().replace('qty:12.500', 'qty:13'));
  await expect(panel(page).getByRole('alert')).toContainText('Quelldaten');
  await expect(panel(page).locator('#gb-price-0')).toHaveValue('7');
  await expect(panel(page).locator('#gb-worksheet-consent')).toHaveCount(0);
  expect(f.posts).toHaveLength(2); expect(f.errors).toEqual([]);
});

test('editing after CSV review invalidates consent and prevents applying stale prices', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page); await importSheet(page);
  await panel(page).locator('#gb-worksheet-consent').check();
  await panel(page).locator('#gb-price-0').fill('99');
  await expect(panel(page).getByRole('heading', { name: 'Preisänderungen vor Übernahme prüfen', exact: true })).toHaveCount(0);
  await expect(panel(page).locator('#gb-price-0')).toHaveValue('99');
  expect(f.posts).toHaveLength(2); expect(f.errors).toEqual([]);
});

test('importing another worksheet invalidates an existing X84 preview without sending a bid', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page); await bidder(page);
  await importSheet(page); await confirm(page);
  await panel(page).getByRole('button', { name: 'X84 bilden und nach Original-XSD prüfen', exact: true }).click();
  await expect(panel(page).getByRole('heading', { name: '5 · X84-Ausgabe kontrollieren', exact: true })).toBeVisible();
  await importSheet(page, sheet('33'));
  await expect(panel(page).getByRole('heading', { name: '5 · X84-Ausgabe kontrollieren', exact: true })).toHaveCount(0);
  await expect(panel(page).locator('#gb-price-0')).toHaveValue('12.345678');
  await expect(panel(page).getByRole('button', { name: 'X84 bilden und nach Original-XSD prüfen', exact: true })).toBeDisabled();
  await panel(page).getByRole('button', { name: 'Arbeitslistenprüfung verwerfen', exact: true }).click();
  await expect(panel(page).locator('#gb-price-0')).toHaveValue('12.345678');
  expect(f.posts.filter(p => p.path.endsWith('/bid-preview'))).toHaveLength(1);
  expect(f.posts.some(p => p.path.endsWith('/bid-export'))).toBe(false); expect(f.errors).toEqual([]);
});

test('tenant changes clear reviewed worksheet, prices and bidder data', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page);
  await panel(page).locator('#gb-name').fill('Tenant A confidential'); await importSheet(page);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
  await expect(panel(page).locator('#gb-worksheet-consent')).toHaveCount(0);
  await expect(panel(page).locator('#gb-price-0')).toHaveCount(0);
  await inspect(page);
  await expect(panel(page).locator('#gb-name')).toHaveValue('');
  await expect(panel(page).locator('#gb-price-0')).toHaveValue(''); expect(f.errors).toEqual([]);
});

test('invalid UTF-8 and oversized worksheets fail locally before any new HTTP request', async ({ page }) => {
  const f = await fixture(page); await page.goto('/trade-imports'); await inspect(page);
  await panel(page).locator('#gb-worksheet').setInputFiles({ name: 'invalid.csv', mimeType: 'text/csv', buffer: Buffer.from([0xc3, 0x28]) });
  await expect(panel(page).getByRole('alert')).toContainText('UTF-8');
  await panel(page).locator('#gb-worksheet').setInputFiles({ name: 'oversize.csv', mimeType: 'text/csv', buffer: Buffer.alloc(4 * 1024 * 1024 + 1, 32) });
  await expect(panel(page).getByRole('alert')).toContainText('4 MiB');
  expect(f.posts).toHaveLength(2); expect(f.errors).toEqual([]);
});

test('worksheet controls do not bypass failed original-schema validation', async ({ page }) => {
  const f = await fixture(page); f.state.schemaValid = false; await page.goto('/trade-imports');
  await panel(page).locator('#gb-original').setInputFiles({ name: 'invalid.x83', mimeType: 'application/xml', buffer: original });
  await panel(page).getByRole('button', { name: 'Originaldatei strukturell und gegen offizielle XSD prüfen', exact: true }).click();
  await expect(panel(page).getByText('Nicht bestanden', { exact: true })).toBeVisible();
  await expect(panel(page).getByRole('alert')).toContainText('Bitte kontrollieren Sie den Export');
  const issue = panel(page).locator('details.review-issue');
  await expect(issue.locator('summary')).toHaveText('Formatfehler 1 – Einzelheiten anzeigen');
  await expect(issue.locator('p')).not.toBeVisible();
  await issue.locator('summary').click();
  await expect(issue.locator('p')).toContainText('GAEB-XSD');
  await expect(panel(page).locator('#gb-worksheet')).toHaveCount(0);
  expect(f.posts).toHaveLength(2); expect(f.errors).toEqual([]);
});
