import {expect,Page,test} from '@playwright/test';
// Calendar due dates retain their day even in a time zone west of UTC.
test.use({timezoneId:'America/Los_Angeles'});
const invoiceId='31000000-0000-4000-8000-000000000001',sourceId='32000000-0000-4000-8000-000000000002';
async function fixture(page:Page,basis:'Contract'|'OriginalInvoice'|'Legacy'='Contract'){
 const input={description:'Anteilig abgerechnete Leistung',quantity:'0.333333',unit:'Stk',unitMaterialCost:'0.05',unitLaborHours:'0',hourlyRate:'0',markupPercent:'0',discountPercent:'0',vatPercent:'19'};
 const priced={inputs:input,unitNet:'0.05',net:basis==='Legacy'?'0.02':'0.01',tax:basis==='Legacy'?'0.00':'0.01',gross:'0.02'};
 const snapshot={net:priced.net,tax:priced.tax,gross:priced.gross,digest:'a'.repeat(64),lines:[priced]};
 const original={...snapshot,net:'0.05',tax:'0.01',gross:'0.06',lines:[{...priced,inputs:{...input,quantity:'1'},net:'0.05',tax:'0.01',gross:'0.06'}]};
 const calculation={methodVersion:'firstblick.invoice-cumulative/1.0.0',basis,basisId:sourceId,sources:[{kind:basis==='OriginalInvoice'?'invoices':'orders',id:sourceId,label:basis==='OriginalInvoice'?'RE-URSPRUNG-001':'AU-2026-001',documentDigest:'b'.repeat(64),snapshot:original}],previousInvoices:[{id:'previous-a',number:'RE-VORHER-001',kind:basis==='OriginalInvoice'?'CreditNote':'Progress',originalInvoiceId:basis==='OriginalInvoice'?sourceId:null,digest:'c'.repeat(64)}],previousBalances:[{lineKey:'d'.repeat(64),quantity:'0.333333',net:'0.02',tax:'0.00'}],lines:[{lineIndex:0,lineKey:'d'.repeat(64),beforeQuantity:'0.333333',afterQuantity:'0.666666',beforeNet:'0.02',beforeTax:'0.00',expectedBeforeNet:'0.02',expectedBeforeTax:'0.00',afterNet:'0.03',afterTax:'0.01',positions:[{sourceIndex:0,positionIndex:0,beforeQuantity:'0.333333',afterQuantity:'0.666666',beforeNet:'0.02',afterNet:'0.03',beforeTax:'0.00',afterTax:'0.01'}]}],digest:'e'.repeat(64)};
 const invoice={id:invoiceId,revision:3,status:'Issued',kind:basis==='OriginalInvoice'?'CreditNote':'Progress',number:'RE-TEST-001',title:'Festgehaltene Teilrechnung',orderId:sourceId,customerId:'customer-a',projectId:'project-a',sellerName:'Testbetrieb',sellerAddress:'Testweg 1',buyerName:'Testkunde',buyerAddress:'Testweg 2',dueDate:'2026-11-01',lines:[input],snapshot,calculation:basis==='Legacy'?null:calculation};
 const state={forbidden:false,holdDetail:false,role:'ManagingDirector'};let readRequests=0,releaseDetail:(()=>void)|undefined;
 await page.route('**/api/v1/**',async route=>{
  const request=route.request(),path=new URL(request.url()).pathname.slice('/api/v1/'.length);
  if(path==='auth/me')return route.fulfill({json:{user:{id:'user-a',displayName:'Testverwaltung',email:'staff@example.test'},activeTenantId:'tenant-a',tenants:[{id:'tenant-a',name:'Testbetrieb',role:state.role,modules:['core','commercial','finance'],capabilities:['*']}]}});
  if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=synthetic-csrf; Path=/; SameSite=Strict'}});
  if(path==='invoices'||path==='invoices/'+invoiceId){
   readRequests++;
   if(state.forbidden)return route.fulfill({status:403,json:{status:403,code:'forbidden'}});
   if(path==='invoices')return route.fulfill({json:{items:[invoice],total:1,page:1,pageSize:25}});
   if(state.holdDetail)await new Promise<void>(resolve=>releaseDetail=resolve);
   return route.fulfill({json:invoice});
  }
  if(path.endsWith('/balance'))return route.fulfill({json:{gross:priced.gross,outstanding:priced.gross,collectible:priced.gross}});
  if(request.method()!=='GET')throw new Error('Read-only invoice evidence made an unexpected command: '+path);
  return route.fulfill({json:{items:[],total:0,page:1,pageSize:25}});
 });
 return {invoice,calculation,state,readRequests:()=>readRequests,releaseDetail:()=>releaseDetail?.()};
}

