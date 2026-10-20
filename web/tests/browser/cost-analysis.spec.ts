import {expect,Page,test} from '@playwright/test';
import {readFile} from 'node:fs/promises';

const orderId='21000000-0000-4000-8000-000000000001';
const otherOrderId='21000000-0000-4000-8000-000000000005';
const timeId='21000000-0000-4000-8000-000000000002';
const stockId='21000000-0000-4000-8000-000000000003';
const supplierId='21000000-0000-4000-8000-000000000004';
const digest='d'.repeat(64);
const order={id:orderId,revision:4,title:'Synthetische Nachkalkulation',number:'AU-TEST-01'};
const initial={
  capturedAt:'2026-10-20T08:00:00Z',scope:'Synthetic source review fixture',
  report:{orderId,projectId:'project-a',contractNet:'1190.00',plannedCost:'800.00',knownActualCost:'0.00',actualCost:null,forecastCost:null,forecastMargin:null,marginPercent:null,markupPercent:null,
    contractSources:[{kind:'orders',id:orderId,revision:4,digest,title:order.title,contractNet:'1190.00',plannedCost:'800.00'}],
    sources:[
      {kind:'time-entries',id:timeId,revision:2,digest,title:'Freigegebene Montage',netCost:null,basis:'',missing:'hourly_cost_missing'},
      {kind:'stock-movements',id:stockId,revision:2,digest,title:'Kostenfreie Ersatzlieferung',netCost:null,basis:'',missing:'material_cost_missing'},
      {kind:'supplier-invoices',id:supplierId,revision:3,digest,title:'ER-TEST-01',netCost:null,basis:'',missing:'supplier_cost_treatment_missing'}
    ],
    missing:['recorded_cost_coverage_not_confirmed','time-entries/'+timeId+':hourly_cost_missing','stock-movements/'+stockId+':material_cost_missing','supplier-invoices/'+supplierId+':supplier_cost_treatment_missing','remaining_cost_forecast_missing'],
    reviewedInputs:{timeCosts:[],supplierCosts:[],zeroStockCosts:[],remainingForecastCost:null,forecastEvidence:'',recordedCostsCoverage:''},methodVersion:'1.0.0',digest
  }
};

async function fixture(page:Page,options:{role?:string;late?:Promise<void>;recalculationError?:boolean;recalculationForbidden?:boolean;extraOrder?:boolean;largeAggregate?:boolean}={}){
  const requests:{path:string;body:any;headers:Record<string,string>}[]=[];
  await page.route('**/api/v1/**',async route=>{
    const request=route.request(),path=new URL(request.url()).pathname.slice('/api/v1/'.length);
    if(path==='auth/me')return route.fulfill({json:{user:{id:'user-a',displayName:'Synthetische Verwaltung',email:'office@example.test'},activeTenantId:'tenant-a',tenants:['a','b'].map(id=>({id:'tenant-'+id,name:'Testbetrieb '+id.toUpperCase(),role:options.role||'Office',modules:['core','commercial','finance'],capabilities:['*']}))}});
    if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=cost-test-token; Path=/; SameSite=Strict'}});
    if(path===`orders/${orderId}/cost-analysis`){
      requests.push({path,body:request.postDataJSON(),headers:request.headers()});
      if(options.late)await options.late;
      if(options.recalculationForbidden&&requests.length>1)return route.fulfill({status:403,json:{status:403,code:'forbidden'}});
      if(options.recalculationError&&requests.length>1)return route.fulfill({status:400,json:{status:400,code:'cost_allocation_exceeded'}});
      const value=structuredClone(initial);
      if(options.largeAggregate){value.report.contractNet='100000000000000.01';value.report.contractSources[0].contractNet='100000000000000.01';}
      if(requests.length>1){
        Object.assign(value.report,{knownActualCost:'80.00',actualCost:'80.00',forecastCost:'120.00',forecastMargin:'1070.00',marginPercent:'89.915966',markupPercent:'891.666667',missing:[],reviewedInputs:request.postDataJSON(),digest:'e'.repeat(64)});
        value.report.sources.forEach(source=>Object.assign(source,{netCost:source.id===timeId?'80.00':'0.00',basis:'Synthetisch geprüfter Ansatz',missing:null}));
      }
      return route.fulfill({json:value});
    }
    if(path==='orders'){const items=request.headers()['x-tenant-id']==='tenant-b'?[]:options.extraOrder?[order,{...order,id:otherOrderId,title:'Anderer Auftrag',number:'AU-TEST-02'}]:[order];return route.fulfill({json:{items,total:items.length,page:1,pageSize:100}});}
    if(path==='orders/'+orderId)return route.fulfill({json:order});
    if(request.method()!=='GET')throw new Error('Unexpected mutation in source report '+path);
    return route.fulfill({json:{items:[],total:0,page:1,pageSize:100}});
  });
  return {requests};
}

