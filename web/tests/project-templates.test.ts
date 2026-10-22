import { describe, expect, it } from 'vitest';
import { AppliedTemplate, ProjectTemplate, TemplateApplication, TemplateApplicationSummary, TemplatePreview, TemplateProject, detailsOf, openTemplateProject,
  templateDueDate, validProjectTemplate, validTemplateApplied, validTemplateApplication, validTemplateChange, validTemplateDetails, validTemplateHistory,
  validTemplatePage, validTemplatePreview, validTemplateProject } from '../src/app/core/project-template.models';

const tenant = '81000000-0000-4000-8000-000000000001', templateId = '81000000-0000-4000-8000-000000000002', projectId = '81000000-0000-4000-8000-000000000003';
const actor = '81000000-0000-4000-8000-000000000004', customer = '81000000-0000-4000-8000-000000000005', building = '81000000-0000-4000-8000-000000000006';
const applicationId = '81000000-0000-4000-8000-000000000007', taskId = '81000000-0000-4000-8000-000000000008', otherTaskId = '81000000-0000-4000-8000-000000000009';
const before = '2026-10-20T08:00:00Z', now = '2026-10-22T08:00:00Z';
const common = (id: string) => ({ id, tenantId: tenant, revision: 1, archived: false, createdAt: before, updatedAt: before });
const template = (): ProjectTemplate => ({ ...common(templateId), name: 'Sanierung vorbereiten', description: 'Dacharbeiten', projectType: 'Renovation', tasks: [
  { title: 'Material prüfen', description: 'Lieferschein abgleichen', offsetDays: -1 }, { title: 'Baustelle einrichten', description: '', offsetDays: 0 }] });
const project = (): TemplateProject => ({ ...common(projectId), revision: 3, name: 'Dach Nord', number: 'P-17', description: '', customerId: customer, buildingId: building,
  type: 'Renovation', status: 'Planning', plannedStart: '2026-10-26', plannedEnd: '2026-11-10' });
const preview = (): TemplatePreview => ({ tenantId: tenant, template: template(), templateDigest: 'a'.repeat(64), project: project(), projectDigest: 'b'.repeat(64),
  anchorDate: '2026-10-26', anchorKind: 'ProjectStart', tasks: [{ ...template().tasks[0], position: 1, dueDate: '2026-10-25' }, { ...template().tasks[1], position: 2, dueDate: '2026-10-26' }], digest: 'c'.repeat(64) });
const application = (): TemplateApplication => ({ ...common(applicationId), createdAt: now, updatedAt: now, templateId, projectId, preview: preview(), appliedBy: actor,
  appliedAt: now, tasks: [{ taskId, position: 1 }, { taskId: otherTaskId, position: 2 }], digest: 'd'.repeat(64) });
const summary = (): TemplateApplicationSummary => ({ id: applicationId, tenantId: tenant, templateId, projectId, templateName: template().name, templateRevision: 1, projectName: project().name,
  anchorDate: preview().anchorDate, anchorKind: 'ProjectStart', appliedBy: actor, appliedAt: now, taskCount: 2, digest: 'd'.repeat(64) });
const applied = (): AppliedTemplate => ({ application: application(), projectRevision: 4, tasks: preview().tasks.map((task, index) => ({ ...common(index === 0 ? taskId : otherTaskId), createdAt: now, updatedAt: now,
  projectId, assignedEmployeeId: null, status: 'Open', title: task.title, description: task.description, dueDate: task.dueDate,
  templateOrigin: { applicationId, templateId, templateRevision: 1, position: index + 1, applicationDigest: 'd'.repeat(64) } })) });
const page = () => ({ items: [{ id: templateId, tenantId: tenant, revision: 1, archived: false, name: template().name, description: template().description, projectType: 'Renovation', taskCount: 2 }], total: 1, page: 1, pageSize: 20 });

