import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';
import { CustomerContact, CustomerContactAction, CustomerContactDetails, CustomerContactsView, blankCustomerContact,
  CustomerContactHistoryView, contactChangeLabel, contactFieldChanges, validCustomerContactHistory,
  validCustomerContactChange, validCustomerContacts } from '../core/customer-contacts.models';

@Component({
  selector: 'app-customer-contacts', imports: [FormsModule], templateUrl: './customer-contacts.component.html',
  styles: [':host{display:block;border-top:1px solid var(--line,#ddd);margin-top:1.5rem;padding-top:1.5rem}.contacts-heading,.actions{display:flex;gap:.75rem;align-items:center;justify-content:space-between;flex-wrap:wrap}.contact-list{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,270px),1fr));gap:1rem;margin:1rem 0}.contact-card{border:1px solid var(--line,#ddd);border-radius:.6rem;padding:1rem;min-width:0}.contact-card h4{margin:0 0 .5rem}.contact-card p{margin:.4rem 0;overflow-wrap:anywhere}.contact-card .notes{white-space:pre-wrap}.contact-card[data-archived=true]{background:var(--surface-alt,#f6f6f6)}.contact-editor{margin:1rem 0;padding:1rem;border:1px solid var(--line,#ddd);border-radius:.6rem}.contact-editor input,.contact-editor textarea{max-width:100%}.checkbox-label input{width:auto}.contact-editor .actions{margin-top:1rem;justify-content:flex-start}']
})
export class CustomerContactsComponent {
  readonly item = input.required<Entity>(); readonly completed = output<{ kind: string; id: string; message: string }>();
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly book = signal<CustomerContactsView | null>(null); readonly busy = signal(false); readonly loading = signal(false); readonly error = signal('');
  readonly editing = signal<string | null>(null); readonly showArchived = signal(false); readonly search = signal('');
  readonly history = signal<CustomerContactHistoryView | null>(null); readonly historyContact = signal<string | null>(null);
  readonly historyLoading = signal(false); readonly historyError = signal('');
  readonly contactChangeLabel = contactChangeLabel; readonly contactFieldChanges = contactFieldChanges;
  readonly writable = computed(() => this.auth.can('customers.write') && this.auth.hasModule('core') && !this.auth.recoveryHold());
  readonly visible = computed(() => {
    const search = this.search().trim().toLocaleLowerCase('de-DE');
    return (this.book()?.contacts || []).filter(contact => (this.showArchived() || !contact.archived)
      && (!search || [contact.name, contact.function, contact.email, contact.phone].join(' ').toLocaleLowerCase('de-DE').includes(search)))
      .sort((a, b) => Number(a.archived) - Number(b.archived) || Number(b.isPrimary) - Number(a.isPrimary) || a.name.localeCompare(b.name, 'de-DE'));
  });
  form: CustomerContactDetails = blankCustomerContact(); private generation = 0; private historyGeneration = 0;

  constructor() {
    effect(() => { this.item().id; this.item().revision; this.auth.accessSignature(); untracked(() => this.reset()); });
    inject(DestroyRef).onDestroy(() => { this.generation++; this.historyGeneration++; });
  }

  private reset(): void {
    this.generation++; this.book.set(null); this.editing.set(null); this.form = blankCustomerContact(); this.error.set('');
    this.closeHistory();
    this.search.set(''); this.showArchived.set(false); this.busy.set(false); this.loading.set(false);
    if (this.auth.can('customers.read')) void this.load();
  }

  async load(): Promise<void> {
    if (!this.auth.can('customers.read') || this.busy()) return;
    const id = this.item().id, tenant = this.auth.activeTenantId(), access = this.auth.accessSignature(), generation = ++this.generation;
    const current = () => generation === this.generation && this.item().id === id && access === this.auth.accessSignature();
    this.loading.set(true); this.error.set(''); this.book.set(null); this.editing.set(null);
    this.closeHistory();
    try {
      const result = await this.api.get<unknown>(`customers/${encodeURIComponent(id)}/contacts`);
      if (!current()) return;
      this.book.set(validCustomerContacts(result, id, tenant));
    } catch (error) { if (current()) { this.error.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (current()) this.loading.set(false); }
  }

  edit(contact?: CustomerContact): void {
    if (!this.writable() || this.busy() || !this.book() || contact?.archived) return;
    this.error.set(''); this.editing.set(contact?.id || '');
    this.form = contact ? { name: contact.name, function: contact.function, email: contact.email, phone: contact.phone, notes: contact.notes, isPrimary: contact.isPrimary } : blankCustomerContact();
  }
  cancel(): void { if (!this.busy()) { this.editing.set(null); this.form = blankCustomerContact(); } }

  async save(): Promise<void> {
    if (this.editing() === null || !this.form.name.trim()) return;
    const details = { ...this.form, name: this.form.name.trim(), function: this.form.function.trim(), email: this.form.email.trim(), phone: this.form.phone.trim() };
    await this.act(this.editing() ? 'update' : 'add', this.editing() || undefined, details);
  }

  async act(action: CustomerContactAction, id?: string, details?: CustomerContactDetails): Promise<void> {
    const before = this.book();
    if (!before || this.busy() || !this.writable()) return;
    if (action !== 'add' && !before.contacts.some(x => x.id === id)) return;
    if (action === 'add' && before.contacts.length >= 100) return;
    const generation = this.generation, access = this.auth.accessSignature();
    const current = () => generation === this.generation && access === this.auth.accessSignature() && this.item().id === before.customerId;
    this.busy.set(true); this.error.set('');
    this.closeHistory();
    try {
      const suffix = action === 'add' ? '' : `/${encodeURIComponent(id!)}/${action}`;
      const result = await this.api.command<unknown>(`customers/${encodeURIComponent(before.customerId)}/contacts${suffix}`, details || {}, before.revision);
      if (!current()) return;
      this.book.set(validCustomerContactChange(result, before, action, id, details)); this.editing.set(null);
      this.completed.emit({ kind: 'customers', id: before.customerId, message: action === 'archive' ? 'Ansprechpartner archiviert.'
        : action === 'restore' ? 'Ansprechpartner wiederhergestellt.' : 'Ansprechpartner gespeichert.' });
    } catch (error) { if (current()) { this.error.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (current()) this.busy.set(false); }
  }

  closeHistory(): void {
    this.historyGeneration++; this.history.set(null); this.historyContact.set(null); this.historyError.set(''); this.historyLoading.set(false);
  }

  async openHistory(contactId: string, page = 1): Promise<void> {
    const book = this.book();
    if (!book || !book.contacts.some(contact => contact.id === contactId) || !this.auth.can('customers.read') || this.busy()) return;
    const generation = ++this.historyGeneration, access = this.auth.accessSignature();
    const current = () => generation === this.historyGeneration && access === this.auth.accessSignature()
      && this.book()?.revision === book.revision && this.item().id === book.customerId;
    this.history.set(null); this.historyContact.set(contactId); this.historyLoading.set(true); this.historyError.set('');
    try {
      const result = await this.api.get<unknown>(`customers/${encodeURIComponent(book.customerId)}/contacts/${encodeURIComponent(contactId)}/history?page=${page}`);
      if (!current()) return;
      this.history.set(validCustomerContactHistory(result, book, contactId, page));
    } catch (error) { if (current()) { this.historyError.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (current()) this.historyLoading.set(false); }
  }

  private async refreshAccess(error: unknown): Promise<void> { if (error instanceof ApiError && [401, 403].includes(error.status)) await this.auth.refresh().catch(() => undefined); }
  date(value: string): string { return new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)); }
}
