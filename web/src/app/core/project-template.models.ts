import { recordDate, recordDigest, recordId, recordInstant, recordObject, recordPage, recordRevision, recordText, requireUnique, sameInstant, sameRecord } from './ledger-contracts';

export type ProjectType = 'Renovation' | 'Repair' | 'Maintenance';
export type ProjectStatus = 'Inquiry' | 'Planning' | 'Active' | 'Completed' | 'Cancelled';
export const projectTypes: Record<ProjectType, string> = { Renovation: 'Sanierung', Repair: 'Reparatur', Maintenance: 'Wartung' };
export const projectStatuses: Record<ProjectStatus, string> = { Inquiry: 'Anfrage', Planning: 'Planung', Active: 'In Arbeit', Completed: 'Abgeschlossen', Cancelled: 'Abgebrochen' };
interface TemplateEntity { id: string; tenantId: string; revision: number; archived: boolean; createdAt: string; updatedAt: string }
export interface TemplateTask { title: string; description: string; offsetDays: number }
export interface TemplateDetails { name: string; description: string; projectType: ProjectType; tasks: TemplateTask[] }
export interface ProjectTemplate extends TemplateEntity, TemplateDetails {}
export interface TemplateSummary { id: string; tenantId: string; revision: number; archived: boolean; name: string; description: string; projectType: ProjectType; taskCount: number }
export interface TemplatePage { items: TemplateSummary[]; total: number; page: number; pageSize: number }
export interface TemplateProject extends TemplateEntity { name: string; number: string | null; description: string | null; customerId: string; buildingId: string; type: ProjectType; status: ProjectStatus; plannedStart: string | null; plannedEnd: string | null }
export interface PlannedTemplateTask extends TemplateTask { position: number; dueDate: string }
export interface TemplatePreview { tenantId: string; template: ProjectTemplate; templateDigest: string; project: TemplateProject; projectDigest: string; anchorDate: string; anchorKind: 'ProjectStart' | 'Explicit'; tasks: PlannedTemplateTask[]; digest: string }
export interface TemplateApplication extends TemplateEntity { templateId: string; projectId: string; preview: TemplatePreview; appliedBy: string; appliedAt: string; tasks: { taskId: string; position: number }[]; digest: string }
export interface TemplateApplicationSummary { id: string; tenantId: string; templateId: string; projectId: string; templateName: string; templateRevision: number; projectName: string; anchorDate: string; anchorKind: 'ProjectStart' | 'Explicit'; appliedBy: string; appliedAt: string; taskCount: number; digest: string }
export interface TemplateHistory { projectId: string; tenantId: string; items: TemplateApplicationSummary[]; total: number; page: number; pageSize: number }
export interface CreatedTemplateTask extends TemplateEntity { projectId: string; assignedEmployeeId: null; title: string; description: string; dueDate: string; status: 'Open'; templateOrigin: { applicationId: string; templateId: string; templateRevision: number; position: number; applicationDigest: string } }
export interface AppliedTemplate { application: TemplateApplication; projectRevision: number; tasks: CreatedTemplateTask[] }

