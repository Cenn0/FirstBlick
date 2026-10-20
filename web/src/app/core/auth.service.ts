import { Injectable, computed, signal } from '@angular/core';
import { ApiError, request } from './transport';
import { OfflineStore } from './offline-store';

export interface TenantAccess { id: string; name: string; role: string; modules: string[]; capabilities: string[]; employeeId?: string }
export interface Session { user: { id: string; email: string; displayName: string }; tenants: TenantAccess[]; activeTenantId?: string }
const sessionKey = 'firstblick-last-session';
const logoutKey = 'firstblick-pending-logout';

@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly session = signal<Session | null>(null);
  readonly activeTenantId = signal('');
  readonly ready = signal(false);
  readonly offlineSession = signal(false);
  readonly bootstrapError = signal('');
  readonly tenant = computed(() => this.session()?.tenants.find(t => t.id === this.activeTenantId()));
  readonly scope = computed(() => this.session() ? `${this.session()!.user.id}|${this.activeTenantId()}` : '');
  private booting?: Promise<void>;
  readonly store = new OfflineStore();

  initialize(): Promise<void> { return this.booting ??= this.load(); }

  private async load(): Promise<void> {
    try {
      if (localStorage.getItem(logoutKey)) {
        await this.finishPendingLogout();
      }
      await this.refresh();
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) await this.clear();
      else {
        const previous = localStorage.getItem(sessionKey);
        if (previous && !localStorage.getItem(logoutKey)) {
          try {
            const cached = JSON.parse(previous) as Session;
            if (cached.user?.id && Array.isArray(cached.tenants)) { this.accept(cached); this.offlineSession.set(true); }
          } catch { localStorage.removeItem(sessionKey); }
        }
        if (!this.session()) this.bootstrapError.set('Der Server ist derzeit nicht erreichbar. Sie können die Verbindung erneut prüfen.');
      }
    } finally { this.ready.set(true); }
  }

  async refresh(): Promise<void> {
    const previous=this.session();
    const value = await request<Session>('/api/v1/auth/me');
    if(previous && (previous.user.id!==value.user.id || previous.tenants.some(t=>{const next=value.tenants.find(n=>n.id===t.id);return !next||t.capabilities.some(c=>!next.capabilities.includes(c))||t.modules.some(m=>!next.modules.includes(m));})))await this.store.purgeUser(previous.user.id);
    this.accept(value);
    this.offlineSession.set(false);
    this.bootstrapError.set('');
  }

  private accept(value: Session): void {
    this.session.set(value);
    const chosen = this.activeTenantId() || value.activeTenantId;
    this.activeTenantId.set(value.tenants.some(t => t.id === chosen) ? chosen! : value.tenants[0]?.id || '');
    localStorage.setItem(sessionKey, JSON.stringify({ ...value, activeTenantId: this.activeTenantId() }));
  }

  async login(email: string, password: string): Promise<void> {
    if (localStorage.getItem(logoutKey)) {
      await this.finishPendingLogout();
    }
    await request('/api/v1/auth/login', { method: 'POST', body: JSON.stringify({ email, password }) });
    await request('/api/v1/auth/csrf');
    await this.refresh();
  }

  async logout(): Promise<void> {
    localStorage.setItem(logoutKey, '1');
    await this.clear();
    try { await this.finishPendingLogout(); }
    catch { this.bootstrapError.set('Lokale Daten sind entfernt. Die Serversitzung wird bei der nächsten Verbindung abgemeldet.'); }
  }

  private async finishPendingLogout():Promise<void>{try{await request('/api/v1/auth/logout',{method:'POST',body:'{}'});}catch(error){if(!(error instanceof ApiError)||error.status!==401)throw error;}localStorage.removeItem(logoutKey);}

  async clear(): Promise<void> {
    let id = this.session()?.user.id;
    if(!id){try{id=(JSON.parse(localStorage.getItem(sessionKey)||'null')as Session|null)?.user.id;}catch{/* Corrupt UI-only cache cannot authorize a request. */}}
    this.session.set(null);
    this.activeTenantId.set('');
    this.offlineSession.set(false);
    localStorage.removeItem(sessionKey);
    if (id) await this.store.purgeUser(id);
  }

  async switchTenant(id: string): Promise<void> {
    if (!this.session()?.tenants.some(t => t.id === id)) throw new Error('Unbekannter Betrieb.');
    this.activeTenantId.set(id);
    localStorage.setItem(sessionKey, JSON.stringify({ ...this.session(), activeTenantId: id }));
  }

  can(capability?: string): boolean {
    if (!capability) return true;
    const rights = this.tenant()?.capabilities || [];
    return rights.includes('*') || rights.includes(capability);
  }

  hasModule(module?: string): boolean {
    return !module || (this.tenant()?.modules || []).some(m => m.toLowerCase() === module.toLowerCase() || m === '*');
  }
}
