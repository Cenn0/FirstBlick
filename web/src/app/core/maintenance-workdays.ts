export const maintenanceWeekdays = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] as const;
export type MaintenanceWeekday = typeof maintenanceWeekdays[number];
export interface MaintenanceWorkdayPolicy { weekdays: MaintenanceWeekday[]; excludedDates: string[] }
export interface MaintenanceExecutionPlan { dueDate: string; scheduledDate: string; contractEndDate: string | null; workdays: MaintenanceWorkdayPolicy }
export interface MaintenancePlanningPreview {
  tenantId: string; contractId: string; contractRevision: number; dueDate: string; scheduledDate: string;
  contractEndDate: string | null; workdays: MaintenanceWorkdayPolicy | null; existingVisitId: string | null; digest: string;
}

function dayNumber(value: unknown): number {
  if (typeof value !== 'string' || !/^(?!0000)\d{4}-\d{2}-\d{2}$/.test(value)) throw new Error('Das Ausführungsdatum ist ungültig.');
  const time = Date.parse(value + 'T00:00:00Z');
  if (!Number.isFinite(time) || new Date(time).toISOString().slice(0, 10) !== value) throw new Error('Das Ausführungsdatum ist ungültig.');
  return Math.floor(time / 86400000);
}

export function validatedWorkdayPolicy(value: unknown): MaintenanceWorkdayPolicy | null {
  if (value === null || value === undefined) return null;
  const p = value as MaintenanceWorkdayPolicy;
  if (!p || !Array.isArray(p.weekdays) || p.weekdays.length < 1 || p.weekdays.length > 7
    || p.weekdays.some(x => !maintenanceWeekdays.includes(x)) || new Set(p.weekdays).size !== p.weekdays.length
    || !Array.isArray(p.excludedDates) || p.excludedDates.length > 366 || new Set(p.excludedDates).size !== p.excludedDates.length)
    throw new Error('Wählen Sie eindeutige Arbeitstage und höchstens 366 unterschiedliche Sperrtage.');
  p.excludedDates.forEach(dayNumber);
  return { weekdays: [...p.weekdays].sort((a, b) => maintenanceWeekdays.indexOf(a) - maintenanceWeekdays.indexOf(b)), excludedDates: [...p.excludedDates].sort() };
}

export function workdayPolicyFromForm(enabled: boolean, weekdays: MaintenanceWeekday[], excludedText: string): MaintenanceWorkdayPolicy | null {
  if (!enabled) return null;
  if (excludedText.length > 5000) throw new Error('Hinterlegen Sie höchstens 366 Sperrtage.');
  return validatedWorkdayPolicy({ weekdays, excludedDates: excludedText.split(/[\s,;]+/).map(x => x.trim()).filter(Boolean) });
}

export function expectedExecutionDate(dueDate: string, endDate: string | null, policy: MaintenanceWorkdayPolicy | null): string {
  const due = dayNumber(dueDate), end = endDate === null ? dayNumber('9999-12-31') : dayNumber(endDate);
  if (end < due) throw new Error('Innerhalb des Vertragszeitraums liegt kein erlaubter Ausführungstag.');
  const valid = validatedWorkdayPolicy(policy);
  if (!valid) return dueDate;
  const excluded = new Set(valid.excludedDates);
  for (let day = due; day <= Math.min(end, due + 366); day++) {
    const date = new Date(day * 86400000), iso = date.toISOString().slice(0, 10);
    if (valid.weekdays.includes(maintenanceWeekdays[date.getUTCDay()]) && !excluded.has(iso)) return iso;
  }
  throw new Error('Innerhalb von 366 Tagen und bis zum Vertragsende liegt kein erlaubter Ausführungstag.');
}

export function validatedExecutionPlan(value: unknown, dueDate: string): MaintenanceExecutionPlan | null {
  if (value === null || value === undefined) return null;
  const p = value as MaintenanceExecutionPlan, workdays = validatedWorkdayPolicy(p.workdays);
  if (!workdays || p.dueDate !== dueDate || (p.contractEndDate !== null && typeof p.contractEndDate !== 'string')
    || p.scheduledDate !== expectedExecutionDate(dueDate, p.contractEndDate, workdays))
    throw new Error('Der gespeicherte Ausführungstag passt nicht zur Wartungsfälligkeit und ihren Arbeitstagen.');
  return { ...p, workdays };
}

export function validatedMaintenancePreview(value: unknown, expected: { tenantId: string; contractId: string; contractRevision: number; dueDate: string }): MaintenancePlanningPreview {
  const p = value as MaintenancePlanningPreview;
  if (!p || p.tenantId !== expected.tenantId || p.contractId !== expected.contractId || p.contractRevision !== expected.contractRevision
    || p.dueDate !== expected.dueDate || !/^[a-f0-9]{64}$/.test(p.digest)
    || (p.contractEndDate !== null && typeof p.contractEndDate !== 'string')
    || (p.existingVisitId !== null && (typeof p.existingVisitId !== 'string' || !p.existingVisitId)))
    throw new Error('Die Ausführungsvorschau passt nicht zum geöffneten Vertragsstand.');
  const workdays = validatedWorkdayPolicy(p.workdays);
  if (p.scheduledDate !== expectedExecutionDate(p.dueDate, p.contractEndDate, workdays))
    throw new Error('Die Ausführungsvorschau passt nicht zu den hinterlegten Arbeitstagen.');
  return { ...p, workdays };
}
