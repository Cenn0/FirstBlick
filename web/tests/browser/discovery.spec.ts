import {expect,Page,Route,test} from '@playwright/test';
import {createHash} from 'node:crypto';
import {readFile} from 'node:fs/promises';

const projectId='11111111-1111-4111-8111-111111111111';
const taskId='22222222-2222-4222-8222-222222222222';
// Minimal valid ZIP container; real contents/manifest/ownership are covered by DiscoveryApiTests.
const zip=Buffer.from('504b05060000000000000000000000000000000000000000','hex');
const sha=createHash('sha256').update(zip).digest('hex');

async function discoveryFixture(page:Page,role='ManagingDirector'){
  const state={historyStatus:200,copyCorrupt:false,holdSearch:false,holdCopy:false};
  const searchRequests:{query:string;page:number;kind:string;tenant:string}[]=[],copyRequests:{query:string;headers:Record<string,string>}[]=[],failures:string[]=[];
  let releaseSearch:(()=>void)|undefined,releaseCopy:(()=>void)|undefined;
  page.on('pageerror',error=>failures.push(error.message));
  await page.route('**/api/v1/**',async(route:Route)=>{
    const request=route.request(),url=new URL(request.url()),path=url.pathname.slice('/api/v1/'.length),tenant=request.headers()['x-tenant-id'];
    const capabilities=role==='Worker'?['projects.read','tasks.read','documents.read','time-entries.read','diary-entries.read']:['*'];
    if(path==='auth/me')return route.fulfill({json:{user:{id:'user-own',email:'own@example.test',displayName:'Alex Beispiel'},activeTenantId:'tenant-a',tenants:[
      {id:'tenant-a',name:'Betrieb Alpha',role,employeeId:role==='Worker'?'employee-own':null,modules:['core','field','commercial','finance','purchasing','maintenance','technical'],capabilities},
      {id:'tenant-b',name:'Betrieb Beta',role,employeeId:role==='Worker'?'employee-own-b':null,modules:['core','field','commercial','finance','purchasing','maintenance','technical'],capabilities}
    ]}});
    if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=synthetic-token; Path=/; SameSite=Strict'}});
    if(path==='search'){
      const query=url.searchParams.get('q')||'',number=Number(url.searchParams.get('page')),kind=url.searchParams.get('kind')||'';
      searchRequests.push({query,page:number,kind,tenant});
      if(state.holdSearch)await new Promise<void>(resolve=>{releaseSearch=resolve;});
      const total=query==='leer'?0:26;
      const items=Array.from({length:total},(_,index)=>({kind:'projects',id:index===0?projectId:`00000000-0000-4000-8000-${String(index).padStart(12,'0')}`,
        revision:1,title:`${query} ${tenant==='tenant-a'?'Alpha':'Beta'} ${index}`,number:`P-${index}`,updatedAt:'2026-10-20T08:00:00Z'})).slice((number-1)*25,number*25);
      return route.fulfill({json:{schema:'firstblick.search/1.0.0',items,total,page:number,pageSize:25,capturedAt:'2026-10-20T09:00:00Z'}});
    }
    if(path===`projects/${projectId}/history`){
      if(state.historyStatus!==200)return route.fulfill({status:state.historyStatus,json:{status:state.historyStatus,code:state.historyStatus===404?'not_found':'forbidden'}});
      return route.fulfill({json:{schema:'firstblick.project-history/1.0.0',projectId,associationScope:'current-accessible-project-links',page:1,pageSize:25,total:2,capturedAt:'2026-10-20T09:00:00Z',items:[
        {id:2,occurredAt:'2026-10-20T08:30:00Z',action:'time.approve',kind:'time-entries',entityId:taskId,revision:2,title:'Zeiteintrag freigegeben',performedByCurrentUser:true,contentDigest:'a'.repeat(64)},
        {id:1,occurredAt:'2026-10-20T08:00:00Z',action:'update',kind:'tasks',entityId:taskId,revision:1,title:'Aufgabe geändert',performedByCurrentUser:false,contentDigest:'b'.repeat(64)}
      ]}});
    }
    if(path==='self/data-copy'){
      copyRequests.push({query:url.search,headers:request.headers()});
      if(state.holdCopy)await new Promise<void>(resolve=>{releaseCopy=resolve;});
      return route.fulfill({contentType:'application/zip',body:zip,headers:{'X-FirstBlick-Export-Schema':'firstblick.self-data-copy/1.0.0','X-Content-SHA256':state.copyCorrupt?'f'.repeat(64):sha}});
    }
    return route.fulfill({json:{items:[],total:0,page:1,pageSize:25}});
  });
  return {state,searchRequests,copyRequests,failures,releaseSearch:()=>releaseSearch?.(),releaseCopy:()=>releaseCopy?.()};
}

