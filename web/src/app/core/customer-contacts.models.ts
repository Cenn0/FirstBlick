export interface CustomerContactDetails { name: string; function: string; email: string; phone: string; notes: string; isPrimary: boolean }
export interface CustomerContact extends CustomerContactDetails {
  id: string; archived: boolean; createdBy: string; createdAt: string; updatedBy: string; updatedAt: string;
  archivedBy: string | null; archivedAt: string | null;
}
export interface CustomerContactsView { customerId: string; tenantId: string; customerName: string; revision: number; contacts: CustomerContact[] }
export type CustomerContactAction = 'add' | 'update' | 'archive' | 'restore';
export type CustomerContactChangeKind = 'Created' | 'Updated' | 'PrimaryCleared' | 'Archived' | 'Restored';
export interface CustomerContactHistoryEntry {
  id: string; tenantId: string; revision: number; archived: boolean; createdAt: string; updatedAt: string;
  customerId: string; contactId: string; customerRevision: number; changeKind: CustomerContactChangeKind;
  before: CustomerContact | null; after: CustomerContact; recordedBy: string; recordedByName: string; recordedAt: string; digest: string;
}
export interface CustomerContactHistoryView {
  customerId: string; tenantId: string; contactId: string; customerName: string; customerRevision: number;
  current: CustomerContact; items: CustomerContactHistoryEntry[]; total: number; page: number; pageSize: number;
}
export const blankCustomerContact = (): CustomerContactDetails => ({ name: '', function: '', email: '', phone: '', notes: '', isPrimary: false });
const guid = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
const text = (value: unknown, max: number, required = false): value is string => typeof value === 'string' && value.length <= max && !value.includes('\0') && (!required || !!value.trim());
const instant = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value) && Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 19) === value.slice(0, 19);
const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);
const fields: (keyof CustomerContact)[] = ['id', 'name', 'function', 'email', 'phone', 'notes', 'isPrimary', 'archived', 'createdBy', 'createdAt', 'updatedBy', 'updatedAt', 'archivedBy', 'archivedAt'];
const sameContact = (a: CustomerContact, b: CustomerContact): boolean => fields.every(field => a[field] === b[field]);

export function validCustomerContacts(value: unknown, customerId: string, tenantId: string): CustomerContactsView {
  if (!object(value) || value['customerId'] !== customerId || value['tenantId'] !== tenantId || !text(value['customerName'], 240, true)
    || !Number.isSafeInteger(value['revision']) || Number(value['revision']) < 1 || !Array.isArray(value['contacts']) || value['contacts'].length > 100)
    throw new Error('Die Ansprechpartner gehören nicht eindeutig zum ausgewählten Kunden und Betrieb.');
  const contacts = value['contacts'];
  for (const row of contacts) {
    if (!object(row) || !guid(row['id']) || !text(row['name'], 240, true) || !text(row['function'], 240)
      || !text(row['email'], 254) || !text(row['phone'], 80) || !text(row['notes'], 2000)
      || typeof row['isPrimary'] !== 'boolean' || typeof row['archived'] !== 'boolean'
      || !guid(row['createdBy']) || !guid(row['updatedBy']) || !instant(row['createdAt']) || !instant(row['updatedAt'])
      || Date.parse(row['createdAt']) > Date.parse(row['updatedAt']))
      throw new Error('Der Server hat unvollständige Ansprechpartner geliefert. Bitte laden Sie den Kunden erneut.');
    if (row['archived'] ? row['isPrimary'] || !guid(row['archivedBy']) || !instant(row['archivedAt'])
      || row['archivedBy'] !== row['updatedBy'] || row['archivedAt'] !== row['updatedAt']
      : row['archivedBy'] !== null || row['archivedAt'] !== null)
      throw new Error('Die Archivangaben des Ansprechpartners sind widersprüchlich.');
  }
  if (new Set(contacts.map(x => x.id.toLowerCase())).size !== contacts.length || contacts.filter(x => x.isPrimary).length > 1)
    throw new Error('Die Ansprechpartner enthalten doppelte Kennungen oder mehrere Hauptkontakte.');
  return value as unknown as CustomerContactsView;
}

