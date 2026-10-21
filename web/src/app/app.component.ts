import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { AuthService } from './core/auth.service';
import { ApiService } from './core/api.service';
import { modules } from './core/modules';
import { errorMessage } from './core/transport';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, FormsModule],
  template: `
    <a class="skip-link" href="#main-content">Zum Inhalt springen</a>
    @if (!auth.ready()) {
      <main class="startup" aria-live="polite"><img src="/icon.svg" alt="" width="56" height="56"><h1>FirstBlick</h1><p>Ihr Arbeitsplatz wird geöffnet …</p></main>
    } @else if (auth.session()) {
      <div class="app-layout">
        @if (menuOpen()) { <button class="nav-backdrop" aria-label="Menü schließen" (click)="closeMenu()"></button> }
        <aside id="main-navigation" class="sidebar" [class.is-open]="menuOpen()" [inert]="mobile()&&!menuOpen()" [attr.aria-hidden]="mobile()&&!menuOpen()?true:null" (keydown)="trapMenu($event)" aria-label="Hauptnavigation">
          <a class="brand" [routerLink]="home()" (click)="menuOpen.set(false)"><img src="/icon.svg" width="36" height="36" alt=""><span>First<span class="brand-accent">Blick</span><small>Ihr Betrieb im Blick.</small></span></a>
          <nav>
            @if(partner()){<a class="nav-item overview-link" routerLink="/portal" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">▤</span>Freigegebene Unterlagen</a><a class="nav-item" routerLink="/settings" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">⚙</span>Mein Zugang</a>}@else{
            <a class="nav-item overview-link" routerLink="/overview" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">▦</span>Übersicht</a>
            @for (group of groups(); track group.name) {
              <div class="nav-group"><p class="nav-group-label">{{group.name}}</p>
                @for (module of group.items; track module.kind) {
                  <a class="nav-item" [routerLink]="'/' + module.kind" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">{{module.icon}}</span>{{module.title}}</a>
                }
                @if(group.name==='Material & Geräte'&&['ManagingDirector','Office','Accounting','SiteManager'].includes(auth.tenant()?.role||'')&&auth.can('equipment-inspections.read')){<a class="nav-item" routerLink="/equipment-inspections" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">✓</span>Geräteprüfungen</a>}
              </div>
            }
            <div class="nav-group"><p class="nav-group-label">Werkzeuge</p>
              @if(auth.hasModule('field')&&auth.can('schedule-assignments.read')){<a class="nav-item" routerLink="/planning-week" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">▦</span>Wochenplan</a>}
              @if(auth.can('projects.read')){<a class="nav-item" routerLink="/discovery" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">⌕</span>Suche & Projektverlauf</a>}
              @if(auth.can('measurements.read')&&(auth.can('quotes.write')||auth.can('material-demands.write'))){<a class="nav-item" routerLink="/quantity-links" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">↔</span>Mengenübernahme</a>}
              @if(auth.hasModule('purchasing')&&auth.can('stock-movements.read')){<a class="nav-item" routerLink="/inventory-control" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">▤</span>Lagerprüfung</a>}
              @if(['ManagingDirector','Office'].includes(auth.tenant()?.role||'')&&auth.can('orders.read')){<a class="nav-item" routerLink="/cost-analysis" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">€</span>Nachkalkulation</a>}
              @if(['ManagingDirector','Office','Accounting'].includes(auth.tenant()?.role||'')&&auth.hasModule('finance')&&auth.can('collection-cases.read')){<a class="nav-item" routerLink="/collections" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">≡</span>Forderungen</a>}
              @if(['ManagingDirector','Office','Accounting'].includes(auth.tenant()?.role||'')&&(auth.can('invoices.read')||auth.can('bank-transactions.write'))){<a class="nav-item" routerLink="/integrations" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">⇄</span>Import & Export</a>}
              @if(auth.hasModule('technical')&&auth.can('technical-results.read')){<a class="nav-item" routerLink="/technical" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">△</span>Dach & Berechnung</a>}
              @if(auth.can('maintenance-visits.read')){<a class="nav-item" routerLink="/maintenance" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">↻</span>Wartungsplanung</a>}
              @if(auth.can('settings.manage')){<a class="nav-item" routerLink="/management" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">✓</span>Betrieb & Freigaben</a>}
              @if(auth.tenant()?.role==='ManagingDirector'&&!auth.recoveryHold()){<a class="nav-item" routerLink="/operations" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">◴</span>Betriebszustand</a>}
              @if(auth.hasModule('portal')&&auth.can('portal-shares.read')){<a class="nav-item" routerLink="/portal" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">▤</span>Kundenportal</a>}
              <a class="nav-item" routerLink="/settings" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">⚙</span>Einstellungen</a>
            </div>}
          </nav>
          <div class="sidebar-footer"><button class="sidebar-logout" (click)="logout()">Abmelden</button><span class="status-dot" [class.offline]="!online()"></span>{{online() ? 'Mit dem Betrieb verbunden' : 'Ohne Verbindung'}}</div>
        </aside>
        <div class="workspace" [inert]="mobile()&&menuOpen()">
          <header class="topbar">
            <button class="icon-button menu-button" [attr.aria-expanded]="menuOpen()" aria-label="Navigation öffnen" aria-controls="main-navigation" (click)="openMenu()">☰</button>
            <div class="tenant-switch"><label for="active-tenant">Betrieb</label><select id="active-tenant" [ngModel]="auth.activeTenantId()" (ngModelChange)="switchTenant($event)">@for(tenant of auth.session()!.tenants;track tenant.id){<option [value]="tenant.id">{{tenant.name}}</option>}</select></div>
            <div class="topbar-actions">
              @if(!partner()){<a class="sync-link" routerLink="/offline" [class.attention]="api.queued().length"><span aria-hidden="true">↻</span><span class="sync-label">{{api.queued().length ? api.queued().length + ' lokal vorgemerkt' : 'Synchronisierung'}}</span></a>}
              <span class="avatar" [title]="auth.session()!.user.displayName || auth.session()!.user.email">{{initials()}}</span>
              <button class="text-button logout-button" (click)="logout()">Abmelden</button>
            </div>
          </header>
          @if(auth.recoveryHold()){<section class="connection-banner recovery-hold" role="alert"><strong>Wiederherstellungssperre aktiv</strong><p>Der Betrieb wird nach einer Wiederherstellung geprüft. Neue Änderungen und Übertragungen sind bis zur Freigabe durch die Betriebsverwaltung gesperrt. Bereits vorgemerkte Zeit- und Tagesberichte bleiben auf diesem Gerät gespeichert.</p><button class="text-button" (click)="refreshAccess()" [disabled]="checkingAccess()">{{checkingAccess()?'Wird geprüft …':'Freigabestatus prüfen'}}</button>@if(!partner()){<a routerLink="/offline">Lokale Vorgänge ansehen</a>}@if(accessError()){<p>{{accessError()}}</p>}</section>}
          @else if (!online() || auth.offlineSession()) {<div class="connection-banner" role="status">@if(partner()){Ohne Verbindung: Freigegebene Unterlagen und Rückmeldungen benötigen eine aktuelle Verbindung zum Betrieb.}@else{Offline: Zeit- und Tagesberichte werden auf diesem Gerät vorgemerkt. Vor dem Übertragen werden Sitzung und Rechte erneut geprüft.<a routerLink="/offline">Vorgänge ansehen</a>}</div>}
          <main id="main-content" class="main-content" tabindex="-1"><router-outlet></router-outlet></main>
          <footer class="page-footer"><span>FirstBlick</span><span>Planen. Ausführen. Den Überblick behalten.</span></footer>
        </div>
        <nav class="mobile-nav" [inert]="mobile()&&menuOpen()" aria-label="Schnellnavigation"><a [routerLink]="home()" routerLinkActive="active"><span aria-hidden="true">▦</span>{{partner()?'Unterlagen':'Übersicht'}}</a>@if(auth.can('projects.read')){<a routerLink="/projects" routerLinkActive="active"><span aria-hidden="true">⌂</span>Projekte</a>}@if(auth.hasModule('field')&&auth.can('time-entries.read')){<a routerLink="/time-entries" routerLinkActive="active"><span aria-hidden="true">◷</span>Zeiten</a>}<button aria-controls="main-navigation" [attr.aria-expanded]="menuOpen()" (click)="openMenu()"><span aria-hidden="true">☰</span>Mehr</button></nav>
      </div>
    } @else { <main id="main-content"><router-outlet></router-outlet></main> }
  `
})
export class AppComponent {
  readonly auth = inject(AuthService);
  readonly api = inject(ApiService);
  private readonly router = inject(Router);
  readonly menuOpen = signal(false);
  private readonly mobileQuery=matchMedia('(max-width:760px)');readonly mobile=signal(this.mobileQuery.matches);private menuTrigger?:HTMLElement;
  readonly online = signal(navigator.onLine);
  readonly checkingAccess=signal(false);readonly accessError=signal('');
  readonly partner = computed(()=>this.auth.tenant()?.role==='Partner');
  readonly home = computed(()=>this.partner()?'/portal':'/overview');
  readonly groups = computed(() => ['Betrieb','Kaufmännisch','Baustelle','Material & Geräte','Team'].map(name => ({ name, items: modules.filter(m => m.group === name && this.auth.hasModule(m.module) && this.auth.can(m.capability)) })).filter(group=>group.items.length));
  readonly initials = computed(() => (this.auth.session()?.user.displayName || this.auth.session()?.user.email || '').split(/[\s@]+/).slice(0,2).map(x => x.charAt(0)).join('').toUpperCase());

