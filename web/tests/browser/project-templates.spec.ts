import { expect, Page, test } from '@playwright/test';
import type { ProjectTemplate, TemplateApplication, TemplatePreview, TemplateProject } from '../../src/app/core/project-template.models';

const templateId = '82000000-0000-4000-8000-000000000001', projectId = '82000000-0000-4000-8000-000000000002', actor = '82000000-0000-4000-8000-000000000003';
const customer = '82000000-0000-4000-8000-000000000004', building = '82000000-0000-4000-8000-000000000005', applicationId = '82000000-0000-4000-8000-000000000006';
const created = '2026-10-21T08:00:00Z', now = '2026-10-22T08:00:00Z';
const id = (index: number) => `82000000-0000-4000-8000-${String(100 + index).padStart(12, '0')}`;
const entity = (value: string) => ({ id: value, tenantId: 'tenant-a', revision: 1, archived: false, createdAt: created, updatedAt: created });
const makeTemplate = (): ProjectTemplate => ({ ...entity(templateId), name: 'Sanierung Standard', description: 'Vorbereitung der Dacharbeiten', projectType: 'Renovation',
  tasks: [{ title: 'Material prüfen', description: 'Lieferschein abgleichen', offsetDays: -1 }, { title: 'Baustelle einrichten', description: '', offsetDays: 0 }] });
const makeProject = (): TemplateProject => ({ ...entity(projectId), revision: 3, name: 'Dachprojekt Nord', number: 'P-17', description: '', customerId: customer, buildingId: building,
  type: 'Renovation', status: 'Planning', plannedStart: '2026-10-26', plannedEnd: '2026-11-10' });
