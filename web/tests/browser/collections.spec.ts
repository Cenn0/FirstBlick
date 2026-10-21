import {expect,Page,Route,test} from '@playwright/test';
import type {CollectionCase,CollectionLedger,CollectionPreview,CollectionStep,CollectionStepInput} from '../../src/app/core/collections.models';

// API-intercepted UI fixtures. Financial calculations, exact hashes, access and row locks are verified by the separate real-PostgreSQL tests.
const tenantA='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',tenantB='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const actorId='11111111-1111-4111-8111-111111111111',invoiceId='22222222-2222-4222-8222-222222222222',caseId='33333333-3333-4333-8333-333333333333',stepId='44444444-4444-4444-8444-444444444444';
const today='2026-10-26',now=today+'T12:00:00Z';
const hash=(value:number)=>value.toString(16).slice(-1).repeat(64);
const copy=<T>(value:T):T=>structuredClone(value);
function ledger():CollectionLedger{return{version:'1.0.0',tenantId:tenantA,invoiceId,invoiceDigest:hash(1),asOf:today,invoice:{number:'RE-2026-00017',buyerName:'Festgehaltener Dachkunde',buyerAddress:'Quellenstraße 8',sellerName:'Beispielbetrieb',sellerAddress:'Werkstraße 4',currency:'EUR',dueDate:'2026-10-01'},balance:{gross:'119.00',credits:'0.00',paid:'0.00',discount:'0.00',retained:'0.00',outstanding:'119.00',collectible:'119.00',overdue:true,customerCredit:'0.00'},sources:[{kind:'invoices',id:invoiceId,revision:4,digest:hash(2)}],digest:hash(3)};}
function entity():CollectionCase{return{id:caseId,tenantId:tenantA,revision:4,invoiceId,invoiceDigest:hash(1),assignedToUserId:actorId,nextReviewOn:'2026-10-30',status:'Active',steps:[],disputes:[],closures:[],historyDigest:hash(4),events:[{id:'55555555-5555-4555-8555-555555555555',action:'opened',at:'2026-10-20T10:00:00Z',on:'2026-10-20',actorId,reason:'Offene Rechnung geprüft',evidenceReference:'',data:{relatedId:null,assignedToUserId:actorId,nextReviewOn:'2026-10-30',amount:null,closureKind:null,snapshotDigest:hash(3)},previousDigest:hash(0),digest:hash(4)}]};}
function preview(value:CollectionCase,source:CollectionLedger,input:CollectionStepInput):CollectionPreview{return{version:'1.0.0',caseId:value.id,caseRevision:value.revision,caseHistoryDigest:value.historyDigest,input:copy(input),ledger:copy(source),subject:'Zahlungserinnerung zur Rechnung RE-2026-00017',text:`Die Rechnung RE-2026-00017 ist nach dem belegten Stand mit ${source.balance.collectible.replace('.',',')} EUR ohne erfasste Einbehalte offen.\nGewünschtes Antwortdatum: ${input.respondBy}.\n${input.additionalMessage}`,digest:hash(value.revision+3)};}
function approved(value:CollectionCase,source:CollectionLedger):CollectionStep{return{id:stepId,preview:preview(value,source,{stage:'PaymentReminder',respondBy:'2026-10-31',reason:'Vorherigen Entwurf geprüft',additionalMessage:''}),approvedBy:actorId,approvedAt:'2026-10-24T10:00:00Z',digest:hash(8),contact:null,withdrawal:null};}

