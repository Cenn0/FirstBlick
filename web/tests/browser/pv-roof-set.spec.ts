import { expect, test, Page } from '@playwright/test';
import { PvInput } from '../../src/app/core/pv-layout.models';
import { PvRoofSetInput } from '../../src/app/core/pv-roof-set.models';

const projectId = '10000000-0000-4000-8000-000000000001';
const recordId = '20000000-0000-4000-8000-000000000002';

function single(layout: PvInput) {
  const width = layout.roofPlaneWidthM;
  const height = layout.roofPlaneHeightM;
  const cols = Math.floor(width / layout.moduleWidthM), rows = Math.floor(height / layout.moduleHeightM);
  const placements = Array.from({ length: cols * rows }, (_, i) => ({
    row: Math.floor(i / cols) + 1, column: i % cols + 1,
    x: (i % cols) * layout.moduleWidthM, y: Math.floor(i / cols) * layout.moduleHeightM,
    widthM: layout.moduleWidthM, heightM: layout.moduleHeightM
  }));
  return {
    input: layout, selectedOrientation: 'Portrait', roofPlaneAreaM2: width * height,
    gridCandidateCount: placements.length, obstructionRejectedCount: 0,
    installedModules: placements.length, installedModuleAreaM2: placements.length *
      layout.moduleWidthM * layout.moduleHeightM,
    nominalDcPowerWp: placements.length * layout.moduleRatedPowerWp, placements,
    structureOrElectricalDesignApproved: false,
    provenance: { methodId: 'pv-roof-plane-grid', methodVersion: '1.0.0',
      inputSha256: 'a'.repeat(64), isNormative: false,
      limitations: ['Geometry only', 'No mounting approval', 'No electrical approval'] }
  };
}
function full(input: PvRoofSetInput) {
  const planes = input.planes.map(plane => ({ name: plane.name, layout: single(plane.layout) }));
  const sum = (key: 'installedModules' | 'nominalDcPowerWp' | 'roofPlaneAreaM2' | 'installedModuleAreaM2' |
    'gridCandidateCount' | 'obstructionRejectedCount') => planes.reduce((n, p) => n + p.layout[key], 0);
  return {
    input, planes, installedModules: sum('installedModules'),
    nominalDcPowerWp: sum('nominalDcPowerWp'), roofPlaneAreaM2: sum('roofPlaneAreaM2'),
    installedModuleAreaM2: sum('installedModuleAreaM2'), gridCandidateCount: sum('gridCandidateCount'),
    obstructionRejectedCount: sum('obstructionRejectedCount'),
    structureOrElectricalDesignApproved: false,
    provenance: { methodId: 'pv-roof-set-grid', methodVersion: '1.0.0',
      inputSha256: 'b'.repeat(64), isNormative: false,
      limitations: ['Independent planes', 'No physical overlap assessment', 'No installation approval'] }
  };
}
async function fixture(page: Page) {
  const calls: { path: string; body: unknown }[] = [];
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: {
      user: { id: 'operator', email: 'operator@example.test', displayName: 'Synthetic operator' },
      activeTenantId: 'tenant-a',
      tenants: [{ id: 'tenant-a', name: 'Synthetic business', role: 'ManagingDirector',
        modules: ['core', 'technical'], capabilities: ['*'] }]
    } });
    if (path === 'auth/csrf') return route.fulfill({ json: {},
      headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'projects') return route.fulfill({ json: { items: [
      { id: projectId, revision: 1, name: 'Synthetic PV project' }
    ], total: 1, page: 1, pageSize: 25 } });
    if (path === 'technical/roof/pv-multi-layout') {
      const body = request.postDataJSON() as PvRoofSetInput;
      calls.push({ path, body });
      return route.fulfill({ json: full(body) });
    }
    if (path === 'technical/runs') {
      const body = request.postDataJSON() as {
        projectId: string; method: string; input: PvRoofSetInput
      };
      calls.push({ path, body });
      return route.fulfill({ json: { ...body, id: recordId, digest: 'c'.repeat(64),
        isNormative: false, result: full(body.input) } });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, errors };
}
async function fillPlane(page: Page, id: number, name: string) {
  await page.locator('#set-name-' + id).fill(name);
  for (const [field, value] of [
    ['width', '2'], ['height', '2'], ['module-width', '1'], ['module-height', '1'],
    ['power', '420']
  ]) await page.locator('#set-' + field + '-' + id).fill(value);
  await page.locator('#set-orientation-' + id).selectOption('Portrait');
  await page.locator('#set-roof-source-' + id).fill('Synthetic surveyed roof plane');
  await page.locator('#set-module-source-' + id).fill('Synthetic module datasheet');
}

for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
  test('multi-plane PV preview and project evidence work with controlled transport at ' +
    viewport.width + 'px', async ({ page }) => {
    await page.setViewportSize(viewport);
    const f = await fixture(page);
    await page.goto('/technical/pv-set');
    await page.locator('#pv-set-source').fill('Synthetic roof set');
    await fillPlane(page, 1, 'South');
    await page.getByRole('button', { name: 'Weitere Dachfläche' }).click();
    await fillPlane(page, 2, 'North');
    await page.getByRole('button', { name: 'Gesamte PV-Belegung berechnen' }).click();
    const result = page.getByRole('region', { name: 'Berechnung mehrerer Dachflächen' });
    await expect(result).toContainText('3360 Wp');
    await expect(result.getByRole('heading', { name: 'South' })).toBeVisible();
    await expect(result.getByRole('heading', { name: 'North' })).toBeVisible();
    const preview = f.calls.find(c => c.path === 'technical/roof/pv-multi-layout')!;
    expect((preview.body as PvRoofSetInput).planes.map(p => p.name)).toEqual(['South', 'North']);
    await page.getByRole('button', { name: 'Projekte suchen' }).click();
    await page.locator('#set-project').selectOption(projectId);
    await page.locator('#set-title').fill('Synthetic PV geometry');
    await page.getByRole('button', { name: 'Im Projekt speichern' }).click();
    await expect(page.getByRole('status')).toContainText('gespeichert');
    const save = f.calls.find(c => c.path === 'technical/runs')!;
    expect(save.body).toMatchObject({ projectId, method: 'roof.pv-multi-layout' });
    expect(f.errors).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

test('duplicate plane names are rejected without sending a calculation', async ({ page }) => {
  const f = await fixture(page);
  await page.goto('/technical/pv-set');
  await page.locator('#pv-set-source').fill('Synthetic roof set');
  await fillPlane(page, 1, 'South');
  await page.getByRole('button', { name: 'Weitere Dachfläche' }).click();
  await fillPlane(page, 2, 'south');
  await page.getByRole('button', { name: 'Gesamte PV-Belegung berechnen' }).click();
  await expect(page.getByRole('alert')).toContainText('eindeutig');
  expect(f.calls).toHaveLength(0);
  expect(f.errors).toEqual([]);
});
