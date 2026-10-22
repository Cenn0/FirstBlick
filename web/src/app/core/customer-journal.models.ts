import { recordDigest, recordEntity, recordId, recordInstant, recordObject, recordPage, recordRevision, recordText, recordZone, requireUnique, sameInstant, sameRecord } from './ledger-contracts';

export type CommunicationChannel = 'Telephone' | 'Email' | 'Meeting' | 'Note';
export type CommunicationDirection = 'Incoming' | 'Outgoing' | 'Internal';
export interface CommunicationContact { id: string; name: string; function: string; email: string; phone: string }
export interface CommunicationProject { id: string; name: string; number: string }
interface JournalEntity { id: string; tenantId: string; revision: number; createdAt: string; updatedAt: string; archived: false }
export interface CommunicationEntry extends JournalEntity {
  customerId: string; customerName: string; channel: CommunicationChannel; direction: CommunicationDirection;
  occurredAt: string; subject: string; text: string; contact: CommunicationContact | null; project: CommunicationProject | null;
  recordedAt: string; recordedBy: string; digest: string;
}
export interface CommunicationVoiding extends JournalEntity {
  customerId: string; communicationId: string; sourceDigest: string; reason: string; voidedAt: string; voidedBy: string; digest: string;
}
export interface CommunicationItem { entry: CommunicationEntry; voiding: CommunicationVoiding | null }
export interface CustomerJournal {
  customerId: string; tenantId: string; customerName: string; customerRevision: number; customerArchived: boolean; timeZone: string;
  items: CommunicationItem[]; total: number; page: number; pageSize: number;
}
export interface CommunicationRequest {
  channel: CommunicationChannel; direction: CommunicationDirection; occurredAt: string; subject: string; text: string;
  contactId: string | null; projectId: string | null;
}
export interface CustomerJournalChange { customerId: string; tenantId: string; customerName: string; customerRevision: number; entry: CommunicationEntry; voiding: CommunicationVoiding | null }
export const communicationChannels: Record<CommunicationChannel, string> = { Telephone: 'Telefonat', Email: 'E-Mail', Meeting: 'Besprechung', Note: 'Interne Notiz' };
export const communicationDirections: Record<CommunicationDirection, string> = { Incoming: 'Eingehend', Outgoing: 'Ausgehend', Internal: 'Intern' };

export function validCommunicationItem(value: unknown, customerId: string, tenantId: string): CommunicationItem {
  if (!recordObject(value) || !recordEntity(value['entry'], tenantId)) throw new Error('Der Kommunikationseintrag ist unvollständig oder gehört zu einem anderen Betrieb.');
  const entry = value['entry'];
  if (entry['revision'] !== 1 || entry['customerId'] !== customerId || !recordText(entry['customerName'], 240, true)
    || !Object.hasOwn(communicationChannels, String(entry['channel'])) || !Object.hasOwn(communicationDirections, String(entry['direction']))
    || entry['channel'] === 'Note' && entry['direction'] !== 'Internal' || !recordInstant(entry['occurredAt'], true)
    || !recordText(entry['subject'], 240, true) || !recordText(entry['text'], 20_000, true) || !recordInstant(entry['recordedAt'])
    || !recordId(entry['recordedBy']) || !recordDigest(entry['digest']) || Date.parse(entry['occurredAt']) > Date.parse(entry['recordedAt']))
    throw new Error('Der Kommunikationseintrag enthält ungültige Angaben oder Zeitpunkte.');
  const contact = entry['contact'], project = entry['project'];
  if (contact !== null && (!recordObject(contact) || !recordId(contact['id']) || !recordText(contact['name'], 240, true)
    || !recordText(contact['function'], 240) || !recordText(contact['email'], 254) || !recordText(contact['phone'], 80)))
    throw new Error('Der damalige Ansprechpartner ist nicht vollständig belegt.');
  if (project !== null && (!recordObject(project) || !recordId(project['id']) || !recordText(project['name'], 240, true) || !recordText(project['number'], 80)))
    throw new Error('Das damalige Projekt ist nicht vollständig belegt.');
  const voiding = value['voiding'];
  if (voiding !== null && (!recordEntity(voiding, tenantId) || voiding['revision'] !== 1 || voiding['customerId'] !== customerId
    || voiding['communicationId'] !== entry['id'] || voiding['sourceDigest'] !== entry['digest'] || voiding['id'] === entry['id']
    || !recordText(voiding['reason'], 2000, true) || !recordInstant(voiding['voidedAt']) || !recordId(voiding['voidedBy'])
    || !recordDigest(voiding['digest']) || Date.parse(voiding['voidedAt']) < Date.parse(entry['recordedAt'])))
    throw new Error('Die Stornierung ist nicht eindeutig an den ursprünglichen Kommunikationseintrag gebunden.');
  return value as unknown as CommunicationItem;
}

