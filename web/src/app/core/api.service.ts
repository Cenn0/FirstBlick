import { Injectable, inject, signal } from '@angular/core';
import { AuthService } from './auth.service';
import { ApiError, request, requestResponse } from './transport';
import { OfflineKind, QueuedMutation, flushQueue } from './offline-store';
import { CommandIdentities } from './command-identities';

export interface Entity { id: string; revision: number; archived?: boolean; [key: string]: unknown }
export interface Page<T = Entity> { items: T[]; total: number; page: number; pageSize: number }

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly auth = inject(AuthService);
  readonly syncing = signal(false);
  readonly queued = signal<QueuedMutation[]>([]);
  private readonly identities=new CommandIdentities(localStorage);

  async get<T>(path: string): Promise<T> { return this.call<T>(path); }
  async upload<T>(path:string,body:Blob,contentType:string,revision:number,key:string):Promise<T>{return this.mutate<T>(path,'PUT',body,revision,undefined,key,contentType);}
  async download(path:string):Promise<Blob>{try{const response=await requestResponse(`/api/v1/${path}`,{},this.auth.activeTenantId(),'application/octet-stream');return await response.blob();}catch(error){if(error instanceof ApiError&&error.status===401)await this.auth.clear();throw error;}}
  async command<T>(path: string, body: unknown, revision?: number, key: string = crypto.randomUUID(), approvalId?: string): Promise<T> {
    return this.mutate<T>(path,'POST',body,revision,approvalId,key);
  }

  async update<T>(path:string,body:unknown,revision:number,approvalId?:string):Promise<T>{return this.mutate<T>(path,'PUT',body,revision,approvalId);}

  private async mutate<T>(path:string,method:string,body:unknown,revision?:number,approvalId?:string,preferredKey?:string,contentType?:string):Promise<T>{
    const scope=this.auth.scope();const identity=await this.identities.acquire(scope,method,path,body,revision,approvalId,preferredKey);
    if(scope!==this.auth.scope())throw new Error('Der Betrieb wurde gewechselt. Öffnen Sie den Vorgang im gewünschten Betrieb erneut.');
    const headers:Record<string,string>={'Idempotency-Key':identity.key};if(revision)headers['If-Match']=`"${revision}"`;if(approvalId)headers['X-Approval-Id']=approvalId;if(contentType)headers['Content-Type']=contentType;
    try{const result=await this.call<T>(path,{method,body:body instanceof Blob?body:body===undefined?undefined:JSON.stringify(body),headers});this.identities.complete(identity);if(scope!==this.auth.scope())throw new Error('Der Vorgang wurde im vorherigen Betrieb gespeichert. Öffnen Sie ihn dort zur Prüfung.');return result;}
    catch(error){if(error instanceof ApiError&&error.status>=400&&error.status<500)this.identities.complete(identity);throw error;}
  }

  private async call<T>(path: string, options: RequestInit = {}): Promise<T> {
    try { return await request<T>(`/api/v1/${path}`, options, this.auth.activeTenantId()); }
    catch (error) {
      if (error instanceof ApiError && error.status === 401) await this.auth.clear();
      throw error;
    }
  }

  async list(kind: string, search = '', page = 1, status = '', filters:Record<string,string> = {}): Promise<Page> {
    const scope=this.auth.scope();
    const query = new URLSearchParams({ ...filters, search, page: String(page), pageSize: '25' });
    if (status) query.set('status', status);
    const value = await this.get<Page>(`${kind}?${query}`);
    if (['time-entries', 'diary-entries'].includes(kind) && !search && page === 1) await this.auth.store.cache(scope, kind as OfflineKind, value);
    return value;
  }

  async save(kind: string, body: Record<string, unknown>, existing?: Entity): Promise<Entity | null> {
    const key = crypto.randomUUID();
    const method = existing ? 'PUT' : 'POST';
    const headers: Record<string, string> = { 'Idempotency-Key': key };
    if (existing) headers['If-Match'] = `"${existing.revision}"`;
    const payload = existing ? { ...body, id: existing.id, revision: existing.revision } : body;
    // Store before sending: a browser restart after the server accepted the request cannot lose its key.
    if (['time-entries', 'diary-entries'].includes(kind)) {
      await this.auth.store.enqueue({ key, scope: this.auth.scope(), kind: kind as OfflineKind, method, body: payload, id: existing?.id, revision: existing?.revision });
      await this.reloadQueue();
      await this.synchronize();
      return null;
    }
    return await this.mutate<Entity>(`${kind}${existing ? `/${existing.id}` : ''}`,method,payload,existing?.revision,undefined,key);
  }

  async archive(kind: string, entity: Entity): Promise<void> {
    await this.mutate(`${kind}/${entity.id}`,'DELETE',undefined,entity.revision);
  }

  async reloadQueue(): Promise<void> { this.queued.set(this.auth.scope() ? await this.auth.store.list(this.auth.scope()) : []); }

  async synchronize(): Promise<void> {
    if (this.syncing() || !navigator.onLine || !this.auth.scope()) return;
    const scope = this.auth.scope();
    const tenantId = this.auth.activeTenantId();
    const run = async () => {
      this.syncing.set(true);
      try {
        // Verify current identity, membership, capabilities and enabled modules before every replay.
        await this.auth.refresh();
        if (this.auth.scope() !== scope) return;
        await flushQueue(this.auth.store, scope, async item => {
          const headers: Record<string, string> = { 'Idempotency-Key': item.key };
          if (item.revision) headers['If-Match'] = `"${item.revision}"`;
          await request(`/api/v1/${item.kind}${item.id ? `/${item.id}` : ''}`, { method: item.method, headers, body: JSON.stringify(item.body) }, tenantId);
        }, () => this.auth.scope() === scope);
      } catch (error) {
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) await this.auth.clear();
      } finally { this.syncing.set(false); await this.reloadQueue(); }
    };
    if (navigator.locks) await navigator.locks.request(`firstblick-sync:${scope}`, { ifAvailable: true }, async lock => { if (lock) await run(); });
    else await run();
  }
}
