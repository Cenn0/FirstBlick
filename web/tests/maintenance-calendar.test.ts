import { describe, expect, it } from 'vitest';
import { MaintenanceCalendarSelection, verifiedMaintenanceCalendar } from '../src/app/core/maintenance-calendar';

const selection: MaintenanceCalendarSelection = { tenantId: 'tenant-a', visitId: 'visit-a', revision: 1, dueDate: '2027-01-31', status: 'Planned' };
const calendar = () => [
  'BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//FirstBlick//Maintenance calendar 1.0//DE', 'CALSCALE:GREGORIAN', 'BEGIN:VEVENT',
  'UID:maintenance-tenanta-visita@firstblick.invalid', 'DTSTAMP:20261021T090102Z', 'CREATED:20261020T080000Z', 'LAST-MODIFIED:20261021T090102Z',
  'SEQUENCE:0', 'DTSTART;VALUE=DATE:20270131', 'DTEND;VALUE=DATE:20270201', 'SUMMARY:Wartung fällig: Dachprüfung',
  'STATUS:CONFIRMED', 'TRANSP:TRANSPARENT', 'CLASS:PRIVATE', 'X-FIRSTBLICK-MAINTENANCE-STATUS:Planned', 'END:VEVENT', 'END:VCALENDAR', ''
].join('\r\n');

async function response(value: string | Uint8Array = calendar(), headers: Record<string, string> = {}): Promise<Response> {
  const bytes = typeof value === 'string' ? new TextEncoder().encode(value) : value;
  const digest = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes as BufferSource)), b => b.toString(16).padStart(2, '0')).join('');
  return new Response(bytes as BodyInit, { headers: { 'Content-Type': 'text/calendar; charset=utf-8', 'ETag': '"1"', 'Content-Length': String(bytes.byteLength), 'X-Content-SHA256': digest, ...headers } });
}

describe('maintenance calendar download contract', () => {
  it('accepts the exact date revision tenant and verified UTF-8 bytes without changing them', async () => {
    const blob = await verifiedMaintenanceCalendar(await response(), selection);
    expect(await blob.text()).toBe(calendar()); expect(blob.type).toBe('text/calendar;charset=utf-8');
  });
  it('accepts a folded Unicode title while checking bytes rather than character count', async () => {
    const value = calendar().replace('SUMMARY:Wartung fällig: Dachprüfung', 'SUMMARY:Wartung fällig: Dachprüfung 🏠\r\n  in Nord');
    expect(await (await verifiedMaintenanceCalendar(await response(value), selection)).text()).toBe(value);
  });
  it('rejects a wrong checksum even when all calendar fields look correct', async () => {
    await expect(verifiedMaintenanceCalendar(await response(calendar(), { 'X-Content-SHA256': '0'.repeat(64) }), selection)).rejects.toThrow('Kalenderdatei');
  });
  it.each([
    { tenantId: 'tenant-b' }, { visitId: 'visit-b' }, { revision: 2 }, { dueDate: '2027-02-01' }, { status: 'Completed' }
  ])('rejects a file belonging to a different opened context: %j', async change => {
    await expect(verifiedMaintenanceCalendar(await response(), { ...selection, ...change } as MaintenanceCalendarSelection)).rejects.toThrow('Kalenderdatei');
  });
  it.each(['2027-02-29', '2026-04-31', '0000-01-01', '9999-12-31'])('rejects invalid or unrepresentable calendar date %s', async dueDate => {
    await expect(verifiedMaintenanceCalendar(await response(), { ...selection, dueDate })).rejects.toThrow('Kalenderdatei');
  });
  it('rejects incorrect MIME type size and missing revision metadata', async () => {
    for (const headers of [{ 'Content-Type': 'text/html' }, { 'Content-Length': '99999' }, { 'Content-Length': '1' }, { 'ETag': '' }])
      await expect(verifiedMaintenanceCalendar(await response(calendar(), headers), selection)).rejects.toThrow('Kalenderdatei');
  });
  it('rejects added invitation data and extra events even with a matching recomputed digest', async () => {
    const attendee = calendar().replace('SUMMARY:', 'ATTENDEE:mailto:private@example.test\r\nSUMMARY:');
    await expect(verifiedMaintenanceCalendar(await response(attendee), selection)).rejects.toThrow('Kalenderdatei');
    const extra = calendar().replace('END:VCALENDAR', 'BEGIN:VEVENT\r\nSUMMARY:Other\r\nEND:VEVENT\r\nEND:VCALENDAR');
    await expect(verifiedMaintenanceCalendar(await response(extra), selection)).rejects.toThrow('Kalenderdatei');
  });
  it('rejects bare newlines invalid Unicode and lines exceeding the byte folding limit', async () => {
    await expect(verifiedMaintenanceCalendar(await response(calendar().replaceAll('\r\n', '\n')), selection)).rejects.toThrow('Kalenderdatei');
    const bytes = new TextEncoder().encode(calendar()); bytes[bytes.length - 4] = 0xff;
    await expect(verifiedMaintenanceCalendar(await response(bytes), selection)).rejects.toThrow('Kalenderdatei');
    await expect(verifiedMaintenanceCalendar(await response(calendar().replace('Dachprüfung', '🏠'.repeat(30))), selection)).rejects.toThrow('Kalenderdatei');
  });
});