async function collectionsFixture(page:Page,options:{initial?:boolean;approved?:boolean;module?:boolean;role?:string;rows?:number}={}){
 const state={entity:options.initial===false?null:entity(),source:ledger(),failApprovalOnce:false,abortApprovalOnce:false,revoked:false,holdPreview:false,holdList:false,rows:options.rows??1};
 if(options.approved&&state.entity)state.entity.steps.push(approved(state.entity,state.source));
 const requests:{path:string;method:string;tenant:string;query:Record<string,string>;body:unknown;headers:Record<string,string>}[]=[],failures:string[]=[];
 const receipts=new Map<string,CollectionCase>();let releasePreview:(()=>void)|undefined,releaseList:(()=>void)|undefined;
 page.on('pageerror',error=>failures.push(error.message));
 function mutate(action:string,reason='Vorgang belegt'){
  const value=state.entity!;value.revision++;value.historyDigest=hash(value.revision);value.events.push({id:`66666666-6666-4666-8666-${String(value.revision).padStart(12,'0')}`,action,at:now,on:today,actorId,reason,evidenceReference:'',data:{relatedId:null,assignedToUserId:null,nextReviewOn:null,amount:null,closureKind:null,snapshotDigest:state.source.digest},previousDigest:value.events.at(-1)!.digest,digest:hash(value.revision)});
 }
 const problem=(route:Route,status:number,code:string)=>route.fulfill({status,json:{status,code,title:code}});
 await page.route('**/api/v1/**',async route=>{
  const request=route.request(),url=new URL(request.url()),path=url.pathname.slice('/api/v1/'.length),tenant=request.headers()['x-tenant-id'];
  requests.push({path,method:request.method(),tenant,query:Object.fromEntries(url.searchParams),body:request.postData()?request.postDataJSON():null,headers:request.headers()});
  if(path==='auth/me')return route.fulfill({json:{user:{id:actorId,email:'collections@example.test',displayName:'Alex Beispiel'},activeTenantId:tenantA,tenants:[tenantA,tenantB].map((id,index)=>({id,name:index?'Betrieb Beta':'Betrieb Alpha',role:options.role||'ManagingDirector',modules:['core','commercial',...(options.module===false?[]:['finance'])],capabilities:['*']}))}});
  if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=collections-synthetic-token; Path=/; SameSite=Strict'}});
  if(path==='dashboard')return route.fulfill({json:{projects:0,quoteDrafts:0,openInvoices:1,tasksDue:0}});
  if(path==='collection-cases/assignees')return route.fulfill({json:[{id:actorId,displayName:'Alex Beispiel'}]});
  if(path==='collection-cases/worklist'){
   const number=Number(url.searchParams.get('page')||'1'),size=Number(url.searchParams.get('pageSize')||'25'),search=url.searchParams.get('q')||'',status=url.searchParams.get('status')||'';
   const total=tenant!==tenantA||!state.entity||search==='leer'||status&&state.entity.status!==status?0:url.searchParams.has('invoiceId')?1:state.rows;
   const items=Array.from({length:total},(_,index)=>({id:index===0?caseId:`77777777-7777-4777-8777-${String(index).padStart(12,'0')}`,revision:state.entity!.revision,invoiceId,invoiceNumber:index===0?'RE-2026-00017':`RE-2026-${index}`,buyerName:'Festgehaltener Dachkunde',invoiceDueOn:'2026-10-01',assignedToUserId:actorId,nextReviewOn:'2026-10-30',status:state.entity!.status,updatedAt:now})).slice((number-1)*size,number*size);
   const value={schema:'firstblick.collection-worklist/1.0.0',capturedAt:now,items,total,page:number,pageSize:size};
   if(state.holdList)await new Promise<void>(resolve=>{releaseList=resolve;});return route.fulfill({json:value});
  }
  if(path==='invoices'||path===`invoices/${invoiceId}`){const invoice={id:invoiceId,tenantId:tenantA,revision:4,number:'RE-2026-00017',buyerName:'Festgehaltener Dachkunde',dueDate:'2026-10-01',status:'Issued',kind:'Final',issuedDigest:hash(1)};return route.fulfill({json:path==='invoices'?{items:[invoice],total:1,page:1,pageSize:25}:invoice});}
  if(path==='collection-cases/from-invoice'){
   const input=request.postDataJSON();state.entity=entity();state.entity.revision=1;state.entity.historyDigest=hash(1);state.entity.assignedToUserId=input.assignedToUserId;state.entity.nextReviewOn=input.nextReviewOn;return route.fulfill({json:copy(state.entity)});
  }
  if(path.startsWith('collection-cases/')){
   if(tenant!==tenantA||!state.entity)return problem(route,404,'not_found');if(state.revoked)return problem(route,403,'forbidden');
   if(path===`collection-cases/${caseId}`)return route.fulfill({json:copy(state.entity)});
   if(path===`collection-cases/${caseId}/balance`)return route.fulfill({json:{schema:'firstblick.collection-balance/1.0.0',caseId,caseRevision:state.entity.revision,status:state.entity.status,ledger:copy(state.source)}});
   const input=request.postDataJSON(),action=path.slice(path.lastIndexOf('/')+1),key=request.headers()['idempotency-key'];
   if(action==='step-preview'){const value=preview(state.entity,state.source,input.input);if(state.holdPreview)await new Promise<void>(resolve=>{releasePreview=resolve;});return route.fulfill({json:value});}
   if(receipts.has(key))return route.fulfill({json:copy(receipts.get(key)!)});
   if(action==='approve-step'){
    if(state.failApprovalOnce){state.failApprovalOnce=false;return problem(route,409,'collection_preview_changed');}
    state.entity.steps.push({...approved(state.entity,state.source),preview:preview(state.entity,state.source,input.input),approvedAt:now});mutate('step.approved');
   }else if(action==='withdraw-step'){state.entity.steps.find(step=>step.id===input.stepId)!.withdrawal={reason:input.reason,actorId,at:now};mutate('step.withdrawn',input.reason);}
   else if(action==='record-contact'){state.entity.steps.find(step=>step.id===input.stepId)!.contact={channel:input.channel,occurredAt:input.occurredAt,occurredOn:'2026-10-25',evidenceReference:input.evidenceReference,recordedBy:actorId,recordedAt:now};mutate('contact.recorded');}
   else if(action==='dispute'){state.entity.status='Disputed';state.entity.disputes.push({id:'88888888-8888-4888-8888-888888888888',amount:input.amount,reason:input.reason,evidenceReference:input.evidenceReference,ledgerDigest:state.source.digest,openedBy:actorId,openedAt:now,openedOn:today,resolvedBy:null,resolvedAt:null,resolutionReason:'',resolutionEvidence:''});mutate('dispute.opened',input.reason);}
   else if(action==='resolve-dispute'){state.entity.status='Active';Object.assign(state.entity.disputes.at(-1)!,{resolvedBy:actorId,resolvedAt:now,resolutionReason:input.reason,resolutionEvidence:input.evidenceReference});mutate('dispute.resolved',input.reason);}
   else if(action==='review'){Object.assign(state.entity,{assignedToUserId:input.assignedToUserId,nextReviewOn:input.nextReviewOn});mutate('review.changed',input.reason);}
   else if(action==='close'){state.entity.status='Closed';state.entity.closures.push({id:'99999999-9999-4999-8999-999999999999',kind:input.kind,reason:input.reason,evidenceReference:input.evidenceReference,ledger:copy(state.source),closedBy:actorId,closedAt:now,reopenedBy:null,reopenedAt:null,reopeningReason:'',reopeningEvidence:''});mutate('closed',input.reason);}
   else if(action==='reopen'){state.entity.status='Active';Object.assign(state.entity,{assignedToUserId:input.assignedToUserId,nextReviewOn:input.nextReviewOn});Object.assign(state.entity.closures.at(-1)!,{reopenedBy:actorId,reopenedAt:now,reopeningReason:input.reason,reopeningEvidence:input.evidenceReference});mutate('reopened',input.reason);}
   else return problem(route,404,'not_found');
   receipts.set(key,copy(state.entity));if(action==='approve-step'&&state.abortApprovalOnce){state.abortApprovalOnce=false;return route.abort('failed');}return route.fulfill({json:copy(state.entity)});
  }
  return route.fulfill({json:{items:[],total:0,page:1,pageSize:25}});
 });
 return{state,requests,failures,releasePreview:()=>releasePreview?.(),releaseList:()=>releaseList?.()};
}

