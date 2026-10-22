import { createHash } from 'node:crypto';
import { expect, Page, test } from '@playwright/test';
import { activitySourceCsv, type ActivityReport } from '../../src/app/core/business-activity.models';

const tenant='80000000-0000-4000-8000-000000000001', secondTenant='80000000-0000-4000-8000-000000000009';
const project='80000000-0000-4000-8000-000000000002', task='80000000-0000-4000-8000-000000000003';
function report():ActivityReport{return {schema:'firstblick.business-activity/1.0.0',tenantId:tenant,timeZone:'Europe/Berlin',capturedAt:'2026-10-22T12:00:00Z',filter:{from:'2026-10-01',to:'2026-10-22',projectId:null,includeArchived:false},
  current:{from:'2026-10-01',to:'2026-10-22',totals:{projectsCreated:1,tasksCreated:0,tasksDue:1},months:[{from:'2026-10-01',to:'2026-10-22',counts:{projectsCreated:1,tasksCreated:0,tasksDue:1}}]},
  previous:{from:'2026-09-09',to:'2026-09-30',totals:{projectsCreated:0,tasksCreated:1,tasksDue:0},months:[{from:'2026-09-09',to:'2026-09-30',counts:{projectsCreated:0,tasksCreated:1,tasksDue:0}}]},
  sources:[{kind:'projects',id:project,projectId:project,revision:1,title:'Dach Nord',currentStatus:'Active',archived:false,createdOn:'2026-10-02',dueDate:null},
    {kind:'tasks',id:task,projectId:project,revision:2,title:'Aufmaß abgeschlossen',currentStatus:'Done',archived:false,createdOn:'2026-09-30',dueDate:'2026-10-15'}],digest:'a'.repeat(64)};}
async function fixture(page:Page, options:{delay?:boolean;delayExport?:boolean;corrupt?:boolean;denied?:boolean;malformed?:boolean}={}){
  let release!:()=>void;const gate=new Promise<void>(resolve=>{release=resolve;});const requests:string[]=[];
  await page.route('**/api/v1/**',async route=>{
    const url=new URL(route.request().url()),path=url.pathname.slice('/api/v1/'.length);
    if(path==='auth/csrf')return route.fulfill({json:{ready:true}});
    if(path==='auth/me')return route.fulfill({json:{user:{id:'80000000-0000-4000-8000-000000000010',displayName:'Testperson',email:'test@example.test'},activeTenantId:tenant,
      tenants:[tenant,secondTenant].map((id,i)=>({id,name:`Betrieb ${i+1}`,role:options.denied?'Worker':'ManagingDirector',modules:['core'],capabilities:options.denied?['projects.read','tasks.read']:['*']}))}});
    if(path==='projects')return route.fulfill({json:{items:[{id:project,revision:1,name:'Dach Nord'}],total:1,page:1,pageSize:100}});
    if(path==='reports/business-activity'){
      requests.push(url.search);const result=report();if(options.malformed)result.current.totals.projectsCreated=45;
      if(options.delay)await gate;return route.fulfill({json:result});
    }
    if(path==='reports/business-activity/export'){
      requests.push(url.search);const format=url.searchParams.get('format'),body=format==='json'?JSON.stringify(report()):activitySourceCsv(report());
      if(options.delayExport)await gate;
      return route.fulfill({body,headers:{'Content-Type':format==='json'?'application/json':'text/csv','X-Content-Sha256':options.corrupt?'0'.repeat(64):createHash('sha256').update(body).digest('hex'),
        'X-Report-Digest':'a'.repeat(64),'X-Report-Tenant':tenant}});
    }
    return route.fulfill({json:{items:[],total:0,page:1,pageSize:25}});
  });return{requests,release};
}
async function load(page:Page){await page.goto('/business-activity');await page.getByLabel('Von',{exact:true}).fill('2026-10-01');await page.getByLabel('Bis einschließlich').fill('2026-10-22');await page.getByRole('button',{name:'Auswertung laden',exact:true}).click();}