  constructor() {
    // Consume secrets before the router's initial navigation or any API request.
    this.auth.captureRecoveryFragment();
    void this.auth.initialize();
    effect(() => { const scope=this.auth.scope();const online=this.online();if(scope)untracked(()=>{void this.api.reloadQueue();if(online)void this.api.synchronize();}); });
    effect(() => { if (this.auth.ready() && !this.auth.session() && !['/login','/activate','/recover','/reset'].some(path=>location.pathname===path)) void this.router.navigateByUrl('/login'); });
    effect(()=>{if(this.auth.ready()&&this.auth.session()&&(location.pathname==='/login'||(this.partner()&&!['/portal','/settings','/activate','/recover','/reset'].some(path=>location.pathname===path))))void this.router.navigateByUrl(this.home());});
    const resize=()=>{this.mobile.set(this.mobileQuery.matches);if(!this.mobileQuery.matches)this.menuOpen.set(false);};
    const escape=(event:KeyboardEvent)=>{if(event.key==='Escape'&&this.menuOpen()){event.preventDefault();this.closeMenu();}};
    this.mobileQuery.addEventListener('change',resize);window.addEventListener('keydown',escape);
    const online = () => { this.online.set(true); void this.api.synchronize(); };
    const offline = () => this.online.set(false);
    window.addEventListener('online', online);
    window.addEventListener('offline', offline);
    inject(DestroyRef).onDestroy(() => {this.mobileQuery.removeEventListener('change',resize);window.removeEventListener('keydown',escape);window.removeEventListener('online', online);window.removeEventListener('offline', offline);});
  }
  openMenu():void{this.menuTrigger=document.activeElement as HTMLElement;this.menuOpen.set(true);setTimeout(()=>document.querySelector<HTMLElement>('.sidebar a')?.focus());}
  closeMenu():void{this.menuOpen.set(false);setTimeout(()=>this.menuTrigger?.focus());}
  trapMenu(event:KeyboardEvent):void{if(!this.mobile()||!this.menuOpen()||event.key!=='Tab')return;const links=Array.from(document.querySelectorAll<HTMLElement>('.sidebar a,.sidebar button'));const first=links[0],last=links.at(-1);if(event.shiftKey&&document.activeElement===first){event.preventDefault();last?.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first?.focus();}}
  async switchTenant(id: string): Promise<void> { const wasPartner=this.partner();await this.auth.switchTenant(id);this.menuOpen.set(false);if(wasPartner||this.partner())await this.router.navigateByUrl(this.home());await this.api.reloadQueue(); }
  async logout(): Promise<void> { await this.auth.logout(); await this.router.navigateByUrl('/login'); }
  async refreshAccess():Promise<void>{this.checkingAccess.set(true);this.accessError.set('');try{await this.auth.refresh();await this.api.reloadQueue();}catch(error){this.accessError.set(errorMessage(error));}finally{this.checkingAccess.set(false);}}
}