async function prepare(page:Page){
 await page.getByLabel('Gewünschte Stufe',{exact:true}).selectOption('PaymentReminder');await page.getByLabel('Gewünschtes Antwortdatum',{exact:true}).fill('2026-11-05');
 await page.getByLabel('Begründung dieses Schritts',{exact:true}).fill('Belegstand und Kundenkontakt geprüft');await page.getByLabel('Ergänzende Nachricht, optional',{exact:true}).fill('Bitte nennen Sie den Stand Ihrer Prüfung.');
 await page.getByRole('button',{name:'Aktuelle Mahnvorschau prüfen',exact:true}).click();await expect(page.getByRole('region',{name:'Konkrete Mahnvorschau'})).toBeVisible();
}
async function confirmPreview(page:Page){await page.getByLabel('Ich habe Text, Betrag, Mahnstufe und gewünschtes Antwortdatum geprüft',{exact:false}).check();await page.getByRole('button',{name:'Internen Entwurf freigeben',exact:true}).click();}
async function clean(page:Page,failures:string[]){expect(failures).toEqual([]);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);}

test('invoice entry requires explicit responsibility and date and approves only the exact checked preview',async({page},testInfo)=>{
 const fixture=await collectionsFixture(page,{initial:false});await page.goto('/collections?invoiceId='+invoiceId);
 await expect(page.getByRole('heading',{name:'Fall für RE-2026-00017 anlegen'})).toBeVisible();
 await page.getByLabel('Verantwortliche Person',{exact:true}).selectOption(actorId);await page.getByLabel('Nächste interne Prüfung',{exact:true}).fill('2026-11-02');await page.getByLabel('Anlass des Rechnungsfalls',{exact:true}).fill('Offene Rechnung konkret geprüft');
 await page.getByRole('button',{name:'Rechnungsfall anlegen',exact:true}).click();await expect(page.getByRole('heading',{name:'RE-2026-00017 · Aktiv',exact:true})).toBeVisible();
 await expect(page.getByLabel('Gewünschte Stufe',{exact:true})).toHaveValue('');await prepare(page);
 await expect(page.getByRole('button',{name:'Internen Entwurf freigeben',exact:true})).toBeDisabled();await expect(page.getByLabel('Gewünschtes Antwortdatum',{exact:true})).toBeDisabled();
 await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:testInfo.outputPath('collections-preview.png'),fullPage:true,animations:'disabled'});
 await confirmPreview(page);await expect(page.getByRole('status').filter({hasText:'Der Text wurde intern freigegeben'})).toBeVisible();
 await expect(page.getByRole('button',{name:'Kontakt oder Rücknahme',exact:true})).toBeEnabled();
 await expect(page.getByRole('button',{name:'Aktuelle Mahnvorschau prüfen',exact:true})).toBeDisabled();
 await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:testInfo.outputPath('collections-approved.png'),fullPage:true,animations:'disabled'});
 const open=fixture.requests.find(item=>item.path.endsWith('/from-invoice'))!,approval=fixture.requests.find(item=>item.path.endsWith('/approve-step'))!;
 expect(open.body).toEqual({invoiceId,assignedToUserId:actorId,nextReviewOn:'2026-11-02',reason:'Offene Rechnung konkret geprüft'});
 expect(approval.body).toEqual({input:{stage:'PaymentReminder',respondBy:'2026-11-05',reason:'Belegstand und Kundenkontakt geprüft',additionalMessage:'Bitte nennen Sie den Stand Ihrer Prüfung.'},previewDigest:hash(4)});
 expect(approval.headers).toMatchObject({'x-tenant-id':tenantA,'x-xsrf-token':'collections-synthetic-token','if-match':'"1"'});expect(approval.headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/);expect(fixture.state.entity?.steps).toHaveLength(1);await clean(page,fixture.failures);
});

