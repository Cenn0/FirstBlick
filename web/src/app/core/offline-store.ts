export type OfflineKind = 'time-entries' | 'diary-entries';
export type QueueState = 'pending' | 'conflict' | 'blocked';

export interface QueuedMutation {
  key: string;
  scope: string;
  kind: OfflineKind;
  method: 'POST' | 'PUT';
  id?: string;
  revision?: number;
  body: Record<string, unknown>;
  state: QueueState;
  createdAt: string;
  attempts: number;
  error?: string;
}

interface CachedList { key: string; scope: string; kind: OfflineKind; savedAt: string; value: unknown }
export interface FieldOption { id:string;name:string }

function finish(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error || new Error('Lokaler Speicherfehler.'));
    transaction.onabort = () => reject(transaction.error || new Error('Lokales Speichern wurde abgebrochen.'));
  });
}
function result<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

/** Stores only explicitly selected field-work data. Cookies and credentials are never stored. */
export class OfflineStore {
  private opening?: Promise<IDBDatabase>;
  constructor(private readonly name = 'firstblick-fieldwork-v1') {}

  private open(): Promise<IDBDatabase> {
    this.opening ??= new Promise((resolve, reject) => {
      const request = indexedDB.open(this.name, 1);
      request.onupgradeneeded = () => {
        const db = request.result;
        db.createObjectStore('mutations', { keyPath: 'key' }).createIndex('scope', 'scope');
        db.createObjectStore('cache', { keyPath: 'key' }).createIndex('scope', 'scope');
      };
      request.onsuccess = () => {
        request.result.onversionchange = () => request.result.close();
        resolve(request.result);
      };
      request.onerror = () => { this.opening = undefined; reject(request.error); };
      request.onblocked = () => { this.opening = undefined; reject(new Error('Bitte schließen Sie ältere FirstBlick-Tabs.')); };
    });
    return this.opening;
  }

  async enqueue(mutation: Omit<QueuedMutation, 'state' | 'attempts' | 'createdAt'>): Promise<QueuedMutation> {
    if (!['time-entries', 'diary-entries'].includes(mutation.kind)) throw new Error('Dieser Vorgang ist online erforderlich.');
    if (!['POST', 'PUT'].includes(mutation.method) || !mutation.scope || !mutation.key) throw new Error('Ungültiger Offline-Vorgang.');
    if (mutation.method === 'PUT' && (!mutation.id || !Number.isInteger(mutation.revision) || mutation.revision! < 1)) throw new Error('Für Änderungen ist ein eindeutiger Ausgangsstand erforderlich.');
    const value: QueuedMutation = { ...structuredClone(mutation), state: 'pending', attempts: 0, createdAt: new Date().toISOString() };
    const db = await this.open();
    const tx = db.transaction('mutations', 'readwrite');
    const completed = finish(tx);
    // add() deliberately rejects key reuse instead of replacing an earlier command.
    tx.objectStore('mutations').add(value);
    await completed;
    return value;
  }