const sortKey = (entry: CommunicationEntry): string => `${entry.occurredAt.slice(0, 19)}|${entry.recordedAt.slice(0, 19)}.${(/\.(\d+)/.exec(entry.recordedAt)?.[1] || '').padEnd(7, '0')}|${entry.id}`;
export function validCustomerJournal(value: unknown, customerId: string, tenantId: string, page: number): CustomerJournal {
  if (!recordPage(value, page, 20) || !recordObject(value) || value['customerId'] !== customerId || value['tenantId'] !== tenantId
    || !recordText(value['customerName'], 240, true) || !recordRevision(value['customerRevision']) || typeof value['customerArchived'] !== 'boolean' || !recordZone(value['timeZone']))
    throw new Error('Die Kommunikationsakte gehört nicht eindeutig zum ausgewählten Kunden und Betrieb oder ist unvollständig.');
  const items = value.items.map(item => validCommunicationItem(item, customerId, tenantId));
  requireUnique(items.map(item => item.entry.id), 'Die Kommunikationsakte enthält doppelte Originaleinträge.');
  requireUnique(items.flatMap(item => item.voiding ? [item.voiding.id] : []), 'Die Kommunikationsakte enthält doppelte Stornierungen.');
  if (items.some((item, index) => index > 0 && sortKey(items[index - 1].entry).localeCompare(sortKey(item.entry)) < 0))
    throw new Error('Die Kommunikationsakte ist nicht in der zugesagten zeitlichen Reihenfolge.');
  return value as unknown as CustomerJournal;
}

export function validJournalChange(value: unknown, before: CustomerJournal, request: CommunicationRequest | { sourceDigest: string; reason: string },
  original?: CommunicationItem): CustomerJournalChange {
  if (!recordObject(value) || value['customerId'] !== before.customerId || value['tenantId'] !== before.tenantId
    || value['customerRevision'] !== before.customerRevision + 1 || value['customerName'] !== before.customerName)
    throw new Error('Das Ergebnis bestätigt nicht den angeforderten Kundenstand. Laden Sie die Kommunikationsakte zur Prüfung neu.');
  const item = validCommunicationItem({ entry: value['entry'], voiding: value['voiding'] }, before.customerId, before.tenantId);
  if ('sourceDigest' in request) {
    if (!original || original.voiding || !item.voiding || !sameRecord(item.entry, original.entry)
      || item.voiding.sourceDigest !== request.sourceDigest || item.voiding.reason !== request.reason)
      throw new Error('Die Stornierung verändert das Original oder bestätigt einen anderen Grund. Laden Sie die Akte zur Prüfung neu.');
  } else {
    if (item.voiding || before.items.some(row => row.entry.id === item.entry.id) || item.entry.customerName !== before.customerName
      || item.entry.channel !== request.channel || item.entry.direction !== request.direction || !sameInstant(item.entry.occurredAt, request.occurredAt)
      || item.entry.subject !== request.subject || item.entry.text !== request.text || (item.entry.contact?.id ?? null) !== request.contactId
      || (item.entry.project?.id ?? null) !== request.projectId)
      throw new Error('Der gespeicherte Kommunikationseintrag bestätigt andere Angaben als die gesendete Eingabe.');
  }
  return value as unknown as CustomerJournalChange;
}
