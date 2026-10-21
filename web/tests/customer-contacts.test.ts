import { describe, expect, it } from 'vitest';
import { CustomerContact, CustomerContactsView, blankCustomerContact, validCustomerContactChange, validCustomerContacts } from '../src/app/core/customer-contacts.models';

const customerId = '70000000-0000-4000-8000-000000000001';
const tenantId = '70000000-0000-4000-8000-000000000002';
const actor = '70000000-0000-4000-8000-000000000003';
const contactId = '70000000-0000-4000-8000-000000000004';
const now = '2026-10-21T12:00:00+00:00';
const later = '2026-10-21T13:00:00+00:00';
const contact = (): CustomerContact => ({ id: contactId, ...blankCustomerContact(), name: 'Objektleitung', function: 'Verwaltung',
  archived: false, createdBy: actor, createdAt: now, updatedBy: actor, updatedAt: now, archivedBy: null, archivedAt: null });
const book = (): CustomerContactsView => ({ customerId, tenantId, customerName: 'Hausverwaltung', revision: 2, contacts: [contact()] });

describe('customer contact response integrity', () => {
  it('accepts empty and bounded contact books while binding customer and business', () => {
    expect(validCustomerContacts(book(), customerId, tenantId).contacts).toHaveLength(1);
    expect(validCustomerContacts({ ...book(), contacts: [] }, customerId, tenantId).contacts).toEqual([]);
    expect(() => validCustomerContacts(book(), contactId, tenantId)).toThrow('ausgewählten');
    expect(() => validCustomerContacts(book(), customerId, actor)).toThrow('ausgewählten');
    expect(() => validCustomerContacts({ ...book(), contacts: null }, customerId, tenantId)).toThrow('ausgewählten');
  });
  it('rejects multiple primary contacts, duplicate identities and invalid archive evidence', () => {
    expect(() => validCustomerContacts({ ...book(), contacts: [contact(), contact()] }, customerId, tenantId)).toThrow('doppelte');
    expect(() => validCustomerContacts({ ...book(), contacts: [{ ...contact(), isPrimary: true }, { ...contact(), id: actor, isPrimary: true }] }, customerId, tenantId)).toThrow('Hauptkontakte');
    expect(() => validCustomerContacts({ ...book(), contacts: [{ ...contact(), archived: true }] }, customerId, tenantId)).toThrow('Archivangaben');
    expect(() => validCustomerContacts({ ...book(), contacts: [{ ...contact(), archivedAt: now }] }, customerId, tenantId)).toThrow('Archivangaben');
  });
  it('rejects malformed types and nonexistent or non-UTC timestamp values', () => {
    for (const invalid of [{ name: '' }, { function: null }, { isPrimary: 'false' }, { id: 'bad' }, { updatedAt: '2026-02-30T10:00:00Z' },
      { updatedAt: '2026-10-21T13:00:00+01:00' }, { updatedAt: '2026-10-20T13:00:00Z' }])
      expect(() => validCustomerContacts({ ...book(), contacts: [{ ...contact(), ...invalid }] }, customerId, tenantId)).toThrow('unvollständige');
  });
  it('requires a new identity and matching form fields for additions', () => {
    const before = book(), details = { ...blankCustomerContact(), name: 'Buchhaltung', isPrimary: true };
    const added = { ...contact(), ...details, id: actor, createdAt: later, updatedAt: later };
    expect(validCustomerContactChange({ ...before, revision: 3, contacts: [...before.contacts, added] }, before, 'add', undefined, details).contacts).toHaveLength(2);
    expect(() => validCustomerContactChange({ ...before, revision: 3 }, before, 'add', undefined, details)).toThrow('nicht eindeutig');
    expect(() => validCustomerContactChange({ ...before, revision: 3, contacts: [...before.contacts, { ...added, name: 'Andere Eingabe' }] }, before, 'add', undefined, details)).toThrow('Eingabe');
  });
  it('permits explicit primary changes and retains other contact data and identity', () => {
    const first = { ...contact(), isPrimary: true }, second = { ...contact(), id: actor, name: 'Buchhaltung' };
    const before = { ...book(), contacts: [first, second] };
    const details = { ...blankCustomerContact(), name: 'Buchhaltung', function: 'Verwaltung', isPrimary: true };
    const after = { ...before, revision: 3, contacts: [{ ...first, isPrimary: false, updatedAt: later }, { ...second, ...details, updatedAt: later }] };
    expect(validCustomerContactChange(after, before, 'update', actor, details).contacts[1].isPrimary).toBe(true);
    expect(() => validCustomerContactChange({ ...after, contacts: [{ ...after.contacts[0], email: 'changed@example.test' }, after.contacts[1]] }, before, 'update', actor, details)).toThrow('Andere Ansprechpartner');
    expect(() => validCustomerContactChange({ ...after, contacts: [after.contacts[0], { ...after.contacts[1], createdBy: tenantId }] }, before, 'update', actor, details)).toThrow('Kontaktidentität');
  });
  it('retains contact values when archiving and restoring without an automatic primary', () => {
    const before = book(); const archived = { ...contact(), archived: true, updatedAt: later, archivedAt: later, archivedBy: actor };
    const after = { ...before, revision: 3, contacts: [archived] };
    expect(validCustomerContactChange(after, before, 'archive', contactId).contacts[0].archived).toBe(true);
    expect(() => validCustomerContactChange({ ...after, contacts: [{ ...archived, phone: 'other' }] }, before, 'archive', contactId)).toThrow('Kontaktdaten');
    expect(validCustomerContactChange({ ...after, revision: 4, contacts: [{ ...archived, archived: false, archivedBy: null, archivedAt: null }] }, after, 'restore', contactId).contacts[0].archived).toBe(false);
    expect(() => validCustomerContactChange(after, before, 'restore', contactId)).toThrow('Archivstatus');
  });
  it('requires exactly the next revision and rejects lost or replaced contacts', () => {
    const before = book();
    for (const revision of [1, 2, 4]) expect(() => validCustomerContactChange({ ...before, revision }, before, 'update', contactId, contact())).toThrow('nicht eindeutig');
    expect(() => validCustomerContactChange({ ...before, revision: 3, contacts: [{ ...contact(), id: actor }] }, before, 'update', actor, contact())).toThrow('nicht eindeutig');
  });
});
