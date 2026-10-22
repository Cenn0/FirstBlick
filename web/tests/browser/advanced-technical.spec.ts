import { expect, Locator, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import fixtures from '../fixtures/advanced-technical.json' with { type: 'json' };
import { AdvancedMode, advancedMethods, normalizeAdvancedInput } from '../../src/app/core/advanced-technical.models';

const projectId = '10000000-0000-4000-8000-000000000001';
const reportId = '20000000-0000-4000-8000-000000000001';
const documentId = '30000000-0000-4000-8000-000000000001';
const pdf = Buffer.from('%PDF-1.4\nSynthetic byte-integrity fixture, not a rendered engineering report.\n%%EOF');
const hash = createHash('sha256').update(pdf).digest('hex');
type Options = { result?: (r: any) => any; saved?: (r: any) => any; badPdf?: boolean; denied?: boolean };
async function fixture(page: Page, mode: AdvancedMode = 'balance', options: Options = {}) {
  const calls: { path: string; body: any }[] = [], errors: string[] = [];
  const f = structuredClone(fixtures[mode]);
  page.on('pageerror', e => errors.push(e.message));
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: {
      user: { id: 'operator', email: 'operator@example.test', displayName: 'Operator' }, activeTenantId: 'tenant',
      tenants: [{ id: 'tenant', name: 'Synthetic', role: 'ManagingDirector', modules: ['core', 'technical'],
        capabilities: options.denied ? ['projects.read'] : ['*'] },
        { id: 'other', name: 'Other synthetic business', role: 'ManagingDirector', modules: ['core', 'technical'], capabilities: ['*'] }]
    } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: projectId, revision: 1, name: 'Synthetic roof project' }], total: 1, page: 1, pageSize: 25 } });
    if (path === advancedMethods[mode].endpoint) {
      calls.push({ path, body: route.request().postDataJSON() });
      return route.fulfill({ json: options.result ? options.result(structuredClone(f.result)) : f.result });
    }
    if (path === 'technical/runs') {
      const body = route.request().postDataJSON(); calls.push({ path, body });
      const record = { ...body, input: f.result.input, id: reportId, revision: 1, tenantId: 'tenant',
        result: 'persistedResult' in f ? f.persistedResult : f.result, methodVersion: '1.0.0', digest: 'b'.repeat(64), isNormative: false };
      return route.fulfill({ json: options.saved ? options.saved(record) : record });
    }
    if (path === `technical-results/${reportId}/pdf`) return route.fulfill({ json: {
      id: documentId, sourceId: reportId, sourceDigest: 'b'.repeat(64), sourceKind: 'technical-results',
      projectId, state: 'Ready', contentType: 'application/pdf', sha256: hash, size: pdf.length
    } });
    if (path === `documents/${documentId}/download`) return route.fulfill({ contentType: 'application/pdf', body: options.badPdf ? Buffer.from('tampered') : pdf });
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, errors, input: f.input };
}
async function put(scope: Locator, id: string, value: unknown) { await scope.locator('#' + id).fill(String(value)); }
async function fill(page: Page, mode: AdvancedMode) {
  const s = page.locator('app-advanced-technical'), f: any = fixtures[mode], input = f.input;
  await s.locator('#advanced-tab-' + mode).click();
  if (mode === 'balance' || mode === 'drying') {
    const a = mode === 'drying' ? input.assembly : input;
    for (const [id, key] of [['am-year', 'climateCalendarYear'], ['am-trial', 'trialMonth']]) await put(s, id, a[key]);
    await s.locator('#am-flow').selectOption(a.heatFlow);
    await s.locator('#am-exterior').selectOption(a.exteriorBoundary); await s.locator('#am-treatment').selectOption(a.climateTreatment);
    await put(s, 'am-source', a.applicability.climateSource); await put(s, 'am-construction', a.applicability.constructionReview);
    for (const checkbox of await s.locator('input[type=checkbox]').all()) await checkbox.check();
    for (let i = 0; i < a.layers.length; i++) {
      if (i) await s.getByRole('button', { name: '+ Schicht hinzufügen', exact: true }).click();
      const l = a.layers[i];
      for (const [id, key] of [['am-name', 'name'], ['am-r', 'resistanceM2KW'], ['am-sd', 'sdM'], ['am-refs', 'designValueSource']]) await put(s, id + '-' + i, l[key]);
      await s.locator('#am-kind-' + i).selectOption(l.kind);
    }
    for (const m of a.months) for (const [id, key] of [['am-it', 'insideTemperatureC'], ['am-et', 'outsideAirTemperatureC'], ['am-irh', 'insideRelativeHumidityPercent'], ['am-erh', 'outsideRelativeHumidityPercent']]) await put(s, id + '-' + m.month, m[key]);
    if (mode === 'drying') {
      await s.locator('#am-wet').selectOption({ label: '1 – Single homogeneous layer' });
      await put(s, 'am-start', input.startingMonth); await put(s, 'am-assess', input.initialAssessmentReview);
    }
  } else {
    const first = mode === 'assembly' ? input.fields[0].layout : input, c = first.catalog;
    await put(s, 'ab-length', input.roofLengthM); await put(s, 'ab-width', input.roofWidthM);
    await put(s, 'ab-source', mode === 'assembly' ? input.surveySource : input.measuredRoofSource);
    await put(s, 'ab-drain', mode === 'assembly' ? input.drainageReview : input.drainageDesignReview);
    if (mode === 'assembly') {
      await put(s, 'ab-plan', input.planRevision);
      for (let i = 0; i < input.fields.length; i++) {
        if (i) await s.getByRole('button', { name: '+ Dachfeld hinzufügen', exact: true }).click();
        const field = input.fields[i], l = field.layout;
        for (const [id, val] of [['id', field.fieldId], ['x', field.originX], ['y', field.originY], ['length', l.roofLengthM], ['width', l.roofWidthM], ['thin', l.lowEdgeThicknessM], ['slope', l.slopePercent]]) await put(s, `af-${id}-${i}`, val);
        await s.locator('#af-axis-' + i).selectOption(l.flowAxis); await s.locator('#af-low-' + i).selectOption({ index: l.lowEdgeAtCoordinateZero ? 0 : 1 });
      }
    } else {
      await put(s, 'ab-thin', input.lowEdgeThicknessM); await put(s, 'ab-slope', input.slopePercent);
      await s.locator('#ab-axis').selectOption(input.flowAxis); await s.locator('#ab-low').selectOption({ index: input.lowEdgeAtCoordinateZero ? 0 : 1 });
    }
    for (const [id, key] of [['ab-manufacturer', 'manufacturer'], ['ab-range', 'productRange'], ['ab-revision', 'catalogRevision'], ['ab-catalog-source', 'sourceReference'], ['ab-run', 'boardRunM'], ['ab-cross', 'boardCrossM']]) await put(s, id, c[key]);
    for (let i = 0; i < c.boards.length; i++) {
      if (i) await s.getByRole('button', { name: '+ Weiteren Herstellerartikel erfassen', exact: true }).click();
      for (const [id, key] of [['sku', 'sku'], ['boardlow', 'thinEdgeM'], ['boardhigh', 'thickEdgeM'], ['units', 'unitsPerPack'], ['price', 'pricePerBoardEur']]) await put(s, `ab-${id}-${i}`, c.boards[i][key]);
    }
  }
  return s;
}
async function calculate(s: Locator) { await s.getByRole('button', { name: 'Rechenlauf mit diesen Angaben starten', exact: true }).click(); }
async function save(s: Locator) {
  await s.getByRole('button', { name: 'Projekte suchen', exact: true }).click();
  await s.locator('#adv-project').selectOption(projectId); await put(s, 'adv-title', 'Synthetic reviewed result');
  await s.getByRole('button', { name: 'Nachprüfung und Ablage im Projekt', exact: true }).click();
}
for (const mode of ['balance', 'drying', 'boards', 'assembly'] as AdvancedMode[]) {
  test(mode + ': explicit input, verified JSON/PDF and project persistence', async ({ page }) => {
    const f = await fixture(page, mode); await page.goto('/technical'); const s = await fill(page, mode); await calculate(s);
    await expect(s.getByRole('heading', { name: 'Rechenlauf und Herkunft', exact: true })).toBeVisible();
    expect(normalizeAdvancedInput(f.calls[0].body, mode)).toEqual(normalizeAdvancedInput(f.input, mode));
    const downloaded = page.waitForEvent('download'); await s.getByRole('button', { name: 'Vollständige Evidenz als JSON', exact: true }).click();
    const json = await downloaded; expect(JSON.parse(await readFile((await json.path())!, 'utf8'))).toEqual(fixtures[mode].result);
    await save(s); await expect(s.getByRole('status').filter({ hasText: 'Gespeichert:' })).toBeVisible();
    const download = page.waitForEvent('download'); await s.getByRole('button', { name: 'Prüfsummenverifizierten PDF-Bericht laden', exact: true }).click();
    expect(await readFile((await (await download).path())!)).toEqual(pdf);
    await s.getByRole('button', { name: 'Projekte suchen', exact: true }).click();
    await expect(s.getByRole('button', { name: 'Prüfsummenverifizierten PDF-Bericht laden', exact: true })).toHaveCount(0);
    expect(f.errors).toEqual([]); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}
test('forged engineering approval is rejected before export and save', async ({ page }) => {
  await fixture(page, 'balance', { result: r => ({ ...r, standardProcedure: { ...r.standardProcedure, din4108Part3Assessment: true } }) });
  await page.goto('/technical'); const s = await fill(page, 'balance'); await calculate(s);
  await expect(s.getByRole('alert')).toContainText('Freigabe'); await expect(s.locator('.result-panel')).toHaveCount(0);
});
test('save with changed outer input and copied preview hash is rejected', async ({ page }) => {
  await fixture(page, 'balance', { saved: r => ({ ...r, input: { ...r.input, trialMonth: 2 } }) });
  await page.goto('/technical'); const s = await fill(page, 'balance'); await calculate(s); await save(s);
  await expect(s.getByRole('alert')).toContainText('aktuellen Eingaben'); await expect(s.getByRole('button', { name: 'Prüfsummenverifizierten PDF-Bericht laden', exact: true })).toHaveCount(0);
});
test('tampered PDF bytes do not trigger a download', async ({ page }) => {
  await fixture(page, 'boards', { badPdf: true }); await page.goto('/technical'); const s = await fill(page, 'boards'); await calculate(s); await save(s);
  const downloads: string[] = []; page.on('download', d => downloads.push(d.suggestedFilename()));
  await s.getByRole('button', { name: 'Prüfsummenverifizierten PDF-Bericht laden', exact: true }).click();
  await expect(s.getByRole('alert')).toContainText('Byte-Prüfung'); expect(downloads).toEqual([]);
});
test('mode change discards a delayed response', async ({ page }) => {
  await fixture(page); let release!: () => void; const gate = new Promise<void>(r => release = r);
  let started!: () => void; const pending = new Promise<void>(r => started = r);
  await page.route('**/api/v1/technical/moisture/iso13788/balance', async route => { started(); await gate; await route.fulfill({ json: fixtures.balance.result }); });
  await page.goto('/technical'); const s = await fill(page, 'balance'); await calculate(s); await pending;
  await s.locator('#advanced-tab-boards').click(); const arrived = page.waitForResponse('**/api/v1/technical/moisture/iso13788/balance'); release(); await arrived;
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())))); await expect(s.locator('.result-panel')).toHaveCount(0);
  await expect(s.locator('fieldset').first()).toBeEnabled();
});
test('missing capability hides advanced form', async ({ page }) => {
  await fixture(page, 'balance', { denied: true }); await page.goto('/technical');
  await expect(page.getByRole('heading', { name: 'Zugriff eingeschränkt', exact: true })).toBeVisible();
  await expect(page.locator('app-advanced-technical form')).toHaveCount(0);
});

