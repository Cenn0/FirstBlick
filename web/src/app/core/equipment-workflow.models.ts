export type EquipmentKind = 'Tool' | 'Machine' | 'Vehicle';
export type InspectionKind = 'Inspection' | 'Maintenance' | 'Repair';
export type InspectionStatus = 'Scheduled' | 'Completed' | 'Cancelled';
export type InspectionResult = 'Passed' | 'Failed';
export type EquipmentDueStatus = 'Unscheduled' | 'Current' | 'Due' | 'Overdue';
export type EquipmentWorklistFilter = 'all' | 'due' | 'blocked' | 'planned';

export interface EquipmentRecord {
  id: string; revision: number; name: string; inventoryNumber: string; kind: EquipmentKind;
  registration: string | null; location: string | null; nextInspectionOn: string | null; available: boolean;
  inspectionBlocked: boolean; pendingInspectionId: string | null; lastInspectionId: string | null;
  lastInspectionOn?: string | null;
}
export interface EquipmentSummary extends EquipmentRecord { dueStatus: EquipmentDueStatus }
export interface EquipmentWorklist {
  businessDate: string; timeZone: string; items: EquipmentSummary[]; total: number; page: number; pageSize: number;
}
export interface EquipmentInspection {
  id: string; revision: number; equipmentId: string; kind: InspectionKind; title: string; scheduledOn: string;
  notes: string; status: InspectionStatus; scopeDigest: string; plannedBy: string;
  equipmentSnapshot: { id: string; name: string; inventoryNumber: string; kind: EquipmentKind; registration: string | null };
  completion: { performedOn: string; result: InspectionResult; inspector: string; evidenceReference: string;
    notes: string; nextInspectionOn: string | null; makeAvailable: boolean; recordedBy: string; recordedAt: string; digest: string } | null;
  cancellation: { reason: string; cancelledBy: string; cancelledAt: string; digest: string } | null;
}
export interface EquipmentHistory {
  equipment: EquipmentRecord; pendingInspection: EquipmentInspection | null; items: EquipmentInspection[];
  total: number; page: number; pageSize: number; businessDate: string; timeZone: string; affectedAssignmentCount: number;
}
export interface EquipmentMutation { equipment: EquipmentRecord; inspection: EquipmentInspection; affectedAssignmentCount: number }

const uuid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
export const validEquipmentId = (value: unknown): value is string => typeof value === 'string' && uuid.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const revision = (value: unknown): value is number => Number.isSafeInteger(value) && Number(value) > 0;
const optionalId = (value: unknown) => value === null || validEquipmentId(value);
const text = (value: unknown, max: number, required = false): value is string => typeof value === 'string' && value.length <= max && (!required || !!value.trim());
const optionalText = (value: unknown, max: number): value is string | null => value === null || text(value, max);
const digest = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const instant = (value: unknown): value is string => typeof value === 'string'
  && /T.*(?:Z|\+00:00)$/.test(value) && Number.isFinite(Date.parse(value));

export function validEquipmentDate(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value) || value < '1900-01-01' || value > '9998-12-31') return false;
  const parsed = new Date(`${value}T12:00:00.000Z`);
  return Number.isFinite(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}
export function equipmentDueStatus(date: string | null, businessDate: string): EquipmentDueStatus {
  return date === null ? 'Unscheduled' : date < businessDate ? 'Overdue' : date === businessDate ? 'Due' : 'Current';
}

function validBusinessCalendar(raw: { businessDate: string; timeZone: string }): void {
  if (!raw || !validEquipmentDate(raw.businessDate) || !text(raw.timeZone, 100, true))
    throw new Error('Die Fälligkeiten enthalten kein gültiges Betriebsdatum. Bitte neu laden.');
  try { new Intl.DateTimeFormat('de-DE', { timeZone: raw.timeZone }).format(); }
  catch { throw new Error('Die Betriebszeitzone konnte nicht gelesen werden. Bitte neu laden.'); }
}
function validPage(raw: { items: unknown[]; total: number; page: number; pageSize: number }, page: number, size: number): void {
  if (!raw || !Array.isArray(raw.items) || raw.page !== page || raw.pageSize !== size
    || !Number.isSafeInteger(raw.total) || raw.total < 0 || raw.items.length > size
    || raw.items.length > raw.total || (raw.items.length > 0 && (page - 1) * size + raw.items.length > raw.total))
    throw new Error('Die Geräte- oder Prüfliste ist unvollständig oder gehört zu einer anderen Seite. Bitte neu laden.');
}