describe('project template boundary contracts', () => {
  it('reads full templates, summaries and historical archived records without making archives applicable', () => {
    expect(validProjectTemplate(template(), tenant, templateId).tasks).toHaveLength(2);
    expect(validTemplatePage(page(), tenant, 1, false).items[0].taskCount).toBe(2);
    expect(validProjectTemplate({ ...template(), archived: true }, tenant).archived).toBe(true);
    const closed = validTemplateProject({ ...project(), status: 'Completed', archived: true }, tenant, projectId);
    expect(openTemplateProject(closed)).toBe(false);
    expect(validTemplateProject({ ...project(), number: null, description: null }, tenant, projectId).number).toBeNull();
  });
  it.each([{ name: '' }, { projectType: 'Roof' }, { tasks: [] }, { tasks: Array(51).fill(template().tasks[0]) }, { description: null }])('rejects invalid template details %o', change => {
    expect(() => validTemplateDetails({ ...detailsOf(template()), ...change })).toThrow();
  });
  it.each([1.5, -3651, 3651, '1', null, NaN])('requires bounded whole calendar offsets: %s', offsetDays => {
    expect(() => validTemplateDetails({ ...detailsOf(template()), tasks: [{ ...template().tasks[0], offsetDays }] })).toThrow();
  });
  it('rejects wrong tenant, identity, source revision, missing archive state and impossible source dates', () => {
    for (const change of [{ tenantId: actor }, { id: projectId }, { revision: 0 }, { archived: undefined }, { updatedAt: '2026-10-19T08:00:00Z' }])
      expect(() => validProjectTemplate({ ...template(), ...change }, tenant, templateId)).toThrow('nicht eindeutig');
    expect(() => validTemplateProject({ ...project(), plannedStart: '2026-02-30' }, tenant, projectId)).toThrow();
    expect(() => validTemplateProject({ ...project(), plannedEnd: '2026-01-01' }, tenant, projectId)).toThrow();
  });
  it('rejects duplicate, wrong-page, cross-business and silently archived summaries', () => {
    for (const change of [{ page: 2 }, { pageSize: 25 }, { total: 2 }]) expect(() => validTemplatePage({ ...page(), ...change }, tenant, 1, false)).toThrow();
    expect(() => validTemplatePage({ ...page(), total: 2, items: [...page().items, ...page().items] }, tenant, 1, false)).toThrow('doppelte');
    expect(() => validTemplatePage({ ...page(), items: [{ ...page().items[0], tenantId: actor }] }, tenant, 1, false)).toThrow('fremden');
    expect(() => validTemplatePage({ ...page(), items: [{ ...page().items[0], archived: true }] }, tenant, 1, false)).toThrow('archivierten');
  });
  it('binds successful template mutations to exact requested fields and the next revision', () => {
    const request = { ...detailsOf(template()), name: 'Überarbeitete Sanierung' }, changed = { ...template(), ...request, revision: 2, updatedAt: now };
    expect(validTemplateChange(changed, tenant, request, template()).revision).toBe(2);
    expect(() => validTemplateChange({ ...changed, revision: 3 }, tenant, request, template())).toThrow('Vorlagenstand');
    expect(() => validTemplateChange({ ...changed, name: 'Anderes Ergebnis' }, tenant, request, template())).toThrow('Eingabe');
    expect(() => validTemplateChange({ ...changed, archived: true }, tenant, request, template())).toThrow('Eingabe');
    expect(validTemplateChange({ ...template(), revision: 2, archived: true, updatedAt: now }, tenant, { archived: true }, template()).archived).toBe(true);
    expect(() => validTemplateChange({ ...template(), revision: 2, archived: true, name: 'Unerwartete Änderung' }, tenant, { archived: true }, template())).toThrow('Archivierung');
  });
});

