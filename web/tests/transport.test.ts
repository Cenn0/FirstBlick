import {afterEach,expect,test,vi} from 'vitest';
import {ApiError,requestResponse} from '../src/app/core/transport';

afterEach(()=>vi.unstubAllGlobals());

test('authenticated reads bypass service workers and browser response caches',async()=>{
  vi.stubGlobal('document',{cookie:''});
  const fetch=vi.fn().mockResolvedValue(new Response('{}',{headers:{'Content-Type':'application/json'}}));vi.stubGlobal('fetch',fetch);
  await requestResponse('/api/v1/time-entries',{},'tenant-a');
  const [,init]=fetch.mock.calls[0];expect(init.credentials).toBe('same-origin');expect(init.cache).toBe('no-store');expect(init.headers.get('ngsw-bypass')).toBe('1');expect(init.headers.get('X-Tenant-Id')).toBe('tenant-a');
});

test('CSRF bootstrap and the following mutation both bypass service workers',async()=>{
  const document={cookie:''};vi.stubGlobal('document',document);
  const fetch=vi.fn().mockImplementation(async(path:string)=>{if(path.endsWith('/csrf'))document.cookie='XSRF-TOKEN=synthetic%2Fcsrf';return new Response('{}',{headers:{'Content-Type':'application/json'}});});vi.stubGlobal('fetch',fetch);
  await requestResponse('/api/v1/diary-entries',{method:'POST',body:'{}'},'tenant-a');
  expect(fetch.mock.calls.map(call=>call[0])).toEqual(['/api/v1/auth/csrf','/api/v1/diary-entries']);
  for(const [,init]of fetch.mock.calls){expect(new Headers(init.headers).get('ngsw-bypass')).toBe('1');expect(init.cache).toBe('no-store');expect(init.credentials).toBe('same-origin');}
  expect(fetch.mock.calls[1][1].headers.get('X-XSRF-TOKEN')).toBe('synthetic/csrf');
});

test('an actual server 401 remains distinguishable from a network failure',async()=>{
  vi.stubGlobal('document',{cookie:''});vi.stubGlobal('fetch',vi.fn().mockResolvedValue(new Response('{"status":401,"code":"unauthorized"}',{status:401,headers:{'Content-Type':'application/problem+json'}})));
  await expect(requestResponse('/api/v1/auth/me')).rejects.toMatchObject({status:401,problem:{code:'unauthorized'}} satisfies Partial<ApiError>);
});
