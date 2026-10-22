import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { RecordChoice, recordId } from '../core/ledger-contracts';
import { DependencyGraph, DependencyTask, DependencyTaskStatus, blockedBy, taskStatuses, validDependencyChange, validDependencyGraph, wouldCreateTaskCycle } from '../core/task-dependencies.models';
import { ApiError, errorMessage } from '../core/transport';
import { LedgerPickerComponent } from './ledger-picker.component';

@Component({ imports: [FormsModule, RouterLink, LedgerPickerComponent], templateUrl: './task-dependencies.component.html',
  styles: ['.dependency-layout{display:grid;grid-template-columns:minmax(200px,.8fr) minmax(280px,1.2fr);gap:1rem}.task-choice{display:block;width:100%;text-align:left;background:transparent;border:0;border-bottom:1px solid var(--line,#ddd);padding:.8rem;overflow-wrap:anywhere}.task-choice.active{background:var(--surface-soft,#f2f5f4)}.task-choice span{display:block;font-size:.8rem;margin-top:.3rem}.predecessor-choice{display:flex;align-items:flex-start;gap:.6rem;padding:.6rem 0}.predecessor-choice input{width:auto;margin-top:.3rem}.predecessor-choice small{display:block}.dependency-actions{display:flex;gap:.5rem;flex-wrap:wrap;margin-top:1rem}.dependency-panel{min-width:0}.dependency-panel li{overflow-wrap:anywhere}@media(max-width:720px){.dependency-layout{grid-template-columns:1fr}}'] })