test('a changed financial source rejects the old preview and requires a freshly read and checked amount',async({page})=>{
 const fixture=await collectionsFixture(page);await page.goto('/collections?caseId='+caseId);await prepare(page);
 fixture.state.source.balance={...fixture.state.source.balance,paid:'50.00',discount:'5.00',retained:'10.00',outstanding:'64.00',collectible:'54.00'};fixture.state.source.digest=hash(9);fixture.state.failApprovalOnce=true;
 await confirmPreview(page);await expect(page.getByRole('alert')).toContainText('Seit der Vorschau');await expect(page.getByRole('region',{name:'Konkrete Mahnvorschau'})).toHaveCount(0);await expect(page.getByRole('button',{name:'Aktuelle Mahnvorschau prüfen',exact:true})).toBeDisabled();
 await page.getByRole('button',{name:'Fall und Saldo aktualisieren',exact:true}).click();await expect(page.getByLabel('Aktuelle Salden')).toContainText('54,00 €');await prepare(page);
 await expect(page.getByRole('region',{name:'Konkrete Mahnvorschau'})).toContainText('54,00 EUR');await confirmPreview(page);await expect(page.getByRole('status').filter({hasText:'Der Text wurde intern freigegeben'})).toBeVisible();expect(fixture.state.entity?.steps).toHaveLength(1);await clean(page,fixture.failures);
});

