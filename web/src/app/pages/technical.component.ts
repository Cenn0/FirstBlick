import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';
import { downloadBlob, sha256Blob } from '../core/integrations.models';
import { AdvancedTechnicalComponent } from './advanced-technical.component';
import { CalculationResult, ClimateDraft, InhomogeneousSectionDraft, LayerDraft, SavedCalculation, TechnicalMode, asObject, calculationInput, climateFields, emptyClimates, emptyInhomogeneousSection, emptyLayer, hasDiffusion, hasLayers, isDigest, monthNames, resultInterfaces, resultMetrics, resultMonths, technicalMethods, technicalModes, validateCalculation, validateSavedCalculation } from '../core/technical-workbench.models';

@Component({ imports: [FormsModule, RouterLink, JsonPipe, AdvancedTechnicalComponent], templateUrl: './technical.component.html', styleUrl: './technical.component.css' })
export class TechnicalComponent {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly mode = signal<TechnicalMode>('roof'); readonly methods = technicalMethods; readonly modes = technicalModes;
  readonly fields = computed(() => technicalMethods[this.mode()].fields);
  readonly allowed = computed(() => !!this.auth.scope() && this.auth.can('technical-results.write'));
  readonly busy = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly result = signal<CalculationResult | null>(null); readonly saved = signal<SavedCalculation | null>(null);
  readonly projects = signal<Entity[]>([]); readonly projectTotal = signal(0); projectPage = 1; projectSearch = ''; projectId = ''; title = '';
  values: Record<string, number | null> = {}; source = ''; layers: LayerDraft[] = [emptyLayer()]; months: ClimateDraft[] = emptyClimates();
  sections: InhomogeneousSectionDraft[] = [emptyInhomogeneousSection(), emptyInhomogeneousSection()]; noMetalBridgingConfirmed = false;
  readonly climateFields = climateFields; readonly monthNames = monthNames; readonly hasLayers = hasLayers; readonly hasDiffusion = hasDiffusion;
  readonly metrics = resultMetrics; readonly interfaces = resultInterfaces; readonly monthlyResults = resultMonths;
  private generation = 0;
  constructor() {
    inject(DestroyRef).onDestroy(() => { this.generation++; });
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  private current(generation: number, access: string): boolean { return generation === this.generation && access === this.auth.accessSignature(); }
  private reset(): void { this.generation++; this.busy.set(false); this.error.set(''); this.notice.set(''); this.result.set(null); this.saved.set(null); this.values = {}; this.source = ''; this.layers = [emptyLayer()]; this.months = emptyClimates(); this.sections = [emptyInhomogeneousSection(), emptyInhomogeneousSection()]; this.noMetalBridgingConfirmed = false; this.projects.set([]); this.projectTotal.set(0); this.projectPage = 1; this.projectId = ''; this.projectSearch = ''; this.title = ''; }
  chooseMode(mode: TechnicalMode): void { if (this.mode() === mode) return; this.mode.set(mode); this.reset(); }
  tabKey(event: KeyboardEvent, current: TechnicalMode): void {
    const index = this.modes.indexOf(current), next = event.key === 'Home' ? 0 : event.key === 'End' ? this.modes.length - 1 : event.key === 'ArrowRight' ? (index + 1) % this.modes.length : event.key === 'ArrowLeft' ? (index + this.modes.length - 1) % this.modes.length : -1;
    if (next < 0) return;
    event.preventDefault(); const list = (event.currentTarget as HTMLElement).parentElement;
    this.chooseMode(this.modes[next]); list?.querySelector<HTMLButtonElement>(`#calculation-tab-${this.modes[next]}`)?.focus();
  }
  edited(): void { this.generation++; this.busy.set(false); this.result.set(null); this.saved.set(null); this.notice.set(''); this.error.set(''); }
  attachmentEdited(): void { this.generation++; this.saved.set(null); this.notice.set(''); }
  addLayer(): void { if (this.layers.length < 60) { this.layers.push(emptyLayer()); this.edited(); } }
  removeLayer(index: number): void { if (this.layers.length > 1) { this.layers.splice(index, 1); this.edited(); } }
  addSection(): void { if (this.sections.length < 16) { this.sections.push(emptyInhomogeneousSection()); this.edited(); } }
  removeSection(index: number): void { if (this.sections.length > 1) { this.sections.splice(index, 1); this.edited(); } }
  addSectionLayer(section: InhomogeneousSectionDraft): void { if (section.layers.length < 60) { section.layers.push(emptyLayer()); this.edited(); } }
  removeSectionLayer(section: InhomogeneousSectionDraft, index: number): void { if (section.layers.length > 1) { section.layers.splice(index, 1); this.edited(); } }
  sectionResults(value: CalculationResult): { name: string; areaFraction: number; path: { totalResistanceM2KW: number; uValueWm2K: number } }[] {
    return value['sections'] as { name: string; areaFraction: number; path: { totalResistanceM2KW: number; uValueWm2K: number } }[] || [];
  }
  changeClimate(month: ClimateDraft, key: string, value: number | null): void { (month as unknown as Record<string, number | null>)[key] = value; this.edited(); }
  climateValue(month: ClimateDraft, key: string): number | null { return month[key as keyof ClimateDraft]; }
  decimal(value: unknown, digits = 4): string { return typeof value === 'number' && Number.isFinite(value) ? new Intl.NumberFormat('de-DE', { maximumFractionDigits: digits }).format(value) : '–'; }
  corners(value: CalculationResult): { x: number; y: number; thicknessM: number }[] { return value['corners'] as { x: number; y: number; thicknessM: number }[] || []; }
  flag(value: CalculationResult, key: string): boolean { return value[key] === true; }
  private failed(error: unknown, generation: number, access: string): void {
    if (!this.current(generation, access)) return;
    if (error instanceof ApiError && [401, 403, 423].includes(error.status)) { this.result.set(null); this.saved.set(null); this.projects.set([]); this.projectId = ''; }
    this.error.set(errorMessage(error));
  }
  async calculate(): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), mode = this.mode();
    this.busy.set(true); this.error.set(''); this.notice.set(''); this.result.set(null); this.saved.set(null);
    try {
      const input = calculationInput(mode, this.values, this.source, this.layers, this.months, this.sections, this.noMetalBridgingConfirmed);
      const response = await this.api.command<unknown>(technicalMethods[mode].endpoint, input);
      if (this.current(generation, access)) this.result.set(validateCalculation(response, mode, input));
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async searchProjects(page = 1): Promise<void> {
    if (!this.allowed() || !this.auth.can('projects.read') || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set(''); this.projects.set([]); this.projectId = ''; this.saved.set(null);
    try {
      const response = await this.api.list('projects', this.projectSearch, page);
      if (!this.current(generation, access)) return;
      if (!Array.isArray(response.items) || !Number.isInteger(response.total)) throw new Error('Die Projektliste ist unvollständig.');
      this.projects.set(response.items.filter(item => !item.archived)); this.projectTotal.set(response.total); this.projectPage = page;
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async save(): Promise<void> {
    const result = this.result();
    if (!result || !this.allowed() || this.busy() || this.saved()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), tenant = this.auth.activeTenantId(), project = this.projectId, mode = this.mode();
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      if (!this.projects().some(p => p.id === project)) throw new Error('Ein Projekt aus der aktuellen Suche auswählen.');
      if (!this.title.trim() || this.title.length > 240) throw new Error('Eine Berichtsbezeichnung mit höchstens 240 Zeichen eingeben.');
      const response = await this.api.command<unknown>('technical/runs', { projectId: project, roofRevisionId: null, title: this.title.trim(), method: technicalMethods[mode].method, input: result.input });
      if (!this.current(generation, access)) return;
      this.saved.set(validateSavedCalculation(response, mode, result.input, tenant, project));
      this.notice.set('Berechnung mit Eingaben und Ergebnis im Projektverlauf gespeichert.');
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  exportJson(): void {
    const result = this.result(); if (!result || !this.allowed() || this.busy()) return;
    downloadBlob(new Blob([JSON.stringify(this.saved() || result, null, 2)], { type: 'application/json' }), `FirstBlick-${technicalMethods[this.mode()].method}-${result.provenance.inputSha256.slice(0, 12)}.json`);
  }
  async pdf(): Promise<void> {
    const record = this.saved(); if (!record || !this.allowed() || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set('');
    try {
      const response = asObject(await this.api.command<unknown>(`technical-results/${record.id}/pdf`, {}, record.revision));
      if (!this.current(generation, access)) return;
      if (typeof response['id'] !== 'string' || !/^[a-f0-9-]{36}$/i.test(response['id']) || response['sourceId'] !== record.id || response['sourceDigest'] !== record.digest || response['sourceKind'] !== 'technical-results' || response['projectId'] !== record.projectId || response['state'] !== 'Ready' || response['contentType'] !== 'application/pdf' || !isDigest(response['sha256'])) throw new Error('Der erzeugte Bericht ist nicht eindeutig an die gespeicherte Berechnung gebunden.');
      const blob = await this.api.download(`documents/${response['id']}/download`);
      if (!this.current(generation, access)) return;
      if (blob.size !== response['size'] || blob.size <= 0 || blob.size > 25 * 1024 * 1024 || await blob.slice(0, 5).text() !== '%PDF-' || await sha256Blob(blob) !== response['sha256']) throw new Error('Größe, PDF-Kennung oder Prüfsumme des Berichts stimmt nicht.');
      if (this.current(generation, access)) downloadBlob(blob, `FirstBlick-Berechnung-${record.id}.pdf`);
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
}
