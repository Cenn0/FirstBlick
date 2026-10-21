import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';
import { localTimeChoices, resolveLocalTime } from '../core/local-time';

export interface PlanningDay { date: string; startUtc: string; endUtc: string }
export interface PlanningAssignment {
  id: string; revision: number; employeeId: string; employeeName: string;
  projectId: string; projectName: string; title: string; start: string; end: string;
  equipmentIds: string[]; days: string[];
}
export interface PlanningResponse {
  weekStart: string; timeZone: string; days: PlanningDay[]; assignments: PlanningAssignment[];
  count: number; maxAssignments: number; note: string;
}
export function mondayOf(date: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) throw new Error('Ein gültiges Kalenderdatum auswählen.');
  const day = new Date(`${date}T12:00:00.000Z`);
  if (!Number.isFinite(day.getTime()) || day.toISOString().slice(0, 10) !== date)
    throw new Error('Ein gültiges Kalenderdatum auswählen.');
  const offset = (day.getUTCDay() + 6) % 7;
  day.setUTCDate(day.getUTCDate() - offset);
  return day.toISOString().slice(0, 10);
}
export function shiftBusinessWeek(monday: string, weeks: number): string {
  const date = new Date(`${mondayOf(monday)}T12:00:00.000Z`);
  date.setUTCDate(date.getUTCDate() + weeks * 7);
  return date.toISOString().slice(0, 10);
}
export function validPlanningResponse(raw: PlanningResponse, monday: string): PlanningResponse {
  if (!raw || raw.weekStart !== monday || !Array.isArray(raw.days) || raw.days.length !== 7 ||
      !Array.isArray(raw.assignments) || !Number.isInteger(raw.count) ||
      raw.count !== raw.assignments.length || raw.count > 1000 ||
      raw.maxAssignments !== 1000 || typeof raw.timeZone !== 'string')
    throw new Error('Der serverseitige Wochenplan ist unvollständig.');
  try { new Intl.DateTimeFormat('de-DE', { timeZone: raw.timeZone }).format(new Date()); }
  catch { throw new Error('Die Zeitzone des Betriebs ist ungültig.'); }
  for (let i = 0; i < 7; i++) {
    if (raw.days[i].date !== shiftBusinessWeek(monday, 0) && i === 0)
      throw new Error('Die Tagesfolge stimmt nicht mit dem gewählten Wochenbeginn überein.');
    const expected = new Date(`${monday}T12:00:00.000Z`);
    expected.setUTCDate(expected.getUTCDate() + i);
    if (raw.days[i].date !== expected.toISOString().slice(0, 10) ||
        !Number.isFinite(Date.parse(raw.days[i].startUtc)) || !Number.isFinite(Date.parse(raw.days[i].endUtc)) ||
        Date.parse(raw.days[i].endUtc) <= Date.parse(raw.days[i].startUtc))
      throw new Error('Die Geschäftstage des Plans sind ungültig.');
    if (i && raw.days[i - 1].endUtc !== raw.days[i].startUtc)
      throw new Error('Die Geschäftstage sind nicht lückenlos.');
  }
  const ids = new Set<string>();
  for (const assignment of raw.assignments) {
    if (ids.has(assignment.id) || !/^[a-f0-9-]{36}$/i.test(assignment.id) ||
        !/^[a-f0-9-]{36}$/i.test(assignment.employeeId) ||
        !/^[a-f0-9-]{36}$/i.test(assignment.projectId) ||
        !Number.isInteger(assignment.revision) || assignment.revision < 1 ||
        typeof assignment.title !== 'string' || typeof assignment.projectName !== 'string' ||
        typeof assignment.employeeName !== 'string' || !Array.isArray(assignment.days) ||
        !Array.isArray(assignment.equipmentIds) ||
        !Number.isFinite(Date.parse(assignment.start)) || !Number.isFinite(Date.parse(assignment.end)) ||
        Date.parse(assignment.end) <= Date.parse(assignment.start))
      throw new Error('Ein Einsatz enthält widersprüchliche Angaben.');
    ids.add(assignment.id);
    const covered = raw.days.filter(day =>
      Date.parse(assignment.start) < Date.parse(day.endUtc) &&
      Date.parse(assignment.end) > Date.parse(day.startUtc)).map(day => day.date);
    if (JSON.stringify(covered) !== JSON.stringify(assignment.days) || !covered.length)
      throw new Error('Ein Einsatz ist falsch den Geschäftstagen zugeordnet.');
  }
  return raw;
}

