const prefix='firstblick-command-key:';
type KeyStorage=Pick<Storage,'getItem'|'setItem'|'removeItem'|'key'|'length'>;

function canonical(value:unknown):string {
  if(Array.isArray(value))return '['+value.map(item=>canonical(item??null)).join(',')+']';
  if(value&&typeof value==='object')return '{'+Object.entries(value).filter(([,item])=>item!==undefined).sort(([a],[b])=>a<b?-1:a>b?1:0).map(([key,item])=>`${JSON.stringify(key)}:${canonical(item)}`).join(',')+'}';
  return JSON.stringify(value??null);
}
async function sha256(bytes:BufferSource):Promise<string>{return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),value=>value.toString(16).padStart(2,'0')).join('');}

/** Only opaque request fingerprints and random keys are retained. No body or automatic replay is stored here. */
export class CommandIdentities {
  constructor(private readonly storage:KeyStorage){}
  async acquire(scope:string,method:string,path:string,body:unknown,revision?:number,approvalId?:string,preferredKey?:string):Promise<{storageKey:string;key:string}>{
    if(!scope)throw new Error('Für diesen Vorgang ist eine Anmeldung erforderlich.');
    const bodyDigest=await sha256(body instanceof Blob?await body.arrayBuffer():new TextEncoder().encode(canonical(body)));
    const fingerprint=await sha256(new TextEncoder().encode(canonical({method,path,revision,approvalId,bodyDigest})));
    const storageKey=prefix+scope+':'+fingerprint;
    const acquire=()=>{const previous=this.storage.getItem(storageKey);if(previous&&/^[a-f0-9-]{36}$/i.test(previous))return {storageKey,key:previous};const key=preferredKey||crypto.randomUUID();this.storage.setItem(storageKey,key);return {storageKey,key};};
    if(typeof navigator!=='undefined'&&navigator.locks)return navigator.locks.request(storageKey,acquire);
    return acquire();
  }
  complete(identity:{storageKey:string;key:string}):void{if(this.storage.getItem(identity.storageKey)===identity.key)this.storage.removeItem(identity.storageKey);}
}

export function purgeCommandIdentities(storage:KeyStorage,userId:string):void{
  const keys:string[]=[];for(let i=0;i<storage.length;i++){const key=storage.key(i);if(key?.startsWith(prefix+userId+'|'))keys.push(key);}for(const key of keys)storage.removeItem(key);
}