export function validCustomerContactChange(value: unknown, before: CustomerContactsView, action: CustomerContactAction,
  id?: string, details?: CustomerContactDetails): CustomerContactsView {
  const after = validCustomerContacts(value, before.customerId, before.tenantId);
  if (after.revision !== before.revision + 1 || after.contacts.length !== before.contacts.length + (action === 'add' ? 1 : 0)
    || before.contacts.some(contact => !after.contacts.some(x => x.id === contact.id)))
    throw new Error('Der neue Kundenstand bestätigt die Änderung nicht eindeutig. Bitte laden Sie erneut.');
  const target = action === 'add' ? after.contacts.find(x => !before.contacts.some(previous => previous.id === x.id)) : after.contacts.find(x => x.id === id);
  if (!target) throw new Error('Der geänderte Ansprechpartner fehlt in der Serverantwort.');
  if (action === 'add' || action === 'update') {
    if (!details || target.archived || target.name !== details.name.trim() || target.function !== details.function.trim()
      || target.email !== details.email.trim() || target.phone !== details.phone.trim() || target.notes !== details.notes || target.isPrimary !== details.isPrimary)
      throw new Error('Die gespeicherten Kontaktdaten stimmen nicht mit Ihrer Eingabe überein.');
  } else if (target.archived !== (action === 'archive') || target.isPrimary)
    throw new Error('Der Archivstatus wurde nicht wie angefordert bestätigt.');
  for (const old of before.contacts) {
    const current = after.contacts.find(x => x.id === old.id)!;
    if (old.createdBy !== current.createdBy || old.createdAt !== current.createdAt || Date.parse(current.updatedAt) < Date.parse(old.updatedAt))
      throw new Error('Die ursprüngliche Kontaktidentität wurde unerwartet verändert.');
    if (old.id !== target.id && !sameContact(old, current)) {
      const allowed = !!details?.isPrimary && old.isPrimary && !old.archived && !current.isPrimary;
      const preserved = { ...old, isPrimary: current.isPrimary, updatedBy: current.updatedBy, updatedAt: current.updatedAt };
      if (!allowed || !sameContact(preserved, current))
        throw new Error('Andere Ansprechpartner wurden unerwartet verändert.');
    }
    if (old.id === target.id && (action === 'archive' || action === 'restore')) {
      for (const field of ['name', 'function', 'email', 'phone', 'notes'] as const)
        if (old[field] !== current[field]) throw new Error('Die Archivaktion hat Kontaktdaten verändert.');
    }
  }
  return after;
}

