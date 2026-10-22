export interface MaintenanceCalendarSelection {
  tenantId: string;
  visitId: string;
  revision: number;
  dueDate: string;
  status: 'Planned' | 'Completed';
}

/** Validate this app's bounded calendar response before creating a downloadable URL. */
export async function verifiedMaintenanceCalendar(response: Response, selected: MaintenanceCalendarSelection): Promise<Blob> {
  const invalid = () => new Error('Die Kalenderdatei stimmt nicht mit dem geöffneten Wartungstermin überein. Bitte öffnen Sie den Termin erneut.');
  const contentType = response.headers.get('Content-Type') || '';
  const sha256 = response.headers.get('X-Content-SHA256') || '';
  const size = response.headers.get('Content-Length');
  if (!response.ok || !/^text\/calendar\s*;\s*charset=utf-8$/i.test(contentType) || !/^[a-f0-9]{64}$/.test(sha256)
    || response.headers.get('ETag') !== `"${selected.revision}"` || !Number.isSafeInteger(selected.revision)
    || selected.revision < 1 || selected.revision > 2_147_483_648 || !selected.tenantId || !selected.visitId
    || !/^\d{4}-\d{2}-\d{2}$/.test(selected.dueDate) || !['Planned', 'Completed'].includes(selected.status)) throw invalid();
  const date = new Date(`${selected.dueDate}T00:00:00Z`);
  if (!Number.isFinite(date.valueOf()) || date.toISOString().slice(0, 10) !== selected.dueDate || Number(selected.dueDate.slice(0, 4)) < 1 || selected.dueDate === '9999-12-31') throw invalid();
  date.setUTCDate(date.getUTCDate() + 1);
  const nextDate = date.toISOString().slice(0, 10).replaceAll('-', '');
  if (size !== null && (!/^\d+$/.test(size) || Number(size) <= 0 || Number(size) > 65_536)) throw invalid();
  const bytes = await response.arrayBuffer();
  if (bytes.byteLength <= 0 || bytes.byteLength > 65_536 || size !== null && Number(size) !== bytes.byteLength) throw invalid();
  const digest = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), b => b.toString(16).padStart(2, '0')).join('');
  if (digest !== sha256) throw invalid();
  let text: string;
  try { text = new TextDecoder('utf-8', { fatal: true }).decode(bytes); } catch { throw invalid(); }
  if (!text.endsWith('\r\n') || /[\r\n]/.test(text.replaceAll('\r\n', ''))
    || text.split('\r\n').some(line => new TextEncoder().encode(line).length > 75)) throw invalid();
  const lines = text.replace(/\r\n[ \t]/g, '').split('\r\n');
  const once = (line: string) => lines.filter(x => x === line).length === 1;
  const uid = `maintenance-${selected.tenantId.replaceAll('-', '').toLowerCase()}-${selected.visitId.replaceAll('-', '').toLowerCase()}@firstblick.invalid`;
  if (lines.length !== 20 || lines[0] !== 'BEGIN:VCALENDAR' || lines[4] !== 'BEGIN:VEVENT' || lines.at(-3) !== 'END:VEVENT' || lines.at(-2) !== 'END:VCALENDAR'
    || !once('VERSION:2.0') || !once('UID:' + uid) || !once('SEQUENCE:' + (selected.revision - 1))
    || !once('DTSTART;VALUE=DATE:' + selected.dueDate.replaceAll('-', '')) || !once('DTEND;VALUE=DATE:' + nextDate)
    || !once('X-FIRSTBLICK-MAINTENANCE-STATUS:' + selected.status) || !once('STATUS:CONFIRMED')
    || !once('CLASS:PRIVATE') || !once('TRANSP:TRANSPARENT')
    || lines.filter(x => x.startsWith('SUMMARY:')).length !== 1
    || lines.filter(x => x.startsWith('DTSTAMP:')).length !== 1 || lines.filter(x => x.startsWith('LAST-MODIFIED:')).length !== 1
    || !/^DTSTAMP:\d{8}T\d{6}Z$/.test(lines[6]) || !/^CREATED:\d{8}T\d{6}Z$/.test(lines[7])
    || lines[8] !== lines[6].replace('DTSTAMP:', 'LAST-MODIFIED:')
    || lines.some(x => /^(METHOD|ATTENDEE|ORGANIZER|RRULE|RDATE|DESCRIPTION|LOCATION|ATTACH|URL)[;:]/.test(x))
    || lines.filter(x => x.startsWith('BEGIN:')).length !== 2 || lines.filter(x => x.startsWith('END:')).length !== 2) throw invalid();
  return new Blob([bytes], { type: 'text/calendar;charset=utf-8' });
}
