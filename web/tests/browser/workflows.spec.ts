import { expect, Page, test } from '@playwright/test';
import { createHash } from 'node:crypto';

async function fixtures(page:Page){
  const customers:Record<string,unknown>[]=[];
  let submitted:Record<string,unknown>|undefined;
  let headers:Record<string,string>|undefined;
  await page.route('**/api/v1/**',async route=>{
    const request=route.request();const url=new URL(request.url());const path=url.pathname.slice('/api/v1/'.length);
    if(path==='auth/me')return route.fulfill({json:{user:{id:'user-a',email:'office@example.test',displayName:'Alex Beispiel'},tenants:[{id:'tenant-a',name:'Testbetrieb',role:'ManagingDirector',modules:['core','commercial','field','purchasing','finance','maintenance','training','technical'],capabilities:['*']}],activeTenantId:'tenant-a'}});
    if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=test-token; Path=/; SameSite=Strict'}});
    if(path==='dashboard')return route.fulfill({json:{projects:0,quoteDrafts:2,openInvoices:1,tasksDue:0}});
    if(path==='customers'&&request.method()==='POST'){submitted=request.postDataJSON() as Record<string,unknown>;headers=request.headers();const saved={...submitted,id:'customer-a',revision:1};customers.push(saved);return route.fulfill({json:saved});}
    if(path==='customers')return route.fulfill({json:{items:customers,total:customers.length,page:1,pageSize:25}});
    if(path==='settings')return route.fulfill({json:{id:'tenant-a',name:'Testbetrieb',modules:['core'],approvalActions:[],policyRevision:1,timeZone:'Europe/Berlin'}});
    if(path==='members'||path==='approvals'||path==='jobs')return route.fulfill({json:[]});
    return route.fulfill({json:{items:[],total:0,page:1,pageSize:25}});
  });
  return {submitted:()=>submitted,headers:()=>headers};
}

test('overview cards open the corresponding filtered collection',async({page})=>{
  await fixtures(page);await page.goto('/overview');
  await expect(page.getByRole('heading',{level:1})).toContainText('Alex');
  await page.locator('.stat-card').filter({hasText:'Angebote im Entwurf'}).click();
  await expect(page).toHaveURL(/\/quotes\?status=Draft$/);
  await expect(page.getByRole('heading',{name:'Angebote',exact:true})).toBeVisible();
  await expect(page.locator('.filter-chip')).toHaveText('Entwurf ×');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBe(true);
});

test('customer form sends a real typed, tenant-bound, CSRF-protected command',async({page})=>{
  const evidence=await fixtures(page);await page.goto('/customers');
  await page.getByRole('button',{name:'Kunde anlegen',exact:true}).first().click();
  const dialog=page.getByRole('dialog');await expect(dialog).toBeVisible();
  await dialog.getByLabel('Name / Firma').fill('Dachkunde Beispiel');
  await dialog.getByLabel('Ansprechpartner').fill('Person Beispiel');
  await dialog.getByLabel('E-Mail',{exact:true}).fill('kunde@example.test');
  await dialog.getByLabel('Anschrift').fill('Teststraße 1');
  await dialog.getByRole('button',{name:'Speichern',exact:true}).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole('button',{name:'Dachkunde Beispiel',exact:true})).toBeVisible();
  expect(evidence.submitted()).toMatchObject({name:'Dachkunde Beispiel',contactName:'Person Beispiel',email:'kunde@example.test',address:'Teststraße 1'});
  expect(evidence.headers()?.['x-tenant-id']).toBe('tenant-a');
  expect(evidence.headers()?.['x-xsrf-token']).toBe('test-token');
  expect(evidence.headers()?.['idempotency-key']).toMatch(/^[\da-f-]{36}$/);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBe(true);
});