interface Options { empty?: boolean; reader?: boolean; history?: boolean; closed?: boolean; archived?: boolean; moduleOff?: boolean; conflict?: 'save' | 'apply'; malformed?: 'list' | 'preview' | 'apply' | 'application'; delay?: 'template' | 'preview' | 'apply' | 'application'; pagination?: boolean; missingStart?: boolean; refreshFailure?: boolean }
async function fixture(page: Page, options: Options = {}) {
  let templates = options.empty ? [] : [makeTemplate()];
  if (options.pagination) templates.push(...Array.from({ length: 20 }, (_, index) => ({ ...makeTemplate(), id: id(index), name: `Weitere Vorlage ${index + 1}` })));
  let project = { ...makeProject(), ...(options.closed ? { status: 'Completed' as const, archived: true } : {}), ...(options.missingStart ? { plannedStart: null } : {}) };
  let revoked = false, mutations = 0, release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; });
  const calls: { path: string; method: string; body: any; headers: Record<string, string> }[] = [];
  const previewFor = (body: { templateId: string; anchorDate: string | null }): TemplatePreview => {
    const template = structuredClone(templates.find(row => row.id === body.templateId)!);
    const sourceProject = structuredClone(project), anchor = body.anchorDate ?? project.plannedStart!;
    return { tenantId: 'tenant-a', template, templateDigest: 'a'.repeat(64), project: sourceProject, projectDigest: 'b'.repeat(64), anchorDate: anchor,
      anchorKind: body.anchorDate ? 'Explicit' : 'ProjectStart', tasks: template.tasks.map((task, index) => ({ ...task, position: index + 1,
        dueDate: new Date(Date.parse(anchor + 'T00:00:00Z') + task.offsetDays * 86_400_000).toISOString().slice(0, 10) })), digest: 'c'.repeat(64) };
  };
  const original = (preview: TemplatePreview): TemplateApplication => ({ ...entity(applicationId), createdAt: now, updatedAt: now, templateId: preview.template.id,
    projectId, preview, appliedBy: actor, appliedAt: now, tasks: preview.tasks.map((_, index) => ({ taskId: id(70 + index), position: index + 1 })), digest: 'd'.repeat(64) });
  let applications: TemplateApplication[] = [];
  if (options.history) {
    const oldProject = project; project = makeProject(); applications = [original(previewFor({ templateId, anchorDate: null }))]; project = oldProject;
  }
  if (options.archived) templates[0] = { ...templates[0], archived: true, revision: 2, name: 'Sanierung überarbeitet' };
  const summary = (application: TemplateApplication) => ({ id: application.id, tenantId: application.tenantId, templateId: application.templateId, projectId,
    templateName: application.preview.template.name, templateRevision: application.preview.template.revision, projectName: application.preview.project.name,
    anchorDate: application.preview.anchorDate, anchorKind: application.preview.anchorKind, appliedBy: actor, appliedAt: application.appliedAt, taskCount: application.tasks.length, digest: application.digest });
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname.slice('/api/v1/'.length), method = request.method(), other = request.headers()['x-tenant-id'] === 'tenant-b';
    if (path === 'auth/csrf') return route.fulfill({ json: { ready: true }, headers: { 'Set-Cookie': 'XSRF-TOKEN=csrf-test; Path=/; SameSite=Strict' } });
    if (path === 'auth/me') return route.fulfill({ json: { user: { id: actor, displayName: 'Testperson', email: 'test@example.test' }, activeTenantId: 'tenant-a',
      tenants: ['a', 'b'].map(value => ({ id: `tenant-${value}`, name: `Testbetrieb ${value.toUpperCase()}`, role: options.reader ? 'Accounting' : 'Office', modules: options.moduleOff ? [] : ['core'],
        capabilities: options.reader || revoked ? ['project-templates.read', 'project-template-applications.read', 'projects.read', 'tasks.read'] : ['*'] })) } });
    if (path === 'project-templates' && method === 'GET') {
      const pageNumber = Number(url.searchParams.get('page') || 1), term = (url.searchParams.get('search') || '').toLowerCase();
      const rows = other ? [] : templates.filter(row => (url.searchParams.get('includeArchived') === 'true' || !row.archived) && row.name.toLowerCase().includes(term));
      const items = rows.slice((pageNumber - 1) * 20, pageNumber * 20).map(row => ({ id: row.id, tenantId: row.tenantId, revision: row.revision, archived: row.archived,
        name: row.name, description: row.description, projectType: row.projectType, taskCount: row.tasks.length }));
      return route.fulfill({ json: { items: options.malformed === 'list' && items.length ? [{ ...items[0], tenantId: 'tenant-b' }] : items, total: rows.length, page: pageNumber, pageSize: 20 } });
    }
    if (path.startsWith('project-templates/') && method === 'GET') {
      const value = structuredClone(templates.find(row => row.id === path.split('/')[1])); if (options.delay === 'template') await gate;
      return value && !other ? route.fulfill({ json: value }) : route.fulfill({ status: 404, json: { detail: 'Vorlage nicht gefunden.' } });
    }
    if (path.startsWith('project-templates') && ['POST', 'PUT'].includes(method)) {
      const body = request.postDataJSON(); calls.push({ path, method, body, headers: request.headers() });
      if (options.conflict === 'save') return route.fulfill({ status: 409, json: { code: 'revision_conflict' } });
      if (method === 'PUT' || path.endsWith('/archive')) {
        const prior = templates.find(row => row.id === path.split('/')[1])!;
        const changed = { ...prior, ...body, revision: prior.revision + 1, updatedAt: now }; templates = templates.map(row => row.id === prior.id ? changed : row); return route.fulfill({ json: changed });
      }
      const value = { ...entity(id(40)), ...body, createdAt: now, updatedAt: now }; templates.push(value); return route.fulfill({ json: value });
    }
    if (path === 'projects') return route.fulfill({ json: { items: other ? [] : [project], total: other ? 0 : 1, page: 1, pageSize: 25 } });
    if (path === `projects/${projectId}`) {
      if (options.refreshFailure && mutations) return route.fulfill({ status: 503, json: { detail: 'Projektstand derzeit nicht abrufbar.' } });
      return route.fulfill({ json: project });
    }
    if (path === `projects/${projectId}/template-preview` || path === `projects/${projectId}/template-apply`) {
      const body = request.postDataJSON(); calls.push({ path, method, body, headers: request.headers() }); const preview = previewFor(body);
      if (path.endsWith('template-preview')) {
        if (options.delay === 'preview') await gate;
        return route.fulfill({ json: options.malformed === 'preview' ? { ...preview, tasks: preview.tasks.map(task => ({ ...task, dueDate: '2026-10-24' })) } : preview });
      }
      if (options.conflict === 'apply') return route.fulfill({ status: 409, json: { code: 'revision_conflict' } });
      const application = original(preview); applications = [application]; project = { ...project, revision: project.revision + 1, updatedAt: now }; mutations++;
      const tasks = preview.tasks.map((task, index) => ({ ...entity(application.tasks[index].taskId), createdAt: now, updatedAt: now, projectId, assignedEmployeeId: null, status: 'Open',
        title: task.title, description: task.description, dueDate: task.dueDate, templateOrigin: { applicationId, templateId: preview.template.id, templateRevision: preview.template.revision, position: index + 1, applicationDigest: application.digest } }));
      if (options.delay === 'apply') await gate;
      return route.fulfill({ json: { application, projectRevision: project.revision, tasks: options.malformed === 'apply' ? tasks.map(task => ({ ...task, assignedEmployeeId: actor })) : tasks } });
    }
    if (path === `projects/${projectId}/template-applications`) return route.fulfill({ json: { tenantId: 'tenant-a', projectId, items: applications.map(summary), total: applications.length, page: 1, pageSize: 20 } });
    if (path === `project-template-applications/${applicationId}`) {
      const value = structuredClone(applications[0]); if (options.delay === 'application') await gate;
      return route.fulfill({ json: options.malformed === 'application' ? { ...value, digest: 'e'.repeat(64) } : value });
    }
    return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 25 } });
  });
  return { calls, release, revoke: () => { revoked = true; } };
}
async function open(page: Page, withProject = false) {
  await page.goto('/project-templates' + (withProject ? `?projectId=${projectId}` : ''));
  await expect(page.getByRole('heading', { name: 'Projektvorlagen', exact: true })).toBeVisible();
}
async function select(page: Page) { await page.getByRole('button', { name: 'Vorlage Sanierung Standard öffnen', exact: true }).click(); await expect(page.locator('.template-detail')).toContainText('Sanierung Standard'); }
async function prepare(page: Page) { await open(page, true); await select(page); await expect(page.getByRole('heading', { name: 'Dachprojekt Nord', exact: true })).toBeVisible(); await page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true }).click(); await expect(page.getByRole('heading', { name: 'Geprüfte Aufgabenvorschau', exact: true })).toBeVisible(); }
const confirmLabel = 'Vorlage, Projekt und Fristen geprüft. Alle Aufgaben unzugewiesen anlegen.';

