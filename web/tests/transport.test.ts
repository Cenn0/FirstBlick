import {afterEach,expect,test,vi} from 'vitest';
import {ApiError,ApiNetworkError,errorMessage,requestResponse} from '../src/app/core/transport';

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
  expect(errorMessage(failure)).toBe('Ein technischer Fehler ist aufgetreten. Ob der Vorgang ausgeführt wurde, ist nicht bestätigt. Prüfen Sie den aktuellen Stand, bevor Sie es erneut versuchen. Anfragekennung: server-id:0001.');
  expect(errorMessage(failure)).not.toContain('Bitte später erneut versuchen.');
  expect(errorMessage(failure)).not.toContain('activity-id');
});

test('invalid correlation and malformed problem shapes cannot become a displayed diagnostic',async()=>{
  vi.stubGlobal('document',{cookie:''});
  vi.stubGlobal('fetch',vi.fn().mockResolvedValue(new Response('null',{status:500,headers:{'X-Request-Id':'<forged>'}})));
  let failure:unknown;try{await requestResponse('/api/v1/projects');}catch(error){failure=error;}
  expect(failure).toBeInstanceOf(ApiError);
  expect((failure as ApiError).requestId).toBeUndefined();
  expect(errorMessage(failure)).toBe('Ein technischer Fehler ist aufgetreten. Ob der Vorgang ausgeführt wurde, ist nicht bestätigt. Prüfen Sie den aktuellen Stand, bevor Sie es erneut versuchen.');
});

test.each([
  [400,'invalid_json','Die übermittelten Angaben konnten nicht gelesen werden.'],
  [403,'module_disabled','Diese Funktion ist für Ihren Betrieb nicht aktiviert.'],
  [404,'not_found','Der angeforderte Eintrag wurde nicht gefunden.'],
  [409,'revision_conflict','Der Eintrag wurde zwischenzeitlich geändert.'],
  [409,'idempotency_conflict','Diese Anfrage wurde bereits mit anderen Angaben verwendet.'],
  [413,'payload_too_large','Die übermittelten Daten sind zu groß.'],
  [415,'invalid_request','Die Anfrage konnte nicht verarbeitet werden.'],
  [423,'recovery_hold','Dieser Betrieb ist nach einer Wiederherstellung vorübergehend gesperrt.'],
  [428,'revision_required','Der gespeicherte Stand konnte nicht geprüft werden.'],
  [429,'rate_limit','Es wurden zu viele Anfragen gesendet.'],
  [503,'provider_unconfigured','Die benötigte externe Anbindung ist noch nicht eingerichtet.'],
  [504,'unexpected','Die Anfrage hat zu lange gedauert.'],
  [599,'unexpected','Ein technischer Fehler ist aufgetreten.']
] as const)('API status %i and code %s have an actionable German message', (status,code,prefix)=>{
  expect(errorMessage(new ApiError(status,{code,detail:'Raw technical message'}))).toContain(prefix);
  expect(errorMessage(new ApiError(status,{code,detail:'Raw technical message'}))).not.toContain('Raw technical message');
});

test('login 401 explains credentials without disclosing account existence',async()=>{
  vi.stubGlobal('document',{cookie:'XSRF-TOKEN=synthetic'});
  vi.stubGlobal('fetch',vi.fn().mockResolvedValue(new Response('',{status:401})));
  let failure:unknown;
  try{await requestResponse('/api/v1/auth/login',{method:'POST',body:'{}'});}catch(error){failure=error;}
  expect(failure).toBeInstanceOf(ApiError);
  expect(errorMessage(failure)).toContain('Bitte prüfen Sie E-Mail-Adresse und Passwort');
  expect(errorMessage(failure)).not.toContain('Sitzung ist abgelaufen');
});

test('expired sessions are distinct from invalid login credentials',()=>{
  expect(errorMessage(new ApiError(401,{code:'unauthorized'},undefined,'/api/v1/auth/me'))).toContain('Bitte melden Sie sich erneut an');
});

test('validation fields are presented with safe German labels and without backend text',()=>{
  const error=new ApiError(400,{code:'invalid_request',errors:{
    email:['The Email field is required.'],
    amount:['The field Amount must be between 1 and 100.'],
    'internal.secret':['/srv/app/secrets must not be shown']
  }});
  const message=errorMessage(error);
  expect(message).toContain('„E-Mail-Adresse“: Bitte füllen Sie dieses Pflichtfeld aus.');
  expect(message).toContain('„Betrag“: Bitte geben Sie einen gültigen Zahlenwert');
  expect(message).toContain('Bitte prüfen Sie die markierten Eingabefelder.');
  expect(message).not.toContain('internal.secret');
  expect(message).not.toContain('/srv/');
});

test('server error metadata and source paths must not be reflected in the UI',()=>{
  const error=new ApiError(500,{code:'unexpected_server_code',detail:'System.InvalidOperationException at /srv/private/token'});
  const message=errorMessage(error);
  expect(message).toContain('Ein technischer Fehler ist aufgetreten.');
  expect(message).not.toContain('System.');
  expect(message).not.toContain('/srv/');
});

test('unknown client code still receives a comprehensible fallback',()=>{
  const message=errorMessage(new ApiError(422,{code:'some_future_code',detail:'Invalid ABC-123 payload'}));
  expect(message).toContain('Bitte prüfen Sie Ihre Eingaben.');
  expect(message).not.toContain('ABC-123');
});

test('request IDs appear only for server failures, and are not taken from arbitrary trace data',()=>{
  expect(errorMessage(new ApiError(400,{code:'invalid_json'},'safe-request-id'))).not.toContain('Anfragekennung');
  expect(errorMessage(new ApiError(503,{code:'server_error'},'<script>'))).not.toContain('<script>');
  expect(errorMessage(new ApiError(503,{code:'server_error'},'safe-request-id'))).toContain('Anfragekennung: safe-request-id.');
});

test('a fetch network failure has a helpful message but native runtime TypeErrors do not leak',async()=>{
  vi.stubGlobal('document',{cookie:''});
  vi.stubGlobal('fetch',vi.fn().mockRejectedValue(new TypeError('failed to fetch: /internal/host')));
  let failure:unknown;
  try{await requestResponse('/api/v1/projects');}catch(error){failure=error;}
  expect(failure).toBeInstanceOf(ApiNetworkError);
  expect(errorMessage(failure)).toContain('Prüfen Sie Ihre Internetverbindung');
  expect(errorMessage(failure)).not.toContain('/internal/host');
  expect(errorMessage(new TypeError('Cannot read properties of secret'))).toContain('Ein unerwarteter Fehler');
});

test('explicit cancellation is distinguished from a disconnected server',async()=>{
  vi.stubGlobal('document',{cookie:''});
  vi.stubGlobal('fetch',vi.fn().mockRejectedValue(Object.assign(new Error('abort raw details'),{name:'AbortError'})));
  let failure:unknown;
  try{await requestResponse('/api/v1/projects');}catch(error){failure=error;}
  expect(errorMessage(failure)).toContain('Die Anfrage wurde abgebrochen');
  expect(errorMessage(failure)).not.toContain('abort raw details');
});

test('invalid successful JSON returns an understandable message',async()=>{
  vi.stubGlobal('document',{cookie:''});
  vi.stubGlobal('fetch',vi.fn().mockResolvedValue(new Response('{not-json',{status:200})));
  const {request}=await import('../src/app/core/transport');
  await expect(request('/api/v1/projects')).rejects.toThrow('Der Server hat eine unvollständige Antwort gesendet');
});
