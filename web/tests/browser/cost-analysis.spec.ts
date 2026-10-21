import {expect,Page,test} from '@playwright/test';
import {readFile} from 'node:fs/promises';

const orderId='21000000-0000-4000-8000-000000000001';
const otherOrderId='21000000-0000-4000-8000-000000000005';
const timeId='21000000-0000-4000-8000-000000000002';
const stockId='21000000-0000-4000-8000-000000000003';
const supplierId='21000000-0000-4000-8000-000000000004';
const digest='d'.repeat(64);
const forecastId='21000000-0000-4000-8000-000000000006';
const order={id:orderId,revision:4,title:'Synthetische Nachkalkulation',number:'AU-TEST-01'};
const initial={
  capturedAt:'2026-10-20T08:00:00Z',scope:'Synthetic source review fixture',
  report:{orderId,projectId:'21000000-0000-4000-8000-000000000009',contractNet:'1190.00',plannedCost:'800.00',knownActualCost:'0.00',actualCost:null,forecastCost:null,forecastMargin:null,marginPercent:null,markupPercent:null,
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

async function fixture(page:Page,options:{role?:string;late?:Promise<void>;recalculationError?:boolean;recalculationForbidden?:boolean;extraOrder?:boolean;largeAggregate?:boolean;staleForecast?:boolean;allocations?:boolean}={}){
  const requests:{path:string;body:any;headers:Record<string,string>}[]=[];
  const savedForecasts:Record<string,any>[]=[];
  const supplierAllocations:Record<string,any>[]=[];
  let activeAllocationIds:string[]=[];
  let allocationSequence=0;
  let lastReport=structuredClone(initial.report);
  await page.route('**/api/v1/**',async route=>{
    const request=route.request(),path=new URL(request.url()).pathname.slice('/api/v1/'.length);
    if(path==='auth/me')return route.fulfill({json:{user:{id:'user-a',displayName:'Synthetische Verwaltung',email:'office@example.test'},activeTenantId:'tenant-a',tenants:['a','b'].map(id=>({id:'tenant-'+id,name:'Testbetrieb '+id.toUpperCase(),role:options.role||'Office',modules:['core','commercial','finance'],capabilities:['*']}))}});
    if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=cost-test-token; Path=/; SameSite=Strict'}});

    if(options.allocations && path==='supplier-invoices' && request.method()==='GET')
      return route.fulfill({json:{items:[{id:supplierId,revision:3,status:'Approved',title:'Synthetischer Eingangsbeleg',number:'ER-TEST-01',approvedDigest:digest}],total:1,page:1,pageSize:100}});
    if(options.allocations && path===`supplier-invoices/${supplierId}` && request.method()==='GET')
      return route.fulfill({json:{id:supplierId,revision:3,status:'Approved',title:'Synthetischer Eingangsbeleg',number:'ER-TEST-01',approvedDigest:digest}});
    if(options.allocations && path===`supplier-invoices/${supplierId}/cost-allocations`){
      if(request.method()==='GET')return route.fulfill({json:{items:supplierAllocations,active:activeAllocationIds,allocatedNet:activeAllocationIds.length?'70':'0',availableNet:activeAllocationIds.length?'30':'100'}});
      const body=request.postDataJSON();
      requests.push({path,body,headers:request.headers()});
      const entry={id:`21000000-0000-4000-8000-${String(++allocationSequence).padStart(12,'0')}`,revision:1,
        supplierInvoiceId:supplierId,projectId:body.projectId,netAmount:body.netAmount,reason:body.reason,reversesAllocationId:null};
      supplierAllocations.push(entry);activeAllocationIds=[entry.id];
      return route.fulfill({json:entry});
    }
    if(options.allocations && path.includes('/reverse') && path.startsWith('supplier-cost-allocations/')){
      const id=path.split('/')[1],original=supplierAllocations.find(item=>item.id===id);
      if(!original)return route.fulfill({status:404,json:{code:'not_found'}});
      requests.push({path,body:request.postDataJSON(),headers:request.headers()});
      const result={...original,id:'21000000-0000-4000-8000-000000009999',reversesAllocationId:id,reason:request.postDataJSON().reason};
      supplierAllocations.push(result);activeAllocationIds=[];
      return route.fulfill({json:result});
    }
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
      lastReport=structuredClone(value.report);
      return route.fulfill({json:value});
    }
    if(path===`orders/${orderId}/cost-forecasts`){
      if(request.method()==='GET')return route.fulfill({json:{items:savedForecasts.slice().reverse().map(item=>({
        id:item.id,version:item.version,capturedAt:item.capturedAt,capturedByUserId:item.capturedByUserId,
        previousForecastId:item.previousForecastId,reason:item.reason,digest:item.digest,
        reportDigest:item.report.digest,actualCost:item.report.actualCost,forecastCost:item.report.forecastCost,
        forecastMargin:item.report.forecastMargin,missingCount:item.report.missing.length
      })),total:savedForecasts.length}});
      const input=request.postDataJSON();
      if(options.staleForecast||input.expectedReportDigest!==lastReport.digest)
        return route.fulfill({status:409,json:{status:409,code:'stale_cost_report',detail:'Die geprüfte Berechnung ist veraltet. Quellen neu prüfen.'}});
      const record={
        id:forecastId,tenantId:'tenant-a',orderId,projectId:lastReport.projectId,version:1,
        previousForecastId:null,capturedAt:'2026-10-20T11:30:00Z',capturedByUserId:'user-a',
        reason:input.reason,report:structuredClone(lastReport),digest:'f'.repeat(64)
      };
      savedForecasts.push(record);
      return route.fulfill({json:record});
    }
    if(path===`cost-forecasts/${forecastId}`){
      const saved=savedForecasts.find(item=>item.id===forecastId);
      return saved?route.fulfill({json:saved}):route.fulfill({status:404,json:{code:'not_found'}});
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


test('a reviewed cost report can be saved, browsed and exported with its immutable version',async({page},testInfo)=>{
  await fixture(page);
  await openReport(page);
  const save=page.getByRole('button',{name:'Geprüften Stand dauerhaft speichern',exact:true});
  await expect(save).toBeDisabled();
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('Synthetic source completeness reviewed');
  await page.getByLabel('Begründung für die neue Prognoseversion',{exact:true}).fill('Reviewed synthetic checkpoint one');
  await expect(save).toBeDisabled();
  await page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen',exact:true}).click();
  await expect(page.locator('.cost-completeness')).toContainText('1.070,00');
  await expect(save).toBeEnabled();
  await save.click();
  await expect(page.locator('table').filter({hasText:'Gespeicherte Prognoseversionen'})).toContainText('v1');
  await expect(page.locator('table').filter({hasText:'Gespeicherte Prognoseversionen'}).getByText('Reviewed synthetic checkpoint one')).toBeVisible();
  await page.getByRole('button',{name:'Prüfstand öffnen'}).click();
  await expect(page.getByText('Gespeicherter Prüfstand v1',{exact:false})).toBeVisible();
  const downloaded=page.waitForEvent('download');
  await page.getByRole('button',{name:'Diese Version als JSON herunterladen'}).click();
  const item=await downloaded;
  const text=await readFile((await item.path())!,'utf8');
  const archived=JSON.parse(text);
  expect(archived.schema).toBe('firstblick.cost-forecast/1.0.0');
  expect(archived.report.digest).toBe('e'.repeat(64));
  expect(archived.version).toBe(1);
  const screenshot=testInfo.outputPath('saved-cost-forecast.png');
  await page.screenshot({path:screenshot,fullPage:true});
  await testInfo.attach('saved-cost-forecast',{path:screenshot,contentType:'image/png'});
});

test('a stale forecast is not persisted and the original reviewed report remains visible',async({page})=>{
  await fixture(page,{staleForecast:true});await openReport(page);
  await page.getByLabel('Begründung für die neue Prognoseversion',{exact:true}).fill('Outdated synthetic sources');
  await page.getByRole('button',{name:'Geprüften Stand dauerhaft speichern'}).click();
  await expect(page.getByRole('alert')).toContainText('veraltet');
  await expect(page.getByText('Für diesen Auftrag wurde noch keine Prognose dauerhaft gespeichert.')).toBeVisible();
  await expect(page.getByRole('heading',{name:order.title,exact:true})).toBeVisible();
});



test('approved supplier cost is explicitly posted and reversed with separate evidence, keeping unreviewed edits blocked',async({page})=>{
  const api=await fixture(page,{allocations:true});await openReport(page);
  await page.getByLabel('Beleg zur verbindlichen Verteilung').selectOption(supplierId);
  await expect(page.getByText('Noch nicht verteilter Belegnettoanteil:')).toBeVisible();
  await page.getByLabel('Neuer Netto-Projektanteil (€)').fill('70');
  await page.getByLabel('Begründung und Herkunft des Projektanteils').fill('Reviewed synthetic invoice share');
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('Unreviewed change');
  await expect(page.getByRole('button',{name:'Projektanteil verbindlich buchen'})).toBeDisabled();
  await page.getByLabel('Abgleich der erfassten Kosten',{exact:true}).fill('');
  // A recalculation closes the edited state before binding a financial posting.
  await page.getByRole('button',{name:'Bericht mit diesen Ansätzen berechnen'}).click();
  await page.getByRole('button',{name:'Projektanteil verbindlich buchen'}).click();
  await expect(page.getByRole('table',{name:'Aktive gebuchte Verteilungen dieses Eingangsbelegs'})).toContainText('70,00');
  expect(api.requests.filter(item=>item.path.endsWith('/cost-allocations'))).toHaveLength(1);
  expect(api.requests.find(item=>item.path.endsWith('/cost-allocations'))?.body).toMatchObject({
    projectId:'21000000-0000-4000-8000-000000000009',netAmount:'70',expectedSourceDigest:digest,
    reason:'Reviewed synthetic invoice share'
  });
  await page.getByLabel('Begründung',{exact:true}).fill('Review found wrong allocation');
  await page.getByRole('button',{name:'Buchung stornieren'}).click();
  await expect(page.getByRole('table',{name:'Aktive gebuchte Verteilungen dieses Eingangsbelegs'})).toHaveCount(0);
  expect(api.requests.some(item=>item.path.endsWith('/reverse')&&item.body.reason==='Review found wrong allocation')).toBe(true);
});
