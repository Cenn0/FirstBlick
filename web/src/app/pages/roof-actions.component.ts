import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { downloadBlob } from '../core/integrations.models';
import { ActionDraft, ActionInput, ActionResult, RoofAction, actionDraftInput, object, roofActions, validActionInput, validActionResult } from '../core/roof-actions.models';
import { errorMessage } from '../core/transport';

@Component({
  imports: [FormsModule, RouterLink, DecimalPipe],
  templateUrl: './roof-actions.component.html',
  styles: ['.action-row{border-top:1px solid var(--border,#dbe0e5);padding-top:1rem;margin-top:1rem}.result-values{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,15rem),1fr));gap:.8rem}.result-values div{min-width:0}.result-values dt{font-size:.9rem}.result-values dd{margin:0;font-weight:600}.digest{overflow-wrap:anywhere}fieldset{min-width:0}.form-field{min-width:0}']
})
export class RoofActionsComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly modes: RoofAction[] = ['drainage', 'wind', 'snow'];
  readonly definitions = roofActions;
  readonly mode = signal<RoofAction>('drainage');
  readonly definition = computed(() => roofActions[this.mode()]);
  readonly allowed = computed(() => !!this.auth.scope() && this.auth.hasModule('technical') && this.auth.can('technical-results.write'));
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly result = signal<ActionResult | null>(null);
  readonly saved = signal<Record<string, unknown> | null>(null);
  readonly projects = signal<Entity[]>([]);
  draft: ActionDraft = { source: '', globals: {}, scenario: '', rows: [] };
  projectSearch = ''; projectId = ''; title = '';
  private generation = 0;
  constructor() {
    inject(DestroyRef).onDestroy(() => this.generation++);
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  private reset(): void {
    this.edited(); this.projects.set([]); this.projectId = ''; this.projectSearch = ''; this.title = '';
    this.draft = { source: '', globals: {}, scenario: '', rows: [] }; this.addRow();
  }
  choose(value: string): void {
    if (!this.modes.includes(value as RoofAction)) return;
    this.mode.set(value as RoofAction); this.reset();
  }
  edited(): void {
    this.generation++; this.busy.set(false); this.result.set(null); this.saved.set(null); this.error.set(''); this.notice.set('');
  }
  addRow(): void {
    if (this.busy() || this.draft.rows.length >= 32) return;
    this.draft.rows.push({ name: '', values: {}, sources: {} }); this.edited();
  }
  removeRow(index: number): void {
    if (this.busy() || this.draft.rows.length <= 1) return;
    this.draft.rows.splice(index, 1); this.edited();
  }
  private current(id: number, signature: string): boolean {
    return id === this.generation && signature === this.auth.accessSignature() && this.allowed();
  }
  async calculate(): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    this.edited();
    const id = ++this.generation, signature = this.auth.accessSignature(), mode = this.mode();
    let input: ActionInput;
    try { input = actionDraftInput(mode, this.draft); }
    catch (error) { this.error.set(errorMessage(error)); return; }
    this.busy.set(true);
    try {
      const raw = await this.api.command<unknown>('technical/roof/' + mode, input);
      if (this.current(id, signature)) this.result.set(validActionResult(mode, raw, input));
    } catch (error) { if (this.current(id, signature)) this.error.set(errorMessage(error)); }
    finally { if (this.current(id, signature)) this.busy.set(false); }
  }
  async searchProjects(): Promise<void> {
    if (!this.allowed() || !this.auth.can('projects.read') || this.busy()) return;
    const id = ++this.generation, signature = this.auth.accessSignature();
    this.error.set(''); this.projects.set([]); this.projectId = ''; this.busy.set(true);
    try {
      const data = await this.api.list('projects', this.projectSearch, 1);
      if (this.current(id, signature)) this.projects.set(data.items.filter(p => !p.archived));
    } catch (error) { if (this.current(id, signature)) this.error.set(errorMessage(error)); }
    finally { if (this.current(id, signature)) this.busy.set(false); }
  }
  async save(): Promise<void> {
    const result = this.result();
    if (!this.allowed() || this.busy() || !result || this.saved()) return;
    this.error.set(''); this.notice.set('');
    if (!this.projects().some(p => p.id === this.projectId) || !this.title.trim() || this.title.length > 240) {
      this.error.set('Projekt aus der aktuellen Suche auswählen und Berichtsbezeichnung mit höchstens 240 Zeichen eingeben.'); return;
    }
    const id = ++this.generation, signature = this.auth.accessSignature(), mode = this.mode();
    const projectId = this.projectId, title = this.title.trim(); this.busy.set(true);
    try {
      const raw = await this.api.command<unknown>('technical/runs', {
        projectId, roofRevisionId: null, title, method: 'roof.' + mode, input: result.input
      });
      if (!this.current(id, signature)) return;
      if (!object(raw) || typeof raw['id'] !== 'string' || !/^[a-f0-9-]{36}$/i.test(raw['id']) ||
          raw['projectId'] !== projectId || raw['title'] !== title || raw['method'] !== 'roof.' + mode ||
          raw['methodVersion'] !== '1.0.0' || raw['isNormative'] !== false ||
          typeof raw['digest'] !== 'string' || !/^[a-f0-9]{64}$/.test(raw['digest']) ||
          JSON.stringify(validActionInput(mode, raw['input'])) !== JSON.stringify(result.input))
        throw new Error('Die Speicherantwort ist nicht korrekt an Projekt, Eingaben und Verfahren gebunden.');
      const confirmed = validActionResult(mode, raw['result'], result.input);
      if (confirmed.provenance.inputSha256 !== result.provenance.inputSha256)
        throw new Error('Der gespeicherte Rechenstand weicht vom geprüften Ergebnis ab.');
      this.saved.set(raw); this.notice.set('Berechnung mit Quellen und Verfahrensgrenzen im Projekt gespeichert.');
    } catch (error) { if (this.current(id, signature)) this.error.set(errorMessage(error)); }
    finally { if (this.current(id, signature)) this.busy.set(false); }
  }
  exportJson(): void {
    const result = this.result();
    if (!this.allowed() || !result || this.busy()) return;
    downloadBlob(new Blob([JSON.stringify(this.saved() || result.raw, null, 2)], { type: 'application/json' }),
      'FirstBlick-' + this.mode() + '-' + result.provenance.inputSha256.slice(0, 12) + '.json');
  }
}
