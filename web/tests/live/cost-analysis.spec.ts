import {expect,APIResponse,Page,Response,test} from '@playwright/test';
import {observeBrowserFailures,verifyProductionStyles} from './production-host';
import {randomUUID} from 'node:crypto';
import {readFile,writeFile} from 'node:fs/promises';

function responseFor(page:Page,path:string):Promise<Response>{return page.waitForResponse(response=>new URL(response.url()).pathname==='/api/v1/'+path&&response.request().method()==='POST');}
async function accepted(response:Promise<Response|APIResponse>):Promise<Record<string,any>>{const result=await response,value=await result.json();expect(result.ok(),`${result.status()} ${JSON.stringify(value)}`).toBe(true);return value;}
async function command(page:Page,tenantId:string,path:string,body:unknown,revision?:number):Promise<Record<string,any>>{
  const token=(await page.context().cookies(page.url())).find(cookie=>cookie.name==='XSRF-TOKEN')?.value;expect(token).toBeTruthy();
  const response=await page.request.post('/api/v1/'+path,{data:body,headers:{'X-Tenant-Id':tenantId,'X-XSRF-TOKEN':decodeURIComponent(token!),'Idempotency-Key':randomUUID(),...(revision===undefined?{}:{'If-Match':`"${revision}"`})}});
  return accepted(Promise.resolve(response));
}
function decimal(value:unknown):string{expect(typeof value).toBe('string');expect(value).toMatch(/^-?\d+(\.\d+)?$/);return String(value).replace(/(\.\d*?)0+$/,'$1').replace(/\.$/,'');}