test('invoice display keeps the actual issued cents and exposes cumulative source positions',async({page},testInfo)=>{
 await fixture(page);await page.goto('/invoices?selected='+invoiceId);
 await expect(page.locator('.collection-panel td[data-label="Fällig am"]')).toHaveText('01.11.2026');
 await expect(page.locator('.detail-panel > dl > div').filter({has:page.getByText('Fällig am',{exact:true})}).locator('dd')).toHaveText('01.11.2026');
 const line=page.locator('.detail-lines tbody tr').first();await expect(line.locator('td[data-label="Einzelpreis netto"]')).toContainText('0,05');await expect(line.locator('td[data-label="Netto dieser Position"]')).toContainText('0,01');await expect(line.locator('td[data-label="Steuer dieser Position"]')).toContainText('0,01');await expect(line.locator('td[data-label="Brutto dieser Position"]')).toContainText('0,02');
 await page.getByText('Herleitung der Rechnungsbeträge ansehen',{exact:true}).click();const evidence=page.locator('.invoice-evidence');await expect(evidence).toContainText('0,333333');await expect(evidence).toContainText('0,666666');await expect(evidence).toContainText('0,02');await expect(evidence).toContainText('0,03');
 await evidence.getByText('Ursprüngliche Positionen und Quellbelege',{exact:true}).click();await expect(evidence.getByRole('link',{name:'AU-2026-001'})).toHaveAttribute('href','/orders?selected='+sourceId);await expect(evidence).toContainText('ursprüngliche Position 1');
 await evidence.getByText('1 berücksichtigte frühere Belege',{exact:true}).click();await expect(evidence.getByRole('link',{name:'RE-VORHER-001'})).toBeVisible();expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
 const screenshot=testInfo.outputPath('invoice-evidence.png');await page.screenshot({path:screenshot,fullPage:true});await testInfo.attach('invoice-evidence',{path:screenshot,contentType:'image/png'});
});

test('correction evidence names its original invoice and preserves its frozen amounts',async({page})=>{
 await fixture(page,'OriginalInvoice');await page.goto('/invoices?selected='+invoiceId);await page.getByText('Herleitung der Rechnungsbeträge ansehen',{exact:true}).click();const evidence=page.locator('.invoice-evidence');await expect(evidence).toContainText('tatsächliche Beträge des Ursprungsbelegs');await evidence.getByText('Ursprüngliche Positionen und Quellbelege',{exact:true}).click();await expect(evidence.getByRole('link',{name:'RE-URSPRUNG-001'})).toHaveAttribute('href','/invoices?selected='+sourceId);await evidence.getByText('1 berücksichtigte frühere Belege',{exact:true}).click();await expect(evidence).toContainText('Korrekturrechnung');
});

test('legacy invoices retain their issued amounts without inventing cumulative evidence',async({page})=>{
 await fixture(page,'Legacy');await page.goto('/invoices?selected='+invoiceId);await expect(page.getByRole('heading',{name:'Festgehaltene Teilrechnung',exact:true})).toBeVisible();await expect(page.getByText('Herleitung der Rechnungsbeträge ansehen',{exact:true})).toHaveCount(0);await expect(page.locator('.detail-lines tbody tr').first().locator('td[data-label="Netto dieser Position"]')).toContainText('0,02');
});


for(const entry of ['list','detail']){
 test(`server-side denial while refreshing the ${entry} removes the invoice and its copied evidence`,async({page})=>{
  const api=await fixture(page);await page.goto('/invoices?selected='+invoiceId);
  await page.getByText('Herleitung der Rechnungsbeträge ansehen',{exact:true}).click();
  await expect(page.locator('.invoice-evidence')).toContainText('angenommener Auftrag');
  api.state.forbidden=true;
  if(entry==='list')await page.getByRole('button',{name:'Suchen',exact:true}).click();
  else await page.getByRole('button',{name:'Festgehaltene Teilrechnung',exact:true}).click();
  await expect(page.getByRole('alert')).toContainText('Berechtigung');
  await expect(page.locator('.detail-panel')).toHaveCount(0);
  await expect(page.locator('.invoice-evidence')).toHaveCount(0);
  await expect(page.getByRole('button',{name:'Festgehaltene Teilrechnung',exact:true})).toHaveCount(0);
 });
}

test('a delayed invoice view cannot restore copied sources after access changed and the new read was denied',async({page})=>{
 const api=await fixture(page);await page.goto('/invoices?selected='+invoiceId);
 await expect(page.getByRole('heading',{name:'Festgehaltene Teilrechnung',exact:true})).toBeVisible();
 const before=api.readRequests();api.state.holdDetail=true;
 await page.getByRole('button',{name:'Festgehaltene Teilrechnung',exact:true}).click();
 await expect.poll(()=>api.readRequests()).toBeGreaterThan(before);
 api.state.forbidden=true;api.state.role='Office';
 const refreshed=page.waitForResponse(response=>response.url().endsWith('/auth/me'));
 await page.evaluate(()=>window.dispatchEvent(new Event('online')));await refreshed;
 await expect(page.getByRole('alert')).toContainText('Berechtigung');
 const delayed=page.waitForResponse(response=>new URL(response.url()).pathname.endsWith('/invoices/'+invoiceId));
 api.releaseDetail();await(await delayed).finished();
 await page.evaluate(()=>new Promise<void>(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve()))));
 await expect(page.locator('.detail-panel')).toHaveCount(0);
 await expect(page.locator('.invoice-evidence')).toHaveCount(0);
});
