import { describe, expect, it } from 'vitest';
import { CommunicationEntry, CommunicationItem, CommunicationRequest, CustomerJournal, validCommunicationItem, validCustomerJournal, validJournalChange } from '../src/app/core/customer-journal.models';
import { validRecordChoices } from '../src/app/core/ledger-contracts';

const customer = '71000000-0000-4000-8000-000000000001', tenant = '71000000-0000-4000-8000-000000000002';
const actor = '71000000-0000-4000-8000-000000000003', entryId = '71000000-0000-4000-8000-000000000004', other = '71000000-0000-4000-8000-000000000005';
const now = '2026-10-22T08:00:00+00:00', later = '2026-10-22T09:00:00Z';
const request = (): CommunicationRequest => ({ channel: 'Telephone', direction: 'Incoming', occurredAt: '2026-10-21T12:00:00Z', subject: 'Dachtermin', text: 'Termin abgestimmt.', contactId: null, projectId: null });
const entry = (): CommunicationEntry => ({ id: entryId, tenantId: tenant, revision: 1, createdAt: now, updatedAt: now, archived: false,
  customerId: customer, customerName: 'Hausverwaltung', channel: 'Telephone', direction: 'Incoming', occurredAt: request().occurredAt,
  subject: request().subject, text: request().text, contact: null, project: null, recordedAt: now, recordedBy: actor, digest: 'a'.repeat(64) });
const item = (): CommunicationItem => ({ entry: entry(), voiding: null });
const book = (): CustomerJournal => ({ customerId: customer, tenantId: tenant, customerName: 'Hausverwaltung', customerRevision: 2, customerArchived: false, timeZone: 'Europe/Berlin', items: [item()], total: 1, page: 1, pageSize: 20 });
const voided = (): CommunicationItem => ({ entry: entry(), voiding: { id: other, tenantId: tenant, revision: 1, createdAt: later, updatedAt: later, archived: false,
  customerId: customer, communicationId: entryId, sourceDigest: 'a'.repeat(64), reason: 'Falscher Termin.', voidedAt: later, voidedBy: actor, digest: 'b'.repeat(64) } });

