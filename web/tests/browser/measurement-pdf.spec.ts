import { expect, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';

const measurementId = '40000000-0000-4000-8000-000000000004';
const documentId = '50000000-0000-4000-8000-000000000005';
const projectId = '60000000-0000-4000-8000-000000000006';
const snapshotDigest = 'a'.repeat(64);
const validPdf = Buffer.from('%PDF-1.4\n% synthetic fixture only, not real rendered building evidence\n%%EOF\n');
const snapshot = { digest: snapshotDigest, roofVersion: 2, roofDigest: 'b'.repeat(64), methodVersion: '1.0.0',
  totals: [{ unit: 'm²', quantity: '10.5' }], lines: [] };
const measurement = { id: measurementId, revision: 3, title: 'Synthetic frozen roof measurement',
  projectId, roofRevisionId: '70000000-0000-4000-8000-000000000007',
  status: 'Frozen', snapshot, lines: [] };

async function fixture(page: Page, corrupt = false) {
  const errors: string[] = [], commands: { path: string; headers: Record<string, string> }[] = [];
  const pdf = corrupt ? Buffer.from('%PDF-invalid-binary') : validPdf;
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: 'synthetic-user', email: 'roof@example.test', displayName: 'Roof expert' },
      activeTenantId: 'tenant-a', tenants: [{ id: 'tenant-a', name: 'Synthetic company', role: 'ManagingDirector',
        modules: ['core', 'technical', 'commercial'], capabilities: ['*'] }] } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'measurements' || path.startsWith('measurements?'))
      return route.fulfill({ json: { items: [measurement], total: 1, page: 1, pageSize: 25 } });
    if (path === `measurements/${measurementId}`) return route.fulfill({ json: measurement });
    if (path.startsWith('projects?')) return route.fulfill({ json: { items: [{ id: projectId, name: 'Synthetic site' }], total: 1, page: 1, pageSize: 100 } });
    if (path.startsWith('roof-revisions?')) return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 100 } });
    if (path === `documents/${documentId}/download`) return route.fulfill({ contentType: 'application/pdf', body: pdf });
    if (path === `measurements/${measurementId}/pdf`) {
      commands.push({ path, headers: req.headers() });
      return route.fulfill({ json: { id: documentId, sourceId: measurementId, sourceDigest: snapshotDigest, sourceKind: 'measurements',
        projectId, state: 'Ready', contentType: 'application/pdf', size: validPdf.length,
        sha256: createHash('sha256').update(validPdf).digest('hex') } });
    }
    return route.fulfill({ status: 404, json: { code: 'not_found' } });
  });
  return { commands, errors };
}

test('frozen measurement PDF is downloaded only after digest, project and bytes agree', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/measurements');
  await page.getByRole('button', { name: 'Öffnen', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Festgehaltenes Aufmaß als geprüftes PDF herunterladen' })).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Festgehaltenes Aufmaß als geprüftes PDF herunterladen' }).click();
  expect((await download).suggestedFilename()).toBe(`FirstBlick-Aufmass-${measurementId}.pdf`);
  expect(f.commands).toHaveLength(1);
  expect(f.commands[0].headers['if-match']).toBe('"3"');
  expect(f.commands[0].headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/i);
  expect(f.errors).toEqual([]);
});

test('corrupt measurement PDF never reaches the download action', async ({ page }) => {
  const f = await fixture(page, true); const downloads: string[] = [];
  page.on('download', download => downloads.push(download.suggestedFilename()));
  await page.goto('/measurements');
  await page.getByRole('button', { name: 'Öffnen', exact: true }).click();
  await page.getByRole('button', { name: 'Festgehaltenes Aufmaß als geprüftes PDF herunterladen' }).click();
  await expect(page.getByRole('alert')).toContainText('SHA-256-Prüfsumme');
  expect(downloads).toEqual([]);
  expect(f.errors).toEqual([]);
});