/** History uses deserialized equipment. Only genuinely absent newly introduced fields receive legacy defaults. */
export function validEquipment(raw: EquipmentRecord, expectedId?: string): EquipmentRecord {
  if (!raw) throw new Error('Die Geräteakte passt nicht zur angeforderten Auswahl. Bitte neu öffnen.');
  const value = { ...raw, nextInspectionOn: raw.nextInspectionOn === undefined ? null : raw.nextInspectionOn,
    inspectionBlocked: raw.inspectionBlocked === undefined ? false : raw.inspectionBlocked,
    pendingInspectionId: raw.pendingInspectionId === undefined ? null : raw.pendingInspectionId,
    lastInspectionId: raw.lastInspectionId === undefined ? null : raw.lastInspectionId };
  if (!validEquipmentId(value.id) || (expectedId && value.id !== expectedId) || !revision(value.revision)
    || !text(value.name, 240, true) || !text(value.inventoryNumber, 100, true)
    || !['Tool', 'Machine', 'Vehicle'].includes(value.kind) || !optionalText(value.registration, 80) || !optionalText(value.location, 240)
    || (value.nextInspectionOn !== null && !validEquipmentDate(value.nextInspectionOn))
    || (value.lastInspectionOn !== undefined && value.lastInspectionOn !== null && !validEquipmentDate(value.lastInspectionOn))
    || typeof value.available !== 'boolean' || typeof value.inspectionBlocked !== 'boolean' || value.inspectionBlocked && value.available
    || !optionalId(value.pendingInspectionId) || !optionalId(value.lastInspectionId))
    throw new Error('Die Geräteakte passt nicht zur angeforderten Auswahl. Bitte neu öffnen.');
  return value;
}

export function validEquipmentWorklist(raw: EquipmentWorklist, page: number): EquipmentWorklist {
  validBusinessCalendar(raw); validPage(raw, page, 25);
  const ids = new Set<string>();
  const items = raw.items.map(item => {
    const value = validEquipment(item);
    if (ids.has(value.id) || typeof item.inspectionBlocked !== 'boolean'
      || item.pendingInspectionId === undefined || item.lastInspectionId === undefined
      || item.dueStatus !== equipmentDueStatus(value.nextInspectionOn, raw.businessDate))
      throw new Error('Die Geräteliste enthält widersprüchliche Fälligkeiten oder doppelte Einträge. Bitte neu laden.');
    ids.add(value.id); return { ...value, dueStatus: item.dueStatus };
  });
  return { ...raw, items };
}

export function validEquipmentInspection(raw: EquipmentInspection, equipmentId: string): EquipmentInspection {
  const snapshot = raw?.equipmentSnapshot;
  if (!raw || !validEquipmentId(raw.id) || !revision(raw.revision) || raw.equipmentId !== equipmentId
    || !['Inspection', 'Maintenance', 'Repair'].includes(raw.kind) || !text(raw.title, 240, true)
    || !validEquipmentDate(raw.scheduledOn) || !text(raw.notes, 4000)
    || !['Scheduled', 'Completed', 'Cancelled'].includes(raw.status) || !digest(raw.scopeDigest) || !validEquipmentId(raw.plannedBy)
    || !snapshot || snapshot.id !== equipmentId || !text(snapshot.name, 240, true) || !text(snapshot.inventoryNumber, 100, true)
    || !['Tool', 'Machine', 'Vehicle'].includes(snapshot.kind) || !optionalText(snapshot.registration, 80))
    throw new Error('Der Prüfbeleg enthält unvollständige oder unpassende Nachweise. Bitte neu öffnen.');
  const completion = raw.completion, cancellation = raw.cancellation;
  if ((raw.status === 'Completed') !== (completion !== null) || (raw.status === 'Cancelled') !== (cancellation !== null))
    throw new Error('Status und Nachweise des Prüfvorgangs widersprechen sich. Bitte neu öffnen.');
  if (completion && (!validEquipmentDate(completion.performedOn) || !['Passed', 'Failed'].includes(completion.result)
    || !text(completion.inspector, 240, true) || !text(completion.evidenceReference, 500, true)
    || !text(completion.notes, 4000, completion.result === 'Failed') || typeof completion.makeAvailable !== 'boolean'
    || !validEquipmentId(completion.recordedBy) || !instant(completion.recordedAt) || !digest(completion.digest)
    || (completion.result === 'Passed' ? !validEquipmentDate(completion.nextInspectionOn) || completion.nextInspectionOn <= completion.performedOn
      : completion.nextInspectionOn !== null || completion.makeAvailable)))
    throw new Error('Der abgeschlossene Prüfnachweis enthält widersprüchliche Angaben. Bitte neu öffnen.');
  if (cancellation && (!text(cancellation.reason, 2000, true) || !validEquipmentId(cancellation.cancelledBy)
    || !instant(cancellation.cancelledAt) || !digest(cancellation.digest)))
    throw new Error('Der Absagenachweis ist unvollständig. Bitte neu öffnen.');
  return raw;
}

