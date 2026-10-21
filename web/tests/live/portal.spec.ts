import {expect,Page,Response,test} from '@playwright/test';
import {createHash,randomUUID} from 'node:crypto';
import {readFile,writeFile} from 'node:fs/promises';

function responseFor(page:Page,path:string,method='POST'):Promise<Response>{return page.waitForResponse(response=>new URL(response.url()).pathname==='/api/v1/'+path&&response.request().method()===method);}
async function accepted(response:Promise<Response>):Promise<Record<string,any>>{const result=await response;const value=await result.json();expect(result.ok(),`${result.status()} ${JSON.stringify(value)}`).toBe(true);return value;}
async function signIn(page:Page,email:string,password:string,target:'overview'|'portal'):Promise<void>{await page.goto('/login');await page.getByLabel('E-Mail-Adresse').fill(email);await page.getByLabel('Passwort',{exact:true}).fill(password);const response=responseFor(page,'auth/login');await page.getByRole('button',{name:'Anmelden',exact:false}).click();await accepted(response);await expect(page).toHaveURL(new RegExp('/'+target+'$'));}
async function command(page:Page,tenantId:string,path:string,data:unknown,revision?:number):Promise<Record<string,any>>{
  const token=(await page.context().cookies(page.url())).find(cookie=>cookie.name==='XSRF-TOKEN')?.value;if(!token)throw new Error('The real sign-in must establish the CSRF cookie.');
  const result=await page.request.post('/api/v1/'+path,{headers:{'X-Tenant-Id':tenantId,'X-XSRF-TOKEN':decodeURIComponent(token),'Idempotency-Key':randomUUID(),...(revision===undefined?{}:{'If-Match':`"${revision}"`})},data});const value=await result.json();expect(result.ok(),`${path}: ${result.status()} ${JSON.stringify(value)}`).toBe(true);return value;
}

