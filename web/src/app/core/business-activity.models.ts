export interface ActivityFilter { from: string; to: string; projectId: string | null; includeArchived: boolean }
export interface ActivitySource { kind: 'projects' | 'tasks'; id: string; revision: number; projectId: string; title: string; currentStatus: string; archived: boolean; createdOn: string; dueDate: string | null }
export interface ActivityCounts { projectsCreated: number; tasksCreated: number; tasksDue: number }
export interface ActivityMonth { from: string; to: string; counts: ActivityCounts }
export interface ActivityPeriod { from: string; to: string; totals: ActivityCounts; months: ActivityMonth[] }
export interface ActivityReport { schema: string; tenantId: string; timeZone: string; capturedAt: string; filter: ActivityFilter; current: ActivityPeriod; previous: ActivityPeriod; sources: ActivitySource[]; digest: string }
const uuid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const hash = /^[a-f0-9]{64}$/;
const zero = '00000000-0000-0000-0000-000000000000';
function fail(): never { throw new Error('Die Auswertung passt nicht eindeutig zu Betrieb, Zeitraum und Quellen. Bitte neu laden.'); }
function object(value: unknown): Record<string, any> { if (!value || typeof value !== 'object' || Array.isArray(value)) fail(); return value as Record<string, any>; }
function id(value: unknown): value is string { return typeof value === 'string' && uuid.test(value) && value !== zero; }
export function calendarDay(value: unknown): number {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) fail();
  const parsed = Date.parse(value + 'T00:00:00Z');
  if (!Number.isFinite(parsed) || new Date(parsed).toISOString().slice(0, 10) !== value) fail();
  return parsed / 86400000;
}
export function activityFilter(from: string, to: string, projectId = '', includeArchived = false): ActivityFilter {
  const first = calendarDay(from), last = calendarDay(to), days = last - first + 1;
  if (days < 1 || days > 366 || from.startsWith('0000') || to.startsWith('9999') || new Date((first - days) * 86400000).getUTCFullYear() < 1)
    throw new Error('Bitte einen Zeitraum von 1 bis 366 Tagen mit gültigem Vorzeitraum wählen.');
  if (projectId && !id(projectId)) throw new Error('Bitte ein Projekt aus der Suche auswählen.');
  return { from, to, projectId: projectId || null, includeArchived };
}
export function activityQuery(filter: ActivityFilter): string {
  const query = new URLSearchParams({ from: filter.from, to: filter.to, includeArchived: String(filter.includeArchived) });
  if (filter.projectId) query.set('projectId', filter.projectId);
  return query.toString();
}
export function sameActivityFilter(left: ActivityFilter, right: ActivityFilter): boolean {
  return left.from === right.from && left.to === right.to && left.projectId === right.projectId && left.includeArchived === right.includeArchived;
}
function count(sources: ActivitySource[], from: string, to: string): ActivityCounts {
  const inside = (date: string | null) => date !== null && date >= from && date <= to;
  return { projectsCreated: sources.filter(x => x.kind === 'projects' && inside(x.createdOn)).length,
    tasksCreated: sources.filter(x => x.kind === 'tasks' && inside(x.createdOn)).length,
    tasksDue: sources.filter(x => x.kind === 'tasks' && inside(x.dueDate)).length };
}
function checkCounts(value: unknown, expected: ActivityCounts): void {
  const data = object(value);
  for (const key of ['projectsCreated', 'tasksCreated', 'tasksDue'] as const)
    if (!Number.isSafeInteger(data[key]) || data[key] < 0 || data[key] !== expected[key]) fail();
}
function period(value: unknown, from: string, to: string, sources: ActivitySource[]): void {
  const data = object(value);
  if (data['from'] !== from || data['to'] !== to || !Array.isArray(data['months']) || data['months'].length < 1 || data['months'].length > 13) fail();
  checkCounts(data['totals'], count(sources, from, to));
  let cursor = calendarDay(from);
  for (const value of data['months']) {
    const month = object(value), start = calendarDay(month['from']), end = calendarDay(month['to']);
    const date = new Date(start * 86400000), nextMonth = new Date(start * 86400000);
    nextMonth.setUTCMonth(date.getUTCMonth() + 1, 1);
    const expectedEnd = Math.min(calendarDay(to), nextMonth.getTime() / 86400000 - 1);
    if (start !== cursor || end !== expectedEnd) fail();
    checkCounts(month['counts'], count(sources, month['from'], month['to']));
    cursor = end + 1;
  }
  if (cursor !== calendarDay(to) + 1) fail();
}
export function validateActivityReport(value: unknown, tenantId: string, filter: ActivityFilter): ActivityReport {
  const data = object(value), returnedFilter = object(data['filter']) as unknown as ActivityFilter;
  if (data['schema'] !== 'firstblick.business-activity/1.0.0' || data['tenantId'] !== tenantId || !id(tenantId)
    || !sameActivityFilter(returnedFilter, filter) || typeof data['digest'] !== 'string' || !hash.test(data['digest'])
    || typeof data['timeZone'] !== 'string' || typeof data['capturedAt'] !== 'string' || !Number.isFinite(Date.parse(data['capturedAt']))
    || !Array.isArray(data['sources']) || data['sources'].length > 5000) fail();
  try { new Intl.DateTimeFormat('de-DE', { timeZone: data['timeZone'] }).format(); } catch { fail(); }
  const previousFrom = new Date((calendarDay(filter.from) - (calendarDay(filter.to) - calendarDay(filter.from) + 1)) * 86400000).toISOString().slice(0, 10);
  const previousTo = new Date((calendarDay(filter.from) - 1) * 86400000).toISOString().slice(0, 10);
  const seen = new Set<string>();
  for (const value of data['sources']) {
    const row = object(value) as unknown as ActivitySource;
    if (!['projects', 'tasks'].includes(row.kind) || !id(row.id) || !id(row.projectId) || !Number.isSafeInteger(row.revision) || row.revision < 1
      || typeof row.title !== 'string' || !row.title.trim() || row.title.length > 240 || typeof row.archived !== 'boolean'
      || row.archived && !filter.includeArchived || filter.projectId !== null && row.projectId !== filter.projectId
      || seen.has(row.kind + row.id)) fail();
    seen.add(row.kind + row.id); calendarDay(row.createdOn); if (row.dueDate !== null) calendarDay(row.dueDate);
    const statuses = row.kind === 'projects' ? ['Inquiry','Planning','Active','Completed','Cancelled'] : ['Open','InProgress','Done','Cancelled'];
    if (!statuses.includes(row.currentStatus) || row.kind === 'projects' && (row.projectId !== row.id || row.dueDate !== null)
      || !((row.createdOn >= previousFrom && row.createdOn <= filter.to) || (row.kind === 'tasks' && row.dueDate !== null && row.dueDate >= previousFrom && row.dueDate <= filter.to))) fail();
  }
  period(data['current'], filter.from, filter.to, data['sources']); period(data['previous'], previousFrom, previousTo, data['sources']);
  return data as unknown as ActivityReport;
}
export function activitySourceCsv(report: ActivityReport): string {
  const cell = (value: string) => {
    // Match .NET Char.IsWhiteSpace explicitly so identical sources have identical CSV bytes.
    const trimmed = value.replace(/^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+/, '');
    if (/^[=+\-@＝＋－＠]/.test(trimmed) || /^[\t\r\n]/.test(value)) value = "'" + value;
    return '"' + value.replaceAll('"', '""') + '"';
  };
  const header = 'Bereich;Kennung;Revision;Projekt;Bezeichnung;Status aktuell;Archiviert;Angelegt am;Faellig am aktuell;Zeitraum von;Zeitraum bis;Vorzeitraum von;Vorzeitraum bis;Zeitzone;Betrieb;Berichtspruefsumme\r\n';
  return header + report.sources.map(row => [row.kind, row.id, String(row.revision), row.projectId, row.title, row.currentStatus,
    String(row.archived), row.createdOn, row.dueDate || '', report.current.from, report.current.to, report.previous.from, report.previous.to,
    report.timeZone, report.tenantId, report.digest].map(cell).join(';') + '\r\n').join('');
}
export async function verifyActivityDownload(response: Response, report: ActivityReport, format: 'csv' | 'json'): Promise<Blob> {
  const type = response.headers.get('Content-Type')?.split(';')[0];
  const expectedType = format === 'csv' ? 'text/csv' : 'application/json';
  const expectedHash = response.headers.get('X-Content-Sha256') || '';
  if (type !== expectedType || !hash.test(expectedHash) || response.headers.get('X-Report-Digest') !== report.digest || response.headers.get('X-Report-Tenant') !== report.tenantId) fail();
  const bytes = await response.arrayBuffer();
  if (!bytes.byteLength || bytes.byteLength > 8 * 1024 * 1024) fail();
  const actualHash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(x => x.toString(16).padStart(2, '0')).join('');
  if (actualHash !== expectedHash) throw new Error('Die Prüfsumme der Exportdatei stimmt nicht. Bitte erneut laden.');
  const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  if (format === 'json') {
    const parsed = validateActivityReport(JSON.parse(text), report.tenantId, report.filter);
    if (parsed.digest !== report.digest || parsed.timeZone !== report.timeZone || JSON.stringify(parsed.sources) !== JSON.stringify(report.sources)) fail();
  } else if (text !== activitySourceCsv(report)) fail();
  return new Blob([bytes], { type: expectedType + ';charset=utf-8' });
}
