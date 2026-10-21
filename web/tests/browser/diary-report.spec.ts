import { expect, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';

const id = '10000000-0000-0000-0000-000000000001';
const pdf = Buffer.from('%PDF-1.4\nSynthetic diary browser download contract\n%%EOF');
async function fixture(page: Page, initial = 'Draft', role = 'ManagingDirector') {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  const state = { item: { id, revision: 3, projectId: 'project-a', employeeId: 'employee-a', date: '2026-10-20', work: 'Dachfläche abgedichtet', weather: 'Trocken', obstructions: '', safetyNotes: '',
    status: initial, submission: initial === 'Draft' ? null : { digest: 'a'.repeat(64), submittedBy: 'author', submittedAt: '2026-10-20T15:00:00Z', content: { employeeUserId: 'author', employeeName: 'Alex' } },
    review: initial === 'Approved' ? { reviewerName: 'Bauleitung', reviewedAt: '2026-10-20T16:00:00Z', notes: 'Geprüft' } : null } as Record<string, unknown>, hold: false, corrupt: false };
  let release = () => {}; const requests: { path: string; body: Record<string, unknown>; tenant: string | undefined; revision: string | undefined }[] = [];
  await page.route('**/api/v1/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname.slice('/api/v1/'.length), tenant = req.headers()['x-tenant-id'];
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: role === 'Worker' ? 'author' : 'reviewer', email: 'person@example.test', displayName: 'Testperson' }, activeTenantId: 'tenant-a', tenants: ['tenant-a', 'tenant-b'].map(t => ({ id: t, name: t === 'tenant-a' ? 'Dachbetrieb A' : 'Dachbetrieb B', role, modules: ['core', 'field', 'maintenance'], capabilities: ['*'], employeeId: role === 'Worker' ? 'employee-a' : 'employee-reviewer' })) } });
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=test; Path=/' } });
    if (path === 'diary-entries' && req.method() === 'GET') return route.fulfill({ json: { items: tenant === 'tenant-b' ? [] : [state.item], total: tenant === 'tenant-b' ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `diary-entries/${id}` && req.method() === 'GET') return route.fulfill({ json: state.item });
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: 'project-a', revision: 1, name: 'Dachsanierung' }], total: 1, page: 1, pageSize: 100 } });
    if (path === 'employees') return route.fulfill({ json: { items: [{ id: 'employee-a', revision: 1, name: 'Alex', userId: 'author', active: true }], total: 1, page: 1, pageSize: 100 } });
    if (path.startsWith(`diary-entries/${id}/`) && req.method() === 'POST') {
      const body = req.postDataJSON() as Record<string, unknown>;
      requests.push({ path, body, tenant, revision: req.headers()['if-match'] });
      if (state.hold) await new Promise<void>(resolve => { release = resolve; });
      if (path.endsWith('/pdf')) return route.fulfill({ json: { id: 'report-a', name: 'Tagesrapport.pdf', contentType: 'application/pdf', size: pdf.length, sha256: createHash('sha256').update(pdf).digest('hex'), sourceKind: 'diary-entries', sourceId: id } });
      state.item = { ...state.item, revision: Number(state.item['revision']) + 1, status: path.endsWith('/submit') ? 'Submitted' : body['approve'] ? 'Approved' : 'Returned',
        submission: { digest: 'a'.repeat(64), submittedBy: 'author', submittedAt: '2026-10-20T15:00:00Z', content: { employeeUserId: 'author', employeeName: 'Alex' } },
        review: path.endsWith('/submit') ? null : { reviewerName: 'Bauleitung', reviewedAt: '2026-10-20T16:00:00Z', notes: body['notes'] } };
      return route.fulfill({ json: state.item });
    }
    if (path === 'documents/report-a/download') return route.fulfill({ body: state.corrupt ? Buffer.from('X'.repeat(pdf.length)) : pdf, contentType: 'application/pdf' });
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  await page.goto(`/diary-entries?selected=${id}`);
  await expect(page.getByRole('heading', { name: 'Prüfung und Tagesrapport', exact: true })).toBeVisible();
  return { state, requests, errors, release: () => release() };
}

