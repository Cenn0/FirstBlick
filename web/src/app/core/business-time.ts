export interface BusinessTimeChoice { instant: string; offset: string; label: string }

function wallTime(instant: number, zone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3, hourCycle: 'h23'
  }).formatToParts(instant);
  const get = (type: string) => parts.find(part => part.type === type)!.value;
  return `${get('year')}-${get('month')}-${get('day')}T${get('hour')}:${get('minute')}:${get('second')}.${get('fractionalSecond')}`;
}

function offsetLabel(minutes: number): string {
  const absolute = Math.abs(minutes);
  return `${minutes >= 0 ? '+' : '-'}${String(Math.floor(absolute / 60)).padStart(2, '0')}:${String(absolute % 60).padStart(2, '0')}`;
}

/** Resolve the BUSINESS wall clock, independent of the browser/device zone. Gaps are rejected. */
export function businessTimeChoices(local: string, zone: string): BusinessTimeChoice[] {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::(\d{2})(?:\.(\d{1,3}))?)?$/.exec(local);
  if (!match) return [];
  const normalized = `${match[1]}T${match[2]}:${match[3] ?? '00'}.${(match[4] ?? '0').padEnd(3, '0')}`;
  const base = Date.parse(normalized + 'Z');
  if (!Number.isFinite(base) || new Date(base).toISOString() !== normalized + 'Z') return [];
  const offsets = new Set<number>();
  // Sampling both sides of the supplied date captures ordinary and non-hour DST transitions.
  for (let hours = -48; hours <= 48; hours += 6) {
    const sample = base + hours * 3_600_000;
    offsets.add(Date.parse(wallTime(sample, zone) + 'Z') - sample);
  }
  const choices = [...offsets].flatMap(offset => {
    const instant = base - offset;
    return wallTime(instant, zone) === normalized
      ? [{ instant: new Date(instant).toISOString(), offset: offsetLabel(offset / 60_000), label: `UTC${offsetLabel(offset / 60_000)}` }]
      : [];
  }).sort((a, b) => a.instant.localeCompare(b.instant));
  return choices.map((choice, index) => ({ ...choice,
    label: choices.length > 1 ? `${index + 1}. Auftreten · ${choice.label}` : choice.label }));
}

export function resolveBusinessTime(local: string, zone: string, offset = ''): string {
  const choices = businessTimeChoices(local, zone);
  if (!choices.length) throw new Error('Diese Uhrzeit existiert in der Betriebszeitzone nicht. Prüfen Sie Datum und Zeitumstellung.');
  if (choices.length === 1) return choices[0].instant;
  const selected = choices.find(choice => choice.offset === offset);
  if (!selected) throw new Error('Diese Uhrzeit kommt zweimal vor. Wählen Sie ausdrücklich den Zeitversatz.');
  return selected.instant;
}

export function businessLocalTime(instant: string, zone: string): string {
  const value = wallTime(Date.parse(instant), zone);
  return value.endsWith(':00.000') ? value.slice(0, -7) : value.endsWith('.000') ? value.slice(0, -4) : value;
}

export function businessOffset(instant: string, zone: string): string {
  const time = Date.parse(instant);
  return offsetLabel((Date.parse(wallTime(time, zone) + 'Z') - time) / 60_000);
}

/** Preserve an existing API timestamp byte-for-byte when its displayed wall time was not edited. */
export function resolveEditedBusinessTime(local: string, zone: string, offset: string, original?: string): string {
  if (original && local === businessLocalTime(original, zone) && offset === businessOffset(original, zone)) return original;
  return resolveBusinessTime(local, zone, offset);
}