test('real personally authenticated portal grants, exact quote acceptance, conversation, verified PDF and revocation',async({page,browser},testInfo)=>{
  const email=process.env['FIRSTBLICK_LIVE_EMAIL'],password=process.env['FIRSTBLICK_LIVE_PASSWORD'],partnerEmail=process.env['FIRSTBLICK_LIVE_PARTNER_EMAIL'],partnerPassword=process.env['FIRSTBLICK_LIVE_PARTNER_PASSWORD'];
  if(!email||!password||!partnerEmail||!partnerPassword)throw new Error('Set FIRSTBLICK_LIVE_EMAIL/PASSWORD and FIRSTBLICK_LIVE_PARTNER_EMAIL/PASSWORD to synthetic operator and partner accounts in the same development business. This test creates real records and portal feedback.');
  const suffix=randomUUID().slice(0,8),errors:string[]=[];page.on('pageerror',error=>errors.push(error.message));
  await signIn(page,email,password,'overview');
  const session=await(await page.request.get('/api/v1/auth/me')).json();const tenantId=session.activeTenantId;expect(tenantId).toBeTruthy();
  const recipients=await(await page.request.get('/api/v1/portal/recipients',{headers:{'X-Tenant-Id':tenantId}})).json();const recipient=recipients.find((entry:{email:string})=>entry.email===partnerEmail);expect(recipient).toBeTruthy();

  // The source record setup uses authenticated HTTP. Every portal interaction below uses the UI, without response interception.
  const customer=await command(page,tenantId,'customers',{name:`Synthetische Portalperson ${suffix}`,address:'Portaltestweg 1, 12345 Teststadt',email:partnerEmail});
  const building=await command(page,tenantId,'buildings',{name:`Portaltestobjekt ${suffix}`,customerId:customer['id'],address:'Portaltestweg 1, 12345 Teststadt'});
  const project=await command(page,tenantId,'projects',{name:`Portaltest Dachsanierung ${suffix}`,customerId:customer['id'],buildingId:building['id'],type:'Renovation',status:'Active'});
  let quote=await command(page,tenantId,'quotes',{title:`Portalangebot ${suffix}`,customerId:customer['id'],projectId:project['id'],lines:[{description:'Freigegebene Dachfläche gemäß Referenzaufmaß',unit:'m²',quantity:'30.75',unitMaterialCost:'12.50',unitLaborHours:'1.00',hourlyRate:'45.00',markupPercent:'10.00',discountPercent:'0.00',vatPercent:'19.00'}]});
  quote=await command(page,tenantId,`quotes/${quote['id']}/freeze`,{},quote['revision']);
  const document=await command(page,tenantId,`quotes/${quote['id']}/pdf`,{},quote['revision']);expect(document['state']).toBe('Ready');

  await page.goto(`/quotes?selected=${quote['id']}`);await page.getByRole('link',{name:'Im Kundenportal freigeben',exact:true}).click();let dialog=page.getByRole('dialog');await expect(dialog).toBeVisible();
  await dialog.getByLabel('Persönlich angemeldete Person',{exact:false}).selectOption(recipient.userId);await expect(dialog.getByLabel('Diese Person darf genau diese Angebotsversion',{exact:false})).not.toBeChecked();await dialog.getByLabel('Diese Person darf genau diese Angebotsversion',{exact:false}).check();
  let response=responseFor(page,'portal/grants');await dialog.getByRole('button',{name:'Zugriff freigeben'}).click();const quoteShare=await accepted(response);await expect(dialog).not.toBeVisible();expect(quoteShare['sourceDigest']).toBe(quote['issuedDigest']);expect(quoteShare['recipientUserId']).toBe(recipient.userId);

  const partnerContext=await browser.newContext({baseURL:new URL(page.url()).origin,viewport:page.viewportSize(),isMobile:testInfo.project.use.isMobile,hasTouch:testInfo.project.use.hasTouch});
  try{
    const partner=await partnerContext.newPage();partner.on('pageerror',error=>errors.push(error.message));const privateRequests:string[]=[];
    partner.on('request',request=>{const path=new URL(request.url()).pathname;if(/^\/api\/v1\/(customers|buildings|projects|quotes|invoices|employees|documents|bank-transactions)(\/|$)/.test(path))privateRequests.push(path);});
    await signIn(partner,partnerEmail,partnerPassword,'portal');await expect(partner.getByRole('heading',{level:1,name:'Freigegebene Unterlagen'})).toBeVisible();
    await expect(partner.getByRole('link',{name:'Angebote',exact:true})).toHaveCount(0);await expect(partner.getByRole('link',{name:'Bankumsätze',exact:true})).toHaveCount(0);await expect(partner.getByRole('link',{name:'Synchronisierung',exact:true})).toHaveCount(0);
    response=responseFor(partner,`portal/shares/${quoteShare['id']}`,'GET');await partner.getByRole('button',{name:quote['title'],exact:true}).click();const detail=await accepted(response);
    for(const internalKey of ['unitMaterialCost','unitLaborHours','hourlyRate','markupPercent','unitCost','pricingDigest'])expect(JSON.stringify(detail)).not.toContain(`"${internalKey}"`);
    expect(detail['quote']['gross']).toBe(quote['snapshot']['gross']);expect(detail['quote']['presentation']['buyerName']).toBe(customer['name']);expect(detail['quote']['lines'][0]['quantity']).toBe('30.75');
    await partner.screenshot({path:testInfo.outputPath('portal-released-quote.png'),fullPage:true});

    await partner.getByRole('button',{name:'Angebot annehmen',exact:true}).click();dialog=partner.getByRole('dialog');await expect(dialog.getByRole('button',{name:'Verbindlich annehmen'})).toBeDisabled();await dialog.getByLabel('Ich habe diese Angebotsversion',{exact:false}).check();
    response=responseFor(partner,`portal/shares/${quoteShare['id']}/accept`);await dialog.getByRole('button',{name:'Verbindlich annehmen'}).click();const acceptanceResponse=await response,acceptanceRequest=acceptanceResponse.request();expect(acceptanceResponse.ok()).toBe(true);const acceptance=await acceptanceResponse.json();
    expect(acceptanceRequest.postDataJSON()).toEqual({digest:quote['issuedDigest']});expect(acceptanceRequest.headers()['if-match']).toBe(`"${quote['revision']}"`);expect(acceptanceRequest.headers()['idempotency-key']).toBeTruthy();expect(acceptance.quote.status).toBe('Accepted');await expect(dialog).not.toBeVisible();
    await expect(partner.getByText('Diese Angebotsversion wurde verbindlich angenommen.')).toBeVisible();

    const question=`Synthetische Rückfrage ${suffix}: Bitte die vereinbarte Dachfläche bestätigen.`;
    await partner.getByLabel('Ihre Rückfrage oder Rückmeldung').fill(question);response=responseFor(partner,`portal/shares/${quoteShare['id']}/feedback`);await partner.getByRole('button',{name:'Rückmeldung speichern'}).click();const feedbackResponse=await response;expect(feedbackResponse.ok()).toBe(true);expect(feedbackResponse.request().headers()['if-match']).toBe(`"${quoteShare['revision']}"`);await expect(partner.locator('.portal-message')).toContainText(question);
    await page.goto(`/portal?selected=${quoteShare['id']}`);await expect(page.locator('.portal-message')).toContainText(question);const reply=`Synthetische Antwort ${suffix}: Die vereinbarte Dachfläche ist im festgehaltenen Angebot dokumentiert.`;
    await page.getByLabel('Antwort an den freigegebenen Portalzugang').fill(reply);response=responseFor(page,`portal/grants/${quoteShare['id']}/reply`);await page.getByRole('button',{name:'Antwort speichern'}).click();await accepted(response);await partner.getByRole('button',{name:'Aktualisieren',exact:true}).click();await expect(partner.locator('.portal-message.from-business')).toContainText(reply);
    await partner.screenshot({path:testInfo.outputPath('portal-accepted-conversation.png'),fullPage:true});

    await page.goto(`/documents?selected=${document['id']}`);await page.getByRole('row').filter({hasText:document['name']}).getByRole('link',{name:'Im Kundenportal freigeben'}).click();dialog=page.getByRole('dialog');await dialog.getByLabel('Persönlich angemeldete Person',{exact:false}).selectOption(recipient.userId);await expect(dialog.getByLabel('Diese Person darf genau diese Angebotsversion',{exact:false})).toHaveCount(0);
    response=responseFor(page,'portal/grants');await dialog.getByRole('button',{name:'Zugriff freigeben'}).click();const documentShare=await accepted(response);await expect(dialog).not.toBeVisible();expect(documentShare['sourceDigest']).toBe(document['sha256']);
    await partner.getByRole('button',{name:'Aktualisieren',exact:true}).click();await partner.getByRole('button',{name:document['name'],exact:true}).click();const downloadEvent=partner.waitForEvent('download');await partner.getByRole('button',{name:'Datei herunterladen'}).click();const download=await downloadEvent,file=await download.path();expect(file).not.toBeNull();const bytes=await readFile(file!),downloadedSha256=createHash('sha256').update(bytes).digest('hex');expect(bytes.subarray(0,5).toString()).toBe('%PDF-');expect(downloadedSha256).toBe(document['sha256']);
    await partner.screenshot({path:testInfo.outputPath('portal-verified-document.png'),fullPage:true});

    response=responseFor(page,`portal/grants/${documentShare['id']}/revoke`);page.once('dialog',confirmation=>confirmation.accept());await page.getByRole('button',{name:'Zugriff beenden',exact:true}).click();await accepted(response);
    await partner.getByRole('button',{name:'Aktualisieren',exact:true}).click();await expect(partner.getByText('Diese Freigabe ist nicht mehr verfügbar.',{exact:false})).toBeVisible();await expect(partner.locator('.portal-detail')).toHaveCount(0);await expect(partner.getByRole('button',{name:'Datei herunterladen'})).toHaveCount(0);
    const blocked=await partner.request.get(`/api/v1/portal/shares/${documentShare['id']}/download`,{headers:{'X-Tenant-Id':tenantId}});expect(blocked.ok()).toBe(false);expect((await blocked.json()).code).toBe('not_found');
    expect(await partner.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(privateRequests).toEqual([]);expect(errors).toEqual([]);

    const evidence={synthetic:true,completedAt:new Date().toISOString(),project:testInfo.project.name,viewport:partner.viewportSize(),runtime:{browser:browser.version(),node:process.version,origin:new URL(partner.url()).origin},setup:'Authenticated real HTTP creates customer, building, project, issued quote and versioned PDF; all portal operations use rendered UI.',resourceIds:{tenantId,partnerUserId:recipient.userId,customerId:customer['id'],projectId:project['id'],quoteId:quote['id'],quoteShareId:quoteShare['id'],documentId:document['id'],documentShareId:documentShare['id']},assertions:{realPersonalAuthentication:true,noApiInterception:true,noPrivateRecordRequests:true,publicResponseContainsOnlySellingValues:true,explicitConsentRequired:true,acceptanceUsesIssuedDigest:true,acceptanceUsesQuoteRevision:true,feedbackUsesShareRevision:true,bothSidesSeeVersionBoundConversation:true,documentSha256MatchesReleasedVersion:true,revocationRemovesOpenDetails:true,revocationBlocksAuthenticatedDownload:true,noHorizontalOverflow:true,browserErrors:errors},pdf:{name:document['name'],size:bytes.length,expectedSha256:document['sha256'],downloadedSha256}};
    const evidencePath=testInfo.outputPath('portal-evidence.json');await writeFile(evidencePath,JSON.stringify(evidence,null,2));await testInfo.attach('portal-evidence',{path:evidencePath,contentType:'application/json'});
  }finally{await partnerContext.close();}
});
