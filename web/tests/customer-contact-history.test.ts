import { describe, expect, it } from 'vitest';
import { CustomerContact, CustomerContactHistoryEntry, CustomerContactHistoryView, CustomerContactsView,
  contactFieldChanges, validCustomerContactHistory } from '../src/app/core/customer-contacts.models';

const customerId = '71000000-0000-4000-8000-000000000001', tenantId = '71000000-0000-4000-8000-000000000002';
const actor = '71000000-0000-4000-8000-000000000003', contactId = '71000000-0000-4000-8000-000000000004';
const now = '2026-10-22T09:00:00Z', later = '2026-10-22T10:00:00Z';
const contact = (): CustomerContact => ({ id: contactId, name: 'Objektleitung', function: 'Verwaltung', email: 'alt@example.test', phone: '', notes: '',
  isPrimary: false, archived: false, createdBy: actor, createdAt: now, updatedBy: actor, updatedAt: now, archivedBy: null, archivedAt: null });
const entry = (): CustomerContactHistoryEntry => ({ id: '71000000-0000-4000-8000-000000000005', tenantId, revision: 1, archived: false,
  createdAt: now, updatedAt: now, customerId, contactId, customerRevision: 2, changeKind: 'Created', before: null, after: contact(),
  recordedAt: now, recordedBy: actor, recordedByName: 'Historischer Name', digest: 'a'.repeat(64) });
function fixture(): { book: CustomerContactsView; history: CustomerContactHistoryView } {
  const original = entry();
  const edit: CustomerContactHistoryEntry = { ...entry(), id: '71000000-0000-4000-8000-000000000006', createdAt: later, updatedAt: later,
    recordedAt: later, customerRevision: 3, changeKind: 'Updated', before: contact(), after: { ...contact(), email: 'neu@example.test', updatedAt: later } };
  const book = { customerId, tenantId, customerName: 'Hausverwaltung', revision: 3, contacts: [edit.after] };
  return { book, history: { customerId, tenantId, contactId, customerName: book.customerName, customerRevision: 3,
    current: edit.after, items: [edit, original], total: 2, page: 1, pageSize: 20 } };
}

describe('retained contact history contracts', () => {
  it('accepts a complete before/after chain and displays only fields actually changed', () => {
    const { book, history } = fixture();
    expect(validCustomerContactHistory(history, book, contactId, 1)).toEqual(history);
    expect(contactFieldChanges(history.items[0])).toEqual([{ label: 'E-Mail', before: 'alt@example.test', after: 'neu@example.test' }]);
    expect(contactFieldChanges(history.items[1]).find(x => x.label === 'Name')?.before).toBe('Nicht vorhanden');
  });
  it('accepts an explicitly absent historical trail without inventing an original creation', () => {
    const { book, history } = fixture();
    expect(validCustomerContactHistory({ ...history, total: 0, items: [] }, book, contactId, 1).items).toEqual([]);
    expect(validCustomerContactHistory({ ...history, total: 1, items: [history.items[0]] }, book, contactId, 1).items[0].before).not.toBeNull();
  });
  it('binds the history to the exact customer, tenant, contact, revision and page', () => {
    const { book, history } = fixture();
    for (const invalid of [{ customerId: actor }, { tenantId: actor }, { contactId: actor }, { customerRevision: 4 }, { page: 2 }, { customerName: 'Fremd' }, { total: 20 }, { pageSize: 100 }])
      expect(() => validCustomerContactHistory({ ...history, ...invalid }, book, contactId, 1)).toThrow('geöffneten Kontaktstand');
  });
  it('rejects changed current contacts, missing links, duplicate entries and swapped ordering', () => {
    const { book, history } = fixture();
    expect(() => validCustomerContactHistory({ ...history, current: { ...history.current, phone: 'falsch' } }, book, contactId, 1)).toThrow('aktuelle Kontaktstand');
    const altered = structuredClone(history); altered.items[0].before!.phone = 'falsch';
    expect(() => validCustomerContactHistory(altered, book, contactId, 1)).toThrow('lückenlose Folge');
    expect(() => validCustomerContactHistory({ ...history, items: [history.items[0], history.items[0]] }, book, contactId, 1)).toThrow('unvollständige');
    expect(() => validCustomerContactHistory({ ...history, items: [...history.items].reverse() }, book, contactId, 1)).toThrow('letzte Änderungsbeleg');
  });
  it('rejects forged identity, actor, timestamp, revision and malformed digest metadata', () => {
    const { book, history } = fixture();
    for (const invalid of [{ recordedBy: contactId }, { recordedAt: now, createdAt: now }, { revision: 2 }, { archived: true },
      { customerRevision: 99 }, { recordedByName: '' }, { digest: 'broken' }, { changeKind: 'Sent' }]) {
      const value = { ...history, items: [{ ...history.items[0], ...invalid }, history.items[1]] };
      expect(() => validCustomerContactHistory(value, book, contactId, 1)).toThrow();
    }
  });
  it('requires the claimed transition while allowing a recorded no-op edit', () => {
    const { book, history } = fixture();
    for (const changeKind of ['Archived', 'Restored', 'PrimaryCleared', 'Created'])
      expect(() => validCustomerContactHistory({ ...history, items: [{ ...history.items[0], changeKind }, history.items[1]] }, book, contactId, 1)).toThrow();
    const noOp = { ...history.items[0], before: { ...history.items[0].after, updatedAt: now } };
    expect(contactFieldChanges(noOp)).toEqual([]);
    expect(validCustomerContactHistory({ ...history, total: 1, items: [noOp] }, book, contactId, 1).items).toHaveLength(1);
  });
});