test('diary submission requires explicit confirmation and then seals the editor', async ({ page }, testInfo) => {
  const context = await fixture(page, 'Draft', 'Worker');
  const submit = page.getByRole('button', { name: 'Zur Prüfung einreichen', exact: true });
  await expect(submit).toBeDisabled();
  await page.getByRole('checkbox', { name: 'Ich habe den Tagesbericht geprüft und möchte diesen Stand einreichen.' }).check();
  await submit.click();
  await expect(page.getByText('Tagesbericht zur Prüfung eingereicht.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Bearbeiten', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Tagesbericht freigeben', exact: true })).toHaveCount(0);
  expect(context.requests).toEqual([{ path: `diary-entries/${id}/submit`, body: {}, tenant: 'tenant-a', revision: '"3"' }]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(context.errors).toEqual([]);
  await page.screenshot({ path: testInfo.outputPath('diary-submitted.png'), fullPage: true });
});

test('review binds the selected digest and the verified PDF downloads', async ({ page }) => {
  const context = await fixture(page, 'Submitted');
  await page.getByLabel('Prüfvermerk · für eine Rückgabe erforderlich').fill('Arbeiten und Unterbrechungen geprüft');
  await page.getByRole('checkbox', { name: 'Ich habe den eingereichten Tagesbericht geprüft.' }).check();
  await page.getByRole('button', { name: 'Tagesbericht freigeben', exact: true }).click();
  await expect(page.getByText('Tagesbericht freigegeben.', { exact: true })).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Tagesrapport als PDF', exact: true }).click();
  expect((await download).suggestedFilename()).toBe('Tagesrapport.pdf');
  expect(context.requests[0]?.body).toEqual({ approve: true, digest: 'a'.repeat(64), notes: 'Arbeiten und Unterbrechungen geprüft' });
  expect(context.requests[1]?.revision).toBe('"4"'); expect(context.errors).toEqual([]);
});

test('return requires a reason and restores editing while preserving review evidence', async ({ page }) => {
  const context = await fixture(page, 'Submitted');
  await page.getByRole('checkbox', { name: 'Ich habe den eingereichten Tagesbericht geprüft.' }).check();
  await expect(page.getByRole('button', { name: 'Zur Überarbeitung zurückgeben', exact: true })).toBeDisabled();
  await page.getByLabel('Prüfvermerk · für eine Rückgabe erforderlich').fill('Bitte Regenpause ergänzen');
  await page.getByRole('button', { name: 'Zur Überarbeitung zurückgeben', exact: true }).click();
  await expect(page.getByText('Bitte Regenpause ergänzen', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Bearbeiten', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Zur Prüfung einreichen', exact: true })).toBeDisabled();
  expect(context.requests[0]?.body['approve']).toBe(false); expect(context.errors).toEqual([]);
});

test('late submission in the previous business cannot restore old details', async ({ page }) => {
  const context = await fixture(page); context.state.hold = true;
  await page.getByRole('checkbox', { name: 'Ich habe den Tagesbericht geprüft und möchte diesen Stand einreichen.' }).check();
  await page.getByRole('button', { name: 'Zur Prüfung einreichen', exact: true }).click();
  await expect.poll(() => context.requests.length).toBe(1);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
  await expect(page.getByRole('heading', { name: 'Prüfung und Tagesrapport', exact: true })).toHaveCount(0);
  context.release();
  await expect(page.getByRole('heading', { name: 'Noch keine Bautagebuch.', exact: true })).toBeVisible();
  await expect(page.getByText('Tagesbericht zur Prüfung eingereicht.', { exact: true })).toHaveCount(0);
  expect(context.errors).toEqual([]);
});

test('changed PDF bytes are rejected before a browser download', async ({ page }) => {
  const context = await fixture(page, 'Approved'); context.state.corrupt = true;
  const downloads: string[] = []; page.on('download', d => downloads.push(d.suggestedFilename()));
  await page.getByRole('button', { name: 'Tagesrapport als PDF', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('stimmt nicht mit der gespeicherten Fassung überein');
  expect(downloads).toEqual([]); expect(context.errors).toEqual([]);
});
