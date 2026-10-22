import { expect, Page, test } from '@playwright/test';
import type { DependencyGraph } from '../../src/app/core/task-dependencies.models';

const project = '91000000-0000-4000-8000-000000000001', first = '91000000-0000-4000-8000-000000000002', second = '91000000-0000-4000-8000-000000000003';
const source = (): DependencyGraph => ({ tenantId: 'tenant-a', projectId: project, projectName: 'Dachsanierung Nord', tasks: [
  { id: first, title: 'Material prüfen', status: 'Open', revision: 1, archived: false, predecessorTaskIds: [] },
  { id: second, title: 'Dach schließen', status: 'Open', revision: 1, archived: false, predecessorTaskIds: [] }
] });
async function fixture(page: Page, options: { reader?: boolean; conflict?: boolean; malformed?: boolean; delayed?: boolean } = {}) {
  let graph = source(); const calls: { path: string; body: any; headers: Record<string, string> }[] = []; let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.slice('/api/v1/'.length), method = request.method();
    if (path === 'auth/csrf') return route.fulfill({ json: {}, headers: { 'Set-Cookie': 'XSRF-TOKEN=test-csrf; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: 'test-user', displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(id => ({ id: `tenant-${id}`, name: `Testbetrieb ${id.toUpperCase()}`, role: options.reader ? 'Accounting' : 'Office', modules: ['core'], capabilities: options.reader ? ['projects.read', 'tasks.read'] : ['*'] })) } });
    if (path === `projects/${project}/task-dependencies` && method === 'GET') {
      if (options.delayed) await gate;
      return route.fulfill({ json: options.malformed ? { ...graph, tenantId: 'tenant-b' } : graph });
    }
    if (path.startsWith('tasks/') && ['PUT', 'POST'].includes(method)) {
      const body = request.postDataJSON(); calls.push({ path, body, headers: request.headers() });
      if (options.conflict) return route.fulfill({ status: 409, json: { code: 'revision_conflict', detail: 'Die Aufgabe wurde zwischenzeitlich geändert.' } });
      const id = path.split('/')[1], original = graph.tasks.find(task => task.id === id)!;
      const changed = { ...original, ...body, revision: original.revision + 1 }; graph = { ...graph, tasks: graph.tasks.map(task => task.id === id ? changed : task) };
      return route.fulfill({ json: changed });
    }
    if (path === 'projects') return route.fulfill({ json: { items: [{ id: project, name: 'Dachsanierung Nord', revision: 1 }], total: 1, page: 1, pageSize: 25 } });
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, release };
}
async function open(page: Page) { await page.goto(`/task-dependencies?projectId=${project}`); await expect(page.getByRole('heading', { name: 'Aufgabenabhängigkeiten', exact: true })).toBeVisible(); }

test('saves predecessors, exposes blockers and advances work after its predecessor is done', async ({ page }) => {
  const api = await fixture(page); await open(page);
  await page.getByRole('button', { name: 'Dach schließen auswählen', exact: true }).click();
  await page.getByRole('checkbox', { name: /Material prüfen/ }).check();
  await page.getByRole('button', { name: 'Abhängigkeiten speichern', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Abhängigkeiten gespeichert');
  await expect(page.getByText('Noch offene Vorgänger', { exact: true })).toBeVisible();
  await expect(page.locator('#dependency-status option[value="InProgress"]')).toBeDisabled();
  expect(api.calls[0].body).toEqual({ predecessorTaskIds: [first] }); expect(api.calls[0].headers['if-match']).toBe('"1"');
  await page.getByRole('button', { name: 'Material prüfen auswählen', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: /Dach schließen/ })).toBeDisabled();
  await page.getByLabel('Aufgabenstatus', { exact: true }).selectOption('Done'); await page.getByRole('button', { name: 'Status speichern', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Aufgabenstatus gespeichert');
  await page.getByRole('button', { name: 'Dach schließen auswählen', exact: true }).click();
  await expect(page.locator('#dependency-status option[value="InProgress"]')).toBeEnabled();
  await page.getByLabel('Aufgabenstatus', { exact: true }).selectOption('InProgress'); await page.getByRole('button', { name: 'Status speichern', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Aufgabenstatus gespeichert');
  await expect(page.getByRole('button', { name: 'Abhängigkeiten speichern', exact: true })).toBeDisabled();
  expect(api.calls.every(call => call.headers['x-tenant-id'] === 'tenant-a' && call.headers['idempotency-key'] && call.headers['x-xsrf-token'] === 'test-csrf')).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('a revision conflict blocks further edits until the graph is reloaded', async ({ page }) => {
  const api = await fixture(page, { conflict: true }); await open(page); await page.getByRole('button', { name: 'Dach schließen auswählen', exact: true }).click();
  await page.getByRole('checkbox', { name: /Material prüfen/ }).check(); await page.getByRole('button', { name: 'Abhängigkeiten speichern', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('zwischenzeitlich'); await expect(page.getByRole('button', { name: 'Abhängigkeiten speichern', exact: true })).toBeDisabled();
  await expect(page.getByRole('status')).toHaveCount(0); expect(api.calls).toHaveLength(1);
  await page.getByRole('button', { name: 'Projektstand neu laden', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Abhängigkeiten speichern', exact: true })).toBeEnabled();
  await expect(page.getByRole('checkbox', { name: /Material prüfen/ })).not.toBeChecked();
});

test('accounting can inspect the graph without mutation controls', async ({ page }) => {
  const api = await fixture(page, { reader: true }); await open(page); await page.getByRole('button', { name: 'Dach schließen auswählen', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: /Material prüfen/ })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Abhängigkeiten speichern', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Status speichern', exact: true })).toHaveCount(0); expect(api.calls).toHaveLength(0);
});

test('malformed or foreign project data is rejected without displaying tasks', async ({ page }) => {
  await fixture(page, { malformed: true }); await open(page); await expect(page.getByRole('alert')).toContainText('unvollständig oder widersprüchlich');
  await expect(page.getByRole('button', { name: 'Dach schließen auswählen', exact: true })).toHaveCount(0);
});

test('a delayed graph response cannot repopulate a different business', async ({ page }) => {
  const api = await fixture(page, { delayed: true }); await open(page);
  await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
  await expect(page.getByRole('button', { name: 'Dach schließen auswählen', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Dachsanierung Nord', exact: true })).toHaveCount(0);
});
