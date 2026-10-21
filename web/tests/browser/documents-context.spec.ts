import {expect,Page,test} from '@playwright/test';
import {createHash} from 'node:crypto';
const bytes=Buffer.from('Synthetische vertrauliche Projektunterlage\n','utf8'),sha256=createHash('sha256').update(bytes).digest('hex');
const documentA={id:'document-a',revision:2,name:'Unterlage aus Betrieb A.txt',contentType:'text/plain',size:bytes.length,sha256,state:'Ready',createdAt:'2026-10-20T08:00:00Z'};
const documentB={...documentA,id:'document-b',name:'Unterlage aus Betrieb B.txt'};
async function fixture(page:Page,options:{late?:Promise<void>;corrupt?:boolean}={}){
 let downloadRequests=0,revoked=false;const downloads:string[]=[];page.on('download',file=>downloads.push(file.suggestedFilename()));
 await page.route('**/api/v1/**',async route=>{const request=route.request(),path=new URL(request.url()).pathname.slice('/api/v1/'.length);if(path==='auth/me')return route.fulfill({json:{user:{id:'user-a',displayName:'Testverwaltung',email:'staff@example.test'},activeTenantId:'tenant-a',tenants:['a','b'].map(id=>({id:'tenant-'+id,name:'Testbetrieb '+id.toUpperCase(),role:'ManagingDirector',modules:['core','commercial','finance'],capabilities:revoked?[]:['*']}))}});if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=synthetic-csrf; Path=/; SameSite=Strict'}});if(path.endsWith('/download')){downloadRequests++;if(options.late)await options.late;return route.fulfill({body:options.corrupt?Buffer.from('changed bytes'):bytes,headers:{'Content-Type':'text/plain'}});}if(path==='documents')return route.fulfill({json:{items:[request.headers()['x-tenant-id']==='tenant-b'?documentB:documentA],total:1,page:1,pageSize:25}});if(request.method()!=='GET')throw new Error('Unexpected document mutation '+path);return route.fulfill({json:{items:[],total:0,page:1,pageSize:25}});});return {downloads,downloadRequests:()=>downloadRequests,revoke:()=>{revoked=true;}};
}
async function releaseLate(page:Page,release:()=>void){const response=page.waitForResponse(value=>value.url().endsWith('/documents/document-a/download'));release();await response;
 // A bounded quiet period after the deliberately withheld response allows Blob/SHA completion before asserting absence of an automatic download.
 await page.waitForTimeout(250);
}

test('a document response from the previous business cannot trigger a late download',async({page})=>{
 let release!:()=>void;const late=new Promise<void>(resolve=>release=resolve),api=await fixture(page,{late});await page.goto('/documents');await page.getByRole('button',{name:'Herunterladen',exact:true}).click();await expect.poll(api.downloadRequests).toBe(1);await page.getByLabel('Betrieb',{exact:true}).selectOption('tenant-b');await expect(page.getByText(documentB.name,{exact:true})).toBeVisible();await expect(page.getByText(documentA.name,{exact:true})).toHaveCount(0);await releaseLate(page,release);expect(api.downloads).toEqual([]);await expect(page.getByRole('button',{name:'Herunterladen',exact:true})).toBeEnabled();
});

test('leaving the documents page discards an unfinished document download',async({page})=>{
 let release!:()=>void;const late=new Promise<void>(resolve=>release=resolve),api=await fixture(page,{late});await page.goto('/documents');await page.getByRole('button',{name:'Herunterladen',exact:true}).click();await expect.poll(api.downloadRequests).toBe(1);await page.getByRole('link',{name:'Übersicht',exact:true}).click();await expect(page).toHaveURL(/\/overview$/);await expect(page.getByRole('heading',{name:'Dokumente',exact:true})).toHaveCount(0);await releaseLate(page,release);expect(api.downloads).toEqual([]);
});

test('document download still verifies its actual bytes before offering a file',async({page})=>{
 const api=await fixture(page,{corrupt:true});await page.goto('/documents');await page.getByRole('button',{name:'Herunterladen',exact:true}).click();await expect(page.getByRole('alert')).toContainText('stimmt nicht mit der gespeicherten Prüfsumme überein');expect(api.downloads).toEqual([]);await expect(page.getByRole('button',{name:'Herunterladen',exact:true})).toBeEnabled();
});

test('withdrawn read access in the same business clears the document and suppresses its delayed download',async({page})=>{
 let release!:()=>void;const late=new Promise<void>(resolve=>release=resolve),api=await fixture(page,{late});
 await page.goto('/documents');await page.getByRole('button',{name:'Herunterladen',exact:true}).click();await expect.poll(api.downloadRequests).toBe(1);
 api.revoke();const refreshed=page.waitForResponse(value=>value.url().endsWith('/auth/me'));
 await page.evaluate(()=>window.dispatchEvent(new Event('online')));await refreshed;
 await expect(page.getByText('Für diesen Bereich fehlt die Leseberechtigung.',{exact:true})).toBeVisible();
 await expect(page.getByText(documentA.name,{exact:true})).toHaveCount(0);
 await releaseLate(page,release);expect(api.downloads).toEqual([]);
});