  async list(scope: string): Promise<QueuedMutation[]> {
    const db = await this.open();
    const tx = db.transaction('mutations');
    const completed = finish(tx);
    const values = await result(tx.objectStore('mutations').index('scope').getAll(scope)) as QueuedMutation[];
    await completed;
    return values.sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.key.localeCompare(b.key));
  }

  async update(value: QueuedMutation): Promise<void> {
    const db = await this.open();
    const tx = db.transaction('mutations', 'readwrite');
    const completed = finish(tx);
    tx.objectStore('mutations').put(structuredClone(value));
    await completed;
  }

  async remove(key: string): Promise<void> {
    const db = await this.open();
    const tx = db.transaction('mutations', 'readwrite');
    const completed = finish(tx);
    tx.objectStore('mutations').delete(key);
    await completed;
  }

  async cache(scope: string, kind: OfflineKind, value: unknown): Promise<void> {
    if (!['time-entries', 'diary-entries'].includes(kind)) throw new Error('Diese Daten werden nicht offline gespeichert.');
    const db = await this.open();
    const tx = db.transaction('cache', 'readwrite');
    const completed = finish(tx);
    tx.objectStore('cache').put({ key: `${scope}|${kind}`, scope, kind, savedAt: new Date().toISOString(), value: structuredClone(value) } satisfies CachedList);
    await completed;
  }

  async cached(scope: string, kind: OfflineKind): Promise<CachedList | undefined> {
    const db = await this.open();
    const tx = db.transaction('cache');
    const completed = finish(tx);
    const value = await result(tx.objectStore('cache').get(`${scope}|${kind}`)) as CachedList | undefined;
    await completed;
    return value;
  }

  async cacheOptions(scope:string,kind:'projects'|'employees',items:FieldOption[]):Promise<void>{
    if(!['projects','employees'].includes(kind))throw new Error('Diese Auswahldaten werden nicht offline gespeichert.');
    const db=await this.open();const tx=db.transaction('cache','readwrite');const completed=finish(tx);
    tx.objectStore('cache').put({key:`${scope}|options:${kind}`,scope,value:items.map(item=>({id:item.id,name:item.name})),savedAt:new Date().toISOString()});
    await completed;
  }

  async options(scope:string,kind:'projects'|'employees'):Promise<FieldOption[]>{
    const db=await this.open();const tx=db.transaction('cache');const completed=finish(tx);
    const value=await result(tx.objectStore('cache').get(`${scope}|options:${kind}`)) as {value:FieldOption[]}|undefined;
    await completed;return value?.value||[];
  }

  async purgeUser(userId: string): Promise<void> {
    const db = await this.open();
    const tx = db.transaction(['mutations', 'cache'], 'readwrite');
    const completed = finish(tx);
    for (const name of ['mutations', 'cache']) {
      const cursor = tx.objectStore(name).openCursor();
      cursor.onsuccess = () => {
        const current = cursor.result;
        if (!current) return;
        if ((current.value.scope as string).startsWith(`${userId}|`)) current.delete();
        current.continue();
      };
    }
    await completed;
  }

  async close(): Promise<void> { if (this.opening) (await this.opening).close(); this.opening = undefined; }
}

export type MutationSender = (mutation: QueuedMutation) => Promise<void>;

/** Ordered at-least-once delivery. The server must persist and enforce the supplied idempotency key. */
export async function flushQueue(store: OfflineStore, scope: string, send: MutationSender, stillCurrent: () => boolean): Promise<number> {
  let sent = 0;
  for (const item of await store.list(scope)) {
    if (!stillCurrent()) break;
    // A conflict blocks subsequent mutations to the same resource; independent rows may continue.
    if (item.state !== 'pending') continue;
    const earlier = (await store.list(scope)).some(x => x.key !== item.key && x.id && x.id === item.id && x.kind === item.kind && x.createdAt <= item.createdAt && x.state !== 'pending');
    if (earlier) continue;
    try {
      await send(item);
      await store.remove(item.key);
      sent++;
    } catch (error) {
      // Scope change/logout may have purged all rows while a request was in flight.
      if (!stillCurrent()) break;
      const status = typeof error === 'object' && error !== null && 'status' in error ? Number(error.status) : 0;
      item.attempts++;
      item.error = status === 409 || status === 412 ? 'Der Serverstand hat sich geändert. Bitte Angaben vergleichen.' : status === 401 || status === 403 ? 'Sitzung oder Berechtigung muss erneut geprüft werden.' : status >= 400 && status < 500 ? 'Die Eingabe wurde abgelehnt. Bitte den Vorgang prüfen.' : 'Verbindung unterbrochen. Erneuter Versuch beim nächsten Synchronisieren.';
      item.state = status === 409 || status === 412 ? 'conflict' : status >= 400 && status < 500 ? 'blocked' : 'pending';
      await store.update(item);
      if (!status || status >= 500 || status === 401 || status === 403 || status === 429) break;
    }
  }
  return sent;
}
