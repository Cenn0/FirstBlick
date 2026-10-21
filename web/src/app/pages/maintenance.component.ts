import { Component, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity, Page } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';

interface CheckTemplate { title: string; instructions: string }
interface Contract extends Entity { title: string; scope: string; nextDueDate: string; intervalMonths: number; active: boolean; automaticPlanning: boolean; planningLeadDays: number; checklistTemplate: CheckTemplate[] }
type CheckState = 'Pending' | 'Done' | 'NotApplicable';
interface Check extends CheckTemplate { id: string; state: CheckState; note: string; recordedBy: string | null; recordedAt: string | null }
interface Visit extends Entity { contractId: string; contractRevision: number; projectId: string; dueDate: string; terms: { title: string; scope: string }; scopeDigest: string; checklist: Check[]; status: 'Planned' | 'Completed'; completionNotes: string; completedAt: string | null; nextDueDate: string | null }
interface VisitSummary extends Entity { contractId: string; projectId: string; dueDate: string; title: string; status: 'Planned' | 'Completed'; totalChecks: number; completedChecks: number }

@Component({
  selector: 'app-maintenance', imports: [FormsModule, RouterLink],
  templateUrl: './maintenance.component.html', styleUrl: './maintenance.component.css'
})
export class MaintenanceComponent implements OnDestroy {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly allowed = computed(() => ['ManagingDirector', 'Office', 'SiteManager', 'Worker'].includes(this.auth.tenant()?.role || '') && this.auth.can('maintenance-visits.read'));
  readonly managers = computed(() => ['ManagingDirector', 'Office', 'SiteManager'].includes(this.auth.tenant()?.role || '') && this.auth.can('maintenance-contracts.read'));
  readonly canManage = computed(() => this.managers() && this.auth.can('maintenance-contracts.write') && this.auth.can('projects.write') && this.auth.hasModule('maintenance') && this.auth.hasModule('core'));
  readonly canCheck = computed(() => this.allowed() && this.auth.can('maintenance-visits.write') && this.auth.hasModule('maintenance'));
  readonly contracts = signal<Contract[]>([]); readonly contract = signal<Contract | null>(null);
  readonly visits = signal<VisitSummary[]>([]); readonly visit = signal<Visit | null>(null);
  readonly busy = signal(false); readonly loading = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly contractPage = signal(1); readonly contractTotal = signal(0); readonly visitPage = signal(1); readonly visitTotal = signal(0);
  active = true; automatic = false; leadDays = 14; template: CheckTemplate[] = []; completionNotes = '';
  checkEdits: Record<string, { state: CheckState; note: string }> = {};
  private generation = 0; private detailVersion = 0; private listVersion = 0;

  constructor() {
    effect(() => { this.auth.accessSignature(); const allowed = this.allowed(); untracked(() => {
      this.generation++; this.detailVersion++; this.listVersion++; this.contracts.set([]); this.contract.set(null); this.visits.set([]); this.visit.set(null);
      this.busy.set(false); this.loading.set(false); this.error.set(''); this.notice.set(''); this.template = []; this.checkEdits = {}; this.completionNotes = '';
      this.contractPage.set(1); this.visitPage.set(1); this.contractTotal.set(0); this.visitTotal.set(0);
      if (allowed) void this.refresh();
    }); });
  }
  ngOnDestroy(): void { this.generation++; this.detailVersion++; }
  date(value: string | null | undefined): string { return value ? value.slice(0, 10).split('-').reverse().join('.') : '–'; }
  status(value: string): string { return value === 'Completed' ? 'Abgeschlossen' : 'Geplant'; }
  private current(generation: number, access: string): boolean { return generation === this.generation && access === this.auth.accessSignature() && this.allowed(); }
  configurationChanged(): boolean { const c = this.contract(); return !c || this.active !== c.active || this.automatic !== c.automaticPlanning || Number(this.leadDays) !== c.planningLeadDays || JSON.stringify(this.template) !== JSON.stringify(c.checklistTemplate); }
  templateValid(): boolean { return this.template.length > 0 && this.template.length <= 50 && this.template.every(x => !!x.title.trim() && x.title.length <= 240 && x.instructions.length <= 4000) && Number.isInteger(Number(this.leadDays)) && Number(this.leadDays) >= 0 && Number(this.leadDays) <= 365; }
  completeEnabled(): boolean { const v = this.visit(); return this.canManage() && !this.busy() && !!v && v.status === 'Planned' && v.checklist.every(x => x.state !== 'Pending' && this.checkEdits[x.id]?.state === x.state && this.checkEdits[x.id]?.note === x.note) && !!this.completionNotes.trim(); }
  checkEnabled(check: Check): boolean { const edit = this.checkEdits[check.id]; return this.canCheck() && !this.busy() && this.visit()?.status === 'Planned' && !!edit && (edit.state !== 'NotApplicable' || !!edit.note.trim()); }
  addCheck(): void { if (this.canManage() && !this.busy() && this.template.length < 50) this.template.push({ title: '', instructions: '' }); }
  removeCheck(index: number): void { if (this.canManage() && !this.busy()) this.template.splice(index, 1); }

