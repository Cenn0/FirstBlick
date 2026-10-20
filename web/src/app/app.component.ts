import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { AuthService } from './core/auth.service';
import { ApiService } from './core/api.service';
import { modules } from './core/modules';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, FormsModule],
  template: `
    <a class="skip-link" href="#main-content">Zum Inhalt springen</a>
    @if (!auth.ready()) {
      <main class="startup" aria-live="polite"><img src="/icon.svg" alt="" width="56" height="56"><h1>FirstBlick</h1><p>Ihr Arbeitsplatz wird geöffnet …</p></main>
    } @else if (auth.session()) {
      <div class="app-layout">
        @if (menuOpen()) { <button class="nav-backdrop" aria-label="Menü schließen" (click)="menuOpen.set(false)"></button> }
        <aside class="sidebar" [class.is-open]="menuOpen()" aria-label="Hauptnavigation">
          <a class="brand" routerLink="/overview" (click)="menuOpen.set(false)"><img src="/icon.svg" width="36" height="36" alt=""><span>First<span class="brand-accent">Blick</span><small>Ihr Betrieb im Blick.</small></span></a>
          <nav>
            <a class="nav-item overview-link" routerLink="/overview" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">▦</span>Übersicht</a>
            @for (group of groups(); track group.name) {
              <div class="nav-group"><p class="nav-group-label">{{group.name}}</p>
                @for (module of group.items; track module.kind) {
                  <a class="nav-item" [routerLink]="'/' + module.kind" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">{{module.icon}}</span>{{module.title}}</a>
                }
              </div>
            }
            <div class="nav-group"><p class="nav-group-label">Werkzeuge</p>
              <a class="nav-item" routerLink="/technical" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">△</span>Dach & Berechnung</a>
              <a class="nav-item" routerLink="/settings" routerLinkActive="active" (click)="menuOpen.set(false)"><span class="nav-icon" aria-hidden="true">⚙</span>Einstellungen</a>
            </div>
          </nav>
          <div class="sidebar-footer"><span class="status-dot" [class.offline]="!online()"></span>{{online() ? 'Mit dem Betrieb verbunden' : 'Ohne Verbindung'}}</div>
        </aside>
        <div class="workspace">
          <header class="topbar">
            <button class="icon-button menu-button" [attr.aria-expanded]="menuOpen()" aria-label="Navigation öffnen" (click)="menuOpen.set(!menuOpen())">☰</button>
            <div class="tenant-switch"><label for="active-tenant">Betrieb</label><select id="active-tenant" [ngModel]="auth.activeTenantId()" (ngModelChange)="switchTenant($event)">@for(tenant of auth.session()!.tenants;track tenant.id){<option [value]="tenant.id">{{tenant.name}}</option>}</select></div>
            <div class="topbar-actions">
              <a class="sync-link" routerLink="/offline" [class.attention]="api.queued().length"><span aria-hidden="true">↻</span><span class="sync-label">{{api.queued().length ? api.queued().length + ' lokal vorgemerkt' : 'Synchronisierung'}}</span></a>
              <span class="avatar" [title]="auth.session()!.user.displayName || auth.session()!.user.email">{{initials()}}</span>
              <button class="text-button logout-button" (click)="logout()">Abmelden</button>
            </div>
          </header>
          @if (!online() || auth.offlineSession()) {<div class="connection-banner" role="status">Offline: Zeit- und Tagesberichte werden auf diesem Gerät vorgemerkt. Vor dem Übertragen werden Sitzung und Rechte erneut geprüft.<a routerLink="/offline">Vorgänge ansehen</a></div>}
          <main id="main-content" class="main-content" tabindex="-1"><router-outlet></router-outlet></main>
          <footer class="page-footer"><span>FirstBlick</span><span>Planen. Ausführen. Den Überblick behalten.</span></footer>
        </div>
        <nav class="mobile-nav" aria-label="Schnellnavigation"><a routerLink="/overview" routerLinkActive="active"><span aria-hidden="true">▦</span>Übersicht</a><a routerLink="/projects" routerLinkActive="active"><span aria-hidden="true">⌂</span>Projekte</a><a routerLink="/time-entries" routerLinkActive="active"><span aria-hidden="true">◷</span>Zeiten</a><button (click)="menuOpen.set(true)"><span aria-hidden="true">☰</span>Mehr</button></nav>
      </div>
    } @else { <main id="main-content"><router-outlet></router-outlet></main> }
  `
})
export class AppComponent {
  readonly auth = inject(AuthService);
  readonly api = inject(ApiService);
  private readonly router = inject(Router);
  readonly menuOpen = signal(false);
  readonly online = signal(navigator.onLine);
  readonly groups = computed(() => [...new Set(modules.map(m => m.group))].map(name => ({ name, items: modules.filter(m => m.group === name && this.auth.hasModule(m.module) && this.auth.can(m.capability)) })));
  readonly initials = computed(() => (this.auth.session()?.user.displayName || this.auth.session()?.user.email || '').split(/[\s@]+/).slice(0,2).map(x => x.charAt(0)).join('').toUpperCase());

  constructor() {
    void this.auth.initialize();
    effect(() => { const scope=this.auth.scope();const online=this.online();if(scope)untracked(()=>{void this.api.reloadQueue();if(online)void this.api.synchronize();}); });
    effect(() => { if (this.auth.ready() && !this.auth.session() && this.router.url !== '/login') void this.router.navigateByUrl('/login'); });
    const online = () => { this.online.set(true); void this.api.synchronize(); };
    const offline = () => this.online.set(false);
    window.addEventListener('online', online);
    window.addEventListener('offline', offline);
    inject(DestroyRef).onDestroy(() => {window.removeEventListener('online', online);window.removeEventListener('offline', offline);});
  }
  async switchTenant(id: string): Promise<void> { await this.auth.switchTenant(id); await this.api.reloadQueue(); }
  async logout(): Promise<void> { await this.auth.logout(); await this.router.navigateByUrl('/login'); }
}
