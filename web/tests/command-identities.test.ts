import { describe,expect,it } from 'vitest';
import { CommandIdentities,purgeCommandIdentities } from '../src/app/core/command-identities';

class MemoryStorage {
  readonly values=new Map<string,string>();get length():number{return this.values.size;}
  key(index:number):string|null{return [...this.values.keys()][index]??null;}
  getItem(key:string):string|null{return this.values.get(key)??null;}
  setItem(key:string,value:string):void{this.values.set(key,value);}
  removeItem(key:string):void{this.values.delete(key);}
}

describe('uncertain online commands',()=>{
  it('uses the original identity after service recreation without retaining business payloads',async()=>{
    const storage=new MemoryStorage();const original=await new CommandIdentities(storage).acquire('person|company','POST','members/invitations',{email:'private@example.test',role:'Worker'});
    const afterReload=await new CommandIdentities(storage).acquire('person|company','POST','members/invitations',{role:'Worker',email:'private@example.test'});
    expect(afterReload.key).toBe(original.key);expect(JSON.stringify([...storage.values])).not.toContain('private@example.test');expect(JSON.stringify([...storage.values])).not.toContain('members/invitations');
  });
  it('binds retry identities to person, company, revision, approval and exact command contents',async()=>{
    const store=new CommandIdentities(new MemoryStorage());const request=await store.acquire('person|a','POST','invoices/i/issue',{},2,'approval-a');
    const variants=await Promise.all([store.acquire('person|b','POST','invoices/i/issue',{},2,'approval-a'),store.acquire('other|a','POST','invoices/i/issue',{},2,'approval-a'),store.acquire('person|a','POST','invoices/i/issue',{},3,'approval-a'),store.acquire('person|a','POST','invoices/i/issue',{},2,'approval-b'),store.acquire('person|a','POST','invoices/i/issue',{different:true},2,'approval-a')]);
    for(const variant of variants)expect(variant.key).not.toBe(request.key);
  });
  it('forgets definitively completed attempts and purges only the departing person',async()=>{
    const storage=new MemoryStorage(),store=new CommandIdentities(storage);const request=await store.acquire('person|a','POST','customers',{name:'A'});store.complete(request);
    expect((await store.acquire('person|a','POST','customers',{name:'A'})).key).not.toBe(request.key);
    const other=await store.acquire('other|a','POST','customers',{name:'A'});purgeCommandIdentities(storage,'person');expect(storage.length).toBe(1);expect(storage.getItem(other.storageKey)).toBe(other.key);
  });
  it('can resume the same binary upload but never shares identity with different bytes',async()=>{
    const storage=new MemoryStorage(),store=new CommandIdentities(storage);const first=await store.acquire('person|a','PUT','documents/doc/content',new Blob(['original']),1);
    expect((await new CommandIdentities(storage).acquire('person|a','PUT','documents/doc/content',new Blob(['original']),1)).key).toBe(first.key);
    expect((await store.acquire('person|a','PUT','documents/doc/content',new Blob(['changed']),1)).key).not.toBe(first.key);
  });
});