  async refresh(): Promise<void> {
    if (!this.allowed()) return; const generation = this.generation, access = this.auth.accessSignature(), listVersion = ++this.listVersion; this.loading.set(true); this.error.set('');
    try {
      const [contracts, visits] = await Promise.all([
        this.managers() ? this.api.get<Page<Contract>>(`maintenance-contracts?pageSize=25&page=${this.contractPage()}`) : Promise.resolve(null),
        this.api.get<Page<VisitSummary>>(`maintenance-visits?page=${this.visitPage()}${this.contract() ? '&contractId=' + encodeURIComponent(this.contract()!.id) : ''}`)
      ]);
      if (!this.current(generation, access) || listVersion !== this.listVersion) return;
      if (contracts) { this.validatePage(contracts); this.contracts.set(contracts.items); this.contractTotal.set(contracts.total); }
      this.validatePage(visits); this.visits.set(visits.items); this.visitTotal.set(visits.total);
    } catch (error) { if (this.current(generation, access) && listVersion === this.listVersion) { this.error.set(maintenanceError(error)); this.contracts.set([]); this.visits.set([]); } }
    finally { if (this.current(generation, access) && listVersion === this.listVersion) this.loading.set(false); }
  }
  async contractPaging(delta: number): Promise<void> { if (this.loading() || this.busy()) return; this.contractPage.update(x => Math.max(1, x + delta)); await this.refresh(); }
  async visitPaging(delta: number): Promise<void> { if (this.loading() || this.busy()) return; this.visitPage.update(x => Math.max(1, x + delta)); await this.refresh(); }
  async selectContract(id: string): Promise<void> {
    if (!this.managers() || this.busy()) return; const generation = this.generation, access = this.auth.accessSignature(), version = ++this.detailVersion;
    this.contract.set(null); this.visit.set(null); this.template = []; this.error.set(''); this.notice.set(''); this.loading.set(true);
    try {
      const value = await this.api.get<Contract>(`maintenance-contracts/${id}`);
      if (!this.current(generation, access) || version !== this.detailVersion) return;
      this.setContract(value, id); this.visitPage.set(1); await this.refresh();
    } catch (error) { if (this.current(generation, access) && version === this.detailVersion) this.error.set(maintenanceError(error)); }
    finally { if (this.current(generation, access) && version === this.detailVersion) this.loading.set(false); }
  }
  async allVisits(): Promise<void> { if (this.busy()) return; this.detailVersion++; this.contract.set(null); this.visit.set(null); this.template = []; this.visitPage.set(1); await this.refresh(); }
  async openVisit(id: string): Promise<void> {
    if (!this.allowed() || this.busy()) return; const generation = this.generation, access = this.auth.accessSignature(), version = ++this.detailVersion;
    this.visit.set(null); this.checkEdits = {}; this.completionNotes = ''; this.error.set(''); this.loading.set(true);
    try {
      const value = await this.api.get<Visit>(`maintenance-visits/${id}`);
      if (!this.current(generation, access) || version !== this.detailVersion) return; this.setVisit(value, id);
    } catch (error) { if (this.current(generation, access) && version === this.detailVersion) this.error.set(maintenanceError(error)); }
    finally { if (this.current(generation, access) && version === this.detailVersion) this.loading.set(false); }
  }
  async saveConfiguration(): Promise<void> {
    const c = this.contract(); if (!c || !this.canManage() || this.busy() || !this.templateValid()) return;
    await this.mutation(async () => {
      const result = await this.api.command<Contract>(`maintenance-contracts/${c.id}/configure`, { active: this.active, automaticPlanning: this.automatic, planningLeadDays: Number(this.leadDays), checklistTemplate: this.template.map(x => ({ ...x })) }, c.revision);
      return () => { this.setContract(result, c.id); this.notice.set('Wartungsablauf gespeichert. Bereits angelegte Termine behalten ihre eigene Checkliste.'); };
    });
  }
  async plan(): Promise<void> {
    const c = this.contract(); if (!c || !this.canManage() || this.busy() || !c.active || this.configurationChanged() || !c.checklistTemplate.length) return;
    await this.mutation(async () => {
      const result = await this.api.command<Visit>(`maintenance-contracts/${c.id}/schedule`, { expectedDueDate: c.nextDueDate }, c.revision);
      return () => { this.setVisit(result); this.notice.set('Wartungsprojekt und Checkliste sind angelegt. Ein bereits vorhandener Termin wird wieder geöffnet.'); };
    });
  }
  async saveCheck(check: Check): Promise<void> {
    const v = this.visit(); if (!v || !this.checkEnabled(check)) return; const edit = this.checkEdits[check.id];
    const pendingEdits = structuredClone(this.checkEdits), completionNotes = this.completionNotes;
    await this.mutation(async () => {
      const result = await this.api.command<Visit>(`maintenance-visits/${v.id}/check`, { checkId: check.id, state: edit.state, note: edit.note }, v.revision);
      return () => { this.setVisit(result, v.id);
        for (const item of result.checklist) if (item.id !== check.id && pendingEdits[item.id]) this.checkEdits[item.id] = pendingEdits[item.id];
        this.completionNotes = completionNotes; this.notice.set('Prüfergebnis gespeichert.'); };
    });
  }
  async complete(): Promise<void> {
    const v = this.visit(); if (!v || !this.completeEnabled()) return;
    await this.mutation(async () => {
      const result = await this.api.command<Visit>(`maintenance-visits/${v.id}/complete`, { notes: this.completionNotes }, v.revision);
      // Retain the confirmed completion even when the subsequent list refresh fails.
      // Re-select the contract to obtain its new revision before scheduling another occurrence.
      return () => { this.setVisit(result, v.id); if (this.contract()?.id === v.contractId) { this.contract.set(null); this.template = []; }
        this.notice.set('Wartung und Projekt abgeschlossen. Der Folgetermin wurde vom ursprünglichen Vertragsdatum berechnet.'); };
    });
  }
  private async mutation(work: () => Promise<() => void>): Promise<void> {
    const generation = this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set(''); this.notice.set('');
    try { const apply = await work(); if (!this.current(generation, access)) return; apply(); await this.refresh(); }
    catch (error) { if (this.current(generation, access)) { this.error.set(maintenanceError(error)); if (error instanceof ApiError && [403, 404].includes(error.status)) { this.visit.set(null); this.contract.set(null); this.checkEdits = {}; this.template = []; } } }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  private validatePage(value: Page<unknown>): void { if (!value || !Array.isArray(value.items) || !Number.isInteger(value.total) || value.total < 0) throw new Error('Die Wartungsliste konnte nicht eindeutig gelesen werden. Bitte aktualisieren.'); }
  private setContract(value: Contract, expectedId: string): void {
    if (!value) throw new Error('Der Wartungsvertrag passt nicht zur angeforderten Auswahl.');
    // Stored contracts from before this workflow are returned as raw JSON. Only
    // absent new fields receive legacy defaults; null or malformed data stays invalid.
    const normalized = { ...value, checklistTemplate: value.checklistTemplate === undefined ? [] : value.checklistTemplate,
      automaticPlanning: value.automaticPlanning === undefined ? false : value.automaticPlanning,
      planningLeadDays: value.planningLeadDays === undefined ? 14 : value.planningLeadDays };
    if (normalized.id !== expectedId || !Number.isSafeInteger(normalized.revision) || normalized.revision < 1 || typeof normalized.active !== 'boolean'
      || typeof normalized.automaticPlanning !== 'boolean' || !Number.isInteger(normalized.planningLeadDays) || normalized.planningLeadDays < 0 || normalized.planningLeadDays > 365
      || !Array.isArray(normalized.checklistTemplate) || normalized.checklistTemplate.length > 50
      || normalized.checklistTemplate.some(x => !x || typeof x.title !== 'string' || typeof x.instructions !== 'string')) throw new Error('Der Wartungsvertrag passt nicht zur angeforderten Auswahl.');
    this.contract.set(normalized); this.active = normalized.active; this.automatic = normalized.automaticPlanning; this.leadDays = normalized.planningLeadDays;
    this.template = normalized.checklistTemplate.map(x => ({ ...x }));
  }
  private setVisit(value: Visit, expectedId?: string): void {
    if (!value || (expectedId && value.id !== expectedId) || !value.id || !value.projectId || !value.contractId || !value.terms || typeof value.terms.scope !== 'string'
      || !Number.isSafeInteger(value.revision) || value.revision < 1 || !['Planned', 'Completed'].includes(value.status) || !/^[a-f0-9]{64}$/.test(value.scopeDigest)
      || !Array.isArray(value.checklist) || value.checklist.length < 1 || value.checklist.length > 50 || new Set(value.checklist.map(x => x.id)).size !== value.checklist.length
      || value.checklist.some(x => !x.id || typeof x.title !== 'string' || typeof x.instructions !== 'string' || typeof x.note !== 'string' || !['Pending', 'Done', 'NotApplicable'].includes(x.state))) throw new Error('Der Wartungstermin enthält unvollständige oder unpassende Prüfdaten. Bitte neu öffnen.');
    this.visit.set(value); this.checkEdits = Object.fromEntries(value.checklist.map(x => [x.id, { state: x.state, note: x.note }])); this.completionNotes = '';
  }
}

function maintenanceError(error: unknown): string {
  if (error instanceof ApiError) {
    const messages: Record<string, string> = {
      maintenance_incomplete: 'Speichern Sie zuerst ein Ergebnis für jeden Prüfschritt.',
      maintenance_sealed: 'Diese Wartung wurde bereits abgeschlossen. Ihre Nachweise bleiben unverändert erhalten.',
      maintenance_recurrence_in_use: 'Für diesen Vertrag wurden bereits Termine geplant. Kunde, Gebäude und Wiederholungsrhythmus bleiben erhalten; verwenden Sie für geänderte Vertragsbedingungen einen neuen Vertrag.',
      maintenance_visit_required: 'Zu dieser Fälligkeit gehört eine Checkliste. Öffnen und schließen Sie den zugehörigen Wartungstermin ab.',
      maintenance_project_mismatch: 'Vertrag, Gebäude und Wartungsprojekt stimmen nicht mehr überein. Prüfen Sie die Zuordnung.',
      maintenance_evidence: 'Die gespeicherten Wartungsnachweise sind widersprüchlich. Eine Änderung wurde nicht bestätigt.',
      stale_occurrence: 'Der Vertrag ist pausiert oder der Folgetermin hat sich geändert. Öffnen Sie den Vertrag erneut.',
      contract_expired: 'Diese Fälligkeit liegt nach dem Vertragsende.'
    };
    if (messages[error.problem.code || '']) return messages[error.problem.code || ''];
  }
  return errorMessage(error);
}