export function validEquipmentHistory(raw: EquipmentHistory, equipmentId: string, page: number): EquipmentHistory {
  validBusinessCalendar(raw); validPage(raw, page, 20);
  const equipment = validEquipment(raw.equipment, equipmentId);
  if (!Number.isSafeInteger(raw.affectedAssignmentCount) || raw.affectedAssignmentCount < 0
    || (equipment.lastInspectionId !== null && !validEquipmentDate(equipment.lastInspectionOn))
    || (equipment.lastInspectionId === null && equipment.lastInspectionOn != null)
    || (equipment.pendingInspectionId === null ? raw.pendingInspection !== null : raw.pendingInspection?.id !== equipment.pendingInspectionId))
    throw new Error('Geräteakte und geplanter Prüfvorgang stimmen nicht überein. Bitte neu laden.');
  if (raw.pendingInspection && (validEquipmentInspection(raw.pendingInspection, equipmentId).status !== 'Scheduled'))
    throw new Error('Der aktuelle Prüfvorgang ist bereits abgeschlossen oder abgesagt. Bitte neu laden.');
  const ids = new Set<string>();
  raw.items.forEach(item => {
    validEquipmentInspection(item, equipmentId);
    if (ids.has(item.id)) throw new Error('Die Prüfhistorie enthält doppelte Nachweise. Bitte neu laden.');
    ids.add(item.id);
  });
  return { ...raw, equipment };
}

export function validEquipmentMutation(raw: EquipmentMutation, equipmentId: string, previous: EquipmentRecord,
  expectedStatus: InspectionStatus, expectedInspectionId?: string): EquipmentMutation {
  const equipment = validEquipment(raw?.equipment, equipmentId);
  const inspection = validEquipmentInspection(raw?.inspection, equipmentId);
  const completion = inspection.completion;
  if (equipment.revision <= previous.revision || inspection.status !== expectedStatus
    || (expectedInspectionId && inspection.id !== expectedInspectionId)
    || (expectedStatus === 'Scheduled' ? equipment.pendingInspectionId !== inspection.id : equipment.pendingInspectionId !== null)
    || (expectedStatus !== 'Completed' && (equipment.available !== previous.available || equipment.inspectionBlocked !== previous.inspectionBlocked
      || equipment.nextInspectionOn !== previous.nextInspectionOn || equipment.lastInspectionId !== previous.lastInspectionId
      || (equipment.lastInspectionOn ?? null) !== (previous.lastInspectionOn ?? null)))
    || (expectedStatus === 'Completed' && (equipment.lastInspectionId !== inspection.id
      || equipment.inspectionBlocked !== (completion!.result === 'Failed')
      || equipment.lastInspectionOn !== completion!.performedOn
      || equipment.available !== (completion!.result === 'Failed' ? false : completion!.makeAvailable || previous.available)
      || equipment.nextInspectionOn !== (completion!.result === 'Passed' ? completion!.nextInspectionOn : previous.nextInspectionOn)))
    || !Number.isSafeInteger(raw.affectedAssignmentCount) || raw.affectedAssignmentCount < 0)
    throw new Error('Die Antwort bestätigt den angeforderten Prüfvorgang nicht eindeutig. Laden Sie die Geräteakte neu, bevor Sie erneut buchen.');
  return { ...raw, equipment, inspection };
}

export const equipmentKindLabels: Record<EquipmentKind, string> = { Tool: 'Werkzeug', Machine: 'Maschine', Vehicle: 'Fahrzeug' };
export const inspectionKindLabels: Record<InspectionKind, string> = { Inspection: 'Prüfung', Maintenance: 'Wartung', Repair: 'Reparatur' };
export const inspectionStatusLabels: Record<InspectionStatus, string> = { Scheduled: 'Geplant', Completed: 'Abgeschlossen', Cancelled: 'Abgesagt' };
export const dueStatusLabels: Record<EquipmentDueStatus, string> = { Unscheduled: 'Kein Prüftermin', Current: 'Termin vorgemerkt', Due: 'Heute fällig', Overdue: 'Prüftermin überschritten' };
