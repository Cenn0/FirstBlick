import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';

interface Submission { digest: string; submittedBy: string; submittedAt: string; content: { employeeUserId?: string; employeeName: string } }
interface Review { reviewerName: string; reviewedAt: string; notes: string }
interface ReportFile { id: string; name: string; contentType: string; size: number; sha256: string; sourceKind: string; sourceId: string }

@Component({
  selector: 'app-diary-report', imports: [FormsModule], templateUrl: './diary-report.component.html',
  styles: [':host{display:block;margin-top:1.5rem}.actions{display:flex;gap:.75rem;flex-wrap:wrap}.review{display:grid;gap:.75rem;margin:1rem 0}.review textarea{width:100%}.consent{display:flex;gap:.6rem;align-items:flex-start;margin:1rem 0}.consent input{width:auto;flex-shrink:0}.notes{white-space:pre-wrap;overflow-wrap:anywhere}']
})
export class DiaryReportComponent {
  readonly item = input.required<Entity>();
  readonly completed = output<{ kind: string; id: string; message: string }>();
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly busy = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly online = signal(navigator.onLine); readonly consent = signal(false); readonly notes = signal('');
  readonly status = computed(() => String(this.item()['status'] || 'Draft'));
  readonly submission = computed(() => (this.item()['submission'] || null) as Submission | null);
  readonly review = computed(() => (this.item()['review'] || null) as Review | null);
  readonly pendingEdits = computed(() => this.api.queued().some(q => q.kind === 'diary-entries' && q.id === this.item().id));
  readonly writable = computed(() => this.auth.hasModule('field') && this.auth.can('diary-entries.write') && !this.auth.recoveryHold());
  readonly canReview = computed(() => this.writable() && ['ManagingDirector', 'SiteManager'].includes(this.auth.tenant()?.role || '')
    && this.submission()?.submittedBy !== this.auth.session()?.user.id
    && this.submission()?.content?.employeeUserId !== this.auth.session()?.user.id
    && this.item()['employeeId'] !== this.auth.tenant()?.employeeId);
  private generation = 0;

  constructor() {
    effect(() => {
      this.auth.accessSignature(); this.item().id; this.item().revision;
      untracked(() => { this.generation++; this.busy.set(false); this.error.set(''); this.notice.set(''); this.consent.set(false); this.notes.set(''); });
    });
    const connection = () => this.online.set(navigator.onLine);
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    inject(DestroyRef).onDestroy(() => { this.generation++; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); });
  }

  async act(action: 'submit' | 'approve' | 'return' | 'pdf'): Promise<void> {
    if (this.busy() || !this.writable() || !this.online()) return;
    if (action === 'submit' && (!this.consent() || !['Draft', 'Returned'].includes(this.status()))) return;
    if (['approve', 'return'].includes(action) && (!this.canReview() || this.status() !== 'Submitted' || !this.consent())) return;
    if (action === 'return' && !this.notes().trim()) return;
    if (action === 'pdf' && this.status() !== 'Approved') return;
    const item = this.item(), generation = this.generation, access = this.auth.accessSignature();
    const current = () => generation === this.generation && access === this.auth.accessSignature()
      && item.id === this.item().id && item.revision === this.item().revision;
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      await this.api.reloadQueue();
      if (!current()) return;
      if (this.pendingEdits()) throw new Error('Übertragen oder klären Sie zuerst die lokal gespeicherten Änderungen dieses Tagesberichts.');
      const path = `diary-entries/${encodeURIComponent(item.id)}/${['approve', 'return'].includes(action) ? 'review' : action}`;
      const body = ['approve', 'return'].includes(action) ? { approve: action === 'approve', digest: this.submission()?.digest, notes: this.notes().trim() } : {};
      const result = await this.api.command<Entity | ReportFile>(path, body, item.revision);
      if (!current()) return;
      if (action === 'pdf') {
        const file = result as ReportFile;
        if (!file.id || file.sourceKind !== 'diary-entries' || file.sourceId !== item.id || file.contentType !== 'application/pdf'
          || !Number.isSafeInteger(file.size) || file.size <= 0 || file.size > 25 * 1024 * 1024 || !/^[a-f0-9]{64}$/.test(file.sha256)
          || typeof file.name !== 'string' || !file.name.endsWith('.pdf') || /[/\\\u0000-\u001f]/.test(file.name))
          throw new Error('Die Angaben zum Rapport sind unvollständig. Es wurde keine Datei heruntergeladen.');
        const response = await this.api.downloadResponse(`documents/${encodeURIComponent(file.id)}/download`);
        const bytes = await response.arrayBuffer();
        const digest = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), b => b.toString(16).padStart(2, '0')).join('');
        if (!current()) return;
        if (bytes.byteLength !== file.size || digest !== file.sha256 || new TextDecoder().decode(bytes.slice(0, 5)) !== '%PDF-')
          throw new Error('Der heruntergeladene Rapport stimmt nicht mit der gespeicherten Fassung überein.');
        const url = URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));
        const link = document.createElement('a'); link.href = url; link.download = file.name; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        this.notice.set('Der geprüfte Tagesrapport wurde heruntergeladen.');
      } else {
        const updated = result as Entity;
        const expected = action === 'submit' ? 'Submitted' : action === 'approve' ? 'Approved' : 'Returned';
        if (updated.id !== item.id || !Number.isSafeInteger(updated.revision) || updated.revision <= item.revision || updated['status'] !== expected)
          throw new Error('Der Server hat keinen vollständigen neuen Berichtsstand geliefert. Laden Sie den Tagesbericht erneut.');
        this.completed.emit({ kind: 'diary-entries', id: item.id, message: action === 'submit'
          ? 'Tagesbericht zur Prüfung eingereicht.' : action === 'approve' ? 'Tagesbericht freigegeben.' : 'Tagesbericht zur Überarbeitung zurückgegeben.' });
      }
    } catch (error) {
      if (current()) {
        this.error.set(errorMessage(error));
        if (error instanceof ApiError && [401, 403].includes(error.status)) await this.auth.refresh().catch(() => undefined);
      }
    } finally { if (current()) this.busy.set(false); }
  }

  date(value: string): string { return new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)); }
}
