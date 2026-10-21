import { businessLocalTime } from './business-time';

export interface PlanningDay { date: string; startUtc: string; endUtc: string }
export interface PlanningResource { id: string; name: string }
export interface PlanningAssignment {
  id: string; revision: number; employeeId: string; employeeName: string;
  projectId: string; projectName: string; title: string; start: string; end: string;
  equipmentIds: string[]; equipment: PlanningResource[]; days: string[];
}
export interface PlanningFilters { employeeId: string | null; projectId: string | null; equipmentId: string | null }
export interface PlanningResponse {
  weekStart: string; timeZone: string; days: PlanningDay[]; assignments: PlanningAssignment[];
  count: number; maxAssignments: number; filters: PlanningFilters; note: string;
}
const uuid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const instant = (value: unknown): value is string => typeof value === 'string'
  && /T.*(?:Z|\+00:00)$/.test(value) && Number.isFinite(Date.parse(value));

export function mondayOf(date: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) throw new Error('Ein gültiges Kalenderdatum auswählen.');
  const day = new Date(`${date}T12:00:00.000Z`);
  if (!Number.isFinite(day.getTime()) || day.toISOString().slice(0, 10) !== date)
    throw new Error('Ein gültiges Kalenderdatum auswählen.');
  day.setUTCDate(day.getUTCDate() - (day.getUTCDay() + 6) % 7);
  if (day.getUTCFullYear() < 2000 || day.getUTCFullYear() > 2099)
    throw new Error('Der Wochenbeginn muss zwischen 2000 und 2099 liegen.');
  return day.toISOString().slice(0, 10);
}

export function shiftBusinessWeek(monday: string, weeks: number): string {
  const date = new Date(`${mondayOf(monday)}T12:00:00.000Z`);
  date.setUTCDate(date.getUTCDate() + weeks * 7);
  return mondayOf(date.toISOString().slice(0, 10));
}

/** Reject stale, mis-scoped, malformed or incomplete projections before offering edits. */
export function validPlanningResponse(raw: PlanningResponse, monday: string, filters: PlanningFilters): PlanningResponse {
  if (!raw || raw.weekStart !== monday || !Array.isArray(raw.days) || raw.days.length !== 7
      || !Array.isArray(raw.assignments) || !Number.isInteger(raw.count) || raw.count < 0
      || raw.count !== raw.assignments.length || raw.count > 1000 || raw.maxAssignments !== 1000
      || typeof raw.timeZone !== 'string' || !raw.filters
      || Object.keys(filters).some(key => raw.filters[key as keyof PlanningFilters] !== filters[key as keyof PlanningFilters]))
    throw new Error('Der serverseitige Wochenplan ist unvollständig oder gehört zu einem anderen Filter.');
  for (let i = 0; i < 7; i++) {
    const day = raw.days[i], expected = new Date(`${monday}T00:00:00Z`);
    expected.setUTCDate(expected.getUTCDate() + i);
    const next = new Date(expected); next.setUTCDate(next.getUTCDate() + 1);
    if (!day || day.date !== expected.toISOString().slice(0, 10) || !instant(day.startUtc) || !instant(day.endUtc)
        || Date.parse(day.endUtc) <= Date.parse(day.startUtc)
        || businessLocalTime(day.startUtc, raw.timeZone) !== `${day.date}T00:00`
        || businessLocalTime(day.endUtc, raw.timeZone) !== `${next.toISOString().slice(0, 10)}T00:00`
        || (i > 0 && Date.parse(raw.days[i - 1].endUtc) !== Date.parse(day.startUtc)))
      throw new Error('Die Geschäftstage stimmen nicht mit der Betriebszeitzone überein.');
  }
  const ids = new Set<string>();
  for (const assignment of raw.assignments) {
    if (!assignment || ids.has(assignment.id) || !uuid.test(assignment.id) || !uuid.test(assignment.employeeId)
        || !uuid.test(assignment.projectId) || !Number.isSafeInteger(assignment.revision) || assignment.revision < 1
        || typeof assignment.title !== 'string' || typeof assignment.projectName !== 'string'
        || typeof assignment.employeeName !== 'string' || !Array.isArray(assignment.days)
        || !Array.isArray(assignment.equipmentIds) || assignment.equipmentIds.length > 100
        || assignment.equipmentIds.some(id => !uuid.test(id))
        || new Set(assignment.equipmentIds).size !== assignment.equipmentIds.length
        || !Array.isArray(assignment.equipment) || assignment.equipment.length !== assignment.equipmentIds.length
        || assignment.equipment.some((item, index) => !item || item.id !== assignment.equipmentIds[index] || typeof item.name !== 'string')
        || !instant(assignment.start) || !instant(assignment.end) || Date.parse(assignment.end) <= Date.parse(assignment.start)
        || Date.parse(assignment.end) - Date.parse(assignment.start) > 31 * 86_400_000
        || (filters.employeeId && filters.employeeId !== assignment.employeeId)
        || (filters.projectId && filters.projectId !== assignment.projectId)
        || (filters.equipmentId && !assignment.equipmentIds.includes(filters.equipmentId)))
      throw new Error('Ein Einsatz enthält widersprüchliche Angaben.');
    ids.add(assignment.id);
    const covered = raw.days.filter(day => Date.parse(assignment.start) < Date.parse(day.endUtc)
      && Date.parse(assignment.end) > Date.parse(day.startUtc)).map(day => day.date);
    if (JSON.stringify(covered) !== JSON.stringify(assignment.days) || !covered.length)
      throw new Error('Ein Einsatz ist falsch den Geschäftstagen zugeordnet.');
  }
  return raw;
}
