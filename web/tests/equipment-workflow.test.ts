import { describe, expect, it } from 'vitest';
import { EquipmentHistory, EquipmentInspection, EquipmentMutation, EquipmentRecord, EquipmentWorklist,
  equipmentDueStatus, validEquipmentDate, validEquipmentHistory, validEquipmentMutation,
  validEquipmentWorklist } from '../src/app/core/equipment-workflow.models';

const equipmentId = '10000000-0000-4000-8000-000000000001';
const inspectionId = '10000000-0000-4000-8000-000000000002';
const personId = '10000000-0000-4000-8000-000000000003';
const equipment = (): EquipmentRecord => ({ id: equipmentId, revision: 2, name: 'Synthetischer Transporter', inventoryNumber: 'FZ-03',
  kind: 'Vehicle', registration: 'TEST-03', location: 'Hof', available: true, nextInspectionOn: '2026-10-21',
  inspectionBlocked: false, pendingInspectionId: inspectionId, lastInspectionId: null });
const inspection = (): EquipmentInspection => ({ id: inspectionId, revision: 1, equipmentId, kind: 'Inspection',
  title: 'Prüfung', scheduledOn: '2026-10-21', notes: '', status: 'Scheduled', plannedBy: personId, scopeDigest: 'a'.repeat(64),
  equipmentSnapshot: { id: equipmentId, name: 'Synthetischer Transporter', inventoryNumber: 'FZ-03', kind: 'Vehicle', registration: 'TEST-03' },
  completion: null, cancellation: null });
const history = (): EquipmentHistory => ({ equipment: equipment(), pendingInspection: inspection(), items: [inspection()],
  businessDate: '2026-10-21', timeZone: 'Europe/Berlin', affectedAssignmentCount: 0, total: 1, page: 1, pageSize: 20 });
const worklist = (): EquipmentWorklist => ({ businessDate: '2026-10-21', timeZone: 'Europe/Berlin',
  items: [{ ...equipment(), dueStatus: 'Due' }], total: 1, page: 1, pageSize: 25 });
function completed(result: 'Passed' | 'Failed' = 'Passed'): EquipmentMutation {
  const old = equipment();
  return { equipment: { ...old, revision: 3, pendingInspectionId: null, lastInspectionId: inspectionId,
    lastInspectionOn: '2026-10-21',
    inspectionBlocked: result === 'Failed', available: result !== 'Failed',
    nextInspectionOn: result === 'Passed' ? '2027-10-21' : old.nextInspectionOn },
  inspection: { ...inspection(), revision: 2, status: 'Completed', completion: {
    performedOn: '2026-10-21', result, inspector: 'Prüfstelle', evidenceReference: 'Bericht-1', notes: result === 'Failed' ? 'Mangel' : '',
    nextInspectionOn: result === 'Passed' ? '2027-10-21' : null, makeAvailable: result === 'Passed',
    recordedBy: personId, recordedAt: '2026-10-21T10:00:00Z', digest: 'b'.repeat(64) } }, affectedAssignmentCount: 0 };
}

