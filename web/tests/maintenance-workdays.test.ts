import { describe, expect, it } from 'vitest';
import { expectedExecutionDate, MaintenanceWorkdayPolicy, validatedExecutionPlan, validatedMaintenancePreview,
  validatedWorkdayPolicy, workdayPolicyFromForm } from '../src/app/core/maintenance-workdays';

const policy: MaintenanceWorkdayPolicy = { weekdays: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'], excludedDates: ['2027-02-01'] };
const source = { tenantId: 'tenant-a', contractId: 'contract-a', contractRevision: 3, dueDate: '2027-01-31' };
const preview = { ...source, scheduledDate: '2027-02-02', contractEndDate: '2027-02-28', workdays: policy, existingVisitId: null, digest: 'a'.repeat(64) };

describe('explicit maintenance workday planning', () => {
  it('retains legacy defaults and requires valid explicit calendars only when enabled', () => {
    expect(validatedWorkdayPolicy(undefined)).toBeNull(); expect(validatedExecutionPlan(undefined, '2027-01-31')).toBeNull();
    expect(workdayPolicyFromForm(false, [], 'not a date')).toBeNull();
    expect(workdayPolicyFromForm(true, ['Friday', 'Monday'], '2027-02-03\n2027-02-01')).toEqual({ weekdays: ['Monday', 'Friday'], excludedDates: ['2027-02-01', '2027-02-03'] });
    expect(() => workdayPolicyFromForm(true, [], '')).toThrow();
    expect(() => workdayPolicyFromForm(true, ['Monday'], '2027-02-01\n2027-02-01')).toThrow();
  });

  it('uses the first permitted date after a weekend and a deliberate excluded day', () => {
    expect(expectedExecutionDate('2027-01-31', null, policy)).toBe('2027-02-02');
    expect(expectedExecutionDate('2027-01-31', null, null)).toBe('2027-01-31');
    expect(expectedExecutionDate('2027-02-02', '2027-02-02', policy)).toBe('2027-02-02');
    expect(() => expectedExecutionDate('2027-01-31', '2027-02-01', policy)).toThrow(/Vertragsende/);
  });

  it.each([['2026-03-29', '2026-03-30'], ['2026-10-25', '2026-10-26'], ['2028-02-29', '2028-02-29']])(
    'keeps calendar days across time changes and leap day: %s', (due, expected) => {
      expect(expectedExecutionDate(due, null, { ...policy, excludedDates: [] })).toBe(expected);
    });

  it('rejects invalid dates weekdays duplicate exclusions and oversized calendars', () => {
    for (const value of ['2027-02-29', '0000-01-01', '2027-13-01', '27-01-01', '2027-02-01T00:00:00Z'])
      expect(() => validatedWorkdayPolicy({ ...policy, excludedDates: [value] })).toThrow();
    for (const weekdays of [[], ['Monday', 'Monday'], ['Funday'], [1]]) expect(() => validatedWorkdayPolicy({ ...policy, weekdays })).toThrow();
    expect(() => validatedWorkdayPolicy({ ...policy, excludedDates: null })).toThrow();
    expect(() => workdayPolicyFromForm(true, ['Monday'], 'x'.repeat(5001))).toThrow();
  });

  it('rejects another tenant contract revision or due date before exposing a planning action', () => {
    expect(validatedMaintenancePreview(preview, source)).toEqual(preview);
    for (const change of [{ tenantId: 'tenant-b' }, { contractId: 'contract-b' }, { contractRevision: 4 }, { dueDate: '2027-02-28' }, { digest: 'x' }, { existingVisitId: '' }])
      expect(() => validatedMaintenancePreview({ ...preview, ...change }, source)).toThrow(/Vertragsstand/);
  });

  it('rejects a shifted result that disagrees with its retained calendar', () => {
    expect(() => validatedMaintenancePreview({ ...preview, scheduledDate: '2027-02-03' }, source)).toThrow(/Arbeitstagen/);
    expect(() => validatedMaintenancePreview({ ...preview, workdays: null }, source)).toThrow(/Arbeitstagen/);
    expect(() => validatedExecutionPlan({ dueDate: source.dueDate, scheduledDate: '2027-02-03', contractEndDate: null, workdays: policy }, source.dueDate)).toThrow();
    const frozen = { dueDate: source.dueDate, scheduledDate: preview.scheduledDate, contractEndDate: null, workdays: policy };
    expect(validatedExecutionPlan(frozen, source.dueDate)).toEqual(frozen);
    expect(() => validatedExecutionPlan(frozen, '2027-02-28')).toThrow();
  });

  it('enforces bounded searching at the end of the supported calendar', () => {
    expect(expectedExecutionDate('9999-12-31', null, { weekdays: ['Friday'], excludedDates: [] })).toBe('9999-12-31');
    expect(() => expectedExecutionDate('9999-12-31', null, { weekdays: ['Monday'], excludedDates: [] })).toThrow();
  });
});