export function validCustomerContactHistory(value: unknown, book: CustomerContactsView, contactId: string,
  page: number): CustomerContactHistoryView {
  const selected = book.contacts.find(contact => contact.id === contactId);
  if (!selected || !object(value) || value['customerId'] !== book.customerId || value['tenantId'] !== book.tenantId
    || value['contactId'] !== contactId || value['customerRevision'] !== book.revision || value['customerName'] !== book.customerName
    || value['page'] !== page || value['pageSize'] !== 20 || !Number.isSafeInteger(value['total']) || Number(value['total']) < 0
    || !Array.isArray(value['items']) || value['items'].length !== Math.min(20, Math.max(0, Number(value['total']) - (page - 1) * 20)))
    throw new Error('Der Änderungsverlauf gehört nicht zum geöffneten Kontaktstand. Bitte laden Sie den Kunden erneut.');
  const validSnapshot = (snapshot: unknown): CustomerContact => validCustomerContacts({ ...book, contacts: [snapshot] }, book.customerId, book.tenantId).contacts[0];
  const current = validSnapshot(value['current']);
  if (!sameContact(current, selected)) throw new Error('Der aktuelle Kontaktstand hat sich geändert. Bitte laden Sie den Kunden erneut.');
  let newer: CustomerContactHistoryEntry | undefined;
  const identities = new Set<string>();
  for (const row of value['items']) {
    if (!object(row) || !guid(row['id']) || identities.has(row['id']) || row['tenantId'] !== book.tenantId
      || row['customerId'] !== book.customerId || row['contactId'] !== contactId || row['revision'] !== 1 || row['archived'] !== false
      || !Number.isSafeInteger(row['customerRevision']) || Number(row['customerRevision']) < 2 || Number(row['customerRevision']) > book.revision
      || !guid(row['recordedBy']) || !text(row['recordedByName'], 160, true) || !instant(row['recordedAt'])
      || row['createdAt'] !== row['recordedAt'] || !instant(row['updatedAt']) || Date.parse(row['updatedAt']) < Date.parse(row['recordedAt'])
      || typeof row['digest'] !== 'string' || !/^[a-f0-9]{64}$/.test(row['digest'])
      || !['Created', 'Updated', 'PrimaryCleared', 'Archived', 'Restored'].includes(String(row['changeKind'])))
      throw new Error('Der Änderungsverlauf enthält unvollständige oder widersprüchliche Nachweise.');
    identities.add(row['id']);
    const after = validSnapshot(row['after']);
    const before = row['before'] === null ? null : validSnapshot(row['before']);
    if (after.id !== contactId || after.updatedBy !== row['recordedBy'] || after.updatedAt !== row['recordedAt']
      || after.createdAt !== current.createdAt || after.createdBy !== current.createdBy
      || Date.parse(after.updatedAt) > Date.parse(current.updatedAt))
      throw new Error('Der Änderungsverlauf bestätigt nicht die ursprüngliche Kontaktidentität.');
    if (row['changeKind'] === 'Created') {
      if (before !== null || after.archived || after.createdBy !== row['recordedBy'] || after.createdAt !== row['recordedAt'])
        throw new Error('Der Anlagebeleg enthält widersprüchliche ursprüngliche Werte.');
    } else {
      if (!before || before.id !== after.id || before.createdAt !== after.createdAt || before.createdBy !== after.createdBy
        || Date.parse(before.updatedAt) > Date.parse(after.updatedAt)) throw new Error('Die früheren Kontaktwerte fehlen oder wurden verändert.');
      const changed = { ...before, updatedBy: after.updatedBy, updatedAt: after.updatedAt };
      const valid = row['changeKind'] === 'Updated' ? !before.archived && !after.archived
        : row['changeKind'] === 'PrimaryCleared' ? before.isPrimary && !before.archived && sameContact({ ...changed, isPrimary: false }, after)
        : row['changeKind'] === 'Archived' ? !before.archived && sameContact({ ...changed, archived: true, isPrimary: false, archivedBy: after.updatedBy, archivedAt: after.updatedAt }, after)
        : before.archived && sameContact({ ...changed, archived: false, isPrimary: false, archivedBy: null, archivedAt: null }, after);
      if (!valid) throw new Error('Die Kontaktwerte passen nicht zur angegebenen Änderungsart.');
    }
    const entry = row as unknown as CustomerContactHistoryEntry;
    if (newer && (entry.customerRevision >= newer.customerRevision || !newer.before || !sameContact(after, newer.before)))
      throw new Error('Die Kontaktänderungen bilden keine lückenlose Folge.');
    if (!newer && page === 1 && !sameContact(after, current)) throw new Error('Der letzte Änderungsbeleg passt nicht zum aktuellen Kontakt.');
    newer = entry;
  }
  return value as unknown as CustomerContactHistoryView;
}

export function contactChangeLabel(kind: CustomerContactChangeKind): string {
  return ({ Created: 'Angelegt', Updated: 'Kontaktdaten gespeichert', PrimaryCleared: 'Hauptkontakt neu zugeordnet',
    Archived: 'Archiviert', Restored: 'Wiederhergestellt' })[kind];
}

export function contactFieldChanges(entry: CustomerContactHistoryEntry): { label: string; before: string; after: string }[] {
  const labels = { name: 'Name', function: 'Funktion', email: 'E-Mail', phone: 'Telefon', notes: 'Hinweise', isPrimary: 'Hauptkontakt', archived: 'Archiviert' } as const;
  const display = (value: string | boolean | undefined): string => value === undefined ? 'Nicht vorhanden' : typeof value === 'boolean' ? value ? 'Ja' : 'Nein' : value || 'Leer';
  return (Object.keys(labels) as (keyof typeof labels)[]).filter(field => entry.before === null || entry.before[field] !== entry.after[field])
    .map(field => ({ label: labels[field], before: display(entry.before?.[field]), after: display(entry.after[field]) }));
}
