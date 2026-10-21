import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';
import { businessLocalTime, businessOffset, businessTimeChoices, resolveEditedBusinessTime } from '../core/business-time';
import { PlanningAssignment, PlanningDay, PlanningFilters, PlanningResponse, mondayOf,
  shiftBusinessWeek, validPlanningResponse } from '../core/planning-week';

type LookupKind = 'projects' | 'employees' | 'equipment';
interface Lookup { items: Entity[]; search: string; page: number; total: number }
const emptyLookup = (): Lookup => ({ items: [], search: '', page: 1, total: 0 });

@Component({
  imports: [FormsModule, RouterLink], templateUrl: './planning-week.component.html',
  styles: [`
    .week-board { display:grid; grid-template-columns:repeat(7,minmax(0,1fr)); gap:.65rem; align-items:start; }
    .week-day { min-width:0; border:1px solid var(--border,#c8d0db); border-radius:.8rem; padding:.65rem; min-height:9rem; }
    .week-day h3 { font-size:.95rem; margin:0 0 .65rem; }
    .dispatch-entry { border-left:3px solid var(--accent,#2456a3); padding:.5rem; margin:.5rem 0; background:color-mix(in srgb,currentColor 5%,transparent); border-radius:.25rem; overflow-wrap:anywhere; }
    .dispatch-entry strong { display:block; line-height:1.35; }
    .dispatch-entry p { margin:.25rem 0; font-size:.83rem; line-height:1.4; }
    .dispatch-entry button { margin-top:.4rem; }
    .dispatch-toolbar { display:flex; flex-wrap:wrap; gap:.65rem; align-items:end; margin-bottom:1rem; }
    .dispatch-toolbar input { max-width:12rem; }
    .lookup-grid { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:1rem; margin:1rem 0; }
    .lookup-grid>div { min-width:0; }
    .lookup-grid .button { margin:.4rem .25rem .4rem 0; }
    .lookup-grid select,.lookup-grid input { max-width:100%; }
    .resource-choice { display:flex; align-items:center; gap:.5rem; margin:.4rem 0; }
    .resource-choice input { width:auto; }
    fieldset { border:0; padding:0; margin:0; min-width:0; }
    .editor { scroll-margin-top:1rem; }
    .zone-note,.empty { font-size:.9rem; }
    @media (max-width:1200px) { .week-board { grid-template-columns:repeat(3,minmax(0,1fr)); } }
    @media (max-width:620px) { .week-board,.lookup-grid { grid-template-columns:1fr; } }
  `]
})
export class PlanningWeekComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly allowed = computed(() => this.auth.hasModule('field') && this.auth.can('schedule-assignments.read'));
  readonly writable = computed(() => this.allowed() && this.auth.can('schedule-assignments.write') && !this.auth.recoveryHold());
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly week = signal<PlanningResponse | null>(null);
  readonly editorOpen = signal(false);
  readonly lookupKinds: LookupKind[] = ['projects', 'employees', 'equipment'];
  readonly labels: Record<LookupKind, string> = { projects: 'Projekte', employees: 'Mitarbeiter', equipment: 'Geräte / Fahrzeuge' };
  lookups: Record<LookupKind, Lookup> = { projects: emptyLookup(), employees: emptyLookup(), equipment: emptyLookup() };
  filterProject = ''; filterEmployee = ''; filterEquipment = '';
  monday = mondayOf(new Date().toISOString().slice(0, 10));
  title = ''; projectId = ''; employeeId = ''; equipmentIds: string[] = [];
  startLocal = ''; endLocal = ''; startOffset = ''; endOffset = ''; cancelled = false;
  editing: PlanningAssignment | null = null;
  private generation = 0;
  private initialWeek = true;
  constructor() {
    const requested = new URLSearchParams(location.search).get('weekStart');
    if (requested) {
      try { this.monday = mondayOf(requested); this.initialWeek = false; }
      catch { /* A malformed bookmark uses the current business week. */ }
    }
    inject(DestroyRef).onDestroy(() => this.generation++);
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  private current(version: number, access: string): boolean {
    return version === this.generation && this.auth.accessSignature() === access;
  }
  private reset(): void {
    this.generation++; this.week.set(null); this.editorOpen.set(false); this.editing = null;
    this.lookups = { projects: emptyLookup(), employees: emptyLookup(), equipment: emptyLookup() };
    this.filterProject = ''; this.filterEmployee = ''; this.filterEquipment = '';
    this.clearForm(); this.error.set(''); this.notice.set(''); this.busy.set(false);
    if (this.allowed()) void this.load();
  }
  private clearForm(): void {
    this.title = ''; this.projectId = ''; this.employeeId = ''; this.equipmentIds = [];
    this.startLocal = ''; this.endLocal = ''; this.startOffset = ''; this.endOffset = ''; this.cancelled = false;
  }
  canLookup(kind: LookupKind): boolean {
    return this.allowed() && this.auth.can(`${kind}.read`)
      && (kind !== 'equipment' || this.auth.hasModule('purchasing'));
  }
  private filters(): PlanningFilters {
    return { employeeId: this.filterEmployee || null, projectId: this.filterProject || null, equipmentId: this.filterEquipment || null };
  }
  chooseWeek(date: string): void {
    if (this.busy() || this.editorOpen()) return;
    try { this.monday = mondayOf(date); this.initialWeek = false; this.closeEditor(); void this.load(); }
    catch (error) { this.error.set(errorMessage(error)); }
  }
  navigate(weeks: number): void {
    if (this.busy()) return;
    try { this.chooseWeek(shiftBusinessWeek(this.monday, weeks)); }
    catch (error) { this.error.set(errorMessage(error)); }
  }
  applyFilters(): void { if (!this.busy()) { this.closeEditor(); void this.load(); } }
  async load(retainNotice = false): Promise<void> {
    if (!this.allowed()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), monday = this.monday, filters = this.filters();
    const query = new URLSearchParams({ weekStart: monday });
    for (const [key, value] of Object.entries(filters)) if (value) query.set(key, value);
    this.busy.set(true); this.week.set(null); this.error.set(''); if (!retainNotice) this.notice.set('');
    let redirect: string | null = null;
    try {
      const result = validPlanningResponse(await this.api.get<PlanningResponse>(`planning/week?${query}`), monday, filters);
      if (!this.current(generation, access)) return;
      if (this.initialWeek) {
        this.initialWeek = false;
        const businessMonday = mondayOf(businessLocalTime(new Date().toISOString(), result.timeZone).slice(0, 10));
        if (businessMonday !== monday) redirect = businessMonday;
      }
      if (!redirect) this.week.set(result);
    } catch (error) { if (this.current(generation, access)) this.error.set(errorMessage(error)); }
    finally {
      if (this.current(generation, access)) {
        this.busy.set(false);
        if (redirect) { this.monday = redirect; void this.load(retainNotice); }
      }
    }
  }
  async search(kind: LookupKind, page = 1): Promise<void> {
    if (!this.canLookup(kind) || this.busy() || page < 1) return;
    const generation = ++this.generation, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set('');
    try {
      const result = await this.api.list(kind, this.lookups[kind].search, page);
      if (!this.current(generation, access)) return;
      if (!Array.isArray(result.items) || !Number.isInteger(result.total) || result.total < 0
          || result.page !== page || result.pageSize !== 25
          || result.items.some(item => !item || typeof item.id !== 'string' || typeof item['name'] !== 'string'))
        throw new Error('Die Stammdatenliste ist unvollständig.');
      this.lookups[kind] = { ...this.lookups[kind], items: result.items.filter(item => !item.archived), total: result.total, page };
    } catch (error) { if (this.current(generation, access)) this.error.set(errorMessage(error)); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  options(kind: LookupKind): Entity[] {
    const items = [...this.lookups[kind].items];
    const selected = this.editing;
    const previous = !selected ? [] : kind === 'projects'
      ? [{ id: selected.projectId, revision: 1, name: selected.projectName }]
      : kind === 'employees' ? [{ id: selected.employeeId, revision: 1, name: selected.employeeName }]
        : selected.equipment.map(item => ({ ...item, revision: 1 }));
    for (const item of previous) if (!items.some(value => value.id === item.id)) items.unshift(item);
    return items;
  }
  newAssignment(day = this.monday): void {
    if (!this.writable() || this.busy() || this.editorOpen() || !this.week()) return;
    this.editing = null; this.clearForm(); this.startLocal = `${day}T08:00`; this.endLocal = `${day}T16:00`;
    this.editorOpen.set(true); this.error.set(''); this.notice.set('');
    setTimeout(() => document.getElementById('dispatch-title')?.focus());
  }
  edit(assignment: PlanningAssignment): void {
    const current = this.week();
    if (!this.writable() || this.busy() || this.editorOpen() || !current || !current.assignments.includes(assignment)) return;
    this.editing = assignment; this.title = assignment.title; this.projectId = assignment.projectId;
    this.employeeId = assignment.employeeId; this.equipmentIds = [...assignment.equipmentIds]; this.cancelled = false;
    this.startLocal = businessLocalTime(assignment.start, current.timeZone); this.endLocal = businessLocalTime(assignment.end, current.timeZone);
    this.startOffset = businessOffset(assignment.start, current.timeZone); this.endOffset = businessOffset(assignment.end, current.timeZone);
    this.editorOpen.set(true); this.error.set(''); this.notice.set('');
    setTimeout(() => document.getElementById('dispatch-title')?.focus());
  }
  closeEditor(): void { this.editorOpen.set(false); this.editing = null; this.clearForm(); }
  toggleEquipment(id: string, checked: boolean): void {
    this.equipmentIds = checked ? [...new Set([...this.equipmentIds, id])] : this.equipmentIds.filter(value => value !== id);
  }
  choices(local: string) { return this.week() ? businessTimeChoices(local, this.week()!.timeZone) : []; }
  validTime(local: string, offset: string): boolean {
    const choices = this.choices(local);
    return choices.length === 1 || choices.some(choice => choice.offset === offset);
  }
  async save(): Promise<void> {
    if (!this.writable() || this.busy() || !this.editorOpen() || !this.week()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), zone = this.week()!.timeZone;
    this.busy.set(true); this.error.set(''); this.notice.set(''); let saved = false;
    try {
      if (!this.options('projects').some(item => item.id === this.projectId)
          || !this.options('employees').some(item => item.id === this.employeeId && item['active'] !== false)
          || this.equipmentIds.some(id => !this.options('equipment').some(item => item.id === id)))
        throw new Error('Projekt, Mitarbeiter und Betriebsmittel aus den aktuellen Listen auswählen.');
      if (!this.title.trim() || this.title.length > 200) throw new Error('Eine Einsatzbezeichnung mit höchstens 200 Zeichen ist erforderlich.');
      const start = resolveEditedBusinessTime(this.startLocal, zone, this.startOffset, this.editing?.start);
      const end = resolveEditedBusinessTime(this.endLocal, zone, this.endOffset, this.editing?.end);
      if (Date.parse(end) <= Date.parse(start)) throw new Error('Das Einsatzende muss nach dem Beginn liegen.');
      if (Date.parse(end) - Date.parse(start) > 31 * 86_400_000) throw new Error('Ein Einsatz darf höchstens 31 Tage dauern.');
      const result = await this.api.save('schedule-assignments', {
        projectId: this.projectId, employeeId: this.employeeId, title: this.title.trim(), start, end,
        cancelled: this.cancelled, equipmentIds: [...this.equipmentIds]
      }, this.editing ? { id: this.editing.id, revision: this.editing.revision } : undefined);
      if (!this.current(generation, access)) return;
      if (!result?.id || !Number.isInteger(result.revision) || result.revision < 1)
        throw new Error('Die Speicherung wurde nicht vollständig bestätigt. Prüfen Sie den Plan vor einer Wiederholung.');
      this.notice.set(this.cancelled ? 'Einsatz abgesagt. Die Änderung bleibt in der Einsatzakte erhalten.' : 'Einsatz gespeichert. Der Wochenplan wird aktualisiert.');
      this.closeEditor(); saved = true;
    } catch (error) { if (this.current(generation, access)) this.error.set(errorMessage(error)); }
    finally {
      if (this.current(generation, access)) { this.busy.set(false); if (saved) void this.load(true); }
    }
  }
  entries(day: PlanningDay): PlanningAssignment[] { return this.week()?.assignments.filter(item => item.days.includes(day.date)) ?? []; }
  formatDate(date: string): string {
    return new Intl.DateTimeFormat('de-DE', { weekday: 'short', day: '2-digit', month: '2-digit', timeZone: 'UTC' }).format(new Date(date + 'T12:00:00Z'));
  }
  formatInstant(instant: string): string {
    return new Intl.DateTimeFormat('de-DE', { timeZone: this.week()?.timeZone, day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', timeZoneName: 'shortOffset' }).format(new Date(instant));
  }
}