test('creates and reorders a checklist, edits it, then archives and restores the retained template', async ({ page }) => {
  const api = await fixture(page, { empty: true }); await open(page); await page.getByRole('button', { name: 'Neue Projektvorlage', exact: true }).click();
  await page.getByLabel('Name der Vorlage', { exact: true }).fill('Reparatur Ablauf'); await page.getByLabel('Projektart', { exact: true }).selectOption('Repair');
  await page.getByLabel('Aufgabe 1 – Titel', { exact: true }).fill('Dach prüfen'); await page.getByLabel('Aufgabe 1 – Kalendertage', { exact: true }).fill('-2');
  await page.getByRole('button', { name: 'Aufgabe hinzufügen', exact: true }).click(); await page.getByLabel('Aufgabe 2 – Titel', { exact: true }).fill('Material bestellen');
  await page.getByRole('button', { name: 'Aufgabe 2 nach oben', exact: true }).click(); await expect(page.getByLabel('Aufgabe 1 – Titel', { exact: true })).toHaveValue('Material bestellen');
  await page.getByRole('button', { name: 'Vorlage speichern', exact: true }).click(); await expect(page.getByRole('status').filter({ hasText: 'Projektvorlage angelegt' })).toBeVisible();
  expect(api.calls[0].body.tasks.map((row: any) => row.title)).toEqual(['Material bestellen', 'Dach prüfen']);
  await page.getByRole('button', { name: 'Vorlage bearbeiten', exact: true }).click(); await page.getByLabel('Beschreibung der Vorlage', { exact: true }).fill('Mit Materialkontrolle');
  await page.getByRole('button', { name: 'Vorlage speichern', exact: true }).click(); await expect(page.locator('.template-detail')).toContainText('Mit Materialkontrolle');
  await page.getByRole('button', { name: 'Vorlage archivieren', exact: true }).click(); await expect(page.getByRole('button', { name: 'Vorlage wiederherstellen', exact: true })).toBeVisible();
  await page.getByLabel('Archivierte Vorlagen anzeigen', { exact: true }).check(); await expect(page.getByRole('button', { name: 'Vorlage Reparatur Ablauf öffnen', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Vorlage wiederherstellen', exact: true }).click(); await expect(page.getByRole('button', { name: 'Vorlage archivieren', exact: true })).toBeVisible();
  expect(api.calls.slice(1).map(call => call.headers['if-match'])).toEqual(['"1"', '"2"', '"3"']);
  expect(api.calls.every(call => call.headers['x-tenant-id'] === 'tenant-a' && call.headers['idempotency-key'] && call.headers['x-xsrf-token'] === 'csrf-test')).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('reviews actual dates, invalidates confirmation after changing the anchor, and applies all unassigned tasks once', async ({ page }) => {
  const api = await fixture(page); await prepare(page); await expect(page.locator('.template-preview')).toContainText('25.10.2026');
  await expect(page.getByRole('button', { name: '2 Aufgaben gemeinsam anlegen', exact: true })).toBeDisabled(); await page.getByLabel(confirmLabel, { exact: true }).check();
  await page.getByLabel('Eigenes Ankerdatum', { exact: true }).check(); await expect(page.locator('.template-preview')).toHaveCount(0); await page.getByLabel('Ankerdatum', { exact: true }).fill('2026-10-31');
  await page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true }).click(); await expect(page.locator('.template-preview')).toContainText('30.10.2026');
  await expect(page.getByLabel(confirmLabel, { exact: true })).not.toBeChecked(); await page.getByLabel(confirmLabel, { exact: true }).check();
  await page.getByRole('button', { name: '2 Aufgaben gemeinsam anlegen', exact: true }).click(); await expect(page.getByRole('status').filter({ hasText: '2 Aufgaben angelegt' })).toBeVisible();
  await expect(page.locator('.application-original')).toContainText('Sanierung Standard'); await expect(page.locator('.application-original')).toContainText('30.10.2026');
  await expect(page.getByText('Diese Vorlage wurde bereits in das Projekt übernommen.', { exact: false })).toBeVisible();
  expect(api.calls.map(call => call.headers['if-match'])).toEqual(['"3"', '"3"', '"3"']);
  expect(api.calls[2].body).toEqual({ templateId, templateRevision: 1, anchorDate: '2026-10-31', previewDigest: 'c'.repeat(64) });
  await expect(page.locator('.application-original').getByRole('link', { name: 'Aktuelle Aufgabe öffnen' })).toHaveCount(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('selects a real project from the searchable picker and requires an explicit date when the start is missing', async ({ page }) => {
  await fixture(page, { missingStart: true }); await open(page); await select(page); await page.getByRole('button', { name: 'Projekt suchen', exact: true }).click();
  await page.getByRole('button', { name: 'Dachprojekt Nord · P-17', exact: true }).click(); await expect(page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true })).toBeDisabled();
  await page.getByLabel('Eigenes Ankerdatum', { exact: true }).check(); await page.getByLabel('Ankerdatum', { exact: true }).fill('2026-10-31');
  await page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true }).click(); await expect(page.locator('.template-preview')).toContainText('30.10.2026');
});

test('accounting can read the original application after the project closes and its template is changed and archived', async ({ page }) => {
  const api = await fixture(page, { reader: true, closed: true, archived: true, history: true }); await open(page, true);
  await expect(page.getByRole('button', { name: 'Neue Projektvorlage', exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Nachweis Sanierung Standard öffnen', exact: true }).click(); await expect(page.locator('.application-original')).toContainText('Sanierung Standard');
  await expect(page.locator('.application-original')).toContainText('damalige Vorlagenversion 1'); await expect(page.locator('.application-original')).not.toContainText('Sanierung überarbeitet'); expect(api.calls).toHaveLength(0);
});

test('a disabled core module preserves history while removing every new-action control', async ({ page }) => {
  const api = await fixture(page, { moduleOff: true, history: true }); await open(page, true); await select(page);
  await expect(page.getByText('Das Modul Betrieb ist deaktiviert.', { exact: false })).toBeVisible(); await expect(page.getByRole('button', { name: 'Vorlage bearbeiten', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Nachweis Sanierung Standard öffnen', exact: true }).click(); await expect(page.locator('.application-original')).toBeVisible(); expect(api.calls).toHaveLength(0);
});

test('server-side template search and pagination retain explicit page boundaries', async ({ page }) => {
  await fixture(page, { pagination: true }); await open(page); await expect(page.locator('.template-list li')).toHaveCount(20);
  await page.getByRole('button', { name: 'Weitere Vorlagen', exact: true }).click(); await expect(page.locator('.template-list li')).toHaveCount(1); await expect(page.locator('.template-pager').first()).toContainText('Seite 2');
  await page.getByLabel('Vorlagen suchen', { exact: true }).fill('Weitere Vorlage 12'); await page.getByRole('button', { name: 'Suchen', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Vorlage Weitere Vorlage 12 öffnen', exact: true })).toBeVisible(); await expect(page.locator('.template-pager').first()).toContainText('Seite 1 · 1 Einträge');
});

test('revision conflicts retain the edited draft and block blind resubmission', async ({ page }) => {
  const api = await fixture(page, { conflict: 'save' }); await open(page); await select(page); await page.getByRole('button', { name: 'Vorlage bearbeiten', exact: true }).click();
  await page.getByLabel('Name der Vorlage', { exact: true }).fill('Mein ungespeicherter Ablauf'); await page.getByRole('button', { name: 'Vorlage speichern', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'zwischenzeitlich geändert' })).toBeVisible(); await expect(page.getByLabel('Name der Vorlage', { exact: true })).toHaveValue('Mein ungespeicherter Ablauf');
  await expect(page.getByRole('button', { name: 'Vorlage speichern', exact: true })).toBeDisabled(); expect(api.calls).toHaveLength(1);
});

test('a changed project invalidates the reviewed preview and never reports created tasks', async ({ page }) => {
  const api = await fixture(page, { conflict: 'apply' }); await prepare(page); await page.getByLabel(confirmLabel, { exact: true }).check(); await page.getByRole('button', { name: '2 Aufgaben gemeinsam anlegen', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'zwischenzeitlich geändert' })).toBeVisible(); await expect(page.locator('.template-preview')).toHaveCount(0); await expect(page.locator('.application-original')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true })).toBeDisabled(); expect(api.calls).toHaveLength(2);
});

test('rejects cross-business template summaries before displaying them', async ({ page }) => {
  await fixture(page, { malformed: 'list' }); await open(page); await expect(page.getByRole('alert').filter({ hasText: 'fremden' })).toBeVisible(); await expect(page.locator('.template-option')).toHaveCount(0);
});

test('rejects altered preview dates before any confirmation or apply action', async ({ page }) => {
  const api = await fixture(page, { malformed: 'preview' }); await open(page, true); await select(page); await page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'berechneten Fristen' })).toBeVisible(); await expect(page.locator('.template-preview')).toHaveCount(0); expect(api.calls).toHaveLength(1);
});

test('rejects an unexpected employee assignment in the apply result without announcing success', async ({ page }) => {
  await fixture(page, { malformed: 'apply' }); await prepare(page); await page.getByLabel(confirmLabel, { exact: true }).check(); await page.getByRole('button', { name: '2 Aufgaben gemeinsam anlegen', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'unzugewiesenen Aufgaben' })).toBeVisible(); await expect(page.locator('.application-original')).toHaveCount(0); await expect(page.getByRole('status').filter({ hasText: 'Aufgaben angelegt' })).toHaveCount(0);
});

test('rejects an original that does not match its selected history digest', async ({ page }) => {
  await fixture(page, { history: true, malformed: 'application' }); await open(page, true); await page.getByRole('button', { name: 'Nachweis Sanierung Standard öffnen', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Historieneintrag' })).toBeVisible(); await expect(page.locator('.application-original')).toHaveCount(0);
});

test('late template reads cannot reopen the former business', async ({ page }) => {
  const api = await fixture(page, { delay: 'template' }); await open(page); await page.getByRole('button', { name: 'Vorlage Sanierung Standard öffnen', exact: true }).click();
  await expect(page.getByText('Vorlage wird geladen …', { exact: true })).toBeVisible(); await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
  await expect(page.locator('.template-detail')).toHaveCount(0); await expect(page.locator('.template-option')).toHaveCount(0);
});

test('editing the anchor while a preview is pending discards the former dates', async ({ page }) => {
  const api = await fixture(page, { delay: 'preview' }); await open(page, true); await select(page); await page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true }).click();
  await expect(page.getByText('Aufgabenvorschau wird geprüft …', { exact: true })).toBeVisible(); await page.getByLabel('Eigenes Ankerdatum', { exact: true }).check(); await page.getByLabel('Ankerdatum', { exact: true }).fill('2026-11-01'); api.release();
  await expect(page.locator('.template-preview')).toHaveCount(0); expect(api.calls).toHaveLength(1);
});

test('write-right revocation discards a pending preview and removes mutation controls', async ({ page }) => {
  const api = await fixture(page, { delay: 'preview' }); await open(page, true); await select(page); await page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true }).click();
  await expect(page.getByText('Aufgabenvorschau wird geprüft …', { exact: true })).toBeVisible(); api.revoke(); await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await expect(page.getByRole('button', { name: 'Neue Projektvorlage', exact: true })).toHaveCount(0); api.release(); await expect(page.locator('.template-preview')).toHaveCount(0);
});

