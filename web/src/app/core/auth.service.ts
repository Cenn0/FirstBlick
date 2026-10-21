import { Injectable, computed, signal } from '@angular/core';
import { ApiError, request } from './transport';
import { OfflineStore } from './offline-store';
import { purgeCommandIdentities } from './command-identities';
import { Session, accessWasRevoked, rememberRecoveryAccess } from './session-access';
export type { TenantAccess, Session } from './session-access';

const sessionKey = 'firstblick-last-session';
const logoutKey = 'firstblick-pending-logout';
const reauthenticationKey = 'firstblick-reauthentication-required';

@Injectable({ providedIn: 'root' })
export class AuthService {
  recoveryEmail='';recoveryToken='';
  captureRecoveryFragment():boolean{const fields=new URLSearchParams(location.hash.slice(1));const token=fields.get('reset-token');const email=fields.get('reset-email');if(!token||!email)return false;this.recoveryToken=token;this.recoveryEmail=email;const reset=new URL('reset',document.baseURI);history.replaceState(history.state,'',reset.pathname);return true;}
  readonly session = signal<Session | null>(null);
  readonly activeTenantId = signal('');
  readonly ready = signal(false);
  readonly offlineSession = signal(false);
  readonly bootstrapError = signal('');
  readonly reauthenticationRequired = signal(localStorage.getItem(reauthenticationKey)==='1');
  readonly tenant = computed(() => this.session()?.tenants.find(t => t.id === this.activeTenantId()));
  readonly recoveryHold = computed(() => this.tenant()?.recoveryHold===true);
  readonly accessSignature = computed(() => {const tenant=this.tenant();return JSON.stringify([this.scope(),tenant?.role,tenant?.employeeId,tenant?.modules,tenant?.capabilities,tenant?.recoveryHold]);});
  readonly scope = computed(() => this.session() ? `${this.session()!.user.id}|${this.activeTenantId()}` : '');
  private booting?: Promise<void>;
  private refreshing?: Promise<void>;
  readonly store = new OfflineStore();

  initialize(): Promise<void> { return this.booting ??= this.load(); }

  private async load(): Promise<void> {
    try {
      if (localStorage.getItem(logoutKey)) {
        await this.finishPendingLogout();
      }
      await this.refresh();
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) this.expire();
      else {
        const previous = localStorage.getItem(sessionKey);
        if (previous && !localStorage.getItem(logoutKey) && !this.reauthenticationRequired()) {
          try {
            const cached = JSON.parse(previous) as Session;
            if (cached.user?.id && Array.isArray(cached.tenants)) { this.accept(cached); this.offlineSession.set(true); }
          } catch { localStorage.removeItem(sessionKey); }
        }
        if (!this.session()) this.bootstrapError.set('Der Server ist derzeit nicht erreichbar. Sie können die Verbindung erneut prüfen.');
      }
    } finally { this.ready.set(true); }
  }

  refresh():Promise<void>{return this.refreshing??=this.loadFreshSession().finally(()=>{this.refreshing=undefined;});}

  private async loadFreshSession(): Promise<void> {
    // Compare the saved access after a reload as well; it is evidence for cleanup, never authorization.
    let previous=this.session();
    if(!previous){try{const cached=JSON.parse(localStorage.getItem(sessionKey)||'null')as Session|null;if(cached?.user?.id&&Array.isArray(cached.tenants))previous=cached;}catch{/* Invalid UI-only metadata is ignored. */}}
    let current:Session;
    try{current=await request<Session>('/api/v1/auth/me');}
    catch(error){if(error instanceof ApiError&&error.status===401)this.expire();throw error;}
    const value = rememberRecoveryAccess(previous,current);
    const revoked=accessWasRevoked(previous,value);
    this.accept(value);
    if(previous&&revoked){purgeCommandIdentities(localStorage,previous.user.id);await this.store.purgeUser(previous.user.id);}
    localStorage.removeItem(reauthenticationKey);
    this.reauthenticationRequired.set(false);
    this.offlineSession.set(false);
    this.bootstrapError.set('');
  }

  markRecoveryHold():void{
    const previous=this.session();if(!previous)return;
    this.accept(rememberRecoveryAccess(previous,{...previous,tenants:previous.tenants.map(tenant=>tenant.id===this.activeTenantId()?{...tenant,recoveryHold:true,capabilities:[]}:tenant)}));
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

  private async finishPendingLogout():Promise<void>{await request('/api/v1/auth/csrf');try{await request('/api/v1/auth/logout',{method:'POST',body:'{}'});}catch(error){if(!(error instanceof ApiError)||error.status!==401)throw error;}localStorage.removeItem(logoutKey);}

  /** A session timeout locks local work. Only fresh server identity and access can unlock it. */
  expire():void{
    if(localStorage.getItem(sessionKey)){localStorage.setItem(reauthenticationKey,'1');this.reauthenticationRequired.set(true);}
    this.session.set(null);this.activeTenantId.set('');this.offlineSession.set(false);
  }

  async clear(): Promise<void> {
    let id = this.session()?.user.id;
    if(!id){try{id=(JSON.parse(localStorage.getItem(sessionKey)||'null')as Session|null)?.user.id;}catch{/* Corrupt UI-only cache cannot authorize a request. */}}
    this.session.set(null);
    this.activeTenantId.set('');
    this.offlineSession.set(false);
    localStorage.removeItem(sessionKey);
    localStorage.removeItem(reauthenticationKey);
    this.reauthenticationRequired.set(false);
    if (id) {purgeCommandIdentities(localStorage,id);await this.store.purgeUser(id);}
  }

  async switchTenant(id: string): Promise<void> {
    if (!this.session()?.tenants.some(t => t.id === id)) throw new Error('Unbekannter Betrieb.');
    this.activeTenantId.set(id);
    localStorage.setItem(sessionKey, JSON.stringify({ ...this.session(), activeTenantId: id }));
  }

  can(capability?: string): boolean {
    if(this.recoveryHold())return false;
    if (!capability) return true;
    const rights = this.tenant()?.capabilities || [];
    return rights.includes('*') || rights.includes(capability);
  }

  hasModule(module?: string): boolean {
    return !module || (this.tenant()?.modules || []).some(m => m.toLowerCase() === module.toLowerCase() || m === '*');
  }
}