test('time entry form exposes explicit start, end and pause intervals',async({page})=>{
  await fixtures(page);await page.goto('/time-entries');
  await page.getByRole('button',{name:'Zeiteintrag anlegen',exact:true}).first().click();
  const dialog=page.getByRole('dialog');
  await expect(dialog.getByLabel('Arbeitsbeginn')).toBeVisible();
  await expect(dialog.getByLabel('Arbeitsende')).toBeVisible();
  await dialog.getByRole('button',{name:'Pause ergänzen'}).click();
  await expect(dialog.getByLabel('Pause von')).toBeVisible();
  await expect(dialog.getByLabel('Pause bis')).toBeVisible();
  await expect(dialog.getByRole('button',{name:'Speichern & synchronisieren'})).toBeDisabled();
  await page.keyboard.press('Escape');await expect(dialog).not.toBeVisible();
});

test('technical form does not invent material values or present a normative proof',async({page})=>{
  await fixtures(page);await page.goto('/technical');
  await expect(page.getByText('Diese Ergebnisse sind keine normativen', {exact:false})).toBeVisible();
  await page.getByRole('tab',{name:'Schichtaufbau & Wärme'}).click();
  await expect(page.getByLabel('Wärmeleitfähigkeit (W/mK)',{exact:true})).toHaveValue('');
  await expect(page.getByLabel('Quelle des Kennwerts')).toHaveValue('');
  await expect(page.getByRole('button',{name:'Aufbau berechnen',exact:true})).toBeDisabled();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBe(true);
});