test('a partial dispute holds the case and explicit resolution, manual closure and reopening preserve the claim',async({page})=>{
 const fixture=await collectionsFixture(page);await page.goto('/collections?caseId='+caseId);await page.getByRole('button',{name:'Einwand erfassen',exact:true}).click();
 await page.getByLabel('Bestrittener Betrag in EUR',{exact:true}).fill('10.25');await page.getByLabel('Nächste interne Prüfung',{exact:true}).fill('2026-11-03');await page.getByLabel('Konkreter Streitgrund',{exact:true}).fill('Teilmenge wird bestritten');await page.getByLabel('Nachweis des Einwands',{exact:true}).fill('Kundenschreiben vom 26.10.2026');
 await page.getByRole('button',{name:'Streitfall mit Mahnpause erfassen',exact:true}).click();await expect(page.getByRole('heading',{name:'Weitere Mahnfreigaben sind pausiert'})).toBeVisible();await expect(page.getByRole('button',{name:'Mahnschritt vorbereiten',exact:true})).toHaveCount(0);
 expect(fixture.requests.find(item=>item.path.endsWith('/dispute'))!.body).toMatchObject({amount:'10.25'});
 await page.getByRole('button',{name:'Streitfall klären',exact:true}).click();await page.getByLabel('Inhalt der ausdrücklichen Klärung',{exact:true}).fill('Mengen gemeinsam geprüft und anerkannt');await page.getByLabel('Nachweis der Klärung',{exact:true}).fill('Beiderseits bestätigtes Protokoll');await page.getByLabel('Nächste interne Prüfung',{exact:true}).fill('2026-11-04');await page.getByRole('button',{name:'Klärung ausdrücklich dokumentieren',exact:true}).click();
 await expect(page.getByRole('heading',{name:'RE-2026-00017 · Aktiv',exact:true})).toBeVisible();await page.getByRole('button',{name:'Fall abschließen',exact:true}).click();await page.getByLabel('Abschlussgrund',{exact:true}).selectOption('ManualHandling');await page.getByLabel('Konkrete Abschlussbegründung',{exact:true}).fill('Belegte gesonderte Bearbeitung vereinbart');await page.getByLabel('Nachweis der weiteren Behandlung',{exact:true}).fill('Interner Vorgang MAN-17');await page.getByLabel('Ich habe den aktuellen Anspruch, erfasste Einbehalte und den ausdrücklichen Abschlussgrund geprüft.',{exact:true}).check();await page.getByRole('button',{name:'Fall ausdrücklich schließen',exact:true}).click();
 await expect(page.getByRole('heading',{name:'RE-2026-00017 · Geschlossen',exact:true})).toBeVisible();await expect(page.getByLabel('Aktuelle Salden')).toContainText('119,00 €');
 await page.getByRole('button',{name:'Wiedereröffnung vorbereiten',exact:true}).click();await page.getByLabel('Verantwortliche Person',{exact:true}).selectOption(actorId);await page.getByLabel('Nächste interne Prüfung',{exact:true}).fill('2026-11-05');await page.getByLabel('Konkreter Wiedereröffnungsgrund',{exact:true}).fill('Erneute Bearbeitung ausdrücklich beschlossen');await page.getByLabel('Nachweis zur Wiedereröffnung',{exact:true}).fill('Interner Vorgang MAN-17 Fortsetzung');await page.getByRole('button',{name:'Fall ausdrücklich wiedereröffnen',exact:true}).click();
 await expect(page.getByRole('heading',{name:'RE-2026-00017 · Aktiv',exact:true})).toBeVisible();expect(fixture.state.entity?.closures).toHaveLength(1);expect(fixture.state.source.balance.outstanding).toBe('119.00');await clean(page,fixture.failures);
});

