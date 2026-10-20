import {afterEach,expect,test,vi} from 'vitest';
import {instantOffset,localDateTime,localTimeChoices,resolveLocalTime} from '../src/app/core/local-time';
afterEach(()=>vi.unstubAllEnvs());

test('a repeated Berlin hour requires an explicit offset and preserves either real instant',()=>{
  vi.stubEnv('TZ','Europe/Berlin');const choices=localTimeChoices('2026-10-25T02:30');expect(choices.map(choice=>[choice.instant,choice.offset])).toEqual([['2026-10-25T00:30:00.000Z','+02:00'],['2026-10-25T01:30:00.000Z','+01:00']]);expect(()=>resolveLocalTime('2026-10-25T02:30')).toThrow(/zweimal/);expect(resolveLocalTime('2026-10-25T02:30','+01:00')).toBe('2026-10-25T01:30:00.000Z');
});
test('skipped local hours and invalid calendar values cannot silently shift',()=>{
  vi.stubEnv('TZ','Europe/Berlin');for(const value of ['2026-03-29T02:30','2026-02-30T10:00','2026-10-20T25:00'])expect(()=>resolveLocalTime(value)).toThrow(/existiert/);
});
test('editing the second Los Angeles hour preserves seconds, milliseconds and offset',()=>{
  vi.stubEnv('TZ','America/Los_Angeles');const original='2026-11-01T08:30:12.345Z';expect(localDateTime(original)).toBe('2026-11-01T01:30:12.345');expect(instantOffset(original)).toBe('-07:00');expect(resolveLocalTime(localDateTime(original),instantOffset(original))).toBe(original);const second='2026-11-01T09:30:12.345Z';expect(resolveLocalTime(localDateTime(second),instantOffset(second))).toBe(second);
});
test('ordinary offset times resolve without an unnecessary choice',()=>{
  vi.stubEnv('TZ','Asia/Kathmandu');expect(localTimeChoices('2026-10-20T10:45')).toHaveLength(1);expect(resolveLocalTime('2026-10-20T10:45')).toBe('2026-10-20T05:00:00.000Z');
});
