import {expect,Page,Response,test} from '@playwright/test';
import {randomUUID} from 'node:crypto';
import {readFile,writeFile} from 'node:fs/promises';

function responseFor(page:Page,path:string,method='POST'):Promise<Response>{return page.waitForResponse(response=>new URL(response.url()).pathname==='/api/v1/'+path&&response.request().method()===method);}
async function accepted(pending:Promise<Response>):Promise<Record<string,any>>{const response=await pending,value=await response.json();expect(response.ok(),`${response.status()} ${JSON.stringify(value)}`).toBe(true);return value;}
function decimal(value:unknown):string{expect(typeof value).toBe('string');expect(value).toMatch(/^-?\d+(\.\d+)?$/);const[whole,fraction='']=String(value).split('.'),clean=fraction.replace(/0+$/,'');return whole+(clean?'.'+clean:'');}
async function command(page:Page,tenantId:string,path:string,body:unknown,revision?:number):Promise<Record<string,any>>{
 const token=(await page.context().cookies(page.url())).find(cookie=>cookie.name==='XSRF-TOKEN')?.value;expect(token).toBeTruthy();
 const response=await page.request.post('/api/v1/'+path,{data:body,headers:{'X-Tenant-Id':tenantId,'X-XSRF-TOKEN':decodeURIComponent(token!),'Idempotency-Key':randomUUID(),...(revision===undefined?{}:{'If-Match':`"${revision}"`})}});
 const value=await response.json();expect(response.ok(),`${path}: ${response.status()} ${JSON.stringify(value)}`).toBe(true);return value;
}
async function read(page:Page,tenantId:string,path:string):Promise<Record<string,any>>{const response=await page.request.get('/api/v1/'+path,{headers:{'X-Tenant-Id':tenantId}});const value=await response.json();expect(response.ok(),`${path}: ${response.status()} ${JSON.stringify(value)}`).toBe(true);return value;}