test('late successful application responses cannot display another business evidence or success', async ({ page }) => {
  const api = await fixture(page, { delay: 'apply' }); await prepare(page); await page.getByLabel(confirmLabel, { exact: true }).check(); await page.getByRole('button', { name: '2 Aufgaben gemeinsam anlegen', exact: true }).click();
  await expect.poll(() => api.calls.length).toBe(2); await page.getByLabel('Betrieb', { exact: true }).selectOption('tenant-b'); api.release();
  await expect(page.locator('.application-original')).toHaveCount(0); await expect(page.getByRole('status').filter({ hasText: 'Aufgaben angelegt' })).toHaveCount(0);
});

test('a failed project refresh preserves the confirmed original and prevents further writes', async ({ page }) => {
  await fixture(page, { refreshFailure: true }); await prepare(page); await page.getByLabel(confirmLabel, { exact: true }).check(); await page.getByRole('button', { name: '2 Aufgaben gemeinsam anlegen', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: '2 Aufgaben angelegt' })).toBeVisible(); await expect(page.getByRole('alert').filter({ hasText: 'Projektstand derzeit' })).toBeVisible();
  await expect(page.locator('.application-original')).toBeVisible(); await expect(page.getByRole('button', { name: 'Fristen und Aufgaben prüfen', exact: true })).toHaveCount(0);
});