test('global search sends literal queries and explicit pages and distinguishes the authorized empty state',async({page})=>{
  const fixture=await discoveryFixture(page,'Worker');await page.goto('/discovery');
  await expect(page.getByRole('heading',{name:'Suche',exact:true})).toBeVisible();
  await page.getByLabel('Suchbegriff',{exact:true}).fill('x');await expect(page.getByRole('button',{name:'Suchen',exact:true})).toBeDisabled();
  await expect(page.getByRole('option',{name:'Angebote',exact:true})).toHaveCount(0);await expect(page.getByRole('option',{name:'Mitarbeiter',exact:true})).toHaveCount(0);
  await page.getByLabel('Suchbegriff',{exact:true}).fill('50% Dach');await page.getByLabel('Bereich',{exact:true}).selectOption('projects');await page.getByRole('button',{name:'Suchen',exact:true}).click();
  const results=page.getByRole('list',{name:'Suchergebnisse',exact:true});await expect(results.getByRole('listitem')).toHaveCount(25);
  await expect(page.getByRole('status').filter({hasText:'26 zugängliche Treffer'})).toBeVisible();
  await page.getByRole('button',{name:'Weiter →',exact:true}).click();await expect(results.getByRole('listitem')).toHaveCount(1);
  expect(fixture.searchRequests).toEqual([{query:'50% Dach',page:1,kind:'projects',tenant:'tenant-a'},{query:'50% Dach',page:2,kind:'projects',tenant:'tenant-a'}]);
  await page.getByLabel('Suchbegriff',{exact:true}).fill('leer');await page.getByRole('button',{name:'Suchen',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Keine zugänglichen Treffer auf dieser Seite.'})).toBeVisible();
  await expect(page.getByText('Welche Einträge erscheinen, richtet sich nach Ihren aktuellen Rechten',{exact:false})).toBeVisible();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(fixture.failures).toEqual([]);
});

test('project history explains current associations and removes old events after revoked or unavailable access',async({page})=>{
  const fixture=await discoveryFixture(page);await page.goto('/discovery?projectId='+projectId);
  await expect(page.getByRole('heading',{name:'Projektverlauf',exact:true})).toBeVisible();
  await expect(page.getByText('Der Verlauf folgt den heutigen Projektzuordnungen.',{exact:false})).toBeVisible();
  await expect(page.getByText('Zeiteintrag freigegeben',{exact:true})).toBeVisible();await expect(page.getByText('Von Ihnen',{exact:true})).toBeVisible();
  fixture.state.historyStatus=403;await page.getByRole('button',{name:'Aktualisieren',exact:true}).click();
  await expect(page.getByRole('alert')).toContainText('kein Zugriff');await expect(page.getByText('Zeiteintrag freigegeben',{exact:true})).toHaveCount(0);
  fixture.state.historyStatus=404;await page.getByRole('button',{name:'Aktualisieren',exact:true}).click();
  await expect(page.getByRole('alert')).toContainText('nicht mehr verfügbar');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(fixture.failures).toEqual([]);
});

test('a delayed search response cannot restore data from a previously selected business',async({page})=>{
  const fixture=await discoveryFixture(page);fixture.state.holdSearch=true;await page.goto('/discovery');
  await page.getByLabel('Suchbegriff',{exact:true}).fill('Alter Vorgang');await page.getByRole('button',{name:'Suchen',exact:true}).click();
  await expect.poll(()=>fixture.searchRequests.length).toBe(1);await page.getByLabel('Betrieb',{exact:true}).selectOption('tenant-b');
  await expect(page.getByLabel('Suchbegriff',{exact:true})).toHaveValue('');fixture.releaseSearch();
  await expect(page.getByRole('heading',{name:'Bezeichnung oder Nummer eingeben.'})).toBeVisible();
  await expect(page.getByText('Alter Vorgang Alpha 0',{exact:true})).toHaveCount(0);
  fixture.state.holdSearch=false;await page.getByLabel('Suchbegriff',{exact:true}).fill('Neuer Vorgang');await page.getByRole('button',{name:'Suchen',exact:true}).click();
  await expect(page.getByRole('link',{name:'Neuer Vorgang Beta 0',exact:true})).toBeVisible();expect(fixture.searchRequests[1].tenant).toBe('tenant-b');expect(fixture.failures).toEqual([]);
});

test('own data copy has no subject selector and downloads exactly the verified bytes for the selected business',async({page})=>{
  const fixture=await discoveryFixture(page);await page.goto('/settings');const widget=page.locator('app-self-copy');
  await expect(widget.getByRole('heading',{name:'Meine Datenkopie',exact:true})).toBeVisible();await expect(widget).toContainText('begrenzte Self-Service-Kopie');
  await expect(widget.locator('input,select')).toHaveCount(0);const pending=page.waitForEvent('download');
  await widget.getByRole('button',{name:'Eigene Daten als ZIP herunterladen',exact:true}).click();const download=await pending;
  expect(download.suggestedFilename()).toMatch(/^FirstBlick-meine-Daten-\d{4}-\d{2}-\d{2}\.zip$/);expect(await readFile((await download.path())!)).toEqual(zip);
  await expect(widget.getByRole('status')).toContainText('geprüft und zum Download bereitgestellt');expect(fixture.copyRequests).toHaveLength(1);
  expect(fixture.copyRequests[0].query).toBe('');expect(fixture.copyRequests[0].headers).toMatchObject({'x-tenant-id':'tenant-a','ngsw-bypass':'1'});
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(fixture.failures).toEqual([]);
});

test('own data copy discards a mismatching ZIP hash and never offers a download',async({page})=>{
  const fixture=await discoveryFixture(page);fixture.state.copyCorrupt=true;let downloads=0;page.on('download',()=>downloads++);
  await page.goto('/settings');const widget=page.locator('app-self-copy');await widget.getByRole('button',{name:'Eigene Daten als ZIP herunterladen',exact:true}).click();
  await expect(widget.getByRole('alert')).toContainText('Prüfsumme des Exportpakets stimmt nicht');expect(downloads).toBe(0);await expect(widget.getByRole('status')).toHaveCount(0);expect(fixture.failures).toEqual([]);
});

test('own data copy suppresses a late download after a business switch and after leaving settings',async({page})=>{
  await page.addInitScript(()=>{
    const observed=window as unknown as {discoveryHashes:number};observed.discoveryHashes=0;const digest=crypto.subtle.digest.bind(crypto.subtle);
    crypto.subtle.digest=async(...args:Parameters<typeof digest>)=>{const value=await digest(...args);observed.discoveryHashes++;return value;};
  });
  const fixture=await discoveryFixture(page);fixture.state.holdCopy=true;let downloads=0;page.on('download',()=>downloads++);
  await page.goto('/settings');const widget=page.locator('app-self-copy');await widget.getByRole('button',{name:'Eigene Daten als ZIP herunterladen',exact:true}).click();
  await expect.poll(()=>fixture.copyRequests.length).toBe(1);await page.getByLabel('Betrieb',{exact:true}).selectOption('tenant-b');
  fixture.releaseCopy();await page.waitForFunction(()=>(window as unknown as {discoveryHashes:number}).discoveryHashes>=1);
  await expect(widget.getByRole('button',{name:'Eigene Daten als ZIP herunterladen',exact:true})).toBeEnabled();expect(downloads).toBe(0);
  await widget.getByRole('button',{name:'Eigene Daten als ZIP herunterladen',exact:true}).click();await expect.poll(()=>fixture.copyRequests.length).toBe(2);
  await page.getByRole('link',{name:'Übersicht',exact:true}).first().click();
  await expect(page).toHaveURL(/\/overview$/);await expect(widget).toHaveCount(0);fixture.releaseCopy();
  await page.waitForFunction(()=>(window as unknown as {discoveryHashes:number}).discoveryHashes>=2);
  expect(downloads).toBe(0);expect(fixture.failures).toEqual([]);
});
