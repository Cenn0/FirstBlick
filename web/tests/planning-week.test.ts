import { describe, expect, it } from 'vitest';
import { businessLocalTime, businessOffset, businessTimeChoices, resolveBusinessTime, resolveEditedBusinessTime } from '../src/app/core/business-time';
import { mondayOf, PlanningResponse, shiftBusinessWeek, validPlanningResponse } from '../src/app/core/planning-week';

const filters = { employeeId: null, projectId: null, equipmentId: null };
function week(): PlanningResponse {
  const start = Date.parse('2026-10-18T22:00:00Z');
  return {
    weekStart: '2026-10-19', timeZone: 'Europe/Berlin', filters, count: 1, maxAssignments: 1000, note: '',
    days: Array.from({ length: 7 }, (_, i) => ({ date: `2026-10-${19 + i}`,
      startUtc: new Date(start + i * 86_400_000).toISOString(),
      endUtc: new Date(start + (i + 1) * 86_400_000 + (i === 6 ? 3_600_000 : 0)).toISOString() })),
    assignments: [{ id: '10000000-0000-4000-8000-000000000001', revision: 2,
      employeeId: '10000000-0000-4000-8000-000000000002', employeeName: 'Team member',
      projectId: '10000000-0000-4000-8000-000000000003', projectName: 'Roof', title: 'Inspection',
      start: '2026-10-25T00:10:00Z', end: '2026-10-25T01:50:00Z', days: ['2026-10-25'], equipmentIds: [], equipment: [] }]
  };
}

describe('business wall clock independent of device zone', () => {
  it('converts a winter and summer working day to exact UTC instants', () => {
    expect(resolveBusinessTime('2026-07-01T08:00', 'Europe/Berlin')).toBe('2026-07-01T06:00:00.000Z');
    expect(resolveBusinessTime('2026-12-01T08:00', 'Europe/Berlin')).toBe('2026-12-01T07:00:00.000Z');
    expect(resolveBusinessTime('2026-07-01T08:00', 'Asia/Kolkata')).toBe('2026-07-01T02:30:00.000Z');
  });
  it('rejects nonexistent dates and the spring gap', () => {
    expect(businessTimeChoices('2026-02-30T08:00', 'Europe/Berlin')).toEqual([]);
    expect(() => resolveBusinessTime('2026-03-29T02:30', 'Europe/Berlin')).toThrow('existiert');
    expect(businessTimeChoices('2026-10-20T24:00', 'Europe/Berlin')).toEqual([]);
  });
  it('requires a deliberate offset for a repeated hour and preserves milliseconds during editing', () => {
    const local = '2026-10-25T02:30:12.345';
    expect(businessTimeChoices(local, 'Europe/Berlin').map(x => x.offset)).toEqual(['+02:00', '+01:00']);
    expect(() => resolveBusinessTime(local, 'Europe/Berlin')).toThrow('zweimal');
    const instant = resolveBusinessTime(local, 'Europe/Berlin', '+01:00');
    expect(instant).toBe('2026-10-25T01:30:12.345Z');
    expect(businessLocalTime(instant, 'Europe/Berlin')).toBe(local);
    expect(businessOffset(instant, 'Europe/Berlin')).toBe('+01:00');
  });
  it('handles a half-hour daylight saving transition', () => {
    expect(businessTimeChoices('2026-04-05T01:45', 'Australia/Lord_Howe').map(x => x.offset)).toEqual(['+11:00', '+10:30']);
  });
  it('preserves submillisecond API precision when only the assignment text changes', () => {
    const original = '2026-10-25T01:30:12.3456789+00:00';
    expect(resolveEditedBusinessTime('2026-10-25T02:30:12.345', 'Europe/Berlin', '+01:00', original)).toBe(original);
    expect(resolveEditedBusinessTime('2026-10-25T02:31:12.345', 'Europe/Berlin', '+01:00', original)).toBe('2026-10-25T01:31:12.345Z');
  });
});

describe('verified weekly view', () => {
  it('uses calendar Mondays across year boundaries', () => {
    expect(mondayOf('2026-01-01')).toBe('2025-12-29');
    expect(shiftBusinessWeek('2025-12-29', 1)).toBe('2026-01-05');
    expect(() => mondayOf('2026-02-30')).toThrow();
    expect(() => mondayOf('1900-01-01')).toThrow();
  });
  it('accepts the 25-hour Sunday without silently normalizing it', () => {
    expect(validPlanningResponse(week(), '2026-10-19', filters).count).toBe(1);
  });
  it('rejects a wrong business-zone boundary even if all days are contiguous', () => {
    const value = week();
    value.days.forEach(day => { day.startUtc = new Date(Date.parse(day.startUtc) + 3_600_000).toISOString(); day.endUtc = new Date(Date.parse(day.endUtc) + 3_600_000).toISOString(); });
    expect(() => validPlanningResponse(value, '2026-10-19', filters)).toThrow('Betriebszeitzone');
  });
  it('rejects stale filters, duplicated identities and false day memberships', () => {
    expect(() => validPlanningResponse(week(), '2026-10-19', { ...filters, employeeId: 'different' })).toThrow('Filter');
    const duplicate = week(); duplicate.assignments.push(duplicate.assignments[0]); duplicate.count++;
    expect(() => validPlanningResponse(duplicate, '2026-10-19', filters)).toThrow('widersprüchliche');
    const wrongDay = week(); wrongDay.assignments[0].days = ['2026-10-24'];
    expect(() => validPlanningResponse(wrongDay, '2026-10-19', filters)).toThrow('Geschäftstagen');
  });
  it('rejects partial results and incorrectly bound equipment', () => {
    const incomplete = week(); incomplete.count = 2;
    expect(() => validPlanningResponse(incomplete, '2026-10-19', filters)).toThrow('unvollständig');
    const wrong = week(); wrong.assignments[0].equipment = [{ id: wrong.assignments[0].id, name: 'Other resource' }];
    expect(() => validPlanningResponse(wrong, '2026-10-19', filters)).toThrow('widersprüchliche');
  });
});