describe('equipment calendar and response boundaries', () => {
  it('uses strict calendar dates and keeps due today distinct from overdue and missing', () => {
    expect(validEquipmentDate('2028-02-29')).toBe(true);
    expect(validEquipmentDate('2026-02-29')).toBe(false);
    expect(validEquipmentDate('2026-04-31')).toBe(false);
    expect(equipmentDueStatus('2026-10-21', '2026-10-21')).toBe('Due');
    expect(equipmentDueStatus('2026-10-20', '2026-10-21')).toBe('Overdue');
    expect(equipmentDueStatus(null, '2026-10-21')).toBe('Unscheduled');
  });
  it('accepts a bounded worklist and rejects mismatching page or false due status', () => {
    expect(validEquipmentWorklist(worklist(), 1).items).toHaveLength(1);
    expect(() => validEquipmentWorklist(worklist(), 2)).toThrow('anderen Seite');
    const forged = worklist(); forged.items[0].dueStatus = 'Current';
    expect(() => validEquipmentWorklist(forged, 1)).toThrow('widersprüchliche');
  });
  it('accepts nullable optional equipment text without rewriting retained inspection evidence', () => {
    const list = worklist(); list.items[0].registration = null; list.items[0].location = null;
    expect(validEquipmentWorklist(list, 1).items[0]).toMatchObject({ registration: null, location: null });
    const value = history(); value.equipment.registration = null; value.equipment.location = null;
    value.pendingInspection!.equipmentSnapshot.registration = null; value.items[0].equipmentSnapshot.registration = null;
    const before = JSON.stringify(value);
    const accepted = validEquipmentHistory(value, equipmentId, 1);
    expect(accepted.pendingInspection).toBe(value.pendingInspection);
    expect(accepted.items[0].scopeDigest).toBe('a'.repeat(64));
    expect(JSON.stringify(value)).toBe(before);
    const result = completed(); result.equipment.registration = null; result.equipment.location = null;
    result.inspection.equipmentSnapshot.registration = null;
    const evidence = JSON.stringify(result.inspection);
    expect(validEquipmentMutation(result, equipmentId, value.equipment, 'Completed', inspectionId).inspection).toBe(result.inspection);
    expect(JSON.stringify(result.inspection)).toBe(evidence);
    value.equipment.location = 123 as unknown as string;
    expect(() => validEquipmentHistory(value, equipmentId, 1)).toThrow('Auswahl');
  });
  it('rejects duplicated identities and unknown calendar zones', () => {
    const duplicate = worklist(); duplicate.items.push(duplicate.items[0]); duplicate.total++;
    expect(() => validEquipmentWorklist(duplicate, 1)).toThrow('doppelte');
    const zone = worklist(); zone.timeZone = 'Invalid/Zone';
    expect(() => validEquipmentWorklist(zone, 1)).toThrow('Betriebszeitzone');
  });
  it('requires the pending record to agree with the selected equipment independently of history pagination', () => {
    const page2 = { ...history(), items: [], page: 2 };
    expect(validEquipmentHistory(page2, equipmentId, 2).pendingInspection?.id).toBe(inspectionId);
    const wrong = history(); wrong.pendingInspection = { ...inspection(), equipmentId: personId };
    expect(() => validEquipmentHistory(wrong, equipmentId, 1)).toThrow('Nachweise');
    const missing = history(); missing.pendingInspection = null;
    expect(() => validEquipmentHistory(missing, equipmentId, 1)).toThrow('stimmen nicht');
  });
  it('rejects missing, inconsistent or fabricated completion and cancellation evidence', () => {
    const forged = history(); forged.pendingInspection!.status = 'Completed';
    expect(() => validEquipmentHistory(forged, equipmentId, 1)).toThrow('widersprechen');
    const closed = completed(); closed.inspection.completion!.digest = 'not-a-hash';
    expect(() => validEquipmentMutation(closed, equipmentId, equipment(), 'Completed', inspectionId)).toThrow('Prüfnachweis');
  });
  it('binds successful mutations to equipment, prior revision, requested inspection and status', () => {
    expect(validEquipmentMutation(completed(), equipmentId, equipment(), 'Completed', inspectionId).equipment.revision).toBe(3);
    const stale = completed(); stale.equipment.revision = 2;
    expect(() => validEquipmentMutation(stale, equipmentId, equipment(), 'Completed', inspectionId)).toThrow('nicht eindeutig');
    expect(() => validEquipmentMutation(completed(), equipmentId, equipment(), 'Completed', personId)).toThrow('nicht eindeutig');
    expect(() => validEquipmentMutation(completed(), personId, equipment(), 'Completed', inspectionId)).toThrow('Auswahl');
  });
  it('requires a negative result to keep the previous inspection date and block disposition', () => {
    const negative = completed('Failed');
    expect(validEquipmentMutation(negative, equipmentId, equipment(), 'Completed', inspectionId).equipment.inspectionBlocked).toBe(true);
    negative.equipment.available = true;
    expect(() => validEquipmentMutation(negative, equipmentId, equipment(), 'Completed', inspectionId)).toThrow();
  });
  it('does not interpret a positive result without explicit availability confirmation as an automatic release', () => {
    const old = { ...equipment(), available: false }, result = completed();
    result.inspection.completion!.makeAvailable = false; result.equipment.available = false;
    expect(validEquipmentMutation(result, equipmentId, old, 'Completed', inspectionId).equipment.available).toBe(false);
    result.equipment.available = true;
    expect(() => validEquipmentMutation(result, equipmentId, old, 'Completed', inspectionId)).toThrow('nicht eindeutig');
  });
  it('rejects unsupported result values and explicit null availability instead of applying a legacy default', () => {
    const value = history(); value.equipment.inspectionBlocked = null as unknown as boolean;
    expect(() => validEquipmentHistory(value, equipmentId, 1)).toThrow('Auswahl');
    const result = completed(); result.inspection.completion!.result = 'Unknown' as 'Passed';
    expect(() => validEquipmentMutation(result, equipmentId, equipment(), 'Completed', inspectionId)).toThrow('Prüfnachweis');
  });
});