test('period comparison, source links and verified export use the reviewed filters',async({page})=>{
  const api=await fixture(page);await load(page);await expect(page.getByRole('heading',{name:'Monatsabschnitte'})).toBeVisible();
  await expect(page.getByRole('link',{name:'Aufmaß abgeschlossen'})).toHaveAttribute('href',`/tasks?selected=${task}`);
  await expect(page.getByText('Vorzeitraum 0 · Änderung +1',{exact:true})).toHaveCount(2);
  const downloaded=page.waitForEvent('download');await page.getByRole('button',{name:'Gesamtbericht als JSON'}).click();
  expect((await downloaded).suggestedFilename()).toBe('firstblick-activity-2026-10-01-2026-10-22.json');expect(api.requests.at(-1)).toContain('expectedDigest='+ 'a'.repeat(64));
  const csvDownloaded=page.waitForEvent('download');await page.getByRole('button',{name:'Quellen als CSV'}).click();
  expect((await csvDownloaded).suggestedFilename()).toBe('firstblick-activity-2026-10-01-2026-10-22.csv');
  await page.getByLabel('Archivierte Datensätze einbeziehen').check();await expect(page.getByRole('button',{name:'Quellen als CSV'})).toBeDisabled();
  await expect(page.getByText('Die Filter wurden geändert.',{exact:false})).toBeVisible();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});
test('corrupted exports stay blocked and do not announce a download',async({page})=>{
  await fixture(page,{corrupt:true});await load(page);await expect(page.getByRole('heading',{name:'Quellen und Export'})).toBeVisible();
  const downloads:string[]=[];page.on('download',event=>downloads.push(event.suggestedFilename()));await page.getByRole('button',{name:'Quellen als CSV'}).click();
  await expect(page.getByRole('alert').filter({hasText:'Prüfsumme'})).toBeVisible();expect(downloads).toEqual([]);
});
test('a delayed report never reappears after changing the business',async({page})=>{
  const api=await fixture(page,{delay:true});await load(page);await expect.poll(()=>api.requests.length).toBe(1);
  await page.getByLabel('Betrieb',{exact:true}).selectOption(secondTenant);api.release();
  await expect(page.getByRole('heading',{name:'Quellen und Export'})).toHaveCount(0);await expect(page.getByRole('button',{name:'Quellen als CSV'})).toHaveCount(0);
});
test('personal roles cannot request business-wide reports and inconsistent totals are rejected',async({page})=>{
  const api=await fixture(page,{denied:true});await page.goto('/business-activity');await expect(page.getByText('Diese betriebsweite Auswertung ist für Ihren Zugang nicht verfügbar.')).toBeVisible();expect(api.requests).toEqual([]);
});
test('invalid server totals never become reviewed report data',async({page})=>{
  await fixture(page,{malformed:true});await load(page);await expect(page.getByRole('alert').filter({hasText:'nicht eindeutig'})).toBeVisible();await expect(page.getByRole('heading',{name:'Quellen und Export'})).toHaveCount(0);
});
test('a delayed export is discarded after leaving the report page',async({page})=>{
  const api=await fixture(page,{delayExport:true});await load(page);await expect(page.getByRole('heading',{name:'Quellen und Export'})).toBeVisible();
  const downloads:string[]=[];page.on('download',event=>downloads.push(event.suggestedFilename()));
  await page.getByRole('button',{name:'Gesamtbericht als JSON'}).click();await expect.poll(()=>api.requests.length).toBe(2);
  await page.getByRole('link',{name:'Projekte',exact:true}).first().click();await expect(page).toHaveURL(/\/projects$/);
  const completed=page.waitForResponse(response=>response.url().includes('/reports/business-activity/export'));
  api.release();await completed;await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  expect(downloads).toEqual([]);
});
