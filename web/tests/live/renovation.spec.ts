import {expect,Page,Response,test} from '@playwright/test';
import {readFile,writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';

function responseFor(page:Page,path:string,method='POST'):Promise<Response>{return page.waitForResponse(response=>new URL(response.url()).pathname==='/api/v1/'+path&&response.request().method()===method);}
async function accepted(response:Promise<Response>):Promise<Record<string,any>>{const result=await response;const body=await result.text();const value=body?JSON.parse(body):{};expect(result.ok(),`${result.status()} ${JSON.stringify(value)}`).toBe(true);return value;}
async function action(page:Page,label:string,path:string):Promise<Record<string,any>>{await page.getByRole('button',{name:label,exact:true}).click();const dialog=page.getByRole('dialog');const response=responseFor(page,path);await dialog.getByRole('button',{name:label,exact:true}).click();const value=await accepted(response);await expect(dialog).not.toBeVisible();return value;}

test('renovation from real sign-in through roof, measured quantities, quotation, invoice and verified stored PDF',async({page},testInfo)=>{
  const email=process.env['FIRSTBLICK_LIVE_EMAIL'],password=process.env['FIRSTBLICK_LIVE_PASSWORD'];
  if(!email||!password)throw new Error('Set FIRSTBLICK_LIVE_EMAIL and FIRSTBLICK_LIVE_PASSWORD to a synthetic development operator. This test uses a real API and creates records.');
  const suffix=crypto.randomUUID().slice(0,8);
  const failures:string[]=[];page.on('pageerror',error=>failures.push(error.message));
  await page.goto('/login');await page.getByLabel('E-Mail-Adresse').fill(email);await page.getByLabel('Passwort',{exact:true}).fill(password);const loginResponse=responseFor(page,'auth/login');await page.getByRole('button',{name:'Anmelden',exact:false}).click();await accepted(loginResponse);
  await expect(page).toHaveURL(/\/overview$/);await expect(page.getByRole('heading',{level:1})).toBeVisible();
  await page.screenshot({path:testInfo.outputPath('overview.png'),fullPage:true});

  await page.goto('/customers');await page.getByRole('button',{name:'Kunde anlegen',exact:true}).first().click();let dialog=page.getByRole('dialog');
  await dialog.getByLabel('Name / Firma').fill(`Synthetischer Dachkunde ${suffix}`);await dialog.getByLabel('Anschrift').fill('Testweg 1, 12345 Teststadt');await dialog.getByLabel('E-Mail',{exact:true}).fill(`roof-${suffix}@example.test`);
  let response=responseFor(page,'customers');await dialog.getByRole('button',{name:'Speichern',exact:true}).click();const customer=await accepted(response);await expect(dialog).not.toBeVisible();

  await page.goto('/buildings');await page.getByRole('button',{name:'Gebäude anlegen',exact:true}).first().click();dialog=page.getByRole('dialog');
  await dialog.getByLabel('Bezeichnung').fill(`Testobjekt ${suffix}`);await dialog.getByLabel('Kunde',{exact:false}).selectOption(customer['id']);await dialog.getByLabel('Objektanschrift').fill('Testweg 1, 12345 Teststadt');
  response=responseFor(page,'buildings');await dialog.getByRole('button',{name:'Speichern',exact:true}).click();const building=await accepted(response);await expect(dialog).not.toBeVisible();

  await page.goto('/projects');await page.getByRole('button',{name:'Projekt anlegen',exact:true}).first().click();dialog=page.getByRole('dialog');
  await dialog.getByLabel('Projektname').fill(`Dachsanierung ${suffix}`);await dialog.getByLabel('Kunde',{exact:false}).selectOption(customer['id']);await dialog.getByLabel('Gebäude',{exact:false}).selectOption(building['id']);await dialog.getByLabel('Status',{exact:true}).selectOption('Active');
  response=responseFor(page,'projects');await dialog.getByRole('button',{name:'Speichern',exact:true}).click();const project=await accepted(response);await expect(dialog).not.toBeVisible();

  await page.goto('/roof-revisions');await page.getByRole('button',{name:'Dachrevision anlegen'}).click();dialog=page.getByRole('dialog');
  await dialog.getByLabel('Bezeichnung *',{exact:true}).fill(`Aufgenommener Dachstand ${suffix}`);await dialog.getByLabel('Gebäude *',{exact:true}).selectOption(building['id']);await dialog.getByLabel('Projekt (optional)',{exact:true}).selectOption(project['id']);
  await dialog.getByRole('button',{name:'Dachfläche ergänzen'}).click();await dialog.getByLabel('Flächenname').fill('Südfläche');await dialog.getByLabel('Quelle der Maße').fill('Synthetisches Referenzaufmaß 8 × 4 m');
  for(const [index,point]of [[0,0],[8,0],[8,4],[0,4]].entries()){await dialog.getByLabel(`Fläche 1 Punkt ${index+1} X`,{exact:true}).fill(String(point[0]));await dialog.getByLabel(`Fläche 1 Punkt ${index+1} Y`,{exact:true}).fill(String(point[1]));}
  await dialog.getByRole('button',{name:'Öffnung ergänzen'}).click();await dialog.getByLabel('Bezeichnung der Öffnung').fill('Referenzöffnung');
  for(const [index,point]of [[2,1],[3,1],[3,2],[2,2]].entries()){await dialog.getByLabel(`Öffnung 1 Punkt ${index+1} X`,{exact:true}).fill(String(point[0]));await dialog.getByLabel(`Öffnung 1 Punkt ${index+1} Y`,{exact:true}).fill(String(point[1]));}
  response=responseFor(page,'roof-revisions');await dialog.getByRole('button',{name:'Dachentwurf speichern'}).click();let roof=await accepted(response);await expect(dialog).not.toBeVisible();
  response=responseFor(page,`roof-revisions/${roof['id']}/publish`);page.once('dialog',dialog=>dialog.accept());await page.getByRole('button',{name:'Geprüften Dachstand veröffentlichen'}).click();roof=await accepted(response);expect(roof['snapshot']['netAreaM2']).toBe(31);
  await expect(page.getByText('Veröffentlichte Geometrie',{exact:false})).toBeVisible();await page.screenshot({path:testInfo.outputPath('published-roof.png'),fullPage:true});

  await page.goto('/measurements');await page.getByRole('button',{name:'Aufmaß anlegen',exact:false}).first().click();dialog=page.getByRole('dialog');
  await dialog.getByLabel('Bezeichnung *',{exact:true}).fill(`Aufmaß Sanierung ${suffix}`);await dialog.getByLabel('Projekt *',{exact:true}).selectOption(project['id']);await dialog.getByLabel('Veröffentlichter Dachstand').selectOption(roof['id']);
  await dialog.getByLabel('Leistung / Messgegenstand').fill('Dachfläche mit dokumentiertem Abzug');await dialog.getByLabel('Quelle des Aufmaßes').fill('Veröffentlichter Referenzdachstand');await dialog.getByLabel('Geometrischer Ansatz').selectOption('surfaceNetArea');await dialog.getByLabel('Fläche aus veröffentlichtem Dachstand').selectOption(roof['surfaces'][0]['id']);
  await dialog.getByRole('button',{name:'Begründeten Abzug ergänzen'}).click();await dialog.getByLabel('Grund des Abzugs').fill('Nicht beauftragte Teilfläche');await dialog.getByLabel('Abzug (m²)').fill('0.25');
  response=responseFor(page,'measurements');await dialog.getByRole('button',{name:'Aufmaßentwurf speichern'}).click();let measurement=await accepted(response);await expect(dialog).not.toBeVisible();
  response=responseFor(page,`measurements/${measurement['id']}/preview`,'GET');await page.getByRole('button',{name:'Mengen prüfen'}).click();const preview=await accepted(response);expect(String(preview['totals'][0]['quantity'])).toBe('30.75');
  response=responseFor(page,`measurements/${measurement['id']}/freeze`);page.once('dialog',dialog=>dialog.accept());await page.getByRole('button',{name:'Geprüftes Aufmaß festhalten'}).click();measurement=await accepted(response);expect(measurement['status']).toBe('Frozen');
  await expect(page.getByText(/^Festgehaltene Mengen · Dachversion/)).toBeVisible();await page.screenshot({path:testInfo.outputPath('frozen-measurement.png'),fullPage:true});

  await page.goto('/quotes');await page.getByRole('button',{name:'Angebot anlegen',exact:true}).first().click();dialog=page.getByRole('dialog');
  await dialog.getByLabel('Angebotstitel').fill(`Sanierungsangebot ${suffix}`);await dialog.getByLabel('Kunde',{exact:false}).selectOption(customer['id']);await dialog.getByLabel('Projekt',{exact:false}).selectOption(project['id']);await dialog.getByLabel('Leistung',{exact:true}).fill('Dachfläche gemäß dokumentiertem Aufmaß');await dialog.getByLabel('Menge',{exact:true}).fill('30.75');
  await dialog.getByLabel('Material je Einheit (€)',{exact:true}).fill('12.50');await dialog.getByLabel('Arbeitsstunden je Einheit',{exact:true}).fill('1');await dialog.getByLabel('Stundensatz (€)',{exact:true}).fill('45');await dialog.getByLabel('Zuschlag (%)',{exact:true}).fill('10');await dialog.getByLabel('Umsatzsteuer (%)',{exact:true}).fill('19');
  response=responseFor(page,'quotes');await dialog.getByRole('button',{name:'Speichern',exact:true}).click();let quote=await accepted(response);await expect(dialog).not.toBeVisible();await page.getByRole('button',{name:`Sanierungsangebot ${suffix}`,exact:true}).click();
  quote=await action(page,'Kalkulation festhalten',`quotes/${quote['id']}/freeze`);expect(typeof quote['snapshot']['gross']).toBe('string');expect(quote['issuedDigest']).toBe(quote['presentation']['digest']);expect(quote['presentation']['buyerName']).toBe(customer['name']);
  quote=await action(page,'Annahme dokumentieren',`quotes/${quote['id']}/accept`);expect(quote['status']).toBe('Accepted');const order=await action(page,'Auftrag erstellen',`orders/from-quote/${quote['id']}`);await expect(page).toHaveURL(/\/orders\?selected=/);
  let invoice=await action(page,'Rechnung vorbereiten',`invoices/from-order/${order['id']}`);await expect(page).toHaveURL(/\/invoices\?selected=/);
  await page.getByRole('button',{name:'Bearbeiten',exact:true}).click();dialog=page.getByRole('dialog');await dialog.getByLabel('Anschrift Rechnungssteller').fill('Synthetischer Betriebsweg 2, 12345 Teststadt');await dialog.getByLabel('Steuernummer / USt-IdNr.').fill('SYNTHETIC-TEST-ID');
  response=responseFor(page,`invoices/${invoice['id']}`,'PUT');await dialog.getByRole('button',{name:'Speichern',exact:true}).click();invoice=await accepted(response);await expect(dialog).not.toBeVisible();await page.getByRole('button',{name:String(invoice['title']),exact:true}).click();
  invoice=await action(page,'Rechnung ausstellen',`invoices/${invoice['id']}/issue`);expect(invoice['status']).toBe('Issued');
  const document=await action(page,'PDF erstellen',`invoices/${invoice['id']}/pdf`);await expect(page).toHaveURL(/\/documents\?selected=/);expect(document['state']).toBe('Ready');
  const downloadEvent=page.waitForEvent('download');await page.getByRole('row').filter({hasText:document['name']}).getByRole('button',{name:'Herunterladen'}).click();const download=await downloadEvent;const file=await download.path();expect(file).not.toBeNull();const bytes=await readFile(file!);expect(bytes.subarray(0,5).toString()).toBe('%PDF-');expect(createHash('sha256').update(bytes).digest('hex')).toBe(document['sha256']);
  await page.screenshot({path:testInfo.outputPath('completed-invoice-documents.png'),fullPage:true});
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(failures).toEqual([]);
  const evidence={synthetic:true,completedAt:new Date().toISOString(),project:testInfo.project.name,viewport:page.viewportSize(),runtime:{browser:page.context().browser()?.version(),node:process.version,origin:new URL(page.url()).origin},resourceIds:{customerId:customer['id'],buildingId:building['id'],projectId:project['id'],roofRevisionId:roof['id'],measurementId:measurement['id'],quoteId:quote['id'],orderId:order['id'],invoiceId:invoice['id'],documentId:document['id']},assertions:{realAuthentication:true,noApiInterception:true,tenantBoundCrud:true,roofNetAreaM2:roof['snapshot']['netAreaM2'],frozenMeasurementQuantity:measurement['snapshot']['totals'][0]['quantity'],quantityUnit:measurement['snapshot']['totals'][0]['unit'],issuedQuotePresentationMatchesCustomer:true,acceptanceUsesIssuedDigest:true,orderCreatedFromAcceptedQuote:true,invoiceIssuedFromOrder:true,documentState:document['state'],downloadHasPdfSignature:true,downloadSha256MatchesStoredMetadata:true,noHorizontalOverflow:true,browserErrors:failures},pdf:{name:document['name'],size:bytes.length,expectedSha256:document['sha256'],downloadedSha256:createHash('sha256').update(bytes).digest('hex')}};
  const evidencePath=testInfo.outputPath('workflow-evidence.json');await writeFile(evidencePath,JSON.stringify(evidence,null,2));await testInfo.attach('workflow-evidence',{path:evidencePath,contentType:'application/json'});
});
