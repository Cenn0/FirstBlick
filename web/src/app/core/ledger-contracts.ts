/** Shared boundary checks for retained journals. Numeric business values stay decimal strings. */
export const recordObject = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);
export const recordId = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
export const recordText = (value: unknown, max: number, required = false): value is string => typeof value === 'string' && value.length <= max && !value.includes('\0') && (!required || !!value.trim());
export const recordRevision = (value: unknown): value is number => Number.isSafeInteger(value) && Number(value) > 0;
export const recordDigest = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
export function recordInstant(value: unknown, wholeSeconds = false): value is string {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)
    && Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 19) === value.slice(0, 19)
    && (!wholeSeconds || !/\.[0-9]*[1-9]/.test(value));
}
export function recordDate(value: unknown): value is string {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) && Number.isFinite(Date.parse(value + 'T00:00:00Z'))
    && new Date(value + 'T00:00:00Z').toISOString().slice(0, 10) === value;
}
export function recordZone(value: unknown): value is string {
  if (typeof value !== 'string' || !value) return false;
  try { new Intl.DateTimeFormat('de-DE', { timeZone: value }).format(0); return true; } catch { return false; }
}
export function recordPage(value: unknown, page: number, pageSize: number): value is Record<string, unknown> & { items: unknown[]; total: number; page: number; pageSize: number } {
  if (!recordObject(value) || value['page'] !== page || value['pageSize'] !== pageSize || !Number.isSafeInteger(value['total']) || Number(value['total']) < 0 || !Array.isArray(value['items'])) return false;
  const expected = Math.min(pageSize, Math.max(0, Number(value['total']) - (page - 1) * pageSize));
  return value['items'].length === expected;
}
export function recordEntity(value: unknown, tenant: string): value is Record<string, unknown> {
  return recordObject(value) && recordId(value['id']) && value['tenantId'] === tenant && recordRevision(value['revision']) && value['archived'] === false
    && recordInstant(value['createdAt']) && recordInstant(value['updatedAt']) && Date.parse(value['updatedAt']) >= Date.parse(value['createdAt']);
}
export function decimalUnits(value: unknown, places: number): bigint | null {
  if (typeof value !== 'string' || !/^\d+(?:\.\d+)?$/.test(value) || value.length > 40) return null;
  const [whole, fraction = ''] = value.split('.');
  if (fraction.slice(places).replace(/0/g, '')) return null;
  return BigInt(whole) * 10n ** BigInt(places) + BigInt(fraction.slice(0, places).padEnd(places, '0') || '0');
}
export const sameInstant = (a: string, b: string): boolean => Date.parse(a) === Date.parse(b);
export function sameRecord(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((item, index) => sameRecord(item, b[index]));
  if (!recordObject(a) || !recordObject(b)) return false;
  const keys = Object.keys(a).sort(), others = Object.keys(b).sort();
  return keys.length === others.length && keys.every((key, index) => key === others[index] && sameRecord(a[key], b[key]));
}
export function requireUnique(ids: string[], message: string): void { if (new Set(ids).size !== ids.length) throw new Error(message); }

export interface RecordChoice { id: string; name: string; revision: number; number?: string; customerId?: string; active?: boolean }
export interface RecordChoicePage { items: RecordChoice[]; total: number; page: number; pageSize: number }
export function validRecordChoices(value: unknown, kind: 'projects' | 'employees', page: number, customerId?: string): RecordChoicePage {
  if (!recordPage(value, page, 25)) throw new Error('Die Auswahlliste ist unvollständig. Suchen Sie erneut.');
  const items = value.items.map(item => {
    if (!recordObject(item) || !recordId(item['id']) || !recordRevision(item['revision']) || !recordText(item['name'], 240, true)
      || (customerId !== undefined && item['customerId'] !== customerId) || kind === 'employees' && typeof item['active'] !== 'boolean')
      throw new Error('Die Auswahlliste enthält einen falschen oder unvollständigen Datensatz.');
    return { id: item['id'], name: item['name'], revision: item['revision'],
      ...(typeof item['number'] === 'string' ? { number: item['number'] } : {}),
      ...(typeof item['customerId'] === 'string' ? { customerId: item['customerId'] } : {}),
      ...(kind === 'employees' ? { active: item['active'] as boolean } : {}) };
  });
  requireUnique(items.map(item => item.id), 'Die Auswahlliste enthält doppelte Datensätze.');
  return { items, total: value.total, page, pageSize: 25 };
}