async function openReport(page:Page){
  await page.goto('/cost-analysis?orderId='+orderId);
  await expect(page.getByRole('heading',{name:order.title,exact:true})).toBeVisible();
}

test('missing cost sources stay unknown and a report download preserves the server result',async({page},testInfo)=>{
  const api=await fixture(page);await openReport(page);
  await expect(page.locator('.cost-completeness')).toContainText('Noch ungeklärt');
  await expect(page.getByText('5 offene Prüfschritte',{exact:true})).toBeVisible();
  expect(api.requests[0].body).toEqual(initial.report.reviewedInputs);
  const downloaded=page.waitForEvent('download');await page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true}).click();
  const download=await downloaded,path=await download.path();expect(path).toBeTruthy();
  const archived=JSON.parse(await readFile(path!,'utf8'));
  expect(archived).toEqual({schema:'firstblick.cost-analysis/1.0.0',...initial});
  expect(api.requests).toHaveLength(1);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  const screenshot=testInfo.outputPath('cost-analysis.png');await page.screenshot({path:screenshot,fullPage:true});await testInfo.attach('cost-analysis',{path:screenshot,contentType:'image/png'});
});

test('reviewed cost evidence, zero stock treatment and forecast are explicit and dirty results cannot be exported',async({page})=>{
  const api=await fixture(page);await openReport(page);
  await page.getByLabel('Diese Zeitbuchung mit einem geprüften Stundensatz bewerten').check();
  await expect(page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen',exact:true})).toBeDisabled();
  await page.getByLabel('Geprüfter Stundensatz (€ / Stunde)',{exact:true}).fill('40');
  await page.getByLabel('Quelle des Stundensatzes',{exact:true}).fill('Geprüfter betrieblicher Satz Oktober 2026');
  await page.getByLabel('Kostenlosen Materialansatz von 0 € ausdrücklich bestätigen').check();
  await page.getByLabel('Nachweis für den kostenlosen Materialansatz',{exact:true}).fill('Dokumentierte kostenfreie Ersatzlieferung');
  await page.getByLabel('Zuordnung dieses Eingangsbelegs ausdrücklich bewerten').check();
  await page.getByLabel('Behandlung des Eingangsbelegs',{exact:true}).selectOption('StockValuation');
  await expect(page.getByLabel('Netto-Kostenanteil dieses Projekts (€)',{exact:true})).toHaveAttribute('readonly','');
  await expect(page.getByLabel('Netto-Kostenanteil dieses Projekts (€)',{exact:true})).toHaveValue('0');
  await page.getByLabel('Grundlage der Belegzuordnung',{exact:true}).fill('Materialverbrauch bereits bewertet');
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('Alle aktuell erfassten Quellen abgeglichen');
  await page.getByLabel('Restkosten ausdrücklich schätzen').check();
  await page.getByLabel('Geschätzte verbleibende Kosten (€)',{exact:true}).fill('40');
  await page.getByLabel('Grundlage der Restkostenschätzung',{exact:true}).fill('Eine dokumentierte Reststunde');
  await expect(page.getByText('Die Eingaben wurden geändert.',{exact:false})).toBeVisible();
  await expect(page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true})).toBeDisabled();
  await page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen',exact:true}).click();
  await expect(page.locator('.cost-completeness')).toContainText('1.070,00');
  expect(api.requests[1].body).toEqual({
    timeCosts:[{timeEntryId:timeId,hourlyCost:'40',evidence:'Geprüfter betrieblicher Satz Oktober 2026'}],
    supplierCosts:[{supplierInvoiceId:supplierId,treatment:'StockValuation',projectNet:'0',evidence:'Materialverbrauch bereits bewertet'}],
    zeroStockCosts:[{stockMovementId:stockId,evidence:'Dokumentierte kostenfreie Ersatzlieferung'}],
    remainingForecastCost:'40',forecastEvidence:'Eine dokumentierte Reststunde',recordedCostsCoverage:'Alle aktuell erfassten Quellen abgeglichen'
  });
  expect(api.requests[1].headers).toMatchObject({'x-tenant-id':'tenant-a','x-xsrf-token':'cost-test-token'});
  await expect(page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true})).toBeEnabled();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});

