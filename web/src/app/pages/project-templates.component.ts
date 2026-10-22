import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { RecordChoice, recordId } from '../core/ledger-contracts';
import { ProjectTemplate, TemplateApplication, TemplateApplicationSummary, TemplateDetails, TemplateHistory, TemplatePage, TemplatePreview, TemplateProject, TemplateSummary,
  detailsOf, openTemplateProject, projectStatuses, projectTypes, validProjectTemplate, validTemplateApplied, validTemplateApplication, validTemplateChange,
  validTemplateDetails, validTemplateHistory, validTemplatePage, validTemplatePreview, validTemplateProject } from '../core/project-template.models';
import { ApiError, errorMessage } from '../core/transport';
import { LedgerPickerComponent } from './ledger-picker.component';

@Component({ selector: 'app-project-templates', imports: [FormsModule, RouterLink, LedgerPickerComponent],
  templateUrl: './project-templates.component.html', styleUrl: './project-templates.component.css' })
export class ProjectTemplatesComponent {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService); private readonly route = inject(ActivatedRoute);
  readonly types = projectTypes; readonly statuses = projectStatuses; readonly typeKeys = Object.keys(projectTypes) as (keyof typeof projectTypes)[];
  readonly readable = computed(() => ['ManagingDirector', 'Office', 'Accounting', 'SiteManager'].includes(this.auth.tenant()?.role || '') && this.auth.can('project-templates.read'));
  readonly manageable = computed(() => this.readable() && this.auth.hasModule('core') && ['ManagingDirector', 'Office', 'SiteManager'].includes(this.auth.tenant()?.role || '') && this.auth.can('project-templates.write'));
  readonly historyReadable = computed(() => this.readable() && this.auth.can('projects.read') && this.auth.can('project-template-applications.read'));
  readonly applicable = computed(() => this.readable() && this.auth.hasModule('core') && ['ManagingDirector', 'Office', 'SiteManager'].includes(this.auth.tenant()?.role || '') && this.auth.can('projects.write') && this.auth.can('tasks.write'));
  readonly templates = signal<TemplatePage | null>(null); readonly template = signal<ProjectTemplate | null>(null); readonly project = signal<TemplateProject | null>(null);
  readonly projectChoice = signal<RecordChoice | null>(null); readonly history = signal<TemplateHistory | null>(null); readonly application = signal<TemplateApplication | null>(null);
  readonly preview = signal<TemplatePreview | null>(null); readonly error = signal(''); readonly notice = signal(''); readonly historyError = signal('');
  readonly listLoading = signal(false); readonly templateLoading = signal(false); readonly projectLoading = signal(false); readonly historyLoading = signal(false); readonly applicationLoading = signal(false);
  readonly previewLoading = signal(false); readonly busy = signal(false); readonly editing = signal(false); readonly staleTemplate = signal(false); readonly staleProject = signal(false);
  readonly knownApplied = signal<string[]>([]);
  readonly alreadyApplied = computed(() => !!this.template() && (this.knownApplied().includes(this.template()!.id) || !!this.history()?.items.some(row => row.templateId === this.template()!.id)));
  search = ''; includeArchived = false; anchorMode: 'project' | 'explicit' = 'project'; anchorDate = ''; confirmed = false;
  draft: TemplateDetails = this.emptyDraft();
  private epoch = 0; private listSequence = 0; private templateSequence = 0; private projectSequence = 0; private previewSequence = 0; private historySequence = 0; private applicationSequence = 0;
  private readonly queryProject = signal(this.route.snapshot.queryParamMap.get('projectId') || ''); private lastAccess = '';
  constructor() {
    const subscription = this.route.queryParamMap.subscribe(query => this.queryProject.set(query.get('projectId') || ''));
    effect(() => {
      const access = this.auth.accessSignature(), query = this.queryProject();
      untracked(() => {
        const changedBusinessOrAccess = !!this.lastAccess && this.lastAccess !== access; this.lastAccess = access; this.reset();
        if (this.readable()) { void this.loadTemplates(); if (!changedBusinessOrAccess && query && this.historyReadable()) void this.loadProject(query); }
      });
    });
    inject(DestroyRef).onDestroy(() => { this.epoch++; subscription.unsubscribe(); });
  }
  private context(): { epoch: number; access: string; tenant: string } { return { epoch: this.epoch, access: this.auth.accessSignature(), tenant: this.auth.activeTenantId() }; }
  private current(context: { epoch: number; access: string; tenant: string }): boolean { return context.epoch === this.epoch && context.access === this.auth.accessSignature() && context.tenant === this.auth.activeTenantId() && this.readable(); }
  private reset(): void {
    this.epoch++; this.templates.set(null); this.template.set(null); this.project.set(null); this.projectChoice.set(null); this.history.set(null); this.application.set(null); this.knownApplied.set([]);
    this.error.set(''); this.notice.set(''); this.historyError.set(''); this.listLoading.set(false); this.templateLoading.set(false); this.projectLoading.set(false); this.historyLoading.set(false); this.applicationLoading.set(false);
    this.busy.set(false); this.editing.set(false); this.staleTemplate.set(false); this.staleProject.set(false); this.search = ''; this.includeArchived = false; this.anchorMode = 'project'; this.anchorDate = ''; this.draft = this.emptyDraft(); this.invalidatePreview();
  }
  private emptyDraft(): TemplateDetails { return { name: '', description: '', projectType: 'Renovation', tasks: [{ title: '', description: '', offsetDays: 0 }] }; }
  invalidatePreview(): void { this.previewSequence++; this.preview.set(null); this.previewLoading.set(false); this.confirmed = false; }
  async loadTemplates(page = 1): Promise<void> {
    if (!this.readable() || this.busy()) return;
    const context = this.context(), sequence = ++this.listSequence, archived = this.includeArchived;
    const query = new URLSearchParams({ search: this.search.trim(), page: String(page), includeArchived: String(archived) });
    this.listLoading.set(true); this.templates.set(null); this.error.set('');
    try {
      const raw = await this.api.get<unknown>(`project-templates?${query}`);
      if (this.current(context) && sequence === this.listSequence) this.templates.set(validTemplatePage(raw, context.tenant, page, archived));
    } catch (error) { if (this.current(context) && sequence === this.listSequence) await this.fail(error); }
    finally { if (this.current(context) && sequence === this.listSequence) this.listLoading.set(false); }
  }
  async selectTemplate(row: TemplateSummary | ProjectTemplate): Promise<void> {
    if (!this.readable() || this.busy()) return;
    const context = this.context(), sequence = ++this.templateSequence;
    this.template.set(null); this.templateLoading.set(true); this.editing.set(false); this.staleTemplate.set(false); this.error.set(''); this.notice.set(''); this.invalidatePreview();
    try {
      const raw = await this.api.get<unknown>(`project-templates/${row.id}`);
      if (this.current(context) && sequence === this.templateSequence) this.template.set(validProjectTemplate(raw, context.tenant, row.id));
    } catch (error) { if (this.current(context) && sequence === this.templateSequence) await this.fail(error); }
    finally { if (this.current(context) && sequence === this.templateSequence) this.templateLoading.set(false); }
  }
  create(): void {
    if (!this.manageable() || this.busy()) return;
    this.templateSequence++; this.template.set(null); this.templateLoading.set(false); this.staleTemplate.set(false); this.draft = this.emptyDraft(); this.editing.set(true); this.error.set(''); this.notice.set(''); this.invalidatePreview();
  }
  edit(): void {
    const template = this.template(); if (!template || template.archived || !this.manageable() || this.busy() || this.staleTemplate()) return;
    this.draft = detailsOf(template); this.editing.set(true); this.error.set(''); this.notice.set(''); this.invalidatePreview();
  }
  cancelEdit(): void { if (!this.busy()) { this.editing.set(false); this.draft = this.emptyDraft(); this.invalidatePreview(); } }
  addTask(): void { if (!this.busy() && this.draft.tasks.length < 50) { this.draft.tasks.push({ title: '', description: '', offsetDays: 0 }); this.invalidatePreview(); } }
  removeTask(index: number): void { if (!this.busy() && this.draft.tasks.length > 1) { this.draft.tasks.splice(index, 1); this.invalidatePreview(); } }
  moveTask(index: number, direction: number): void {
    const next = index + direction; if (this.busy() || next < 0 || next >= this.draft.tasks.length) return;
    [this.draft.tasks[index], this.draft.tasks[next]] = [this.draft.tasks[next], this.draft.tasks[index]]; this.invalidatePreview();
  }
  async save(): Promise<void> {
    if (!this.manageable() || !this.editing() || this.busy() || this.staleTemplate()) return;
    const context = this.context(), before = this.template(); let request: TemplateDetails;
    try { request = validTemplateDetails(detailsOf(this.draft)); } catch (error) { this.error.set(errorMessage(error)); return; }
    this.busy.set(true); this.error.set(''); this.notice.set(''); this.invalidatePreview();
    try {
      const raw = before ? await this.api.update<unknown>(`project-templates/${before.id}`, request, before.revision) : await this.api.command<unknown>('project-templates', request);
      if (!this.current(context) || !this.manageable()) return;
      this.template.set(validTemplateChange(raw, context.tenant, request, before ?? undefined)); this.editing.set(false); this.notice.set(before ? 'Projektvorlage gespeichert.' : 'Projektvorlage angelegt.');
    } catch (error) { if (this.current(context)) { if (!(error instanceof ApiError) || [409, 412, 503].includes(error.status)) this.staleTemplate.set(true); await this.fail(error); } }
    finally { if (this.current(context)) this.busy.set(false); }
    if (this.current(context) && !this.editing()) await this.loadTemplates();
  }
  async archive(): Promise<void> {
    const before = this.template(); if (!before || !this.manageable() || this.busy() || this.editing() || this.staleTemplate()) return;
    const context = this.context(), request = { archived: !before.archived }; this.busy.set(true); this.error.set(''); this.notice.set(''); this.invalidatePreview();
    try {
      const raw = await this.api.command<unknown>(`project-templates/${before.id}/archive`, request, before.revision);
      if (!this.current(context) || !this.manageable()) return;
      this.template.set(validTemplateChange(raw, context.tenant, request, before)); this.notice.set(request.archived ? 'Vorlage archiviert. Frühere Anwendungen bleiben erhalten.' : 'Vorlage wiederhergestellt.');
    } catch (error) { if (this.current(context)) { this.staleTemplate.set(true); await this.fail(error); } }
    finally { if (this.current(context)) this.busy.set(false); }
    if (this.current(context) && !this.staleTemplate()) await this.loadTemplates();
  }
  async chooseProject(choice: RecordChoice | null): Promise<void> {
    if (this.busy()) return;
    this.projectChoice.set(choice); if (choice) await this.loadProject(choice.id); else this.clearProject();
  }
  private clearProject(): void {
    this.projectSequence++; this.historySequence++; this.applicationSequence++; this.project.set(null); this.projectChoice.set(null); this.history.set(null); this.application.set(null); this.knownApplied.set([]);
    this.projectLoading.set(false); this.historyLoading.set(false); this.applicationLoading.set(false); this.staleProject.set(false); this.historyError.set(''); this.invalidatePreview();
  }
  async loadProject(id: string, preserveReceipt = false): Promise<void> {
    if (!this.historyReadable() || !recordId(id) || this.busy()) return;
    const context = this.context(), receipt = preserveReceipt ? this.application() : null, applied = preserveReceipt ? this.knownApplied() : [];
    this.clearProject(); const sequence = this.projectSequence; this.application.set(receipt); this.knownApplied.set(applied); this.projectLoading.set(true); this.error.set('');
    try {
      const raw = await this.api.get<unknown>(`projects/${id}`);
      if (!this.current(context) || sequence !== this.projectSequence || !this.historyReadable()) return;
      const project = validTemplateProject(raw, context.tenant, id); this.project.set(project); this.projectChoice.set({ id: project.id, name: project.name, number: project.number ?? undefined, revision: project.revision });
    } catch (error) { if (this.current(context) && sequence === this.projectSequence) { this.staleProject.set(true); await this.fail(error); } }
    finally { if (this.current(context) && sequence === this.projectSequence) this.projectLoading.set(false); }
    if (this.current(context) && sequence === this.projectSequence && this.project()) await this.loadHistory();
  }
  async loadHistory(page = 1): Promise<void> {
    const project = this.project(); if (!project || !this.historyReadable() || this.busy()) return;
    const context = this.context(), sequence = ++this.historySequence, projectSequence = this.projectSequence; this.historyLoading.set(true); this.historyError.set(''); this.history.set(null);
    try {
      const raw = await this.api.get<unknown>(`projects/${project.id}/template-applications?page=${page}`);
      if (this.current(context) && sequence === this.historySequence && projectSequence === this.projectSequence && this.historyReadable()) this.history.set(validTemplateHistory(raw, context.tenant, project.id, page));
    } catch (error) { if (this.current(context) && sequence === this.historySequence && projectSequence === this.projectSequence) { this.historyError.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (this.current(context) && sequence === this.historySequence && projectSequence === this.projectSequence) this.historyLoading.set(false); }
  }
  async openApplication(summary: TemplateApplicationSummary): Promise<void> {
    if (!this.historyReadable() || this.busy()) return;
    const context = this.context(), sequence = ++this.applicationSequence, projectSequence = this.projectSequence; this.application.set(null); this.applicationLoading.set(true); this.historyError.set('');
    try {
      const raw = await this.api.get<unknown>(`project-template-applications/${summary.id}`);
      if (this.current(context) && sequence === this.applicationSequence && projectSequence === this.projectSequence && this.historyReadable()) this.application.set(validTemplateApplication(raw, context.tenant, summary.projectId, summary));
    } catch (error) { if (this.current(context) && sequence === this.applicationSequence && projectSequence === this.projectSequence) { this.historyError.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (this.current(context) && sequence === this.applicationSequence && projectSequence === this.projectSequence) this.applicationLoading.set(false); }
  }
  canPreview(): boolean {
    const template = this.template(), project = this.project();
    return this.applicable() && !!template && !!project && !template.archived && openTemplateProject(project) && template.projectType === project.type && !this.alreadyApplied()
      && !this.busy() && !this.editing() && !this.staleProject() && !this.staleTemplate() && !this.templateLoading() && !this.projectLoading()
      && (this.anchorMode === 'explicit' ? !!this.anchorDate : !!project.plannedStart);
  }
  async loadPreview(): Promise<void> {
    if (!this.canPreview() || this.previewLoading()) return;
    const template = this.template()!, project = this.project()!, context = this.context(), anchorDate = this.anchorMode === 'explicit' ? this.anchorDate : null;
    this.invalidatePreview(); const sequence = this.previewSequence; this.previewLoading.set(true); this.error.set(''); this.notice.set('');
    try {
      const raw = await this.api.command<unknown>(`projects/${project.id}/template-preview`, { templateId: template.id, templateRevision: template.revision, anchorDate }, project.revision);
      if (this.current(context) && sequence === this.previewSequence && this.canPreview()) this.preview.set(validTemplatePreview(raw, template, project, anchorDate));
    } catch (error) { if (this.current(context) && sequence === this.previewSequence) { if (error instanceof ApiError && [409, 412].includes(error.status)) this.staleProject.set(true); await this.fail(error); } }
    finally { if (this.current(context) && sequence === this.previewSequence) this.previewLoading.set(false); }
  }
  async apply(): Promise<void> {
    const preview = this.preview(); if (!preview || !this.confirmed || !this.canPreview() || this.previewLoading()) return;
    const context = this.context(), sequence = this.previewSequence, actor = this.auth.session()!.user.id;
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      const raw = await this.api.command<unknown>(`projects/${preview.project.id}/template-apply`, { templateId: preview.template.id, templateRevision: preview.template.revision,
        anchorDate: preview.anchorKind === 'Explicit' ? preview.anchorDate : null, previewDigest: preview.digest }, preview.project.revision);
      if (!this.current(context) || sequence !== this.previewSequence || !this.applicable()) return;
      const result = validTemplateApplied(raw, preview, actor); this.application.set(result.application); this.knownApplied.update(ids => [...ids, result.application.templateId]);
      this.notice.set(`${result.tasks.length} Aufgaben angelegt. Der Anwendungsnachweis bleibt erhalten.`); this.invalidatePreview();
    } catch (error) { if (this.current(context) && sequence === this.previewSequence) { this.staleProject.set(true); this.invalidatePreview(); await this.fail(error); } }
    finally { if (this.current(context)) this.busy.set(false); }
    if (this.current(context) && this.knownApplied().includes(preview.template.id)) await this.loadProject(preview.project.id, true);
  }
  isOpen(project: TemplateProject): boolean { return openTemplateProject(project); }
  day(value: string): string { return new Intl.DateTimeFormat('de-DE', { timeZone: 'UTC', dateStyle: 'medium' }).format(new Date(value + 'T00:00:00Z')); }
  time(value: string): string { return new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)); }
  offset(days: number): string { return days === 0 ? 'Am Ankerdatum' : `${Math.abs(days)} Kalendertag${Math.abs(days) === 1 ? '' : 'e'} ${days < 0 ? 'davor' : 'danach'}`; }
  private async fail(error: unknown): Promise<void> {
    const messages: Record<string, string> = {
      project_template_revision: 'Die Vorlage wurde zwischenzeitlich geändert. Laden Sie sie neu und prüfen Sie ihre aktuelle Version.',
      project_template_preview_changed: 'Projekt oder Vorlage haben sich seit der Vorschau geändert. Laden Sie beide Stände neu und prüfen Sie die Aufgaben erneut.',
      project_template_already_applied: 'Diese Vorlage wurde bereits übernommen. Laden Sie die Anwendungshistorie und prüfen Sie die erzeugten Aufgaben.',
      project_template_inactive: 'Nur aktive Vorlagen können in offene Projekte übernommen werden.',
      project_template_type: 'Vorlage und Projekt müssen dieselbe Projektart haben.',
      project_template_anchor: 'Hinterlegen Sie den geplanten Projektstart oder wählen Sie ein eigenes Ankerdatum.',
      project_template_date: 'Eine Aufgabenfrist liegt außerhalb des unterstützten Kalenderbereichs. Prüfen Sie Ankerdatum und Kalendertage.'
    };
    this.error.set(error instanceof ApiError && messages[error.problem.code || ''] || errorMessage(error)); await this.refreshAccess(error);
  }
  private async refreshAccess(error: unknown): Promise<void> { if (error instanceof ApiError && [401, 403].includes(error.status)) await this.auth.refresh().catch(() => undefined); }
}
