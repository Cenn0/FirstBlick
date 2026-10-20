import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, Entity, Page } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';
import { labelFor, moduleByKind } from '../core/modules';

@Component({
  imports: [RouterLink],
  template: `
    <header class="page-heading">
      <div><p class="eyebrow">{{today}}</p><h1>{{greeting}}, {{firstName()}}.</h1><p>Das Wichtigste für Ihren Betrieb auf einen Blick.</p></div>
      @if(allowed('projects','write')){<a class="button primary" routerLink="/projects" [queryParams]="{new:'1'}"><span aria-hidden="true">+</span> Projekt anlegen</a>}
    </header>
    @if(error()){<div class="alert error" role="alert">{{error()}} <button class="text-button" (click)="load()">Erneut laden</button></div>}
    <section class="stat-grid" aria-label="Betriebsübersicht">
      @for(card of cards();track card.key){
        <a class="stat-card" [routerLink]="card.path" [queryParams]="card.query"><div class="stat-card-top"><span>{{card.label}}</span><span class="stat-icon" aria-hidden="true">{{card.icon}}</span></div><strong>{{loading()?'…':count(card.key)}}</strong><span class="stat-link">{{card.action}} <span aria-hidden="true">↗</span></span></a>
      }
    </section>
    <div class="dashboard-columns">
      @if(allowed('tasks')){<section class="panel"><div class="panel-heading"><div><p class="eyebrow">DAS STEHT AN</p><h2>Offene Aufgaben</h2></div><a routerLink="/tasks" [queryParams]="{status:'Open'}">Alle ansehen →</a></div>
        @for(task of tasks();track task.id){<a class="activity-row" routerLink="/tasks" [queryParams]="{selected:task.id}"><span class="task-circle" aria-hidden="true"></span><div><strong>{{task['title']}}</strong><span>{{task['dueDate'] ? 'Fällig: '+formatDate(task['dueDate']) : 'Ohne Termin'}}</span></div><span class="badge">{{labelFor(task['status'])}}</span></a>}
        @empty{<div class="empty-state compact"><span class="empty-icon" aria-hidden="true">✓</span><h3>{{loading()?'Aufgaben werden geladen …':'Hier ist Platz für den nächsten Schritt.'}}</h3><p>Projektaufgaben helfen, die nächsten Schritte und Fristen im Blick zu behalten.</p>@if(allowed('tasks','write')){<a routerLink="/tasks" [queryParams]="{new:'1'}">Aufgabe anlegen →</a>}</div>}
      </section>}
      <section class="panel quick-actions"><p class="eyebrow">DIREKT LOSLEGEN</p><h2>Kurze Wege</h2>
        @for(action of quickActions();track action.kind){<a [routerLink]="'/'+action.kind" [queryParams]="action.create?{new:'1'}:{}"><span class="quick-icon" aria-hidden="true">{{action.icon}}</span><div><strong>{{action.title}}</strong><span>{{action.description}}</span></div><span aria-hidden="true">→</span></a>}
        @empty{<p class="muted">Ihre verfügbaren Bereiche finden Sie in der Navigation.</p>}
      </section>
    </div>
    @if(allowed('projects')){<section class="panel project-panel"><div class="panel-heading"><div><p class="eyebrow">IN ARBEIT</p><h2>Aktuelle Projekte</h2></div><a routerLink="/projects" [queryParams]="{status:'Active'}">Zur Projektübersicht →</a></div><div class="project-cards">
      @for(project of projects();track project.id){<a class="project-card" routerLink="/projects" [queryParams]="{selected:project.id}"><div class="project-card-top"><span class="project-symbol" aria-hidden="true">⌂</span><span class="badge">{{labelFor(project['status'])}}</span></div><h3>{{project['name']}}</h3><p>{{project['number'] || 'Projektakte'}}</p><span class="project-link">Projekt öffnen →</span></a>}
      @empty{<div class="empty-state compact"><h3>{{loading()?'Projekte werden geladen …':'Zurzeit keine Projekte in Arbeit.'}}</h3><p>Aktive Projekte und Ihre zugewiesenen Baustellen erscheinen hier.</p>@if(allowed('customers','write')){<a class="button secondary" routerLink="/customers" [queryParams]="{new:'1'}">Kunde anlegen</a>}</div>}
    </div></section>}
  `
})
export class DashboardComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly error = signal('');
  readonly loading = signal(true);
  readonly stats = signal<Record<string, number>>({});
  readonly tasks = signal<Entity[]>([]);
  readonly projects = signal<Entity[]>([]);
  readonly labelFor = labelFor;
  readonly today = new Intl.DateTimeFormat('de-DE', { weekday:'long', day:'numeric', month:'long', year:'numeric' }).format(new Date());
  readonly greeting = new Date().getHours() < 11 ? 'Guten Morgen' : new Date().getHours() < 18 ? 'Guten Tag' : 'Guten Abend';
  readonly cards = computed(() => [
    {key:'projects', kind:'projects', label:'Projekte', path:'/projects', query:{}, icon:'⌂', action:'Projekte ansehen'},
    {key:'quoteDrafts', kind:'quotes', label:'Angebote im Entwurf', path:'/quotes', query:{status:'Draft'}, icon:'A', action:'Angebote bearbeiten'},
    {key:'openInvoices', kind:'invoices', label:'Offene Rechnungen', path:'/invoices', query:{openOnly:'true'}, icon:'€', action:'Rechnungen prüfen'},
    {key:'tasksDue', kind:'tasks', label:'Fällige Aufgaben', path:'/tasks', query:{status:'Open', due:'today'}, icon:'✓', action:'Aufgaben öffnen'}
  ].filter(card => this.allowed(card.kind)));
  readonly quickActions = computed(() => [
    {kind:'time-entries', title:'Arbeitszeit erfassen', description:'Zeiten und Pausen festhalten', icon:'◷', create:true},
    {kind:'diary-entries', title:'Tagesbericht schreiben', description:'Direkt von der Baustelle', icon:'B', create:true},
    {kind:'quotes', title:'Angebot vorbereiten', description:'Leistungen sauber kalkulieren', icon:'A', create:true},
    {kind:'schedule-assignments', title:'Einsätze ansehen', description:'Team und Baustellen verbinden', icon:'▦', create:false}
  ].filter(action => this.allowed(action.kind, action.create ? 'write' : 'read')));

  constructor() { effect(() => { if (this.auth.scope()) untracked(() => void this.load()); }); }
  allowed(kind:string, right='read'):boolean { return this.auth.hasModule(moduleByKind.get(kind)?.module) && this.auth.can(`${kind}.${right}`); }
  firstName():string { return (this.auth.session()?.user.displayName || 'willkommen').split(' ')[0]; }
  count(key:string):string { return this.stats()[key] === undefined ? '–' : String(this.stats()[key]); }
  formatDate(value:unknown):string { return new Intl.DateTimeFormat('de-DE').format(new Date(String(value))); }
  async load():Promise<void> {
    this.loading.set(true); this.error.set('');
    const scope = this.auth.scope();
    const empty:Page = {items:[], total:0, page:1, pageSize:25};
    try {
      const [stats,tasks,projects] = await Promise.all([
        this.api.get<Record<string,number>>('dashboard'),
        this.allowed('tasks') ? this.api.list('tasks','',1,'Open') : Promise.resolve(empty),
        this.allowed('projects') ? this.api.list('projects','',1,'Active') : Promise.resolve(empty)
      ]);
      if (scope !== this.auth.scope()) return;
      this.stats.set(stats); this.tasks.set(tasks.items.slice(0,5)); this.projects.set(projects.items.slice(0,4));
    } catch(error) { if(scope === this.auth.scope()) this.error.set(errorMessage(error)); }
    finally { if(scope === this.auth.scope()) this.loading.set(false); }
  }
}
