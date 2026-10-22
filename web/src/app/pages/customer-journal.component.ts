import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { businessLocalTime, businessTimeChoices, resolveBusinessTime } from '../core/business-time';
import { CustomerContact, validCustomerContacts } from '../core/customer-contacts.models';
import { CommunicationChannel, CommunicationDirection, CommunicationItem, CommunicationRequest, CustomerJournal,
  communicationChannels, communicationDirections, validCustomerJournal, validJournalChange } from '../core/customer-journal.models';
import { RecordChoice } from '../core/ledger-contracts';
import { ApiError, errorMessage } from '../core/transport';
import { LedgerPickerComponent } from './ledger-picker.component';

@Component({
  selector: 'app-customer-journal', imports: [FormsModule, RouterLink, LedgerPickerComponent], templateUrl: './customer-journal.component.html',
  styles: [':host{display:block;border-top:1px solid var(--line,#ddd);margin-top:1.5rem;padding-top:1.5rem}.journal-heading,.journal-actions,.journal-pager{display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:.7rem}.journal-list{display:grid;gap:.8rem;margin:1rem 0}.journal-entry,.journal-editor{border:1px solid var(--line,#ddd);border-radius:.6rem;padding:1rem;min-width:0}.journal-entry[data-voided=true]{background:#f5f6f3}.journal-entry h4{margin:0 0 .5rem}.journal-entry p{overflow-wrap:anywhere;margin-bottom:.6rem}.journal-text{white-space:pre-wrap}.journal-editor{margin:1rem 0}.journal-pager{font-size:.8rem}.journal-editor fieldset{min-width:0}.void-form{margin-top:.8rem;padding-top:.8rem;border-top:1px solid var(--line,#ddd)}.journal-actions{justify-content:flex-start;margin-top:.8rem}.journal-heading p{margin-bottom:.5rem}.journal-entry .badge{margin-right:.4rem}.journal-editor .form-grid{margin-bottom:.8rem}']
})
export class CustomerJournalComponent {
  readonly item = input.required<Entity>(); readonly completed = output<{ kind: string; id: string; message: string }>();
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly opened = signal(false); readonly journal = signal<CustomerJournal | null>(null); readonly error = signal(''); readonly notice = signal('');
  readonly loading = signal(false); readonly busy = signal(false); readonly stale = signal(false); readonly editing = signal(false); readonly voiding = signal<string | null>(null);
  readonly contacts = signal<CustomerContact[]>([]); readonly contactsLoading = signal(false); readonly project = signal<RecordChoice | null>(null);
  readonly readable = computed(() => this.auth.can('customers.read'));
  readonly writable = computed(() => this.auth.can('customers.write') && this.auth.hasModule('core') && !this.auth.recoveryHold() && this.journal()?.customerArchived === false);
  readonly channels = communicationChannels; readonly directions = communicationDirections;
  channel: CommunicationChannel = 'Telephone'; direction: CommunicationDirection = 'Incoming'; occurredLocal = ''; offset = ''; subject = ''; text = ''; contactId = ''; voidReason = ''; confirmed = false;
  private generation = 0; private identity = '';