test('real project sources and explicit cost assumptions produce a reproducible report in the user interface',async({page},testInfo)=>{
  const email=process.env['FIRSTBLICK_LIVE_EMAIL'],password=process.env['FIRSTBLICK_LIVE_PASSWORD'];
  if(!email||!password)throw new Error('Set FIRSTBLICK_LIVE_EMAIL/PASSWORD for an isolated synthetic operator. This test creates actual local source records.');
  const suffix=randomUUID().slice(0,8),day=new Date().toISOString().slice(0,10),failures=observeBrowserFailures(page);
  await page.goto('/login');await verifyProductionStyles(page);expect(failures).toEqual([]);await page.getByLabel('E-Mail-Adresse').fill(email);await page.getByLabel('Passwort',{exact:true}).fill(password);
  let response=responseFor(page,'auth/login');await page.getByRole('button',{name:'Anmelden',exact:false}).click();await accepted(response);await expect(page).toHaveURL(/\/overview$/);
  const session=await(await page.request.get('/api/v1/auth/me')).json(),tenantId=session.activeTenantId;
  const customer=await command(page,tenantId,'customers',{name:'Kosten-Testkunde '+suffix,address:'Synthetischer Testweg 1'});
  const building=await command(page,tenantId,'buildings',{name:'Kosten-Testobjekt '+suffix,customerId:customer.id,address:'Synthetischer Testweg 1'});
  const project=await command(page,tenantId,'projects',{name:'Kosten-Testprojekt '+suffix,customerId:customer.id,buildingId:building.id,status:'Active',type:'Renovation'});
  let quote=await command(page,tenantId,'quotes',{title:'Kosten-Testauftrag '+suffix,customerId:customer.id,projectId:project.id,lines:[{description:'Synthetischer Vertrag',quantity:'1',unit:'Psch',unitMaterialCost:'800',unitLaborHours:'0',hourlyRate:'0',markupPercent:'25',discountPercent:'0',vatPercent:'19'}]});
  quote=await command(page,tenantId,`quotes/${quote.id}/freeze`,{},quote.revision);quote=await command(page,tenantId,`quotes/${quote.id}/accept`,{digest:quote.issuedDigest},quote.revision);
  const order=await command(page,tenantId,`orders/from-quote/${quote.id}`,{},quote.revision);
  const employee=await command(page,tenantId,'employees',{name:'Kosten-Testperson '+suffix,email:'cost-'+suffix+'@example.test',active:true,weeklyHours:'40'});
  let time=await command(page,tenantId,'time-entries',{employeeId:employee.id,projectId:project.id,start:day+'T07:00:00Z',end:day+'T15:00:00Z',activity:'Geprüfte Montage '+suffix,breaks:[{start:day+'T11:00:00Z',end:day+'T11:30:00Z'}],approved:false});
  time=await command(page,tenantId,`time-entries/${time.id}/approve`,{},time.revision);
  const supplier=await command(page,tenantId,'suppliers',{name:'Kosten-Testlieferant '+suffix});
  let supplierInvoice=await command(page,tenantId,'supplier-invoices',{supplierId:supplier.id,number:'ER-COST-'+suffix,title:'Zusätzliche Fremdleistung '+suffix,invoiceDate:day,dueDate:day,net:'80',tax:'15.2',gross:'95.2',kind:'Invoice',status:'Draft'});
  supplierInvoice=await command(page,tenantId,`supplier-invoices/${supplierInvoice.id}/approve`,{},supplierInvoice.revision);

  await page.goto('/orders?selected='+order.id);
  response=responseFor(page,`orders/${order.id}/cost-analysis`);await page.getByRole('link',{name:'Nachkalkulation mit Belegquellen',exact:true}).click();
  const initial=await accepted(response);expect(initial.report.actualCost).toBeNull();expect(initial.report.forecastCost).toBeNull();
  expect(initial.report.sources).toHaveLength(1);expect(initial.report.sources[0]).toMatchObject({kind:'time-entries',id:time.id,netCost:null,missing:'hourly_cost_missing'});
  await expect(page.locator('.cost-completeness')).toContainText('Noch ungeklärt');
  await page.getByLabel('Diese Zeitbuchung mit einem geprüften Stundensatz bewerten').check();
  await page.getByLabel('Geprüfter Stundensatz (€ / Stunde)',{exact:true}).fill('40');
  await page.getByLabel('Quelle des Stundensatzes',{exact:true}).fill('Synthetisch geprüfter Satz 40 EUR je Nettoarbeitsstunde');
  await page.getByText('Weiteren freigegebenen Eingangsbeleg auswählen',{exact:true}).click();
  await page.getByLabel('Eingangsbelege durchsuchen',{exact:true}).fill(suffix);
  await page.getByLabel('Freigegebener Eingangsbeleg',{exact:true}).selectOption(supplierInvoice.id);
  await page.getByRole('button',{name:'Beleg zur Bewertung ergänzen',exact:true}).click();
  await page.getByLabel('Behandlung des Eingangsbelegs',{exact:true}).selectOption('ExternalService');
  await page.getByLabel('Netto-Kostenanteil dieses Projekts (€)',{exact:true}).fill('80');
  await page.getByLabel('Grundlage der Belegzuordnung',{exact:true}).fill('Synthetische Fremdleistung ausschließlich für dieses Projekt');
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('Alle Quellen dieses synthetischen Falls geprüft');
  await page.getByLabel('Restkosten ausdrücklich schätzen').check();
  await page.getByLabel('Geschätzte verbleibende Kosten (€)',{exact:true}).fill('100');
  await page.getByLabel('Grundlage der Restkostenschätzung',{exact:true}).fill('Synthetisch festgelegte verbleibende Arbeit');
  await expect(page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true})).toBeDisabled();
  response=responseFor(page,`orders/${order.id}/cost-analysis`);await page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen',exact:true}).click();const calculated=await accepted(response);
  expect(decimal(calculated.report.contractNet)).toBe('1000');expect(decimal(calculated.report.plannedCost)).toBe('800');
  expect(decimal(calculated.report.actualCost)).toBe('380');expect(decimal(calculated.report.forecastCost)).toBe('480');expect(decimal(calculated.report.forecastMargin)).toBe('520');
  expect(calculated.report.missing).toEqual([]);
  expect(calculated.report.sources.find((source:any)=>source.id===time.id)).toMatchObject({revision:time.revision});
  expect(decimal(calculated.report.sources.find((source:any)=>source.id===time.id).netCost)).toBe('300');
  expect(decimal(calculated.report.sources.find((source:any)=>source.id===supplierInvoice.id).netCost)).toBe('80');
  await expect(page.locator('.cost-completeness')).toContainText('520,00');
  const downloaded=page.waitForEvent('download');await page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true}).click();
  const download=await downloaded,downloadPath=testInfo.outputPath('source-cost-report.json');await download.saveAs(downloadPath);
  expect(JSON.parse(await readFile(downloadPath,'utf8'))).toEqual({schema:'firstblick.cost-analysis/1.0.0',...calculated});
  response=responseFor(page,`orders/${order.id}/cost-analysis`);await page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen',exact:true}).click();const repeated=await accepted(response);expect(repeated.report.digest).toBe(calculated.report.digest);
  for(const[kind,source]of[['orders',order],['time-entries',time],['supplier-invoices',supplierInvoice]]as const){const current=await(await page.request.get(`/api/v1/${kind}/${source.id}`,{headers:{'X-Tenant-Id':tenantId}})).json();expect(current.revision).toBe(source.revision);}
  await page.screenshot({path:testInfo.outputPath('source-cost-report.png'),fullPage:true});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(failures).toEqual([]);
  const evidence={synthetic:true,completedAt:new Date().toISOString(),project:testInfo.project.name,viewport:page.viewportSize(),runtime:{browser:page.context().browser()?.version(),node:process.version},assertions:{noApiInterception:true,setupViaAuthenticatedHttp:true,sourceReviewAndJsonExportViaUi:true,missingRatesStayUnknown:true,explicitRateAndSupplierAllocationAndForecast:true,workedHours:'7.5',hourlyCost:'40',laborCost:'300',supplierCost:'80',actualCost:'380',forecastCost:'480',forecastMargin:'520',downloadEqualsCapturedServerResult:true,repeatedReportDigestStable:true,sourceRevisionsUnchanged:true,noHorizontalOverflow:true,browserErrors:failures},report:calculated.report};
  const evidencePath=testInfo.outputPath('cost-browser-evidence.json');await writeFile(evidencePath,JSON.stringify(evidence,null,2));await testInfo.attach('cost-browser-evidence',{path:evidencePath,contentType:'application/json'});
});
