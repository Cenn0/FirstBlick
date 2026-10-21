export interface CustomerContactDetails { name: string; function: string; email: string; phone: string; notes: string; isPrimary: boolean }
export interface CustomerContact extends CustomerContactDetails {
  id: string; archived: boolean; createdBy: string; createdAt: string; updatedBy: string; updatedAt: string;
  archivedBy: string | null; archivedAt: string | null;
}
export interface CustomerContactsView { customerId: string; tenantId: string; customerName: string; revision: number; contacts: CustomerContact[] }
export type CustomerContactAction = 'add' | 'update' | 'archive' | 'restore';
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