test('authorization failure clears an existing preview and project links', async ({ page }) => {
  await fixture(page, 'boards'); await page.goto('/technical'); const s = await fill(page, 'boards'); await calculate(s);
  await page.route('**/api/v1/technical/runs', route => route.fulfill({ status: 403, json: { title: 'forbidden', detail: 'Synthetic revoked access' } }));
  await save(s); await expect(s.getByRole('alert')).toContainText('Berechtigung');
  await expect(s.locator('.result-panel')).toHaveCount(0); await expect(s.locator('#adv-project')).toHaveCount(0);
});
test('changing an entered source discards preview and saved links', async ({ page }) => {
  await fixture(page, 'boards'); await page.goto('/technical'); const s = await fill(page, 'boards'); await calculate(s); await save(s);
  await put(s, 'ab-source', 'Changed roof survey'); await expect(s.locator('.result-panel')).toHaveCount(0);
  await expect(s.getByRole('button', { name: 'Prüfsummenverifizierten PDF-Bericht laden', exact: true })).toHaveCount(0);
});
test('switching the business clears result and project bindings', async ({ page }) => {
  await fixture(page, 'boards'); await page.goto('/technical'); const s = await fill(page, 'boards'); await calculate(s); await save(s);
  await page.locator('#active-tenant').selectOption('other'); await expect(s.locator('.result-panel')).toHaveCount(0);
  await expect(s.locator('#adv-project')).toHaveCount(0);
});