test.describe('explicit local contact times',()=>{
 test.use({timezoneId:'Europe/Berlin'});
 test('manual contact rejects a missing spring hour and requires the explicit autumn offset and own confirmation',async({page})=>{
  const fixture=await collectionsFixture(page,{approved:true});await page.goto('/collections?caseId='+caseId);await page.getByRole('button',{name:'Kontakt oder Rücknahme',exact:true}).click();
  await page.getByLabel('Tatsächliche Kontaktart',{exact:true}).selectOption('Email');await page.getByLabel('Tatsächlicher Kontaktzeitpunkt',{exact:true}).fill('2026-03-29T02:30');await expect(page.getByRole('alert')).toContainText('Diese Ortszeit existiert');
  await page.getByLabel('Tatsächlicher Kontaktzeitpunkt',{exact:true}).fill('2026-10-25T02:30');await expect(page.getByLabel('Doppelte Ortsstunde eindeutig zuordnen',{exact:true})).toHaveValue('');await page.getByLabel('Doppelte Ortsstunde eindeutig zuordnen',{exact:true}).selectOption('+01:00');
  await page.getByLabel('Kontaktbeleg oder überprüfbare Fundstelle',{exact:true}).fill('Gespeicherte Ausgangskopie MAIL-17');await expect(page.getByRole('button',{name:'Manuellen Kontakt belegen',exact:true})).toBeDisabled();await page.getByLabel('Ich bestätige als eigene Angabe',{exact:false}).check();await page.getByRole('button',{name:'Manuellen Kontakt belegen',exact:true}).click();
  await expect(page.getByRole('status').filter({hasText:'Der manuell belegte Kontakt'})).toBeVisible();const request=fixture.requests.find(item=>item.path.endsWith('/record-contact'))!;expect(request.body).toEqual({stepId,stepDigest:hash(8),channel:'Email',occurredAt:'2026-10-25T01:30:00.000Z',evidenceReference:'Gespeicherte Ausgangskopie MAIL-17'});expect(fixture.requests.filter(item=>item.path.endsWith('/record-contact'))).toHaveLength(1);await clean(page,fixture.failures);
 });
});

test('an ambiguous approval completion reuses the exact command body, revision and idempotency key',async({page})=>{
 const fixture=await collectionsFixture(page);fixture.state.abortApprovalOnce=true;await page.goto('/collections?caseId='+caseId);await prepare(page);await confirmPreview(page);
 await expect(page.getByRole('heading',{name:'Bestätigung noch offen'})).toBeVisible();await expect(page.getByRole('button',{name:'Einwand erfassen',exact:true})).toBeDisabled();expect(fixture.state.entity?.steps).toHaveLength(1);
 await page.getByRole('button',{name:'Denselben Vorgang erneut prüfen',exact:true}).click();await expect(page.getByRole('heading',{name:'Bestätigung noch offen'})).toHaveCount(0);await expect(page.getByRole('status').filter({hasText:'Der Text wurde intern freigegeben'})).toBeVisible();
 const commands=fixture.requests.filter(item=>item.path.endsWith('/approve-step'));expect(commands).toHaveLength(2);expect(commands[1].body).toEqual(commands[0].body);expect(commands[1].headers['idempotency-key']).toBe(commands[0].headers['idempotency-key']);expect(commands[1].headers['if-match']).toBe('"4"');expect(fixture.state.entity?.steps).toHaveLength(1);await clean(page,fixture.failures);
});