export class TaskDependenciesComponent {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService); private readonly route = inject(ActivatedRoute);
  readonly readable = computed(() => ['ManagingDirector', 'Office', 'SiteManager', 'Accounting'].includes(this.auth.tenant()?.role || '') && this.auth.can('tasks.read') && this.auth.can('projects.read'));
  readonly writable = computed(() => this.readable() && this.auth.hasModule('core') && this.auth.can('tasks.write'));
  readonly graph = signal<DependencyGraph | null>(null); readonly selectedId = signal(''); readonly projectChoice = signal<RecordChoice | null>(null);
  readonly selected = computed(() => this.graph()?.tasks.find(task => task.id === this.selectedId()) ?? null);
  readonly activeTasks = computed(() => this.graph()?.tasks.filter(task => !task.archived) ?? []);
  readonly loading = signal(false); readonly busy = signal(false); readonly stale = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly statuses = taskStatuses; readonly statusKeys = Object.keys(taskStatuses) as DependencyTaskStatus[];
  chosenPredecessors: string[] = []; nextStatus: DependencyTaskStatus = 'Open'; search = '';
  private generation = 0; private lastAccess = ''; private readonly queryProject = signal(this.route.snapshot.queryParamMap.get('projectId') || '');
  constructor() {
    const subscription = this.route.queryParamMap.subscribe(query => this.queryProject.set(query.get('projectId') || ''));
    effect(() => {
      const access = this.auth.accessSignature(), id = this.queryProject();
      untracked(() => {
        const changedAccess = !!this.lastAccess && this.lastAccess !== access; this.lastAccess = access; this.reset();
        if (!changedAccess && this.readable() && recordId(id)) void this.load(id);
      });
    });
    inject(DestroyRef).onDestroy(() => { this.generation++; subscription.unsubscribe(); });
  }
  private reset(): void { this.generation++; this.graph.set(null); this.selectedId.set(''); this.projectChoice.set(null); this.loading.set(false); this.busy.set(false); this.stale.set(false); this.error.set(''); this.notice.set(''); this.chosenPredecessors = []; this.search = ''; }
  private context() { return { generation: this.generation, access: this.auth.accessSignature(), tenant: this.auth.activeTenantId() }; }
  private current(context: { generation: number; access: string; tenant: string }): boolean { return context.generation === this.generation && context.access === this.auth.accessSignature() && context.tenant === this.auth.activeTenantId() && this.readable(); }
  async chooseProject(choice: RecordChoice | null): Promise<void> { if (this.busy()) return; if (choice) await this.load(choice.id); else this.reset(); }
  async load(projectId: string, retainSelection = ''): Promise<void> {
    if (!this.readable() || !recordId(projectId) || this.busy()) return;
    this.generation++; const context = this.context(); this.graph.set(null); this.selectedId.set(''); this.projectChoice.set(null); this.loading.set(true); this.stale.set(false); this.error.set('');
    try {
      const raw = await this.api.get<unknown>(`projects/${projectId}/task-dependencies`); if (!this.current(context)) return;
      const graph = validDependencyGraph(raw, context.tenant, projectId); this.graph.set(graph); this.projectChoice.set({ id: projectId, name: graph.projectName, revision: 1 });
      const task = graph.tasks.find(row => row.id === retainSelection && !row.archived); if (task) this.select(task);
    } catch (error) { if (this.current(context)) { this.stale.set(true); await this.fail(error); } }
    finally { if (this.current(context)) this.loading.set(false); }
  }
  select(task: DependencyTask): void { if (this.busy()) return; this.selectedId.set(task.id); this.chosenPredecessors = [...task.predecessorTaskIds]; this.nextStatus = task.status; this.search = ''; this.error.set(''); this.notice.set(''); }
  blockers(task: DependencyTask): DependencyTask[] { return this.graph() ? blockedBy(this.graph()!, task) : []; }
  candidates(): DependencyTask[] { const graph = this.graph(), task = this.selected(); return !graph || !task ? [] : graph.tasks.filter(row => !row.archived && row.id !== task.id && row.title.toLocaleLowerCase('de').includes(this.search.trim().toLocaleLowerCase('de'))); }
  candidateBlocked(candidate: DependencyTask): boolean { const task = this.selected(), graph = this.graph(); return !task || !graph || wouldCreateTaskCycle(graph, task.id, candidate.id); }
  toggle(id: string, checked: boolean): void { if (this.busy()) return; this.chosenPredecessors = checked ? [...this.chosenPredecessors, id] : this.chosenPredecessors.filter(value => value !== id); }
  async saveDependencies(): Promise<void> { const task = this.selected(); if (!task || task.status !== 'Open') return; await this.mutate('dependencies', { predecessorTaskIds: [...this.chosenPredecessors].sort() }); }
  async saveStatus(): Promise<void> { await this.mutate('dependency-status', { status: this.nextStatus }); }
  private async mutate(action: 'dependencies' | 'dependency-status', body: { predecessorTaskIds: string[] } | { status: DependencyTaskStatus }): Promise<void> {
    const before = this.selected(), graph = this.graph(); if (!before || !graph || !this.writable() || this.busy() || this.stale()) return;
    const context = this.context(); this.busy.set(true); this.error.set(''); this.notice.set(''); let changed = false;
    try {
      const raw = action === 'dependencies' ? await this.api.update<unknown>(`tasks/${before.id}/${action}`, body, before.revision)
        : await this.api.command<unknown>(`tasks/${before.id}/${action}`, body, before.revision);
      if (!this.current(context) || !this.writable()) return;
      validDependencyChange(raw, before, body); changed = true;
    } catch (error) { if (this.current(context)) { this.stale.set(true); await this.fail(error); } }
    finally { if (this.current(context)) this.busy.set(false); }
    if (changed && this.current(context)) {
      await this.load(graph.projectId, before.id);
      if (this.readable() && this.graph()?.projectId === graph.projectId && !this.stale()) this.notice.set(action === 'dependencies' ? 'Abhängigkeiten gespeichert und Projektstand neu geprüft.' : 'Aufgabenstatus gespeichert und Projektstand neu geprüft.');
    }
  }
  private async fail(error: unknown): Promise<void> {
    this.error.set(errorMessage(error));
    if (error instanceof ApiError && [401, 403].includes(error.status)) {
      this.graph.set(null); this.selectedId.set(''); this.projectChoice.set(null);
      try { await this.auth.refresh(); } catch { /* Cleared source content stays hidden. */ }
    }
  }
}
