import { expect, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { technicalMethods, TechnicalMode, monthNames } from '../../src/app/core/technical-workbench.models';

const projectId = '10000000-0000-4000-8000-000000000001', recordId = '20000000-0000-4000-8000-000000000002', documentId = '30000000-0000-4000-8000-000000000003';
const pdf = Buffer.from('%PDF-1.4\n% synthetic download integrity fixture, not a rendered report\n%%EOF\n');
// Controlled transport fixtures: actual numerical and persistence correctness is tested by the C#/PostgreSQL suites.
function calculation(mode: TechnicalMode, input: Record<string, unknown>): Record<string, unknown> {
  const provenance = { methodId: technicalMethods[mode].provenance, methodVersion: '1.0.0', inputSha256: 'a'.repeat(64), isNormative: false, limitations: ['Synthetic transport fixture; no normative result.'] };
  if (mode === 'tapered') return { input, provenance, projectedAreaM2: 120, minimumThicknessM: .08, maximumThicknessM: .28, averageThicknessM: .18, calculatedVolumeM3: 21.6, corners: [{ x: 0, y: 0, thicknessM: .08 }, { x: 12, y: 0, thicknessM: .08 }, { x: 12, y: 10, thicknessM: .28 }, { x: 0, y: 10, thicknessM: .28 }] };
  if (mode === 'roof') return { input, provenance, surfaceAreaM2: 60, projectedAreaM2: 48, slopingLengthM: 5, pitchDegrees: 36.8698976, perimeterM: 34 };
  if (mode === 'inhomogeneous') return {
    input, provenance, upperResistanceM2KW: 4.5, lowerResistanceM2KW: 4.2,
    estimatedResistanceM2KW: 4.35, uValueWm2K: 1 / 4.35, heatFluxWm2: 40 / 4.35,
    upperToLowerRatio: 4.5 / 4.2, halfSpreadPercent: 100 * .3 / 8.7,
    metalBridgingModelled: false, constructionComplianceAssessed: false,
    sections: (input['sections'] as { name: string; areaFraction: number }[]).map(section => ({
      name: section.name, areaFraction: section.areaFraction, path: { totalResistanceM2KW: 4.3 }
    }))
  };
  if (mode === 'thermal') return { input, provenance, uValueWm2K: .3, totalResistanceM2KW: 3.3333333, heatFluxWm2: 6, interfaces: [{ name: 'After synthetic layer', temperatureC: 1 }] };
  if (mode === 'moisture') return { input, provenance, uValueWm2K: .3, totalSdM: 2, insideSurfaceTemperatureC: 19, insideSurfaceRelativeHumidityPercent: 65, anyInterfaceSupersaturated: true, insideSurfaceAboveEightyPercentRh: false, requiresDetailedAssessment: true, interfaces: [{ name: 'Synthetic cold interface', temperatureC: 0, vapourPressurePa: 700, saturationPressurePa: 610.5, relativeHumidityPercent: 114.66, exceedsSaturation: true }] };
  const months = (input['months'] as Record<string, unknown>[]).map(m => { const { month, ...climate } = m; return { month, screening: calculation('moisture', { ...climate, insideSurfaceResistanceM2KW: input['insideSurfaceResistanceM2KW'], outsideSurfaceResistanceM2KW: input['outsideSurfaceResistanceM2KW'], layers: input['layers'] }) }; });
  return { input, provenance, months, supersaturatedMonths: months.map(m => m.month), highInteriorSurfaceHumidityMonths: [], requiresDetailedAssessment: true };
}
async function fixture(page: Page, role = 'ManagingDirector') {
  const state = { status: 200, wrongInput: false, normative: false, hold: false, corruptPdf: false };
  const errors: string[] = [], requests: { path: string; body: Record<string, unknown>; headers: Record<string, string> }[] = [];
  let release: (() => void) | undefined;
  page.on('pageerror', e => errors.push(e.message));
  await page.route('**/api/v1/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: 'synthetic-user', email: 'synthetic@example.test', displayName: 'Synthetic operator' }, activeTenantId: 'tenant-a', tenants: ['a', 'b'].map(k => ({ id: `tenant-${k}`, name: `Betrieb ${k.toUpperCase()}`, role, modules: ['core', 'commercial', 'technical'], capabilities: role === 'ManagingDirector' ? ['*'] : ['projects.read'] })) } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: projectId, revision: 1, name: 'Synthetisches Dachprojekt' }], total: 1, page: 1, pageSize: 25 } });
    if (path === `documents/${documentId}/download`) return route.fulfill({ contentType: 'application/pdf', body: state.corruptPdf ? Buffer.from('%PDF-damaged') : pdf });
    if (path === `technical-results/${recordId}/pdf`) {
      requests.push({ path, body: req.postDataJSON(), headers: req.headers() });
      return route.fulfill({ json: { id: documentId, projectId, sourceId: recordId, sourceKind: 'technical-results', sourceDigest: 'b'.repeat(64), state: 'Ready', contentType: 'application/pdf', size: pdf.length, sha256: createHash('sha256').update(pdf).digest('hex') } });
    }
    if (path === 'technical/runs') {
      const body = req.postDataJSON(); requests.push({ path, body, headers: req.headers() });
      const mode = (Object.keys(technicalMethods) as TechnicalMode[]).find(m => technicalMethods[m].method === body.method)!;
      return route.fulfill({ json: { ...body, id: recordId, tenantId: req.headers()['x-tenant-id'], revision: 1, methodVersion: '1.0.0', result: calculation(mode, body.input), digest: 'b'.repeat(64), isNormative: false } });
    }
    const mode = (Object.keys(technicalMethods) as TechnicalMode[]).find(m => technicalMethods[m].endpoint === path);
    if (mode) {
      const body = req.postDataJSON(); requests.push({ path, body, headers: req.headers() });
      if (state.hold) await new Promise<void>(resolve => { release = resolve; });
      if (state.status !== 200) return route.fulfill({ status: state.status, json: { code: 'forbidden' } });
      const result = calculation(mode, state.wrongInput ? { ...body, wrong: true } : body);
      if (state.normative) (result['provenance'] as Record<string, unknown>)['isNormative'] = true;
      return route.fulfill({ json: result });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { state, errors, requests, release: () => release?.() };
}
async function fillTapered(page: Page) {
  await page.getByRole('tab', { name: 'Gefälledämmung', exact: true }).click();
  for (const [id, value] of Object.entries({ lengthM: '12', widthM: '10', minimumThicknessM: '.08', lengthGradientPercent: '0', widthGradientPercent: '2' })) await page.locator(`#${id}`).fill(value);
  await page.getByLabel('Quelle der Maße', { exact: true }).fill('Synthetic independent plan');
}
async function saveTapered(page: Page) {
  await fillTapered(page); await page.getByRole('button', { name: 'Berechnen', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Berechnungsergebnis', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Projekte suchen', exact: true }).click();
  await page.getByLabel('Projekt für den Bericht').selectOption(projectId);
  await page.getByLabel('Berichtsbezeichnung').fill('Synthetic quantity review');
  await page.getByRole('button', { name: 'Berechnung im Projekt speichern', exact: true }).click();
  await expect(page.getByRole('button', { name: 'PDF erzeugen und herunterladen' })).toBeEnabled();
}
async function fillLayer(page: Page) {
  await page.getByLabel('Material', { exact: true }).fill('Synthetic layer');
  await page.getByLabel('Dicke (m)', { exact: true }).fill('.12');
  await page.getByLabel('Wärmeleitfähigkeit (W/mK)', { exact: true }).fill('.04');
  await page.getByLabel('Diffusionswiderstandszahl μ', { exact: true }).fill('10');
  await page.getByLabel('Quelle des Kennwerts', { exact: true }).fill('Synthetic material reference');
  await page.locator('#insideSurfaceResistanceM2KW').fill('.13');
  await page.locator('#outsideSurfaceResistanceM2KW').fill('.04');
}

test('tapered quantities can be reviewed, stored in a selected project and exported as verified PDF and JSON', async ({ page }, info) => {
  const f = await fixture(page); await page.goto('/technical'); await saveTapered(page);
  await expect(page.locator('dl>div').filter({ has: page.getByText('Dämmstoffvolumen ohne Verschnitt', { exact: true }) }).locator('dd')).toHaveText('21,6 m³');
  const jsonDownload = page.waitForEvent('download'); await page.getByRole('button', { name: 'Ergebnis als JSON exportieren' }).click(); expect((await jsonDownload).suggestedFilename()).toMatch(/\.json$/);
  const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'PDF erzeugen und herunterladen' }).click(); expect((await download).suggestedFilename()).toBe(`FirstBlick-Berechnung-${recordId}.pdf`);
  const saved = f.requests.find(r => r.path === 'technical/runs')!; expect(saved.body).toMatchObject({ projectId, roofRevisionId: null, method: 'roof.tapered-insulation', input: { minimumThicknessM: .08, lengthGradientPercent: 0 } });
  expect(saved.headers['x-tenant-id']).toBe('tenant-a'); expect(saved.headers['x-xsrf-token']).toBe('synthetic'); expect(saved.headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/i);
  expect(f.requests.find(r => r.path.endsWith('/pdf'))?.headers['if-match']).toBe('"1"');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(f.errors).toEqual([]);
  await page.screenshot({ path: info.outputPath('technical-tapered.png'), fullPage: true });
});

test('editing dimensions invalidates the calculation, saved evidence and download actions', async ({ page }) => {
  const f = await fixture(page); await page.goto('/technical'); await saveTapered(page); await page.locator('#widthM').fill('11');
  await expect(page.getByRole('heading', { name: 'Berechnungsergebnis', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'PDF erzeugen und herunterladen' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Ergebnis als JSON exportieren' })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('a corrupt PDF is never offered as a download', async ({ page }) => {
  const f = await fixture(page); f.state.corruptPdf = true; const downloads: string[] = []; page.on('download', x => downloads.push(x.suggestedFilename()));
  await page.goto('/technical'); await saveTapered(page); await page.getByRole('button', { name: 'PDF erzeugen und herunterladen' }).click();
  await expect(page.getByRole('alert')).toContainText('Prüfsumme'); expect(downloads).toEqual([]); expect(f.errors).toEqual([]);
});

for (const failure of ['wrongInput', 'normative'] as const) test(`invalid ${failure} response cannot be saved or exported`, async ({ page }) => {
  const f = await fixture(page); f.state[failure] = true; await page.goto('/technical'); await fillTapered(page); await page.getByRole('button', { name: 'Berechnen', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible(); await expect(page.getByRole('button', { name: 'Ergebnis als JSON exportieren' })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('a late calculation cannot restore the previous business or its inputs', async ({ page }) => {
  const f = await fixture(page); f.state.hold = true; await page.goto('/technical'); await fillTapered(page); await page.getByRole('button', { name: 'Berechnen', exact: true }).click();
  await expect.poll(() => f.requests.length).toBe(1); await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); f.release();
  await expect(page.locator('#lengthM')).toHaveValue(''); await expect(page.getByRole('heading', { name: 'Berechnungsergebnis', exact: true })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('a server-side revoked permission clears calculation and stored project evidence', async ({ page }) => {
  const f = await fixture(page); await page.goto('/technical'); await saveTapered(page); f.state.status = 403;
  await page.getByRole('button', { name: 'Berechnen', exact: true }).click(); await expect(page.getByRole('alert')).toContainText('Berechtigung');
  await expect(page.getByRole('button', { name: 'Ergebnis als JSON exportieren' })).toHaveCount(0); await expect(page.getByRole('button', { name: 'PDF erzeugen und herunterladen' })).toHaveCount(0); expect(f.errors).toEqual([]);
});

test('single-climate moisture screening displays explicit warnings without a normative conclusion', async ({ page }, info) => {
  const f = await fixture(page); await page.goto('/technical'); await page.getByRole('tab', { name: 'Feuchte – Einzelklima', exact: true }).click(); await fillLayer(page);
  for (const [key, value] of Object.entries({ insideTemperatureC: '20', outsideTemperatureC: '0', insideRelativeHumidityPercent: '60', outsideRelativeHumidityPercent: '80' })) await page.locator(`#${key}`).fill(value);
  await page.getByRole('button', { name: 'Berechnen', exact: true }).click(); await expect(page.getByText('Mögliche Übersättigung an mindestens einer Schichtgrenze.', { exact: false })).toBeVisible();
  await expect(page.getByText('Kein normativer Nachweis.', { exact: true })).toBeVisible(); expect(f.requests[0].body['layers']).toEqual([{ name: 'Synthetic layer', thicknessM: .12, conductivityWmK: .04, vapourResistanceFactor: 10, valueSource: 'Synthetic material reference' }]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); expect(f.errors).toEqual([]); await page.screenshot({ path: info.outputPath('technical-moisture.png'), fullPage: true });
});

test('all twelve climates require explicit values and render separate monthly evidence, not a year-end balance', async ({ page }) => {
  const f = await fixture(page); await page.goto('/technical'); await page.getByRole('tab', { name: 'Feuchte – 12 Monate', exact: true }).click(); await fillLayer(page);
  await expect(page.getByRole('button', { name: 'Berechnen', exact: true })).toBeDisabled();
  for (let month = 1; month <= 12; month++) for (const [key, value] of Object.entries({ insideTemperatureC: '20', outsideTemperatureC: '0', insideRelativeHumidityPercent: '60', outsideRelativeHumidityPercent: '80' })) await page.locator(`#climate-${month}-${key}`).fill(value);
  await page.getByRole('button', { name: 'Berechnen', exact: true }).click(); await expect(page.locator('.month-result')).toHaveCount(12);
  await expect(page.getByText('Keine Jahrestauwasserbilanz und keine Austrocknungsreserve.', { exact: false })).toBeVisible();
  expect((f.requests[0].body['months'] as { month: number }[]).map(m => m.month)).toEqual(monthNames.map((_, i) => i + 1)); expect(f.errors).toEqual([]);
});

test('technical methods are keyboard navigable and no default calculation is sent', async ({ page }) => {
  const f = await fixture(page); await page.goto('/technical'); const first = page.getByRole('tab', { name: 'Dachgeometrie', exact: true }); await first.focus(); await page.keyboard.press('End');
  await expect(page.getByRole('tab', { name: 'Gefälledämmung', exact: true })).toBeFocused(); await expect(page.getByRole('tab', { name: 'Gefälledämmung', exact: true })).toHaveAttribute('aria-selected', 'true'); await page.keyboard.press('Home'); await expect(first).toBeFocused(); expect(f.requests).toEqual([]); expect(f.errors).toEqual([]);
});

test('a worker without technical capability cannot open calculator controls', async ({ page }) => {
  const f = await fixture(page, 'Worker'); await page.goto('/technical'); await expect(page.getByRole('heading', { name: 'Zugriff eingeschränkt' })).toBeVisible(); await expect(page.getByRole('tab')).toHaveCount(0); expect(f.requests).toEqual([]); expect(f.errors).toEqual([]);
});

test('inhomogeneous rafter workbench validates area sections, displays non-normative bounds and persists a bound snapshot', async ({ page }) => {
  const f = await fixture(page); await page.goto('/technical');
  await page.getByRole('tab', { name: 'Sparrendach – Widerstandsgrenzen' }).click();
  await expect(page.getByRole('button', { name: 'Berechnen', exact: true })).toBeDisabled();
  for (const [id, value] of Object.entries({
    insideTemperatureC: '20', outsideTemperatureC: '-20',
    insideSurfaceResistanceM2KW: '.13', outsideSurfaceResistanceM2KW: '.04'
  })) await page.locator(`#${id}`).fill(value);
  await page.locator('#inhomogeneous-source').fill('Synthetic material reference');
  for (const [i, section] of [{ name: 'Rafter', fraction: '.15', conductivity: '.12' }, { name: 'Insulation', fraction: '.85', conductivity: '.04' }].entries()) {
    await page.locator(`#section-name-${i}`).fill(section.name);
    await page.locator(`#section-fraction-${i}`).fill(section.fraction);
    await page.locator(`#section-material-${i}-0`).fill(section.name);
    await page.locator(`#section-thickness-${i}-0`).fill('.2');
    await page.locator(`#section-conductivity-${i}-0`).fill(section.conductivity);
    await page.locator(`#section-source-${i}-0`).fill('Synthetic manufacturer values');
  }
  await expect(page.getByRole('button', { name: 'Berechnen', exact: true })).toBeDisabled();
  await page.getByLabel('Ich bestätige, dass in diesem Modell keine metallischen Wärmebrücken').check();
  await page.getByRole('button', { name: 'Berechnen', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Berechnungsergebnis' })).toBeVisible();
  await expect(page.getByText('Oberer Widerstandsgrenze', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Obere Widerstandsgrenze', { exact: true })).toBeVisible();
  await expect(page.getByText('kein normativer Nachweis', { exact: false }).first()).toBeVisible();
  const call = f.requests.find(r => r.path === 'technical/thermal/inhomogeneous')!;
  const input = call.body;
  expect(input['noMetalBridgingConfirmed']).toBe(true);
  expect((input['sections'] as { name: string; areaFraction: number }[]).map(x => x.name)).toEqual(['Insulation', 'Rafter']);
  await page.getByRole('button', { name: 'Projekte suchen', exact: true }).click();
  await page.getByLabel('Projekt für den Bericht').selectOption(projectId);
  await page.getByLabel('Berichtsbezeichnung').fill('Synthetic roof assessment');
  await page.getByRole('button', { name: 'Berechnung im Projekt speichern', exact: true }).click();
  await expect(page.getByRole('button', { name: 'PDF erzeugen und herunterladen' })).toBeEnabled();
  expect(f.requests.find(r => r.path === 'technical/runs')?.body['method']).toBe('thermal.inhomogeneous');
  await page.locator('#section-fraction-0').fill('.2');
  await expect(page.getByRole('heading', { name: 'Berechnungsergebnis' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'PDF erzeugen und herunterladen' })).toHaveCount(0);
  expect(f.errors).toEqual([]);
});
