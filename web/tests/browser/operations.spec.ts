import { expect, Page, test } from '@playwright/test';

function snapshot(pending = 7) {
  return {
    schema: 'firstblick.operations/1.0.0', capturedAt: '2026-10-20T08:50:00Z',
    queue: { pending, due: 3, leased: 2, expiredLeases: 1, failed: 4, uncertain: 2, oldestDueAgeSeconds: 361 },
    documents: { ready: 12, staged: 3, stalled: 1 }, thresholds: { dueAgeSeconds: 300, stagedAgeSeconds: 900 },
    signals: [{ code: 'external_outcome_uncertain', severity: 'action', affected: 2 }, { code: 'queue_delay', severity: 'warning', affected: 3 }]
  };
}

async function fixture(page: Page, role = 'ManagingDirector') {
  const state = { status: 200, holdAlpha: false, malformed: false };
  const requests: string[] = [], errors: string[] = [];
  let releaseAlpha: (() => void) | undefined;
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length);
    if (path === 'auth/me') return route.fulfill({ json: {
      user: { id: 'synthetic-operator', email: 'operator@example.test', displayName: 'Alex Beispiel' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(key => ({ id: `tenant-${key}`, name: key === 'a' ? 'Betrieb Alpha' : 'Betrieb Beta', role,
        modules: ['core', 'finance', 'commercial'], capabilities: role === 'ManagingDirector' ? ['*'] : ['projects.read'] }))
    } });
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Strict' } });
    if (path === 'operations/snapshot') {
      const tenant = request.headers()['x-tenant-id']; requests.push(tenant);
      if (state.holdAlpha && tenant === 'tenant-a') await new Promise<void>(resolve => { releaseAlpha = resolve; });
      if (state.status !== 200) return route.fulfill({ status: state.status, json: { code: state.status === 403 ? 'forbidden' : 'server_error', detail: 'Der Betriebszustand konnte nicht ermittelt werden.' } });
      return route.fulfill({ json: state.malformed ? { ...snapshot(), queue: {} } : snapshot(tenant === 'tenant-b' ? 52 : 7) });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { state, requests, errors, releaseAlpha: () => releaseAlpha?.() };
}

function pendingCount(page: Page) { return page.locator('dl>div').filter({ has: page.getByText('Zur Bearbeitung vorgemerkt', { exact: true }) }).locator('dd'); }

test('operational snapshot distinguishes counts, documented warning thresholds and unconfirmed provider outcomes', async ({ page }, testInfo) => {
  const context = await fixture(page); await page.goto('/operations');
  await expect(page.getByRole('heading', { name: 'Betriebsübersicht', exact: true })).toBeVisible();
  await expect(pendingCount(page)).toHaveText('7');
  await expect(page.getByText('Versandresultat klären', { exact: true })).toBeVisible();
  await expect(page.getByText('Der Zustelldienst könnte den Auftrag bereits angenommen haben.', { exact: false })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Was diese Ansicht erfasst', exact: true })).toBeVisible();
  await expect(page.getByText('Die Warnung zur Warteschlange beginnt ab 5 Min.', { exact: false })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Hintergrundaufträge und Klärungsfälle öffnen →', exact: true })).toHaveAttribute('href', '/management');
  expect(context.requests).toEqual(['tenant-a']);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(context.errors).toEqual([]);
  await page.screenshot({ path: testInfo.outputPath('operations.png'), fullPage: true });
});

for (const role of ['Office', 'Worker']) {
  test(`operations does not request data or show administrative navigation for ${role}`, async ({ page }) => {
    const context = await fixture(page, role); await page.goto('/operations');
    await expect(page.getByRole('heading', { name: 'Zugriff eingeschränkt', exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Betriebsübersicht', exact: true })).toHaveCount(0);
    expect(context.requests).toEqual([]); expect(context.errors).toEqual([]);
  });
}

test('refresh removes stale values and displays an unavailable result without inventing empty healthy counts', async ({ page }) => {
  const context = await fixture(page); await page.goto('/operations'); await expect(pendingCount(page)).toHaveText('7');
  context.state.status = 503; await page.getByRole('button', { name: 'Aktualisieren', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('konnte nicht ermittelt werden');
  await expect(pendingCount(page)).toHaveCount(0);
  await expect(page.getByText('überschreiten aktuell keine', { exact: false })).toHaveCount(0);
  context.state.status = 403; await page.getByRole('button', { name: 'Aktualisieren', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Berechtigung'); await expect(pendingCount(page)).toHaveCount(0);
  expect(context.errors).toEqual([]);
});

test('late metrics for a previous business cannot overwrite the newly selected business', async ({ page }) => {
  const context = await fixture(page); context.state.holdAlpha = true; await page.goto('/operations');
  await expect.poll(() => context.requests.length).toBe(1);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b');
  await expect(pendingCount(page)).toHaveText('52'); context.releaseAlpha();
  await expect(pendingCount(page)).toHaveText('52');
  await page.getByRole('button', { name: 'Aktualisieren', exact: true }).click(); await expect(pendingCount(page)).toHaveText('52');
  expect(context.requests).toEqual(['tenant-a', 'tenant-b', 'tenant-b']); expect(context.errors).toEqual([]);
});

test('incomplete successful response is rejected and never rendered as valid monitoring data', async ({ page }) => {
  const context = await fixture(page); context.state.malformed = true; await page.goto('/operations');
  await expect(page.getByRole('alert')).toContainText('unvollständige Daten');
  await expect(pendingCount(page)).toHaveCount(0); expect(context.errors).toEqual([]);
});