describe('reviewed dates and retained project application', () => {
  it('uses calendar days across DST and leap days and rejects DateOnly overflow', () => {
    expect(templateDueDate('2026-10-26', -1)).toBe('2026-10-25');
    expect(templateDueDate('2028-03-01', -1)).toBe('2028-02-29');
    expect(templateDueDate('2027-12-31', 1)).toBe('2028-01-01');
    for (const [anchor, offset] of [['0001-01-01', -1], ['9999-12-31', 1], ['0000-01-01', 0], ['2026-02-30', 1]] as const)
      expect(() => templateDueDate(anchor, offset)).toThrow();
  });
  it('accepts only the exact chosen sources, anchor and ordered task dates', () => {
    expect(validTemplatePreview(preview(), template(), project(), null).tasks[0].dueDate).toBe('2026-10-25');
    for (const change of [{ template: { ...template(), revision: 2 } }, { project: { ...project(), name: 'Anderes Dach' } }, { projectDigest: 'invalid' }, { tenantId: actor }, { anchorKind: 'Explicit' }, { anchorDate: '2026-10-27' }])
      expect(() => validTemplatePreview({ ...preview(), ...change }, template(), project(), null)).toThrow();
    for (const change of [{ title: 'Andere Arbeit' }, { description: 'Geändert' }, { position: 2 }, { offsetDays: 0 }, { dueDate: '2026-10-24' }])
      expect(() => validTemplatePreview({ ...preview(), tasks: [{ ...preview().tasks[0], ...change }, preview().tasks[1]] }, template(), project(), null)).toThrow('Aufgaben');
    expect(() => validTemplatePreview(preview(), { ...template(), archived: true }, project(), null)).toThrow();
    expect(() => validTemplatePreview(preview(), template(), { ...project(), status: 'Completed' }, null)).toThrow();
  });
  it('retains explicit anchors independently from current project start', () => {
    const explicit: TemplatePreview = { ...preview(), anchorKind: 'Explicit', anchorDate: '2026-10-31', tasks: [{ ...preview().tasks[0], dueDate: '2026-10-30' }, { ...preview().tasks[1], dueDate: '2026-10-31' }] };
    expect(validTemplatePreview(explicit, template(), project(), '2026-10-31').anchorKind).toBe('Explicit');
    expect(() => validTemplatePreview(explicit, template(), project(), null)).toThrow('Ankerdatum');
  });
  it('binds history and full originals without requiring today’s project or template to remain active', () => {
    const history = { projectId, tenantId: tenant, items: [summary()], total: 1, page: 1, pageSize: 20 };
    expect(validTemplateHistory(history, tenant, projectId, 1).items).toHaveLength(1);
    expect(validTemplateApplication(application(), tenant, projectId, summary()).preview.template.name).toBe(template().name);
    expect(() => validTemplateHistory({ ...history, projectId: customer }, tenant, projectId, 1)).toThrow('nicht eindeutig');
    expect(() => validTemplateHistory({ ...history, total: 2, items: [summary(), summary()] }, tenant, projectId, 1)).toThrow('doppelte');
    expect(() => validTemplateApplication(application(), tenant, projectId, { ...summary(), digest: 'f'.repeat(64) })).toThrow('Historieneintrag');
  });
  it('rejects mutable, incomplete or chronologically inconsistent application evidence', () => {
    for (const change of [{ revision: 2 }, { archived: true }, { appliedAt: '2026-10-18T08:00:00Z' }, { tasks: [application().tasks[0]] }, { appliedBy: '' }])
      expect(() => validTemplateApplication({ ...application(), ...change }, tenant, projectId)).toThrow();
    expect(() => validTemplateApplication({ ...application(), tasks: [{ taskId, position: 1 }, { taskId, position: 2 }] }, tenant, projectId)).toThrow('doppelte');
  });
  it('confirms every created unassigned task and its exact retained origin before showing success', () => {
    expect(validTemplateApplied(applied(), preview(), actor).tasks).toHaveLength(2);
    for (const change of [{ assignedEmployeeId: actor }, { status: 'Done' }, { dueDate: '2026-10-27' }, { projectId: customer }, { revision: 2 },
      { templateOrigin: { ...applied().tasks[0].templateOrigin, applicationDigest: 'e'.repeat(64) } }])
      expect(() => validTemplateApplied({ ...applied(), tasks: [{ ...applied().tasks[0], ...change }, applied().tasks[1]] }, preview(), actor)).toThrow('Aufgaben');
    expect(() => validTemplateApplied({ ...applied(), projectRevision: 5 }, preview(), actor)).toThrow('Projektstand');
    expect(() => validTemplateApplied(applied(), preview(), customer)).toThrow('Person');
  });
});