@Component({
  imports: [FormsModule, RouterLink],
  templateUrl: './planning-week.component.html',
  styles: [`
    .week-board { display:grid; grid-template-columns: repeat(7,minmax(0,1fr)); gap: .65rem; align-items: start; }
    .week-day { min-width:0; border:1px solid var(--border, #c8d0db); border-radius: .8rem; padding:.7rem; min-height:9rem; }
    .week-day h3 { font-size: .95rem; margin:0 0 .65rem; }
    .dispatch-entry { border-left:3px solid var(--accent, #2456a3); padding:.5rem; margin:.5rem 0; background:color-mix(in srgb, currentColor 5%, transparent); border-radius:.25rem; overflow-wrap:anywhere; }
    .dispatch-entry strong { display:block; line-height:1.35; }
    .dispatch-entry p { margin:.25rem 0; font-size:.83rem; line-height:1.4; }
    .dispatch-empty { font-size:.82rem; opacity:.75; }
    .dispatch-toolbar { display:flex; flex-wrap:wrap; gap:.65rem; align-items:center; margin-bottom:1rem; }
    .dispatch-toolbar input { max-width:12rem; }
    @media (max-width: 1100px) { .week-board { grid-template-columns: repeat(3,minmax(0,1fr)); } }
    @media (max-width: 620px) { .week-board { grid-template-columns: 1fr; } }
  `]
})
export class PlanningWeekComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly allowed = computed(() => this.auth.hasModule('field') && this.auth.can('schedule-assignments.read'));
  readonly writable = computed(() => this.allowed() && this.auth.can('schedule-assignments.write') &&
    !this.auth.recoveryHold());
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly week = signal<PlanningResponse | null>(null);
  readonly projects = signal<Entity[]>([]);
  readonly employees = signal<Entity[]>([]);
  readonly deviceZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  readonly canCreate = computed(() => this.writable() && this.week()?.timeZone === this.deviceZone);
  monday = mondayOf(new Date().toLocaleDateString('en-CA'));
  title = '';
  projectId = '';
  employeeId = '';
  projectSearch = '';
  employeeSearch = '';
  projectPage = 1;
  employeePage = 1;
  projectTotal = 0;
  employeeTotal = 0;
  startLocal = '';
  endLocal = '';
  startOffset = '';
  endOffset = '';
  readonly timeChoices = localTimeChoices;
  private generation = 0;
  constructor() {
    inject(DestroyRef).onDestroy(() => this.generation++);
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  private current(version: number, access: string): boolean {
    return version === this.generation && this.auth.accessSignature() === access;
  }
  private reset(): void {
    this.generation++;
    this.week.set(null); this.projects.set([]); this.employees.set([]);
    this.projectId = ''; this.employeeId = ''; this.title = '';
    this.projectSearch = ''; this.employeeSearch = '';
    this.projectPage = 1; this.employeePage = 1; this.projectTotal = 0; this.employeeTotal = 0;
    this.startLocal = ''; this.endLocal = ''; this.startOffset = ''; this.endOffset = '';
    this.error.set(''); this.notice.set(''); this.busy.set(false);
    if (this.allowed()) void this.load();
  }
  chooseWeek(date: string): void {
    try { this.monday = mondayOf(date); this.week.set(null); void this.load(); }
    catch (e) { this.error.set(errorMessage(e)); }
  }
  navigate(weeks: number): void {
    this.monday = shiftBusinessWeek(this.monday, weeks); this.week.set(null); void this.load();
  }
  async load(): Promise<void> {
    if (!this.allowed()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), monday = this.monday;
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      const response = await this.api.get<PlanningResponse>(`planning/week?weekStart=${encodeURIComponent(monday)}`);
      if (this.current(generation, access)) this.week.set(validPlanningResponse(response, monday));
    } catch (e) { if (this.current(generation, access)) { this.week.set(null); this.error.set(errorMessage(e)); } }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async search(type: 'project' | 'employee', page = 1): Promise<void> {
    if (!this.writable() || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set('');
    try {
      const result = await this.api.list(type === 'project' ? 'projects' : 'employees',
        type === 'project' ? this.projectSearch : this.employeeSearch, page);
      if (!this.current(generation, access)) return;
      if (!Array.isArray(result.items) || !Number.isInteger(result.total))
        throw new Error('Die Person-/Projektliste ist unvollständig.');
      if (type === 'project') { this.projects.set(result.items.filter(x => !x.archived)); this.projectTotal = result.total; this.projectPage = page; this.projectId = ''; }
      else { this.employees.set(result.items.filter(x => !x.archived && x['active'] !== false)); this.employeeTotal = result.total; this.employeePage = page; this.employeeId = ''; }
    } catch (e) { if (this.current(generation, access)) this.error.set(errorMessage(e)); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async save(): Promise<void> {
    if (!this.canCreate() || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      if (!this.projects().some(x => x.id === this.projectId) ||
          !this.employees().some(x => x.id === this.employeeId))
        throw new Error('Bitte aktuelle Projekt- und Mitarbeiterlisten verwenden.');
      if (!this.title.trim() || this.title.length > 240) throw new Error('Eine kurze Einsatzbezeichnung ist erforderlich.');
      const start = resolveLocalTime(this.startLocal, this.startOffset);
      const end = resolveLocalTime(this.endLocal, this.endOffset);
      if (Date.parse(end) <= Date.parse(start)) throw new Error('Das Einsatzende muss nach dem Beginn liegen.');
      const result = await this.api.save('schedule-assignments',
        { projectId: this.projectId, employeeId: this.employeeId, title: this.title.trim(),
          start, end, cancelled: false, equipmentIds: [] });
      if (!this.current(generation, access)) return;
      if (!result?.id) throw new Error('Die Speicherung wurde nicht als abgeschlossen bestätigt.');
      this.notice.set('Einsatz gespeichert. Die serverseitigen Planungsinvarianten wurden geprüft.');
      this.title = ''; this.startLocal = ''; this.endLocal = ''; this.startOffset = ''; this.endOffset = '';
    } catch (e) { if (this.current(generation, access)) this.error.set(errorMessage(e)); }
    finally { if (this.current(generation, access)) { this.busy.set(false); void this.loadAfterSave(generation, access); } }
  }
  private async loadAfterSave(generation: number, access: string): Promise<void> {
    if (!this.current(generation, access)) return;
    const success = this.notice();
    await this.load();
    if (this.auth.accessSignature() === access && success) this.notice.set(success);
  }
  entries(day: PlanningDay): PlanningAssignment[] {
    return this.week()?.assignments.filter(assignment => assignment.days.includes(day.date)) ?? [];
  }
  formatDate(date: string): string {
    return new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(date + 'T12:00:00Z'));
  }
  formatTime(instant: string): string {
    const zone = this.week()?.timeZone;
    return new Intl.DateTimeFormat('de-DE', { timeZone: zone, hour: '2-digit', minute: '2-digit' }).format(new Date(instant));
  }
}
