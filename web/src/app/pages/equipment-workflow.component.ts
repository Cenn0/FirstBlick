import { Component, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';
import { EquipmentDocumentsComponent } from './equipment-documents.component';
import { EquipmentHistory, EquipmentMutation, EquipmentRecord, EquipmentWorklist, EquipmentWorklistFilter,
  InspectionKind, InspectionResult, dueStatusLabels, equipmentDueStatus, equipmentKindLabels,
  inspectionKindLabels, inspectionStatusLabels, validEquipmentDate, validEquipmentHistory,
  validEquipmentMutation, validEquipmentWorklist, validEquipmentId, validEquipmentInspection, EquipmentInspection } from '../core/equipment-workflow.models';

@Component({
  selector: 'app-equipment-workflow', imports: [FormsModule, RouterLink, EquipmentDocumentsComponent],
  templateUrl: './equipment-workflow.component.html', styleUrl: './equipment-workflow.component.css'
})
export class EquipmentWorkflowComponent implements OnDestroy {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly allowed = computed(() => ['ManagingDirector', 'Office', 'Accounting', 'SiteManager'].includes(this.auth.tenant()?.role || '')
    && this.auth.can('equipment-inspections.read'));
  readonly writable = computed(() => this.allowed() && ['ManagingDirector', 'Office', 'SiteManager'].includes(this.auth.tenant()?.role || '')
    && this.auth.can('equipment-inspections.write') && this.auth.hasModule('purchasing') && !this.auth.offlineSession());
  readonly worklist = signal<EquipmentWorklist | null>(null); readonly detail = signal<EquipmentHistory | null>(null);
  readonly linkedDocumentInspection = signal<EquipmentInspection | null>(null);
  readonly busy = signal(false); readonly listLoading = signal(false); readonly detailLoading = signal(false);
  readonly error = signal(''); readonly notice = signal(''); readonly stale = signal(false);
  readonly kinds = equipmentKindLabels; readonly inspectionKinds = inspectionKindLabels; readonly statuses = inspectionStatusLabels;
  search = ''; status: EquipmentWorklistFilter = 'all'; page = 1;
  planKind: InspectionKind = 'Inspection'; planTitle = ''; planDate = ''; planNotes = '';
  performedOn = ''; result: InspectionResult = 'Passed'; inspector = ''; evidenceReference = ''; completionNotes = '';
  nextInspectionOn = ''; makeAvailable = false; completionConfirmed = false; cancellationReason = '';
  private generation = 0; private listVersion = 0; private detailVersion = 0; private selectedId = '';
  private linkedEquipment = new URLSearchParams(location.search).get('equipmentId');
  private linkedInspection = new URLSearchParams(location.search).get('inspectionId');

  constructor() {
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  ngOnDestroy(): void { this.generation++; this.listVersion++; this.detailVersion++; }
  private reset(): void {
    this.generation++; this.listVersion++; this.detailVersion++; this.selectedId = ''; this.search = ''; this.status = 'all'; this.page = 1;
    this.worklist.set(null); this.detail.set(null); this.linkedDocumentInspection.set(null); this.busy.set(false); this.listLoading.set(false); this.detailLoading.set(false);
    this.error.set(''); this.notice.set(''); this.stale.set(false); this.clearForms();
    if (this.allowed()) {
      void this.loadList();
      const linked = this.linkedEquipment; this.linkedEquipment = null;
      if (validEquipmentId(linked)) void this.openEquipment(linked);
      const inspection = this.linkedInspection; this.linkedInspection = null;
      if (!validEquipmentId(linked) && validEquipmentId(inspection)) void this.openLinkedInspection(inspection);
    }
  }
  private async openLinkedInspection(id: string): Promise<void> {
    const generation = this.generation, access = this.auth.accessSignature(), version = this.detailVersion;
    try {
      const raw = await this.api.get<EquipmentInspection>(`equipment-inspections/${id}`);
      if (!this.current(generation, access) || version !== this.detailVersion) return;
      const inspection = validEquipmentInspection(raw, raw?.equipmentId);
      if (inspection.id !== id || inspection.status !== 'Completed') throw new Error('Die Prüfakte gehört nicht zum angeforderten Originalbeleg.');
      await this.openEquipment(inspection.equipmentId);
      if (this.current(generation, access) && this.selectedId === inspection.equipmentId && this.detail()?.equipment.id === inspection.equipmentId)
        this.linkedDocumentInspection.set(inspection);
    } catch (error) { if (this.current(generation, access)) this.error.set(equipmentError(error)); }
  }
  private current(generation: number, access: string): boolean {
    return generation === this.generation && access === this.auth.accessSignature() && this.allowed();
  }
  private clearForms(): void {
    this.planKind = 'Inspection'; this.planTitle = ''; this.planDate = ''; this.planNotes = '';
    this.performedOn = ''; this.result = 'Passed'; this.inspector = ''; this.evidenceReference = ''; this.completionNotes = '';
    this.nextInspectionOn = ''; this.makeAvailable = false; this.completionConfirmed = false; this.cancellationReason = '';
  }
  date(value: string | null | undefined): string { return value ? value.slice(0, 10).split('-').reverse().join('.') : '–'; }
  due(equipment: EquipmentRecord, businessDate: string): string { return dueStatusLabels[equipmentDueStatus(equipment.nextInspectionOn, businessDate)]; }
  availability(equipment: EquipmentRecord, businessDate: string): string {
    if (equipment.inspectionBlocked) return 'Prüfsperre';
    if (!equipment.available) return 'Nicht verfügbar';
    if (equipment.nextInspectionOn && equipment.nextInspectionOn < businessDate) return 'Prüftermin überschritten';
    return equipment.nextInspectionOn ? `Verfügbar bis ${this.date(equipment.nextInspectionOn)}` : 'Verfügbar · Prüftermin nicht festgelegt';
  }
  isBlocked(equipment: EquipmentRecord, businessDate: string): boolean {
    return equipment.inspectionBlocked || !equipment.available || !!equipment.nextInspectionOn && equipment.nextInspectionOn < businessDate;
  }
  applyFilters(): void {
    if (this.busy()) return; this.page = 1; this.clearSelection(); void this.loadList();
  }
  private clearSelection(): void {
    this.detailVersion++; this.selectedId = ''; this.detail.set(null); this.linkedDocumentInspection.set(null); this.detailLoading.set(false); this.stale.set(false); this.clearForms();
  }
  async listPage(delta: number): Promise<void> {
    if (this.busy() || this.listLoading()) return;
    this.page = Math.max(1, this.page + delta); await this.loadList();
  }
  async refresh(): Promise<void> {
    if (this.busy()) return;
    this.error.set(''); this.notice.set('');
    await this.loadList();
  }
  private async loadList(): Promise<void> {
    if (!this.allowed()) return;
    const generation = this.generation, access = this.auth.accessSignature(), version = ++this.listVersion, page = this.page;
    const query = new URLSearchParams({ search: this.search.trim(), status: this.status, page: String(page) });
    this.listLoading.set(true);
    try {
      const raw = await this.api.get<EquipmentWorklist>(`equipment/inspection-worklist?${query}`);
      if (!this.current(generation, access) || version !== this.listVersion) return;
      this.worklist.set(validEquipmentWorklist(raw, page));
    } catch (error) {
      if (this.current(generation, access) && version === this.listVersion) { this.worklist.set(null); this.error.set(equipmentError(error)); }
    } finally { if (this.current(generation, access) && version === this.listVersion) this.listLoading.set(false); }
  }
  async openEquipment(id: string): Promise<void> {
    if (this.busy()) return;
    this.clearSelection(); this.selectedId = id; this.error.set(''); this.notice.set(''); await this.loadDetail(1, true);
  }
  async reloadDetail(): Promise<void> {
    if (this.busy() || !this.selectedId) return;
    this.error.set(''); this.notice.set(''); await this.loadDetail(1, true);
  }
  async historyPage(delta: number): Promise<void> {
    const detail = this.detail(); if (this.busy() || this.detailLoading() || !detail) return;
    await this.loadDetail(Math.max(1, detail.page + delta), false);
  }
  private async loadDetail(page: number, resetForms: boolean): Promise<void> {
    if (!this.allowed() || !this.selectedId) return;
    const generation = this.generation, access = this.auth.accessSignature(), version = ++this.detailVersion, id = this.selectedId;
    this.detailLoading.set(true);
    try {
      const raw = await this.api.get<EquipmentHistory>(`equipment/${id}/inspection-history?page=${page}`);
      if (!this.current(generation, access) || version !== this.detailVersion || id !== this.selectedId) return;
      const value = validEquipmentHistory(raw, id, page);
      if (!resetForms && this.detail()?.equipment.revision !== value.equipment.revision) {
        this.stale.set(true); this.error.set('Die Geräteakte hat sich während des Blätterns geändert. Laden Sie sie neu und gleichen Sie Ihre Eingaben ab.');
      } else if (resetForms) {
        this.clearForms(); this.stale.set(false); this.planDate = value.equipment.nextInspectionOn || value.businessDate;
        this.performedOn = value.businessDate;
      }
      this.detail.set(value);
    } catch (error) {
      if (this.current(generation, access) && version === this.detailVersion) {
        this.error.set(equipmentError(error)); this.stale.set(true);
        if (!this.detail() || error instanceof ApiError && [403, 404].includes(error.status)) this.clearSelection();
      }
    } finally { if (this.current(generation, access) && version === this.detailVersion) this.detailLoading.set(false); }
  }
  planValid(): boolean {
    return this.actionAllowed() && !this.detail()?.pendingInspection && !!this.planTitle.trim() && this.planTitle.length <= 240
      && validEquipmentDate(this.planDate) && this.planNotes.length <= 4000 && ['Inspection', 'Maintenance', 'Repair'].includes(this.planKind);
  }
  completionValid(): boolean {
    const detail = this.detail();
    return this.actionAllowed() && !!detail?.pendingInspection && validEquipmentDate(this.performedOn)
      && this.performedOn <= detail.businessDate && (!detail.equipment.lastInspectionOn || this.performedOn >= detail.equipment.lastInspectionOn)
      && !!this.inspector.trim() && this.inspector.length <= 240
      && !!this.evidenceReference.trim() && this.evidenceReference.length <= 500 && this.completionNotes.length <= 4000
      && this.completionConfirmed && (this.result === 'Passed'
        ? validEquipmentDate(this.nextInspectionOn) && this.nextInspectionOn > this.performedOn
        : this.result === 'Failed' && !!this.completionNotes.trim() && !this.makeAvailable && !this.nextInspectionOn);
  }
  cancellationValid(): boolean { return this.actionAllowed() && !!this.detail()?.pendingInspection && !!this.cancellationReason.trim() && this.cancellationReason.length <= 2000; }
  private actionAllowed(): boolean { return this.writable() && !!this.detail() && !this.busy() && !this.detailLoading() && !this.stale(); }
  resultChanged(): void {
    this.completionConfirmed = false;
    if (this.result === 'Failed') { this.makeAvailable = false; this.nextInspectionOn = ''; }
  }
  async plan(): Promise<void> {
    const detail = this.detail(); if (!detail || !this.planValid()) return;
    const body = { kind: this.planKind, title: this.planTitle.trim(), scheduledOn: this.planDate, notes: this.planNotes.trim() };
    await this.mutate('inspection-plan', body, 'Scheduled', 'Prüfvorgang geplant. Die bestehende Geräteverfügbarkeit bleibt erhalten.');
  }
  async complete(): Promise<void> {
    const inspection = this.detail()?.pendingInspection; if (!inspection || !this.completionValid()) return;
    const body = { inspectionId: inspection.id, inspectionRevision: inspection.revision, performedOn: this.performedOn,
      result: this.result, inspector: this.inspector.trim(), evidenceReference: this.evidenceReference.trim(),
      notes: this.completionNotes.trim(), nextInspectionOn: this.result === 'Passed' ? this.nextInspectionOn : null,
      makeAvailable: this.result === 'Passed' && this.makeAvailable };
    await this.mutate('inspection-complete', body, 'Completed', this.result === 'Failed'
      ? 'Prüfergebnis gespeichert. Das Gerät ist für neue Einsätze gesperrt.'
      : this.makeAvailable ? 'Prüfergebnis gespeichert. Verfügbarkeit und nächster Prüftermin wurden übernommen.'
        : 'Prüfergebnis gespeichert. Der nächste Prüftermin wurde übernommen; die bisherige Verfügbarkeit bleibt erhalten.', inspection.id);
  }
  async cancel(): Promise<void> {
    const inspection = this.detail()?.pendingInspection; if (!inspection || !this.cancellationValid()) return;
    await this.mutate('inspection-cancel', { inspectionId: inspection.id, inspectionRevision: inspection.revision, reason: this.cancellationReason.trim() },
      'Cancelled', 'Prüfvorgang mit Begründung abgesagt. Die bestehende Verfügbarkeit und eine Prüfsperre bleiben erhalten.', inspection.id);
  }
  private async mutate(path: string, body: Record<string, unknown>, status: 'Scheduled' | 'Completed' | 'Cancelled', notice: string, inspectionId?: string): Promise<void> {
    const detail = this.detail(); if (!detail || !this.actionAllowed()) return;
    const generation = this.generation, access = this.auth.accessSignature(), equipment = detail.equipment;
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      const raw = await this.api.command<EquipmentMutation>(`equipment/${equipment.id}/${path}`, body, equipment.revision);
      if (!this.current(generation, access)) return;
      const value = validEquipmentMutation(raw, equipment.id, equipment, status, inspectionId);
      const evidence = (status === 'Scheduled' ? value.inspection : status === 'Completed' ? value.inspection.completion : value.inspection.cancellation) as unknown as Record<string, unknown>;
      const fields = status === 'Scheduled' ? ['kind', 'title', 'scheduledOn', 'notes']
        : status === 'Completed' ? ['performedOn', 'result', 'inspector', 'evidenceReference', 'notes', 'nextInspectionOn', 'makeAvailable'] : ['reason'];
      if (fields.some(key => evidence[key] !== body[key]))
        throw new Error('Der gespeicherte Nachweis bestätigt andere Angaben als die gesendeten Daten. Laden Sie die Geräteakte zur Prüfung neu.');
      this.detail.set({ ...detail, equipment: value.equipment, pendingInspection: status === 'Scheduled' ? value.inspection : null,
        items: detail.items.map(item => item.id === value.inspection.id ? value.inspection : item),
        affectedAssignmentCount: value.affectedAssignmentCount });
      this.clearForms(); this.notice.set(notice);
      // Read the committed history; a subsequent read failure does not erase a confirmed command outcome.
      await Promise.all([this.loadList(), this.loadDetail(1, true)]);
    } catch (error) {
      if (!this.current(generation, access)) return;
      this.error.set(equipmentError(error));
      if (error instanceof ApiError && [403, 404].includes(error.status)) this.clearSelection();
      else if (!(error instanceof ApiError) || error.status === 409 || error.status === 412) this.stale.set(true);
    } finally { if (this.current(generation, access)) this.busy.set(false); }
  }
}

function equipmentError(error: unknown): string {
  if (error instanceof ApiError) {
    const messages: Record<string, string> = {
      inspection_already_planned: 'Für dieses Gerät besteht bereits ein offener Prüfvorgang. Laden Sie die Geräteakte neu.',
      inspection_sealed: 'Dieser Prüfvorgang wurde bereits abgeschlossen oder abgesagt. Laden Sie die Geräteakte neu.',
      inspection_evidence: 'Die gespeicherten Prüfnachweise sind widersprüchlich. Eine neue Buchung wurde nicht bestätigt.',
      inspection_state: 'Gerätezustand und Prüfnachweis stimmen nicht überein. Laden Sie die Geräteakte neu.',
      inspection_equipment_changed: 'Die Geräteidentität wurde verändert. Prüfen Sie den geplanten Vorgang und legen Sie ihn bei Bedarf neu an.',
      inspection_out_of_order: 'Das Durchführungsdatum liegt vor dem zuletzt dokumentierten Ergebnis.',
      inspection_future_result: 'Eine künftige Prüfung kann erst nach ihrer Durchführung dokumentiert werden.',
      inspection_next_date: 'Der ausdrücklich festgelegte Folgetermin muss nach dem Durchführungsdatum liegen.',
      equipment_unavailable: 'Dieses Gerät ist für den gewählten Einsatz nicht verfügbar.'
    };
    if (messages[error.problem.code || '']) return messages[error.problem.code || ''];
  }
  return errorMessage(error);
}
