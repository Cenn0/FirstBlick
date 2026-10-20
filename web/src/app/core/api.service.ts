import { Injectable, inject, signal } from '@angular/core';
import { AuthService } from './auth.service';
import { ApiError, request } from './transport';
import { OfflineKind, QueuedMutation, flushQueue } from './offline-store';

export interface Entity { id: string; revision: number; archived?: boolean; [key: string]: unknown }
export interface Page<T = Entity> { items: T[]; total: number; page: number; pageSize: number }

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly auth = inject(AuthService);
  readonly syncing = signal(false);
  readonly queued = signal<QueuedMutation[]>([]);

  async get<T>(path: string): Promise<T> { return this.call<T>(path); }
  async command<T>(path: string, body: unknown, revision?: number, key = crypto.randomUUID(), approvalId?: string): Promise<T> {
    const headers: Record<string, string> = { 'Idempotency-Key': key };
    if(approvalId)headers['X-Approval-Id']=approvalId;
    if (revision) headers['If-Match'] = `"${revision}"`;
    return this.call<T>(path, { method: 'POST', body: JSON.stringify(body), headers });
  }

  async update<T>(path:string,body:unknown,revision:number,approvalId?:string):Promise<T>{const headers:Record<string,string>={'Idempotency-Key':crypto.randomUUID(),'If-Match':`"${revision}"`};if(approvalId)headers['X-Approval-Id']=approvalId;return this.call<T>(path,{method:'PUT',body:JSON.stringify(body),headers});}

  private async call<T>(path: string, options: RequestInit = {}): Promise<T> {
    try { return await request<T>(`/api/v1/${path}`, options, this.auth.activeTenantId()); }
    catch (error) {
      if (error instanceof ApiError && error.status === 401) await this.auth.clear();
      throw error;
    }
  }

  async list(kind: string, search = '', page = 1, status = ''): Promise<Page> {
    const query = new URLSearchParams({ search, page: String(page), pageSize: '25' });
    if (status) query.set('status', status);
    const value = await this.get<Page>(`${kind}?${query}`);
    if (['time-entries', 'diary-entries'].includes(kind) && !search && page === 1) await this.auth.store.cache(this.auth.scope(), kind as OfflineKind, value);
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
    return await this.call<Entity>(`${kind}${existing ? `/${existing.id}` : ''}`, { method, headers, body: JSON.stringify(payload) });
  }

  async archive(kind: string, entity: Entity): Promise<void> {
    await this.call(`${kind}/${entity.id}`, { method: 'DELETE', headers: { 'If-Match': `"${entity.revision}"`, 'Idempotency-Key': crypto.randomUUID() } });
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
