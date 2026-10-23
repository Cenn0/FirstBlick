import {afterEach,expect,test,vi} from 'vitest';
import {ApiError,errorMessage,requestResponse} from '../src/app/core/transport';

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

test('server request correlation is shown without treating W3C traceId as a request ID',async()=>{
  vi.stubGlobal('document',{cookie:''});
  const fetch=vi.fn().mockResolvedValue(new Response(JSON.stringify({status:503,code:'server_error',detail:'Bitte später erneut versuchen.',requestId:'body-id',traceId:'activity-id'}),{status:503,headers:{'Content-Type':'application/problem+json','X-Request-Id':'server-id:0001'}}));
  vi.stubGlobal('fetch',fetch);
  let failure:unknown;try{await requestResponse('/api/v1/projects');}catch(error){failure=error;}
  expect(failure).toBeInstanceOf(ApiError);
  expect((failure as ApiError).requestId).toBe('server-id:0001');
  expect(errorMessage(failure)).toBe('Bitte später erneut versuchen. Anfragekennung: server-id:0001.');
  expect(errorMessage(failure)).not.toContain('activity-id');
});

test('invalid correlation and malformed problem shapes cannot become a displayed diagnostic',async()=>{
  vi.stubGlobal('document',{cookie:''});
  vi.stubGlobal('fetch',vi.fn().mockResolvedValue(new Response('null',{status:500,headers:{'X-Request-Id':'<forged>'}})));
  let failure:unknown;try{await requestResponse('/api/v1/projects');}catch(error){failure=error;}
  expect(failure).toBeInstanceOf(ApiError);
  expect((failure as ApiError).requestId).toBeUndefined();
  expect(errorMessage(failure)).toBe('Anfrage fehlgeschlagen (500).');
});