test('real historical receivable requires a fresh checked amount after payment before internal reminder approval',async({page},testInfo)=>{
 const fixturePath=process.env['FIRSTBLICK_COLLECTION_FIXTURES_PATH'],email=process.env['FIRSTBLICK_LIVE_EMAIL'],password=process.env['FIRSTBLICK_LIVE_PASSWORD'];
 if(!fixturePath)throw new Error('Missing historical collection fixture manifest. Set FIRSTBLICK_COLLECTION_FIXTURES_PATH after running the isolated Core+EntityStore collection fixture runner. This test does not skip or backdate the public issue API.');
 if(!email||!password)throw new Error('Set FIRSTBLICK_LIVE_EMAIL/PASSWORD for the isolated synthetic operator.');
 const fixture=JSON.parse(await readFile(fixturePath,'utf8'));expect(fixture).toMatchObject({schema:'firstblick.collection-browser-fixtures/1.0.0',synthetic:true});
 const source=fixture.invoices?.[testInfo.project.name];expect(source,'The manifest must contain an unused invoice for this viewport.').toBeTruthy();expect(decimal(source.gross)).toBe('119');
 const failures:string[]=[];page.on('pageerror',error=>failures.push(error.message));page.on('console',message=>{if(message.type()==='error'&&message.text().includes('Content Security Policy'))failures.push(message.text());});
 await page.goto('/login');await page.getByLabel('E-Mail-Adresse').fill(email);await page.getByLabel('Passwort',{exact:true}).fill(password);
 let response=responseFor(page,'auth/login');await page.getByRole('button',{name:'Anmelden',exact:false}).click();await accepted(response);await expect(page).toHaveURL(/\/overview$/);
 await expect(page.locator('.sidebar')).toHaveCSS('position','fixed');
 const session=await(await page.request.get('/api/v1/auth/me')).json();expect(session.user.id).toBe(fixture.userId);expect(session.activeTenantId).toBe(fixture.tenantId);const tenantId=fixture.tenantId;
 const invoiceBefore=await read(page,tenantId,'invoices/'+source.id);expect(invoiceBefore).toMatchObject({id:source.id,status:'Issued',issuedDigest:source.issuedDigest,revision:source.revision,dueDate:source.dueDate});
 expect(new Date(invoiceBefore.dueDate+'T00:00:00Z').getTime()).toBeLessThan(Date.now()-86400000);
 const nextDate=new Date(Date.now()+7*86400000).toISOString().slice(0,10);

 await page.goto('/invoices?selected='+source.id);await page.getByRole('link',{name:'Forderung bearbeiten',exact:true}).click();
 await expect(page.getByRole('heading',{name:'Fall für '+source.number+' anlegen',exact:true})).toBeVisible();
 await page.getByLabel('Verantwortliche Person',{exact:true}).selectOption(fixture.userId);await page.getByLabel('Nächste interne Prüfung',{exact:true}).fill(nextDate);await page.getByLabel('Anlass des Rechnungsfalls',{exact:true}).fill('Historischer synthetischer Beleg und offene Salden ausdrücklich geprüft');
 response=responseFor(page,'collection-cases/from-invoice');await page.getByRole('button',{name:'Rechnungsfall anlegen',exact:true}).click();let collection=await accepted(response);
 await expect(page.getByRole('heading',{name:source.number+' · Aktiv',exact:true})).toBeVisible();await expect(page.getByLabel('Aktuelle Salden')).toContainText('119,00 €');
 async function preview(){
  await page.getByLabel('Gewünschte Stufe',{exact:true}).selectOption('PaymentReminder');await page.getByLabel('Gewünschtes Antwortdatum',{exact:true}).fill(nextDate);await page.getByLabel('Begründung dieses Schritts',{exact:true}).fill('Aktuellen synthetischen Belegstand und Kundenansprache geprüft');await page.getByLabel('Ergänzende Nachricht, optional',{exact:true}).fill('Bitte teilen Sie uns den Stand Ihrer Prüfung mit.');
  const pending=responseFor(page,`collection-cases/${collection.id}/step-preview`);await page.getByRole('button',{name:'Aktuelle Mahnvorschau prüfen',exact:true}).click();const value=await accepted(pending);await expect(page.getByRole('region',{name:'Konkrete Mahnvorschau'})).toBeVisible();return value;
 }
 const oldPreview=await preview();expect(decimal(oldPreview.ledger.balance.collectible)).toBe('119');expect(oldPreview.ledger.sources).toHaveLength(1);
 await expect(page.getByRole('button',{name:'Internen Entwurf freigeben',exact:true})).toBeDisabled();
 const imported=await command(page,tenantId,'bank-transactions/import',{accountReference:'Synthetische Forderungsprüfung '+randomUUID(),transactions:[{externalId:randomUUID(),bookedOn:fixture.businessDate,amount:'20',counterparty:'Synthetischer Forderungskunde',reference:'Synthetischer Zahlungseingang zur Rechnung '+source.number,currency:'EUR'}]});
 const bank=imported.items[0],payment=await command(page,tenantId,'payments/allocate',{invoiceId:source.id,bankTransactionId:bank.id,amount:'20',discountAmount:'0',retainedAmount:'0',reason:'Synthetischer Zahlungseingang nach der ersten Mahnvorschau'},bank.revision);
 await page.getByLabel('Ich habe Text, Betrag, Mahnstufe und gewünschtes Antwortdatum geprüft',{exact:false}).check();response=responseFor(page,`collection-cases/${collection.id}/approve-step`);await page.getByRole('button',{name:'Internen Entwurf freigeben',exact:true}).click();
 const stale=await response,problem=await stale.json();expect(stale.status()).toBe(400);expect(problem.code).toBe('collection_preview_changed');await expect(page.getByRole('alert')).toContainText('Seit der Vorschau');
 const afterRejection=await read(page,tenantId,'collection-cases/'+collection.id);expect(afterRejection.revision).toBe(collection.revision);expect(afterRejection.steps).toEqual([]);
 await page.getByRole('button',{name:'Fall und Saldo aktualisieren',exact:true}).click();await expect(page.getByLabel('Aktuelle Salden')).toContainText('99,00 €');
 const freshPreview=await preview();expect(decimal(freshPreview.ledger.balance.collectible)).toBe('99');expect(freshPreview.digest).not.toBe(oldPreview.digest);expect(freshPreview.ledger.sources).toHaveLength(2);expect(freshPreview.ledger.sources).toContainEqual(expect.objectContaining({kind:'payment-allocations',id:payment.id,revision:payment.revision}));
 await expect(page.getByRole('region',{name:'Konkrete Mahnvorschau'})).toContainText('99,00 EUR');await expect(page.getByRole('button',{name:'Internen Entwurf freigeben',exact:true})).toBeDisabled();
 await page.getByLabel('Ich habe Text, Betrag, Mahnstufe und gewünschtes Antwortdatum geprüft',{exact:false}).check();response=responseFor(page,`collection-cases/${collection.id}/approve-step`);await page.getByRole('button',{name:'Internen Entwurf freigeben',exact:true}).click();collection=await accepted(response);
 await expect(page.getByRole('status').filter({hasText:'Der Text wurde intern freigegeben'})).toBeVisible();await expect(page.getByRole('button',{name:'Kontakt oder Rücknahme',exact:true})).toBeEnabled();await expect(page.getByRole('button',{name:'Aktuelle Mahnvorschau prüfen',exact:true})).toBeDisabled();
 expect(collection.steps).toHaveLength(1);expect(collection.steps[0].preview).toEqual(freshPreview);expect(collection.steps[0].contact).toBeNull();expect(collection.events.map((event:any)=>event.action)).toEqual(['opened','step.approved']);
 expect(await read(page,tenantId,'invoices/'+source.id)).toEqual(invoiceBefore);const current=await read(page,tenantId,`collection-cases/${collection.id}/balance`);expect(decimal(current.ledger.balance.outstanding)).toBe('99');expect(current.caseRevision).toBe(collection.revision);
 await page.getByText('1 freigegebene Entwürfe und manuelle Kontakte',{exact:true}).click();await page.getByText('Damals freigegebener Text und Bindung',{exact:true}).click();await expect(page.getByRole('heading',{name:'Zahlungserinnerung zur Rechnung '+source.number,exact:true})).toBeVisible();
 await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:testInfo.outputPath('collections-real-approved.png'),fullPage:true,animations:'disabled'});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(failures).toEqual([]);
 const evidence={synthetic:true,completedAt:new Date().toISOString(),viewport:testInfo.project.name,runtime:{browser:page.context().browser()?.version(),node:process.version},fixture:{schema:fixture.schema,setupMethod:fixture.setupMethod,invoice:source},assertions:{noApiInterception:true,realSignIn:true,historicalInvoiceExplicitlySeededViaCoreAndEntityStore:true,caseOpeningAndPreviewAndApprovalViaUi:true,paymentViaAuthenticatedHttp:true,oldAmount:'119',actualPayment:'20',freshAmount:'99',stalePreviewRejectedWithoutCaseMutation:true,exactFreshTextAndSourcesRetained:true,invoiceDigestAndRevisionUnchanged:true,noRecordedContact:true,noHorizontalOverflow:true,browserErrors:failures},caseId:collection.id,paymentId:payment.id,oldPreviewDigest:oldPreview.digest,preview:freshPreview,approvedStep:collection.steps[0]};
 const path=testInfo.outputPath('collections-live-evidence.json');await writeFile(path,JSON.stringify(evidence,null,2));await testInfo.attach('collections-live-evidence',{path,contentType:'application/json'});
});
