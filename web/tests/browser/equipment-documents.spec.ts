import { createHash, randomUUID } from 'node:crypto';
import { expect, Page, test } from '@playwright/test';
import type { InspectionDocument } from '../../src/app/core/equipment-documents.models';
import type { EquipmentInspection, EquipmentRecord } from '../../src/app/core/equipment-workflow.models';

const equipmentId = '41000000-0000-4000-8000-000000000001';
const inspectionId = '41000000-0000-4000-8000-000000000002';
const documentId = '41000000-0000-4000-8000-000000000003';
const userId = '41000000-0000-4000-8000-000000000004';
const pdf = Buffer.from('%PDF-1.7\nSynthetic retained inspection original\n%%EOF');
const sha = createHash('sha256').update(pdf).digest('hex');
interface Options { role?: string; state?: 'Staged' | 'Ready' | 'Archived'; badBinding?: boolean; badUpload?: boolean; corruptDownload?: boolean; delayList?: boolean; historicalLink?: boolean; loseFirstStageResponse?: boolean }

async function fixture(page: Page, options: Options = {}) {
  const writes: { path: string; headers: Record<string, string>; body: unknown }[] = [];
  const equipment: EquipmentRecord = { id: equipmentId, revision: 3, name: 'Prüfgerät Nord', inventoryNumber: 'FB-P-04', kind: 'Machine',
    registration: '', location: 'Betriebshof', nextInspectionOn: '2027-10-21', available: true, inspectionBlocked: false,
    pendingInspectionId: null, lastInspectionId: inspectionId, lastInspectionOn: '2026-10-21' };
  const inspection: EquipmentInspection = { id: inspectionId, revision: 2, equipmentId, kind: 'Inspection', title: 'Jahresprüfung Prüfgerät',
    scheduledOn: '2026-10-21', notes: '', status: 'Completed', plannedBy: userId, scopeDigest: 'a'.repeat(64),
    equipmentSnapshot: { id: equipmentId, name: equipment.name, inventoryNumber: equipment.inventoryNumber, kind: 'Machine', registration: '' },
    completion: { performedOn: '2026-10-21', result: 'Passed', inspector: 'Prüfstelle', evidenceReference: 'PB-014', notes: '',
      nextInspectionOn: '2027-10-21', makeAvailable: true, recordedBy: userId, recordedAt: '2026-10-21T10:00:00Z', digest: 'd'.repeat(64) }, cancellation: null };
  const original = (state: 'Staged' | 'Ready' | 'Archived'): InspectionDocument => ({ id: documentId, tenantId: 'tenant-a', name: 'Original.pdf',
    contentType: 'application/pdf', size: pdf.length, sha256: sha, versionId: state === 'Staged' ? null : 'stored-version-1', state,
    revision: state === 'Staged' ? 1 : 2, projectId: null, sourceKind: 'equipment-inspections', sourceId: inspectionId,
    sourceDigest: inspection.completion!.digest, rendererVersion: 'equipment-inspection-original-v1', createdAt: '2026-10-21T11:00:00Z' });
  let documents = options.state ? [original(options.state)] : [];
  const stageReceipts = new Map<string, { body: string; document: InspectionDocument }>();
  let lostStageResponse = false;
  let release: (() => void) | undefined; const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length);
    const other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: userId, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role: options.role || 'SiteManager', modules: ['core', 'purchasing'], capabilities: ['*'] })) } });
    if (path === 'equipment/inspection-worklist') return route.fulfill({ json: { businessDate: '2026-10-21', timeZone: 'Europe/Berlin',
      items: other ? [] : [{ ...equipment, dueStatus: 'Current' }], total: other ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `equipment/${equipmentId}/inspection-history`) return route.fulfill({ json: { equipment, pendingInspection: null, items: options.historicalLink ? [] : [inspection],
      total: options.historicalLink ? 0 : 1, page: 1, pageSize: 20, businessDate: '2026-10-21', timeZone: 'Europe/Berlin', affectedAssignmentCount: 0 } });
    if (path === `equipment-inspections/${inspectionId}`) return route.fulfill({ json: inspection });
    if (path === `equipment-inspections/${inspectionId}/documents`) {
      if (request.method() === 'POST') {
        const body = request.postDataJSON(), key = request.headers()['idempotency-key'];
        writes.push({ path, headers: request.headers(), body });
        const previous = stageReceipts.get(key);
        if (previous && previous.body !== JSON.stringify(body))
          return route.fulfill({ status: 409, json: { code: 'idempotency_conflict', detail: 'This request key was already used for different content.' } });
        let staged = previous?.document;
        if (!staged) {
          staged = { ...original('Staged'), id: documents.length ? randomUUID() : documentId,
            name: body.name, contentType: body.contentType, size: body.size, sha256: body.sha256 };
          documents = [staged, ...documents]; stageReceipts.set(key, { body: JSON.stringify(body), document: staged });
        }
        if (options.loseFirstStageResponse && !lostStageResponse) { lostStageResponse = true; return route.abort('failed'); }
        return route.fulfill({ json: staged });
      }
      if (options.delayList) await gate;
      return route.fulfill({ json: { inspectionId, equipmentId, sourceDigest: inspection.completion!.digest,
        items: documents.map(d => options.badBinding ? { ...d, sourceId: equipmentId } : d), total: documents.length, page: 1, pageSize: 20 } });
    }
    const staged = documents.find(document => path === `equipment-inspections/${inspectionId}/documents/${document.id}/content`);
    if (staged) {
      writes.push({ path, headers: request.headers(), body: request.postDataBuffer() });
      const ready: InspectionDocument = { ...staged, state: 'Ready', revision: staged.revision + 1, versionId: 'stored-version-1' };
      documents = documents.map(document => document.id === staged.id ? ready : document);
      return route.fulfill({ json: options.badUpload ? { ...ready, sourceDigest: 'c'.repeat(64) } : ready });
    }
    if (path === `equipment-inspections/${inspectionId}/documents/${documentId}/download`)
      return route.fulfill({ body: options.corruptDownload ? Buffer.from('%PDF-1.7\nBROKEN') : pdf,
        headers: { 'Content-Type': 'application/pdf', 'X-Content-SHA256': sha } });
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { writes, release: () => release?.(), documentCount: () => documents.length };
}
async function open(page: Page) {
  await page.goto('/equipment-inspections');
  await page.getByRole('button', { name: /Prüfgerät Nord FB-P-04/ }).click();
  await page.locator('.history-record summary').click();
  await page.getByRole('button', { name: 'Originalbelege öffnen', exact: true }).click();
}
const selectPdf = (page: Page, buffer: Buffer = pdf) => page.getByLabel('Originalprüfbericht auswählen', { exact: true })
  .setInputFiles({ name: 'Original.pdf', mimeType: 'application/pdf', buffer });

test('site manager uploads an original and downloads the byte-verified retained version', async ({ page }) => {
  const api = await fixture(page); await open(page); await selectPdf(page);
  await page.getByRole('button', { name: 'Originalbeleg speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Originalbeleg vollständig gespeichert und geprüft.' })).toBeVisible();
  expect(api.writes).toHaveLength(2);
  expect(api.writes[0].body).toMatchObject({ name: 'Original.pdf', sha256: sha, size: pdf.length, projectId: null });
  expect(api.writes[0].headers['if-match']).toBe('"2"');
  expect(api.writes[1].headers['if-match']).toBe('"1"');
  expect(api.writes[1].body).toEqual(pdf);
  const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click();
  expect((await download).suggestedFilename()).toBe('Original.pdf');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('accounting can download archived originals without gaining upload rights', async ({ page }) => {
  const api = await fixture(page, { role: 'Accounting', state: 'Archived' }); await open(page);
  await expect(page.getByText('Archiviert · Original erhalten', { exact: false })).toBeVisible();
  await expect(page.getByLabel('Originalprüfbericht auswählen')).toHaveCount(0);
  const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click();
  await download; expect(api.writes).toHaveLength(0);
});

test('a document deep link exposes its historical inspection beyond the current history page', async ({ page }) => {
  await fixture(page, { state: 'Staged', historicalLink: true });
  await page.goto(`/equipment-inspections?inspectionId=${inspectionId}`);
  await expect(page.getByRole('heading', { name: 'Verknüpfter Prüfbericht: Jahresprüfung Prüfgerät', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Originalbelege öffnen', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Übertragung fortsetzen', exact: true })).toBeVisible();
});

test('a document deep link on the current history page keeps one usable original panel', async ({ page }) => {
  const api = await fixture(page, { state: 'Staged' });
  await page.goto(`/equipment-inspections?inspectionId=${inspectionId}`);
  await expect(page.getByRole('heading', { name: 'Verknüpfter Prüfbericht: Jahresprüfung Prüfgerät', exact: true })).toBeVisible();
  await page.locator('.history-record summary').click();
  await expect(page.locator('app-equipment-documents')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Originalbelege öffnen', exact: true })).toHaveCount(1);
  await page.getByRole('button', { name: 'Originalbelege öffnen', exact: true }).click();
  await page.getByRole('button', { name: 'Übertragung fortsetzen', exact: true }).click();
  await expect(page.getByLabel('Originalprüfbericht auswählen', { exact: true })).toHaveCount(1);
  await selectPdf(page);
  await page.getByRole('button', { name: 'Originalbeleg speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Originalbeleg vollständig gespeichert und geprüft.' })).toBeVisible();
  expect(api.writes).toHaveLength(1);
  expect(api.writes[0].path).toMatch(/\/content$/);
  expect(api.writes[0].body).toEqual(pdf);
});

test('interrupted original uploads resume only with the matching file', async ({ page }) => {
  const api = await fixture(page, { state: 'Staged' }); await open(page);
  await page.getByRole('button', { name: 'Übertragung fortsetzen', exact: true }).click();
  await selectPdf(page, Buffer.from('%PDF-1.7\nDifferent original'));
  await page.getByRole('button', { name: 'Originalbeleg speichern', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('entspricht nicht der begonnenen Originalübertragung'); expect(api.writes).toHaveLength(0);
  await selectPdf(page); await page.getByRole('button', { name: 'Originalbeleg speichern', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Originalbeleg vollständig gespeichert' })).toBeVisible();
  expect(api.writes).toHaveLength(1); expect(api.writes[0].path).toMatch(/\/content$/);
});

test('the same file can be selected again after switching to a staged original', async ({ page }) => {
  const api = await fixture(page, { state: 'Staged' }); await open(page); await selectPdf(page);
  const input = page.getByLabel('Originalprüfbericht auswählen', { exact: true });
  const save = page.getByRole('button', { name: 'Originalbeleg speichern', exact: true });
  await expect(input).toHaveValue('');
  await expect(page.getByText('Ausgewählte Datei:', { exact: false })).toContainText('Original.pdf');
  await page.getByRole('button', { name: 'Übertragung fortsetzen', exact: true }).click();
  await expect(save).toBeDisabled();
  await expect(page.getByText('Ausgewählte Datei:', { exact: false })).toHaveCount(0);
  await selectPdf(page); await expect(input).toHaveValue(''); await expect(save).toBeEnabled();
  await save.click();
  await expect(page.getByRole('status').filter({ hasText: 'Originalbeleg vollständig gespeichert und geprüft.' })).toBeVisible();
  expect(api.writes).toHaveLength(1); expect(api.writes[0].body).toEqual(pdf);
});

test('a lost staging response reuses the same metadata key without duplicating the original', async ({ page }) => {
  const api = await fixture(page, { loseFirstStageResponse: true }); await open(page); await selectPdf(page);
  const save = page.getByRole('button', { name: 'Originalbeleg speichern', exact: true });
  await save.click(); await expect(page.getByRole('alert')).toBeVisible();
  expect(api.writes).toHaveLength(1); expect(api.documentCount()).toBe(1);
  await selectPdf(page); await save.click();
  await expect(page.getByRole('status').filter({ hasText: 'Originalbeleg vollständig gespeichert und geprüft.' })).toBeVisible();
  expect(api.writes).toHaveLength(3); expect(api.documentCount()).toBe(1);
  expect(api.writes[1].headers['idempotency-key']).toBe(api.writes[0].headers['idempotency-key']);
  expect(api.writes[1].body).toEqual(api.writes[0].body); expect(api.writes[2].body).toEqual(pdf);
});

for (const changed of [
  { description: 'new filename with the same bytes', name: 'Weiterer-Pruefbericht.pdf', buffer: pdf },
  { description: 'new bytes with the same filename and size', name: 'Original.pdf', buffer: Buffer.from(pdf.toString().replace('retained', 'modified')) }
]) {
  test(`a lost staging response permits ${changed.description} with a separate metadata key`, async ({ page }) => {
    const api = await fixture(page, { loseFirstStageResponse: true }); await open(page); await selectPdf(page);
    const save = page.getByRole('button', { name: 'Originalbeleg speichern', exact: true });
    await save.click(); await expect(page.getByRole('alert')).toBeVisible(); expect(api.writes).toHaveLength(1);
    await page.getByLabel('Originalprüfbericht auswählen', { exact: true })
      .setInputFiles({ name: changed.name, mimeType: 'application/pdf', buffer: changed.buffer });
    await save.click();
    await expect(page.getByRole('status').filter({ hasText: 'Originalbeleg vollständig gespeichert und geprüft.' })).toBeVisible();
    expect(api.writes).toHaveLength(3); expect(api.documentCount()).toBe(2);
    expect(api.writes[1].headers['idempotency-key']).not.toBe(api.writes[0].headers['idempotency-key']);
    expect(api.writes[1].body).toMatchObject({ name: changed.name, size: changed.buffer.length,
      contentType: 'application/pdf', sha256: createHash('sha256').update(changed.buffer).digest('hex') });
    expect(api.writes[2].body).toEqual(changed.buffer);
  });
}

test('mismatched inspection metadata cannot become a downloadable original', async ({ page }) => {
  await fixture(page, { state: 'Ready', badBinding: true }); await open(page);
  await expect(page.getByRole('alert')).toContainText('Originalbeleg gehört nicht');
  await expect(page.getByRole('button', { name: 'Original herunterladen' })).toHaveCount(0);
});

test('a changed completion in the upload response never announces archive success', async ({ page }) => {
  await fixture(page, { badUpload: true }); await open(page); await selectPdf(page);
  await page.getByRole('button', { name: 'Originalbeleg speichern', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Originalbeleg gehört nicht');
  await expect(page.getByRole('status').filter({ hasText: 'Originalbeleg vollständig gespeichert' })).toHaveCount(0);
});

test('corrupt original downloads are rejected before a browser download is started', async ({ page }) => {
  await fixture(page, { state: 'Ready', corruptDownload: true }); await open(page);
  let count = 0; page.on('download', () => count++);
  await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('beschädigt'); expect(count).toBe(0);
});

test('late original lists cannot repopulate a different business', async ({ page }) => {
  const api = await fixture(page, { state: 'Ready', delayList: true }); await open(page);
  await expect(page.getByRole('status').filter({ hasText: 'Originalbelege werden geladen' })).toBeVisible();
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
  await expect(page.getByRole('heading', { name: 'Geräteakte auswählen', exact: true })).toBeVisible();
  await expect(page.getByText('Original.pdf', { exact: true })).toHaveCount(0); expect(api.writes).toHaveLength(0);
});

test('business changes during the asynchronous download checksum suppress the file', async ({ page }) => {
  await fixture(page, { state: 'Ready' }); await open(page);
  await expect(page.getByRole('button', { name: 'Original herunterladen', exact: true })).toBeVisible();
  await page.evaluate(() => {
    const digest = crypto.subtle.digest.bind(crypto.subtle);
    (window as any).hashStarted = false;
    crypto.subtle.digest = async (...args: Parameters<typeof digest>) => {
      (window as any).hashStarted = true;
      await new Promise<void>(resolve => { (window as any).releaseOriginalHash = resolve; });
      return digest(...args);
    };
  });
  let count = 0; page.on('download', () => count++);
  await page.getByRole('button', { name: 'Original herunterladen', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).hashStarted)).toBe(true);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
  await page.evaluate(() => (window as any).releaseOriginalHash());
  await expect(page.getByRole('heading', { name: 'Geräteakte auswählen', exact: true })).toBeVisible(); expect(count).toBe(0);
});
