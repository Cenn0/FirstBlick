import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity, Page } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';
import { ActivityCounts, ActivityFilter, ActivityReport, activityFilter, activityQuery, sameActivityFilter, validateActivityReport, verifyActivityDownload } from '../core/business-activity.models';

@Component({ imports: [FormsModule, RouterLink], templateUrl: './business-activity.component.html', styleUrl: './business-activity.component.css' })
export class BusinessActivityComponent {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly report = signal<ActivityReport | null>(null); readonly projects = signal<Entity[]>([]);
  readonly loading = signal(false); readonly downloading = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly allowed = computed(() => ['ManagingDirector','Office','Accounting','SiteManager'].includes(this.auth.tenant()?.role || '')
    && this.auth.hasModule('core') && this.auth.can('projects.read') && this.auth.can('tasks.read'));
  readonly metrics: { key: keyof ActivityCounts; label: string; detail: string }[] = [
    {key:'projectsCreated',label:'Projekte angelegt',detail:'Gespeichertes Anlagedatum in der Betriebszeitzone.'},
    {key:'tasksCreated',label:'Aufgaben angelegt',detail:'Gespeichertes Anlagedatum in der Betriebszeitzone.'},
    {key:'tasksDue',label:'Aufgaben mit Fälligkeit',detail:'Aktuell hinterlegte Fälligkeit, unabhängig vom heutigen Status.'}
  ];
  from = new Date().toISOString().slice(0, 7) + '-01'; to = new Date().toISOString().slice(0, 10);
  projectId = ''; projectSearch = ''; includeArchived = false; sourcePage = 1;
  private generation = 0; private searchGeneration = 0; private access = '';
  constructor() {
    inject(DestroyRef).onDestroy(() => { this.generation++; this.searchGeneration++; this.report.set(null); });
    effect(() => { const signature = this.auth.accessSignature(); untracked(() => {
      if (signature !== this.access) {
        this.access = signature; this.generation++; this.searchGeneration++; this.report.set(null); this.projects.set([]);
        this.projectId = ''; this.projectSearch = ''; this.error.set(''); this.notice.set(''); this.loading.set(false); this.downloading.set(false);
        if (this.allowed()) void this.searchProjects();
      }
    }); });
  }
  filter(): ActivityFilter { return activityFilter(this.from, this.to, this.projectId, this.includeArchived); }
  changed(): boolean { try { const report = this.report(); return !!report && !sameActivityFilter(report.filter, this.filter()); } catch { return true; } }
  delta(key: keyof ActivityCounts): string { const report = this.report(); if (!report) return ''; const delta = report.current.totals[key] - report.previous.totals[key]; return (delta > 0 ? '+' : '') + delta; }
  date(value: string): string { return new Intl.DateTimeFormat('de-DE', { timeZone:'UTC' }).format(new Date(value + 'T00:00:00Z')); }
  timestamp(value: string, timeZone: string): string { return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium',timeStyle:'short',timeZone}).format(new Date(value)); }
  sourceRows() { return this.report()?.sources.slice((this.sourcePage - 1) * 25, this.sourcePage * 25) || []; }
  sourcePages(): number { return Math.max(1, Math.ceil((this.report()?.sources.length || 0) / 25)); }
  async searchProjects(): Promise<void> {
    if (!this.allowed()) return;
    const access = this.auth.accessSignature(), version = ++this.searchGeneration;
    try { const page = await this.api.get<Page>('projects?' + new URLSearchParams({search:this.projectSearch,pageSize:'100'}));
      if (access === this.auth.accessSignature() && version === this.searchGeneration && this.allowed()) this.projects.set(page.items);
    } catch (error) { if (access === this.auth.accessSignature() && version === this.searchGeneration) this.error.set(errorMessage(error)); }
  }
  async load(): Promise<void> {
    if (!this.allowed()) return;
    const access = this.auth.accessSignature(), version = ++this.generation, tenant = this.auth.activeTenantId();
    this.report.set(null); this.notice.set(''); this.error.set(''); this.loading.set(true); this.sourcePage = 1;
    try { const filter = this.filter(), result = await this.api.get<unknown>('reports/business-activity?' + activityQuery(filter));
      if (access !== this.auth.accessSignature() || version !== this.generation || !this.allowed()) return;
      this.report.set(validateActivityReport(result, tenant, filter));
    } catch (error) { if (access === this.auth.accessSignature() && version === this.generation) this.error.set(errorMessage(error)); }
    finally { if (access === this.auth.accessSignature() && version === this.generation) this.loading.set(false); }
  }
  async download(format: 'csv' | 'json'): Promise<void> {
    const report = this.report(); if (!report || this.downloading() || !this.allowed() || this.changed()) return;
    const access = this.auth.accessSignature(), version = this.generation;
    this.downloading.set(true); this.error.set(''); this.notice.set('');
    try { const response = await this.api.downloadResponse('reports/business-activity/export?' + activityQuery(report.filter) + '&format=' + format + '&expectedDigest=' + report.digest);
      const blob = await verifyActivityDownload(response, report, format);
      if (access !== this.auth.accessSignature() || version !== this.generation || this.report() !== report || !this.allowed() || this.changed()) return;
      const url = URL.createObjectURL(blob), link = document.createElement('a'); link.href = url;
      link.download = `firstblick-activity-${report.filter.from}-${report.filter.to}.${format}`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
      this.notice.set('Exportdatei geprüft und zum Download bereitgestellt.');
    } catch (error) { if (access === this.auth.accessSignature() && version === this.generation) this.error.set(errorMessage(error)); }
    finally { if (access === this.auth.accessSignature() && version === this.generation) this.downloading.set(false); }
  }
}