const integer = (value: unknown, min: number, max: number): value is number => Number.isSafeInteger(value) && Number(value) >= min && Number(value) <= max;
const calendarDate = (value: unknown): value is string => recordDate(value) && value >= '0001-01-01';
const projectType = (value: unknown): value is ProjectType => typeof value === 'string' && Object.hasOwn(projectTypes, value);
const projectStatus = (value: unknown): value is ProjectStatus => typeof value === 'string' && Object.hasOwn(projectStatuses, value);
function entity(value: unknown, tenant: string): value is Record<string, unknown> {
  return recordObject(value) && recordId(value['id']) && value['tenantId'] === tenant && recordRevision(value['revision']) && typeof value['archived'] === 'boolean'
    && recordInstant(value['createdAt']) && recordInstant(value['updatedAt']) && Date.parse(value['updatedAt']) >= Date.parse(value['createdAt']);
}
export function validTemplateDetails(value: unknown): TemplateDetails {
  if (!recordObject(value) || !recordText(value['name'], 240, true) || !recordText(value['description'], 8000) || !projectType(value['projectType'])
    || !Array.isArray(value['tasks']) || !integer(value['tasks'].length, 1, 50) || value['tasks'].some(task => !recordObject(task)
      || !recordText(task['title'], 240, true) || !recordText(task['description'], 4000) || !integer(task['offsetDays'], -3650, 3650)))
    throw new Error('Die Vorlage benötigt einen Namen, eine Projektart und 1 bis 50 Aufgaben mit ganzen Kalendertagen zwischen −3650 und 3650.');
  return value as unknown as TemplateDetails;
}
export function validProjectTemplate(value: unknown, tenant: string, id?: string): ProjectTemplate {
  if (!entity(value, tenant) || id !== undefined && value['id'] !== id) throw new Error('Die Vorlage gehört nicht eindeutig zum ausgewählten Eintrag und Betrieb.');
  validTemplateDetails(value);
  return value as unknown as ProjectTemplate;
}
export function validTemplatePage(value: unknown, tenant: string, page: number, includeArchived: boolean): TemplatePage {
  if (!recordPage(value, page, 20)) throw new Error('Die Vorlagenliste ist unvollständig oder bestätigt eine andere Seite.');
  for (const row of value.items) {
    if (!recordObject(row) || !recordId(row['id']) || row['tenantId'] !== tenant || !recordRevision(row['revision']) || typeof row['archived'] !== 'boolean'
      || !includeArchived && row['archived'] || !recordText(row['name'], 240, true) || !recordText(row['description'], 8000) || !projectType(row['projectType']) || !integer(row['taskCount'], 1, 50))
      throw new Error('Die Vorlagenliste enthält einen fremden, unvollständigen oder unerwartet archivierten Eintrag.');
  }
  requireUnique(value.items.map(row => (row as TemplateSummary).id), 'Die Vorlagenliste enthält doppelte Einträge.');
  return value as unknown as TemplatePage;
}
export function validTemplateChange(value: unknown, tenant: string, request: TemplateDetails | { archived: boolean }, before?: ProjectTemplate): ProjectTemplate {
  const template = validProjectTemplate(value, tenant, before?.id);
  if (template.revision !== (before ? before.revision + 1 : 1) || before && (!sameInstant(template.createdAt, before.createdAt) || Date.parse(template.updatedAt) < Date.parse(before.updatedAt)))
    throw new Error('Das Ergebnis bestätigt nicht den angeforderten Vorlagenstand. Bitte laden Sie ihn zur Prüfung neu.');
  if ('archived' in request) {
    if (!before || template.archived !== request.archived || !sameRecord(detailsOf(template), detailsOf(before))) throw new Error('Die Archivierung verändert Vorlageninhalte oder bestätigt eine andere Verfügbarkeit.');
  } else if (template.archived !== false || !sameRecord(detailsOf(template), request)) throw new Error('Die gespeicherte Vorlage bestätigt andere Angaben als die gesendete Eingabe.');
  return template;
}
export function detailsOf(template: TemplateDetails): TemplateDetails {
  return { name: template.name, description: template.description, projectType: template.projectType, tasks: template.tasks.map(task => ({ title: task.title, description: task.description, offsetDays: task.offsetDays })) };
}
export function validTemplateProject(value: unknown, tenant: string, id: string): TemplateProject {
  if (!entity(value, tenant) || value['id'] !== id || !recordText(value['name'], 240, true) || value['number'] !== null && !recordText(value['number'], 80) || value['description'] !== null && !recordText(value['description'], 20_000)
    || !recordId(value['customerId']) || !recordId(value['buildingId']) || !projectType(value['type']) || !projectStatus(value['status'])
    || value['plannedStart'] !== null && !calendarDate(value['plannedStart']) || value['plannedEnd'] !== null && !calendarDate(value['plannedEnd'])
    || typeof value['plannedStart'] === 'string' && typeof value['plannedEnd'] === 'string' && value['plannedEnd'] < value['plannedStart'])
    throw new Error('Das Projekt gehört nicht eindeutig zur Auswahl und zum Betrieb oder enthält ungültige Termine.');
  return value as unknown as TemplateProject;
}
export const openTemplateProject = (project: TemplateProject): boolean => !project.archived && project.status !== 'Completed' && project.status !== 'Cancelled';
export function templateDueDate(anchor: string, offsetDays: number): string {
  if (!calendarDate(anchor) || !integer(offsetDays, -3650, 3650)) throw new Error('Ankerdatum oder Kalendertage sind ungültig.');
  const date = new Date(Date.parse(anchor + 'T00:00:00Z') + offsetDays * 86_400_000).toISOString().slice(0, 10);
  if (!calendarDate(date)) throw new Error('Eine Aufgabenfrist liegt außerhalb des unterstützten Kalenderbereichs.');
  return date;
}
export function validTemplatePreview(value: unknown, template: ProjectTemplate, project: TemplateProject, anchor: string | null): TemplatePreview {
  if (!recordObject(value) || value['tenantId'] !== project.tenantId || template.tenantId !== project.tenantId || template.archived || !openTemplateProject(project)
    || template.projectType !== project.type || !recordDigest(value['templateDigest']) || !recordDigest(value['projectDigest']) || !recordDigest(value['digest']))
    throw new Error('Die Vorschau ist nicht eindeutig an die aktive Vorlage und das offene Projekt gebunden.');
  const sourceTemplate = validProjectTemplate(value['template'], project.tenantId, template.id), sourceProject = validTemplateProject(value['project'], project.tenantId, project.id);
  if (!sameRecord(sourceTemplate, template) || !sameRecord(sourceProject, project)) throw new Error('Die Vorschau enthält einen anderen Vorlagen- oder Projektstand. Bitte neu laden.');
  if (value['anchorKind'] !== (anchor === null ? 'ProjectStart' : 'Explicit') || value['anchorDate'] !== (anchor ?? project.plannedStart) || !calendarDate(value['anchorDate'])
    || !Array.isArray(value['tasks']) || value['tasks'].length !== template.tasks.length) throw new Error('Die Vorschau bestätigt ein anderes Ankerdatum oder eine unvollständige Aufgabenliste.');
  for (let index = 0; index < template.tasks.length; index++) {
    const actual = value['tasks'][index], expected = template.tasks[index];
    if (!recordObject(actual) || actual['position'] !== index + 1 || actual['title'] !== expected.title || actual['description'] !== expected.description || actual['offsetDays'] !== expected.offsetDays
      || actual['dueDate'] !== templateDueDate(value['anchorDate'], expected.offsetDays)) throw new Error('Die Vorschau verändert Aufgaben, ihre Reihenfolge oder ihre berechneten Fristen.');
  }
  return value as unknown as TemplatePreview;
}
export function validTemplateHistory(value: unknown, tenant: string, projectId: string, page: number): TemplateHistory {
  if (!recordPage(value, page, 20) || value['tenantId'] !== tenant || value['projectId'] !== projectId) throw new Error('Die Anwendungshistorie gehört nicht eindeutig zum ausgewählten Projekt und Betrieb.');
  for (const row of value.items) {
    if (!recordObject(row) || !recordId(row['id']) || row['tenantId'] !== tenant || row['projectId'] !== projectId || !recordId(row['templateId'])
      || !recordText(row['templateName'], 240, true) || !recordRevision(row['templateRevision']) || !recordText(row['projectName'], 240, true)
      || !calendarDate(row['anchorDate']) || !['ProjectStart', 'Explicit'].includes(String(row['anchorKind'])) || !recordId(row['appliedBy']) || !recordInstant(row['appliedAt'])
      || !integer(row['taskCount'], 1, 50) || !recordDigest(row['digest'])) throw new Error('Die Anwendungshistorie enthält einen unvollständigen oder fremden Nachweis.');
  }
  requireUnique(value.items.map(row => (row as TemplateApplicationSummary).id), 'Die Anwendungshistorie enthält doppelte Nachweise.');
  return value as unknown as TemplateHistory;
}
export function validTemplateApplication(value: unknown, tenant: string, projectId: string, summary?: TemplateApplicationSummary): TemplateApplication {
  if (!entity(value, tenant) || value['revision'] !== 1 || value['archived'] || value['projectId'] !== projectId || !recordId(value['templateId'])
    || !recordId(value['appliedBy']) || !recordInstant(value['appliedAt']) || !sameInstant(String(value['createdAt']), value['appliedAt']) || !recordDigest(value['digest'])
    || !recordObject(value['preview'])) throw new Error('Der Anwendungsnachweis ist unvollständig oder bestätigt ein anderes Projekt.');
  const source = value['preview'];
  const template = validProjectTemplate(source['template'], tenant, String(value['templateId'])), project = validTemplateProject(source['project'], tenant, projectId);
  const preview = validTemplatePreview(source, template, project, source['anchorKind'] === 'Explicit' ? String(source['anchorDate']) : null);
  if (Date.parse(value['appliedAt']) < Math.max(Date.parse(template.updatedAt), Date.parse(project.updatedAt)) || !Array.isArray(value['tasks']) || value['tasks'].length !== preview.tasks.length
    || value['tasks'].some((task, index) => !recordObject(task) || !recordId(task['taskId']) || task['position'] !== index + 1))
    throw new Error('Der Anwendungsnachweis bestätigt keine vollständige geordnete Aufgabenübernahme.');
  requireUnique(value['tasks'].map(task => task.taskId), 'Der Anwendungsnachweis enthält doppelte Aufgaben.');
  if (summary && (value['id'] !== summary.id || value['templateId'] !== summary.templateId || value['digest'] !== summary.digest || value['appliedBy'] !== summary.appliedBy
    || !sameInstant(value['appliedAt'], summary.appliedAt) || preview.template.revision !== summary.templateRevision || preview.template.name !== summary.templateName
    || preview.project.name !== summary.projectName || preview.anchorDate !== summary.anchorDate || preview.anchorKind !== summary.anchorKind || preview.tasks.length !== summary.taskCount))
    throw new Error('Der Originalnachweis bestätigt nicht den ausgewählten Historieneintrag.');
  return value as unknown as TemplateApplication;
}
export function validTemplateApplied(value: unknown, preview: TemplatePreview, actor: string): AppliedTemplate {
  if (!recordObject(value) || value['projectRevision'] !== preview.project.revision + 1 || !Array.isArray(value['tasks'])) throw new Error('Die Übernahme bestätigt nicht den nächsten Projektstand.');
  const application = validTemplateApplication(value['application'], preview.tenantId, preview.project.id);
  if (!sameRecord(application.preview, preview) || application.appliedBy !== actor || value['tasks'].length !== preview.tasks.length) throw new Error('Die Übernahme bestätigt eine andere Vorschau oder Person.');
  for (let index = 0; index < preview.tasks.length; index++) {
    const actual = value['tasks'][index], expected = preview.tasks[index];
    if (!entity(actual, preview.tenantId) || actual['id'] !== application.tasks[index].taskId || actual['revision'] !== 1 || actual['archived'] || actual['projectId'] !== preview.project.id
      || actual['assignedEmployeeId'] !== null || actual['status'] !== 'Open' || actual['title'] !== expected.title || actual['description'] !== expected.description || actual['dueDate'] !== expected.dueDate
      || !sameInstant(String(actual['createdAt']), application.appliedAt) || !sameRecord(actual['templateOrigin'], { applicationId: application.id, templateId: preview.template.id,
        templateRevision: preview.template.revision, position: index + 1, applicationDigest: application.digest }))
      throw new Error('Die Übernahme bestätigt nicht alle geprüften unzugewiesenen Aufgaben und ihre Herkunft.');
  }
  return value as unknown as AppliedTemplate;
}