test('quote detail fetches the immutable snapshot before accepting its exact digest',async({page})=>{
  await fixtures(page);
  let accepted:unknown;let revision:string|undefined;
  const quote={id:'quote-a',revision:4,title:'Testdach Angebot',status:'Issued',number:'A-2026-00001'};
  await page.route('**/api/v1/quotes**',route=>{
    const req=route.request();const path=new URL(req.url()).pathname;
    if(path.endsWith('/accept')){accepted=req.postDataJSON();revision=req.headers()['if-match'];return route.fulfill({json:{...quote,status:'Accepted'}});}
    if(path.endsWith('/quote-a'))return route.fulfill({json:{...quote,snapshot:{net:100,tax:19,gross:119,digest:'verified-snapshot'},lines:[]}});
    return route.fulfill({json:{items:[quote],total:1,page:1,pageSize:25}});
  });
  await page.goto('/quotes');await page.getByRole('button',{name:'Testdach Angebot',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Festgehaltene Kalkulation'})).toBeVisible();
  await page.getByRole('button',{name:'Annahme dokumentieren',exact:true}).click();
  await page.getByRole('dialog').getByRole('button',{name:'Annahme dokumentieren',exact:true}).click();
  await expect(page.getByRole('dialog')).not.toBeVisible();
  expect(accepted).toEqual({digest:'verified-snapshot'});expect(revision).toBe('"4"');
});

test('password recovery fragment is consumed into a separate reset form and removed from the URL',async({page})=>{
  await fixtures(page);await page.route('**/api/v1/auth/me',route=>route.fulfill({status:401,json:{status:401}}));
  await page.goto('/#reset-email=test%40example.test&reset-token=temporary-code');
  await expect(page).toHaveURL(/\/reset$/);
  await expect(page.getByRole('heading',{name:'Neues Passwort setzen'})).toBeVisible();
  await expect(page.getByLabel('E-Mail-Adresse',{exact:true})).toHaveValue('test@example.test');
  await expect(page.getByLabel('Wiederherstellungscode')).toHaveValue('temporary-code');
  expect(await page.evaluate(()=>localStorage.getItem('firstblick-last-session'))).toBeNull();
});

test('management can request an independent review without applying a rejected policy change',async({page})=>{
  await fixtures(page);let proposed:Record<string,unknown>|undefined;let changes=0;
  await page.route('**/api/v1/settings',route=>{if(route.request().method()==='PUT'){changes++;return route.fulfill({status:409,json:{status:409,code:'approval_required',detail:'Zweite Freigabe erforderlich.'}});}return route.fulfill({json:{id:'tenant-a',name:'Testbetrieb',modules:['core'],approvalActions:[],policyRevision:7,timeZone:'Europe/Berlin'}});});
  await page.route('**/api/v1/approvals',route=>{if(route.request().method()==='POST'){proposed=route.request().postDataJSON()as Record<string,unknown>;return route.fulfill({json:{id:'approval-a'}});}return route.fulfill({json:[]});});
  await page.goto('/management');await page.getByLabel('Betriebsname').fill('Geprüfter Testbetrieb');await page.getByRole('button',{name:'Betriebseinstellungen speichern'}).click();
  await page.getByRole('button',{name:'Änderung zur Freigabe vorlegen'}).click();
  await expect(page.getByRole('tab',{name:'Freigaben',exact:true})).toHaveAttribute('aria-selected','true');
  expect(changes).toBe(1);expect(proposed).toMatchObject({action:'settings.change',kind:'settings',entityId:'tenant-a',revision:7,payload:{name:'Geprüfter Testbetrieb',policyRevision:7}});
});

test('a partial delivery uses the full order lines and the source revision',async({page})=>{
  await fixtures(page);let received:unknown;let headers:Record<string,string>|undefined;
  const order={id:'purchase-a',revision:3,title:'Blechprofile Test',status:'Ordered',number:'BE-2026-001'};
  await page.route('**/api/v1/purchase-orders**',route=>{
    const path=new URL(route.request().url()).pathname;
    if(path.endsWith('/receive')){received=route.request().postDataJSON();headers=route.request().headers();return route.fulfill({json:{order:{...order,revision:4,status:'PartiallyReceived'},receipt:{id:'receipt-a'}}});}
    if(path.endsWith('/purchase-a'))return route.fulfill({json:{...order,lines:[{id:'line-a',materialId:'material-a',description:'Blechprofil',quantity:10,unitNet:12,vatPercent:19}]}});
    return route.fulfill({json:{items:[order],total:1,page:1,pageSize:25}});
  });
  await page.goto('/purchase-orders');await page.getByRole('button',{name:'Blechprofile Test',exact:true}).click();
  await page.getByRole('button',{name:'Wareneingang erfassen',exact:true}).click();
  const dialog=page.getByRole('dialog');await dialog.getByLabel('Bestellposition').selectOption('line-a');await dialog.getByLabel('Menge',{exact:false}).fill('4');
  await dialog.getByRole('button',{name:'Wareneingang erfassen',exact:true}).click();
  await expect(dialog).not.toBeVisible();expect(received).toMatchObject({lineId:'line-a',location:'Hauptlager'});expect(Number((received as Record<string,unknown>)['quantity'])).toBe(4);expect(headers?.['if-match']).toBe('"3"');
});

test('document upload stages a checksum and sends the original bytes with their content type',async({page})=>{
  await fixtures(page);const body=Buffer.from('Projektunterlage Test\n','utf8');const sha=createHash('sha256').update(body).digest('hex');
  let staged:Record<string,unknown>|undefined;let uploaded:Buffer|null=null;let uploadHeaders:Record<string,string>|undefined;
  const documents:Record<string,unknown>[]=[];
  await page.route('**/api/v1/documents**',route=>{
    const request=route.request();const path=new URL(request.url()).pathname;
    if(path.endsWith('/content')){uploaded=request.postDataBuffer();uploadHeaders=request.headers();documents.push({...staged,id:'document-a',revision:2,state:'Ready',createdAt:'2026-10-20T08:00:00Z'});return route.fulfill({json:documents[0]});}
    if(request.method()==='POST'){staged=request.postDataJSON()as Record<string,unknown>;return route.fulfill({json:{...staged,id:'document-a',revision:1,state:'Staged'}});}
    return route.fulfill({json:{items:documents,total:documents.length,page:1,pageSize:25}});
  });
  await page.goto('/documents');await page.getByRole('button',{name:'Datei hochladen'}).click();
  const dialog=page.getByRole('dialog');await dialog.getByLabel('Datei auswählen').setInputFiles({name:'unterlage.txt',mimeType:'text/plain',buffer:body});await dialog.getByRole('button',{name:'Datei speichern'}).click();
  await expect(dialog).not.toBeVisible();await expect(page.getByText('Geprüft & verfügbar')).toBeVisible();
  expect(staged).toEqual({name:'unterlage.txt',contentType:'text/plain',size:body.length,sha256:sha,projectId:null});
  expect(uploaded).toEqual(body);expect(uploadHeaders?.['content-type']).toBe('text/plain');expect(uploadHeaders?.['if-match']).toBe('"1"');expect(uploadHeaders?.['x-xsrf-token']).toBe('test-token');
});

test('custom form publishing preserves the field schema and submission uses its published revision',async({page})=>{
  await fixtures(page);let definition:Record<string,unknown>|undefined;let submitted:Record<string,unknown>|undefined;let revision:string|undefined;
  await page.route('**/api/v1/projects**',route=>route.fulfill({json:{items:[{id:'project-a',revision:1,name:'Testbaustelle'}],total:1,page:1,pageSize:25}}));
  await page.route('**/api/v1/form-definitions**',route=>{
    const request=route.request();const path=new URL(request.url()).pathname;
    if(path.endsWith('/publish')){definition={...definition,published:true,revision:2};return route.fulfill({json:definition});}
    if(path.endsWith('/submit')){submitted=request.postDataJSON()as Record<string,unknown>;revision=request.headers()['if-match'];return route.fulfill({json:{id:'submission-a',revision:1,projectId:'project-a',submittedAt:'2026-10-20T08:00:00Z',definitionVersion:1,fields:definition!['fields'],values:submitted['values']}});}
    if(request.method()==='POST'){definition={...request.postDataJSON(),id:'definition-a',revision:1};return route.fulfill({json:definition});}
    if(path.endsWith('/definition-a'))return route.fulfill({json:definition});
    return route.fulfill({json:{items:definition?[definition]:[],total:definition?1:0,page:1,pageSize:25}});
  });
  await page.goto('/form-definitions');await page.getByRole('button',{name:'Formular anlegen'}).click();
  const editor=page.getByRole('dialog');await editor.getByLabel('Formularname').fill('Baustellenkontrolle');await editor.getByLabel('Beschriftung').fill('Feststellung');await editor.getByLabel('Pflichtfeld').check();await editor.getByRole('button',{name:'Entwurf speichern'}).click();
  await expect(editor).not.toBeVisible();page.once('dialog',dialog=>dialog.accept());await page.getByRole('button',{name:'Geprüfte Vorlage veröffentlichen'}).click();
  await expect(page.getByRole('button',{name:'Formular verbindlich erfassen'})).toBeVisible();await page.getByLabel('Projekt *',{exact:true}).selectOption('project-a');await page.getByLabel('Feststellung').fill('Dachfläche geprüft');await page.getByRole('button',{name:'Formular verbindlich erfassen'}).click();
  await expect(page.getByText('Formular mit dieser Vorlagenversion verbindlich erfasst.')).toBeVisible();
  const field=(definition!['fields']as {key:string;label:string;required:boolean}[])[0];expect(field).toMatchObject({label:'Feststellung',required:true});expect(submitted).toEqual({projectId:'project-a',values:{[field.key]:'Dachfläche geprüft'}});expect(revision).toBe('"2"');
});

test('field worker navigation hides financial actions and mobile drawer restores focus on Escape',async({page,isMobile})=>{
  await fixtures(page);await page.route('**/api/v1/auth/me',route=>route.fulfill({json:{user:{id:'worker-a',email:'worker@example.test',displayName:'Alex Baustelle'},tenants:[{id:'tenant-a',name:'Testbetrieb',role:'Worker',modules:['core','field'],capabilities:['projects.read','tasks.read','time-entries.read','time-entries.write','diary-entries.read','diary-entries.write']}],activeTenantId:'tenant-a'}}));
  await page.goto('/overview');await expect(page.locator('.stat-card')).toHaveCount(2);await expect(page.getByRole('link',{name:'Angebot vorbereiten'})).toHaveCount(0);await expect(page.getByRole('link',{name:'Projekt anlegen'})).toHaveCount(0);
  if(isMobile){const opener=page.getByRole('button',{name:'Navigation öffnen'});await opener.click();await expect(page.locator('.sidebar')).not.toHaveAttribute('inert');await page.keyboard.press('Escape');await expect(opener).toBeFocused();await expect(page.locator('.sidebar')).toHaveAttribute('inert');}
});