test('a late preview cannot restore the prior business after switching tenants',async({page})=>{
 const fixture=await collectionsFixture(page);fixture.state.holdPreview=true;await page.goto('/collections?caseId='+caseId);
 await page.getByLabel('Gewünschte Stufe',{exact:true}).selectOption('PaymentReminder');await page.getByLabel('Gewünschtes Antwortdatum',{exact:true}).fill('2026-11-05');await page.getByLabel('Begründung dieses Schritts',{exact:true}).fill('Alter Betrieb geprüft');await page.getByRole('button',{name:'Aktuelle Mahnvorschau prüfen',exact:true}).click();await expect.poll(()=>fixture.requests.filter(item=>item.path.endsWith('/step-preview')).length).toBe(1);
 await page.getByLabel('Betrieb',{exact:true}).selectOption(tenantB);await expect(page.getByRole('heading',{name:'RE-2026-00017 · Aktiv',exact:true})).toHaveCount(0);fixture.releasePreview();
 await expect(page.getByRole('region',{name:'Konkrete Mahnvorschau'})).toHaveCount(0);await expect(page.getByRole('heading',{name:'Zahlungserinnerung zur Rechnung RE-2026-00017',exact:true})).toHaveCount(0);expect(fixture.requests.filter(item=>item.path.endsWith('/approve-step'))).toHaveLength(0);await clean(page,fixture.failures);
});

test('worklist search keeps literal characters, explicitly pages in SQL contract and explains empty access',async({page})=>{
 const fixture=await collectionsFixture(page,{rows:26});await page.goto('/collections');const list=page.getByRole('complementary',{name:'Fallübersicht'});
 await expect(list.getByRole('listitem')).toHaveCount(25);await page.getByLabel('Rechnungsnummer oder Kundenname',{exact:true}).fill('50%_ Dach');await page.getByRole('button',{name:'Fälle suchen',exact:true}).click();await expect.poll(()=>fixture.requests.filter(item=>item.path==='collection-cases/worklist'&&item.query['q']==='50%_ Dach').length).toBe(1);
 await page.getByRole('button',{name:'Weitere Fälle',exact:true}).click();await expect(list.getByRole('listitem')).toHaveCount(1);const last=fixture.requests.filter(item=>item.path==='collection-cases/worklist').at(-1)!;expect(last.query).toEqual({page:'2',pageSize:'25',status:'',q:'50%_ Dach'});
 await page.getByLabel('Rechnungsnummer oder Kundenname',{exact:true}).fill('leer');await page.getByRole('button',{name:'Fälle suchen',exact:true}).click();await expect(page.getByText('Für diesen Filter sind keine zugänglichen Rechnungsfälle vorhanden.',{exact:true})).toBeVisible();await clean(page,fixture.failures);
});

test('selected alias is supported and invalid, conflicting or duplicate case identifiers never form an API path',async({page})=>{
 const fixture=await collectionsFixture(page);await page.goto('/collection-cases?selected='+caseId);await expect(page.getByRole('heading',{name:'RE-2026-00017 · Aktiv',exact:true})).toBeVisible();
 for(const query of ['caseId=not-a-uuid','caseId='+caseId+'&selected='+invoiceId,'caseId='+caseId+'&caseId='+invoiceId]){
  const before=fixture.requests.filter(item=>item.path.startsWith('collection-cases/')).length;await page.goto('/collections?'+query);await expect(page.getByRole('alert')).toContainText('genau einen gültigen');expect(fixture.requests.filter(item=>item.path.startsWith('collection-cases/'))).toHaveLength(before);
 }
 await clean(page,fixture.failures);
});

test('a disabled finance module keeps a read-only case and revoked reads clear prior private details',async({page})=>{
 const fixture=await collectionsFixture(page,{module:false});await page.goto('/collections?caseId='+caseId);await expect(page.getByRole('heading',{name:'RE-2026-00017 · Aktiv',exact:true})).toBeVisible();await expect(page.getByRole('button',{name:'Mahnschritt vorbereiten',exact:true})).toHaveCount(0);await expect(page.getByText('Sie können den verfügbaren Stand lesen.',{exact:false})).toBeVisible();
 fixture.state.revoked=true;fixture.state.holdList=true;await page.getByRole('button',{name:'Fall und Saldo aktualisieren',exact:true}).click();await expect(page.getByRole('alert')).toContainText('Der aktuelle Zugang erlaubt');fixture.releaseList();
 await expect(page.getByRole('heading',{name:'RE-2026-00017 · Aktiv',exact:true})).toHaveCount(0);await expect(page.getByText('Festgehaltener Dachkunde',{exact:true})).toHaveCount(0);await clean(page,fixture.failures);
});