describe('customer journal response boundaries', () => {
  it('accepts retained originals and separate voidings without rewriting history', () => {
    expect(validCustomerJournal(book(), customer, tenant, 1).items[0].entry.text).toBe('Termin abgestimmt.');
    expect(validCommunicationItem(voided(), customer, tenant).entry).toEqual(entry());
  });
  it('rejects wrong customer, business, revision, timezone and missing pagination', () => {
    for (const change of [{ customerId: other }, { tenantId: other }, { customerRevision: 0 }, { timeZone: 'Invalid/Zone' }, { page: 2 }, { total: 3 }, { pageSize: 25 }])
      expect(() => validCustomerJournal({ ...book(), ...change }, customer, tenant, 1)).toThrow('nicht eindeutig');
  });
  it('requires an explicit boolean archive status while retaining historical customer journals', () => {
    expect(validCustomerJournal({ ...book(), customerArchived: true }, customer, tenant, 1).items).toEqual(book().items);
    for (const customerArchived of [undefined, null, 'false', 0])
      expect(() => validCustomerJournal({ ...book(), customerArchived }, customer, tenant, 1)).toThrow('nicht eindeutig');
  });
  it('rejects non-UTC, nonexistent, future relative to recording, and subsecond event times', () => {
    for (const occurredAt of ['2026-02-30T08:00:00Z', '2026-10-22T12:00:00+02:00', '2026-10-22T12:00:00Z', '2026-10-21T12:00:00.001Z'])
      expect(() => validCommunicationItem({ ...item(), entry: { ...entry(), occurredAt } }, customer, tenant)).toThrow('Zeitpunkte');
  });
  it('rejects external notes, malformed snapshots and edited original revisions', () => {
    for (const change of [{ channel: 'Note', direction: 'Outgoing' }, { channel: 'Sms' }, { revision: 2 }, { text: '' }, { digest: 'wrong' }])
      expect(() => validCommunicationItem({ ...item(), entry: { ...entry(), ...change } }, customer, tenant)).toThrow();
    expect(() => validCommunicationItem({ ...item(), entry: { ...entry(), contact: { id: other, name: 'Kontakt' } } }, customer, tenant)).toThrow('Ansprechpartner');
    expect(() => validCommunicationItem({ ...item(), entry: { ...entry(), project: { id: other, name: 'Projekt' } } }, customer, tenant)).toThrow('Projekt');
  });
  it('rejects duplicate originals and backwards chronological pages', () => {
    expect(() => validCustomerJournal({ ...book(), total: 2, items: [item(), item()] }, customer, tenant, 1)).toThrow('doppelte');
    const newer = { entry: { ...entry(), id: other, occurredAt: '2026-10-22T07:00:00Z' }, voiding: null };
    expect(() => validCustomerJournal({ ...book(), total: 2, items: [item(), newer] }, customer, tenant, 1)).toThrow('Reihenfolge');
  });
  it('binds voids to the original digest, customer, actor and chronology', () => {
    for (const change of [{ sourceDigest: 'c'.repeat(64) }, { communicationId: actor }, { customerId: actor }, { voidedBy: '' }, { reason: '' }, { voidedAt: '2026-10-20T08:00:00Z' }])
      expect(() => validCommunicationItem({ ...voided(), voiding: { ...voided().voiding, ...change } }, customer, tenant)).toThrow('Stornierung');
  });
  it('confirms creation fields, chosen links and exactly the next customer revision', () => {
    const before = { ...book(), items: [], total: 0 }, after = { ...book(), customerRevision: 3, ...item() };
    expect(validJournalChange(after, before, request()).entry.id).toBe(entryId);
    for (const change of [{ subject: 'Andere Eingabe' }, { direction: 'Outgoing' }, { occurredAt: '2026-10-20T12:00:00Z' }])
      expect(() => validJournalChange({ ...after, entry: { ...entry(), ...change } }, before, request())).toThrow('Eingabe');
    expect(() => validJournalChange(after, before, { ...request(), contactId: other })).toThrow('Eingabe');
    expect(() => validJournalChange({ ...after, customerRevision: 4 }, before, request())).toThrow('Kundenstand');
  });
  it('compares immutable values independent of JSON property order during voiding', () => {
    const after = { ...book(), customerRevision: 3, ...voided() };
    const reordered = Object.fromEntries(Object.entries(entry()).reverse());
    expect(validJournalChange({ ...after, entry: reordered }, book(), { sourceDigest: entry().digest, reason: 'Falscher Termin.' }, item()).voiding).not.toBeNull();
    expect(() => validJournalChange({ ...after, entry: { ...entry(), text: 'Neu geschrieben' } }, book(), { sourceDigest: entry().digest, reason: 'Falscher Termin.' }, item())).toThrow('Original');
    expect(() => validJournalChange(after, book(), { sourceDigest: entry().digest, reason: 'Anderer Grund' }, item())).toThrow('Grund');
  });
});

describe('record pickers', () => {
  it('requires customer-scoped projects and complete active-employee state', () => {
    const page = { items: [{ id: other, revision: 1, name: 'Dachprojekt', customerId: customer }], total: 1, page: 1, pageSize: 25 };
    expect(validRecordChoices(page, 'projects', 1, customer).items[0].id).toBe(other);
    expect(() => validRecordChoices(page, 'projects', 1, actor)).toThrow('falschen');
    expect(() => validRecordChoices(page, 'employees', 1)).toThrow('unvollständigen');
    expect(validRecordChoices({ ...page, items: [{ ...page.items[0], active: false }] }, 'employees', 1).items[0].active).toBe(false);
  });
});