  constructor() {
    effect(() => { this.item().id; this.item().revision; this.auth.accessSignature(); untracked(() => this.reset()); });
    inject(DestroyRef).onDestroy(() => { this.generation++; });
  }
  private reset(): void {
    const identity = `${this.item().id}|${this.auth.accessSignature()}`, keepOpen = identity === this.identity && this.opened(); this.identity = identity;
    this.generation++; this.opened.set(keepOpen); this.journal.set(null); this.error.set(''); this.notice.set(''); this.loading.set(false); this.busy.set(false); this.stale.set(false); this.clearForm();
    if (keepOpen && this.readable()) void this.load(1, true);
  }
  private clearForm(): void {
    this.editing.set(false); this.voiding.set(null); this.contacts.set([]); this.contactsLoading.set(false); this.project.set(null);
    this.channel = 'Telephone'; this.direction = 'Incoming'; this.occurredLocal = ''; this.offset = ''; this.subject = ''; this.text = ''; this.contactId = ''; this.voidReason = ''; this.confirmed = false;
  }
  private current(generation: number, access: string, id: string): boolean { return generation === this.generation && access === this.auth.accessSignature() && id === this.item().id && this.readable(); }
  async toggle(): Promise<void> {
    if (this.busy()) return;
    if (this.opened()) { this.generation++; this.opened.set(false); this.journal.set(null); this.clearForm(); this.loading.set(false); return; }
    this.opened.set(true); await this.load(1, true);
  }
  async load(page = 1, reset = true): Promise<void> {
    if (!this.readable() || !this.opened() || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), id = this.item().id, tenant = this.auth.activeTenantId();
    const before = this.journal(); this.loading.set(true); this.error.set('');
    if (reset) { this.clearForm(); this.stale.set(false); }
    try {
      const raw = await this.api.get<unknown>(`customers/${id}/communications?page=${page}`);
      if (!this.current(generation, access, id)) return;
      const value = validCustomerJournal(raw, id, tenant, page);
      if (!reset && before && value.customerRevision !== before.customerRevision) {
        this.stale.set(true); this.error.set('Die Kundenakte wurde während des Blätterns geändert. Laden Sie sie vor weiteren Einträgen neu.');
      }
      this.journal.set(value);
    } catch (error) { if (this.current(generation, access, id)) { this.error.set(errorMessage(error)); this.stale.set(true); if (error instanceof ApiError && [403, 404].includes(error.status)) this.journal.set(null); await this.refreshAccess(error); } }
    finally { if (this.current(generation, access, id)) this.loading.set(false); }
  }
  async newEntry(): Promise<void> {
    const book = this.journal(); if (!book || !this.actionAllowed()) return;
    this.clearForm(); this.editing.set(true); this.error.set(''); this.notice.set('');
    this.occurredLocal = businessLocalTime(new Date(Math.floor(Date.now() / 1000) * 1000).toISOString(), book.timeZone);
    const generation = this.generation, access = this.auth.accessSignature(), id = this.item().id;
    this.contactsLoading.set(true);
    try {
      const raw = await this.api.get<unknown>(`customers/${id}/contacts`);
      if (!this.current(generation, access, id) || !this.editing()) return;
      const contacts = validCustomerContacts(raw, id, this.auth.activeTenantId());
      if (contacts.revision !== book.customerRevision) { this.stale.set(true); throw new Error('Die Ansprechpartner oder Kundendaten wurden geändert. Laden Sie die Kommunikationsakte neu.'); }
      this.contacts.set(contacts.contacts.filter(contact => !contact.archived));
    } catch (error) { if (this.current(generation, access, id)) { this.error.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (this.current(generation, access, id)) this.contactsLoading.set(false); }
  }
  channelChanged(): void { if (this.channel === 'Note') this.direction = 'Internal'; this.confirmed = false; }
  choices() { const book = this.journal(); return book ? businessTimeChoices(this.occurredLocal, book.timeZone) : []; }
  actionAllowed(): boolean { return this.writable() && !!this.journal() && !this.busy() && !this.loading() && !this.stale(); }
  saveAllowed(): boolean {
    return this.actionAllowed() && this.editing() && !this.contactsLoading() && !!this.subject.trim() && !!this.text.trim()
      && this.subject.length <= 240 && this.text.length <= 20_000 && this.confirmed && !!this.occurredLocal;
  }
  cancel(): void { if (!this.busy()) this.clearForm(); }
  async save(): Promise<void> {
    const before = this.journal(); if (!before || !this.saveAllowed()) return;
    try {
      const occurredAt = resolveBusinessTime(this.occurredLocal, before.timeZone, this.offset);
      if (Date.parse(occurredAt) > Date.now()) throw new Error('Der dokumentierte Kontakt darf nicht in der Zukunft liegen.');
      const request: CommunicationRequest = { channel: this.channel, direction: this.direction, occurredAt, subject: this.subject.trim(), text: this.text.trim(), contactId: this.contactId || null, projectId: this.project()?.id || null };
      if (request.contactId && !this.contacts().some(contact => contact.id === request.contactId)) throw new Error('Wählen Sie einen aktuellen Ansprechpartner aus der Liste.');
      await this.mutate(request);
    } catch (error) { this.error.set(errorMessage(error)); }
  }
  beginVoid(item: CommunicationItem): void {
    if (!this.actionAllowed() || item.voiding) return;
    this.clearForm(); this.voiding.set(item.entry.id); this.error.set(''); this.notice.set('');
  }
  async voidEntry(item: CommunicationItem): Promise<void> {
    if (!this.actionAllowed() || item.voiding || this.voiding() !== item.entry.id || !this.voidReason.trim() || this.voidReason.length > 2000) return;
    await this.mutate({ sourceDigest: item.entry.digest, reason: this.voidReason.trim() }, item);
  }
  private async mutate(request: CommunicationRequest | { sourceDigest: string; reason: string }, original?: CommunicationItem): Promise<void> {
    const before = this.journal(); if (!before || !this.actionAllowed()) return;
    const generation = this.generation, access = this.auth.accessSignature(), id = before.customerId;
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      const raw = await this.api.command<unknown>(`customers/${id}/communications${original ? `/${original.entry.id}/void` : ''}`, request, before.customerRevision);
      if (!this.current(generation, access, id)) return;
      const changed = validJournalChange(raw, before, request, original);
      this.journal.set({ ...before, customerRevision: changed.customerRevision, items: original
        ? before.items.map(item => item.entry.id === changed.entry.id ? { entry: changed.entry, voiding: changed.voiding } : item)
        : before.items, total: before.total + (original ? 0 : 1) });
      this.clearForm(); this.notice.set(original ? 'Eintrag mit Begründung storniert. Das Original bleibt erhalten.' : 'Kommunikation dokumentiert.');
      this.completed.emit({ kind: 'customers', id, message: this.notice() });
    } catch (error) {
      if (this.current(generation, access, id)) {
        this.error.set(errorMessage(error));
        if (!(error instanceof ApiError) || [409, 412, 503].includes(error.status)) this.stale.set(true);
        await this.refreshAccess(error);
      }
    } finally { if (this.current(generation, access, id)) this.busy.set(false); }
  }
  date(value: string): string { return new Intl.DateTimeFormat('de-DE', { timeZone: this.journal()?.timeZone, dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)); }
  private async refreshAccess(error: unknown): Promise<void> { if (error instanceof ApiError && [401, 403].includes(error.status)) await this.auth.refresh().catch(() => undefined); }
}