test('a rejected source allocation keeps the original result and the pending review visible',async({page})=>{
  const api=await fixture(page,{recalculationError:true});await openReport(page);
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('Quelle muss noch korrigiert werden');
  await page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen',exact:true}).click();
  await expect(page.getByRole('alert')).toContainText('Projektanteil darf den Nettoansatz');
  await expect(page.locator('.cost-completeness')).toContainText('Noch ungeklärt');
  await expect(page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true})).toBeDisabled();
  await expect(page.getByLabel('Abgleich der erfassten Kosten',{exact:true})).toHaveValue('Quelle muss noch korrigiert werden');
  expect(api.requests).toHaveLength(2);
});

test('accounting access alone cannot load individual cost assumptions',async({page})=>{
  const api=await fixture(page,{role:'Accounting'});await page.goto('/cost-analysis?orderId='+orderId);
  await expect(page.getByText('Diese Auswertung mit persönlichen Kostenansätzen', {exact:false})).toBeVisible();
  await expect(page.getByLabel('Auftrag für die Nachkalkulation',{exact:true})).toHaveCount(0);
  expect(api.requests).toHaveLength(0);
});

test('a delayed cost report from the previous business is discarded after switching business',async({page})=>{
  let release!:()=>void;const late=new Promise<void>(resolve=>release=resolve),api=await fixture(page,{late});
  await page.goto('/cost-analysis?orderId='+orderId);await expect.poll(()=>api.requests.length).toBe(1);
  await page.getByLabel('Betrieb',{exact:true}).selectOption('tenant-b');
  await expect(page.getByLabel('Auftrag für die Nachkalkulation',{exact:true})).toHaveValue('');
  const response=page.waitForResponse(value=>value.url().includes('/cost-analysis'));release();await response;
  await expect(page.locator('.cost-result')).toHaveCount(0);
  await expect(page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true})).toHaveCount(0);
  expect(api.requests).toHaveLength(1);
});


test('server-side permission revocation clears the captured costs and entered assumptions',async({page})=>{
  const api=await fixture(page,{recalculationForbidden:true});await openReport(page);
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('Persönliche Kostenbasis geprüft');
  await page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen',exact:true}).click();
  await expect(page.getByRole('alert')).toContainText('Berechtigung');
  await expect(page.locator('.cost-result')).toHaveCount(0);
  await expect(page.getByLabel('Abgleich der erfassten Kosten',{exact:true})).toHaveCount(0);
  await expect(page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true})).toHaveCount(0);
  await expect(page.getByLabel('Auftrag für die Nachkalkulation',{exact:true})).toHaveValue('');
  expect(api.requests).toHaveLength(2);
});


test('cancelling an order change preserves unpersisted cost assumptions and the selected report',async({page})=>{
  const api=await fixture(page,{extraOrder:true});await openReport(page);
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('Noch nicht heruntergeladener Prüfstand');
  page.once('dialog',dialog=>dialog.dismiss());
  await page.getByLabel('Auftrag für die Nachkalkulation',{exact:true}).selectOption(otherOrderId);
  await expect(page.getByLabel('Auftrag für die Nachkalkulation',{exact:true})).toHaveValue(orderId);
  await expect(page.getByRole('heading',{name:order.title,exact:true})).toBeVisible();
  await expect(page.getByLabel('Abgleich der erfassten Kosten',{exact:true})).toHaveValue('Noch nicht heruntergeladener Prüfstand');
  await expect(page.getByRole('button',{name:'Aktuellen Bericht als JSON',exact:true})).toBeDisabled();
  expect(api.requests).toHaveLength(1);
});

test('contract aggregates preserve their exact cents beyond binary-float precision',async({page})=>{
  await fixture(page,{largeAggregate:true});await openReport(page);
  const total=page.locator('.cost-result .totals').first().locator('span').filter({hasText:'Angenommener Vertragswert'}).locator('strong');
  await expect(total).toHaveText('100.000.000.000.000,01 €');
});
