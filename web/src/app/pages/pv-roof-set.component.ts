import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { downloadBlob } from '../core/integrations.models';
import { PvDraft } from '../core/pv-layout.models';
import { PvRoofSetDraft, PvRoofSetInput, PvRoofSetResult, validPvRoofSetInput, validPvRoofSetResult } from '../core/pv-roof-set.models';
import { errorMessage } from '../core/transport';

const emptyLayout = (): PvDraft => ({
  roofPlaneWidthM: null, roofPlaneHeightM: null, moduleWidthM: null,
  moduleHeightM: null, moduleRatedPowerWp: null, edgeClearanceM: 0,
  moduleGapXM: 0, moduleGapYM: 0, exclusionClearanceM: 0,
  orientation: 'MostModules', exclusions: [], roofMeasurementSource: '', moduleDataSource: ''
});
type SavedResult = { id: string; projectId: string; method: string; digest: string; isNormative: false;
  input: PvRoofSetInput; result: PvRoofSetResult };

@Component({
  imports: [FormsModule, RouterLink],
  templateUrl: './pv-roof-set.component.html',
  styleUrl: './pv-roof-set.component.css'
})
export class PvRoofSetComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly allowed = computed(() => !!this.auth.scope() && this.auth.hasModule('technical') &&
    this.auth.can('technical-results.write'));
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly result = signal<PvRoofSetResult | null>(null);
  readonly saved = signal<SavedResult | null>(null);
  readonly projects = signal<Entity[]>([]);
  projectId = '';
  projectSearch = '';
  title = '';
  draft: PvRoofSetDraft = { roofSetSource: '', planes: [] };
  private latestInput: PvRoofSetInput | null = null;
  private generation = 0;
  private nextPlaneId = 1;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.generation++);
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }

  private reset(): void {
    this.generation++;
    this.busy.set(false); this.error.set(''); this.notice.set('');
    this.result.set(null); this.saved.set(null); this.projects.set([]);
    this.latestInput = null; this.projectId = ''; this.projectSearch = ''; this.title = '';
    this.nextPlaneId = 1;
    this.draft = { roofSetSource: '', planes: [{ id: this.nextPlaneId++, name: '', layout: emptyLayout() }] };
  }

  edited(): void {
    this.generation++;
    this.busy.set(false); this.result.set(null); this.saved.set(null);
    this.latestInput = null; this.error.set(''); this.notice.set('');
  }

  addPlane(): void {
    if (this.busy() || this.draft.planes.length >= 8) return;
    this.draft.planes.push({ id: this.nextPlaneId++, name: '', layout: emptyLayout() });
    this.edited();
  }

  removePlane(id: number): void {
    if (this.busy() || this.draft.planes.length <= 1) return;
    this.draft.planes = this.draft.planes.filter(plane => plane.id !== id);
    this.edited();
  }

  addExclusion(planeId: number): void {
    const plane = this.draft.planes.find(p => p.id === planeId);
    if (this.busy() || !plane || plane.layout.exclusions.length >= 64) return;
    plane.layout.exclusions.push({ name: '', x: 0, y: 0, widthM: null, heightM: null });
    this.edited();
  }

  removeExclusion(planeId: number, index: number): void {
    const plane = this.draft.planes.find(p => p.id === planeId);
    if (this.busy() || !plane) return;
    plane.layout.exclusions.splice(index, 1);
    this.edited();
  }

  private current(id: number, signature: string): boolean {
    return id === this.generation && signature === this.auth.accessSignature() && this.allowed();
  }

  async calculate(): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    const id = ++this.generation, signature = this.auth.accessSignature();
    this.error.set(''); this.notice.set(''); this.result.set(null); this.saved.set(null);
    this.latestInput = null;
    let input: PvRoofSetInput;
    try { input = validPvRoofSetInput(this.draft); }
    catch (error) { this.error.set(errorMessage(error)); return; }
    this.busy.set(true);
    try {
      const response = await this.api.command<unknown>('technical/roof/pv-multi-layout', input);
      if (!this.current(id, signature)) return;
      const checked = validPvRoofSetResult(response, input);
      this.latestInput = input;
      this.result.set(checked);
    } catch (error) {
      if (this.current(id, signature)) this.error.set(errorMessage(error));
    } finally {
      if (id === this.generation && signature === this.auth.accessSignature()) this.busy.set(false);
    }
  }

  async searchProjects(): Promise<void> {
    if (!this.allowed() || !this.auth.can('projects.read') || this.busy()) return;
    const id = ++this.generation, signature = this.auth.accessSignature();
    this.error.set(''); this.projects.set([]); this.projectId = '';
    this.busy.set(true);
    try {
      const response = await this.api.list('projects', this.projectSearch, 1);
      if (this.current(id, signature)) this.projects.set(response.items.filter(p => !p.archived));
    } catch (error) {
      if (this.current(id, signature)) this.error.set(errorMessage(error));
    } finally {
      if (id === this.generation && signature === this.auth.accessSignature()) this.busy.set(false);
    }
  }

  async save(): Promise<void> {
    if (!this.allowed() || this.busy() || this.saved() || !this.result() || !this.latestInput) return;
    const id = ++this.generation, signature = this.auth.accessSignature();
    const input = this.latestInput;
    this.error.set(''); this.notice.set('');
    if (!this.projects().some(p => p.id === this.projectId)) {
      this.error.set('Projekt aus der aktuellen Suche auswählen.'); return;
    }
    if (!this.title.trim() || this.title.length > 240) {
      this.error.set('Berichtsbezeichnung mit höchstens 240 Zeichen eingeben.'); return;
    }
    this.busy.set(true);
    try {
      const raw = await this.api.command<unknown>('technical/runs', {
        projectId: this.projectId, roofRevisionId: null, title: this.title.trim(),
        method: 'roof.pv-multi-layout', input
      });
      if (!this.current(id, signature)) return;
      if (!raw || typeof raw !== 'object') throw new Error('Ungültige Speicherantwort.');
      const record = raw as Record<string, unknown>;
      if (typeof record['id'] !== 'string' || record['projectId'] !== this.projectId ||
          record['method'] !== 'roof.pv-multi-layout' || record['isNormative'] !== false ||
          typeof record['digest'] !== 'string' || !/^[a-f0-9]{64}$/.test(record['digest']))
        throw new Error('Der gespeicherte Nachweis ist nicht korrekt an das Projekt gebunden.');
      const confirmed = validPvRoofSetResult(record['result'], input);
      if (confirmed.provenance.inputSha256 !== this.result()?.provenance.inputSha256)
        throw new Error('Der gespeicherte Entwurf weicht von der bestätigten Berechnung ab.');
      this.saved.set(record as SavedResult);
      this.notice.set('Die geprüften Dachflächen und ihre Eingaben wurden als technischer Projektstand gespeichert.');
    } catch (error) {
      if (this.current(id, signature)) this.error.set(errorMessage(error));
    } finally {
      if (id === this.generation && signature === this.auth.accessSignature()) this.busy.set(false);
    }
  }

  exportJson(): void {
    const result = this.result();
    if (!this.allowed() || !result || this.busy()) return;
    downloadBlob(new Blob([JSON.stringify(this.saved() || result, null, 2)], { type: 'application/json' }),
      'FirstBlick-PV-Dachgruppe-' + result.provenance.inputSha256.slice(0, 12) + '.json');
  }
}
