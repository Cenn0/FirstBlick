import {expect,Page,test} from '@playwright/test';
import {createHash} from 'node:crypto';

function publicQuote(){return {id:'quote-a',revision:7,number:'AN-2026-1',title:'Freigegebene Dachleistung',version:2,status:'Issued',validUntil:null,issuedDigest:'a'.repeat(64),currency:'EUR',presentation:{sellerName:'Dachbetrieb',buyerName:'Portalperson',buyerAddress:'Objektanschrift 1',issuedAt:'2026-10-20T08:00:00Z'},net:'100.00',tax:'19.00',gross:'119.00',lines:[{description:'Dachleistung',unit:'m²',quantity:'10.00',unitNet:'10.00',vatPercent:'19.00',net:'100.00',tax:'19.00',gross:'119.00'}]};}

async function portalFixture(page:Page,options:{staff?:boolean;document?:boolean}={}){
  let available=true;let quote=publicQuote();let accepted:unknown;let acceptanceRevision='';let feedbackRevision='';let granted:Record<string,unknown>|undefined;
  const feedback:Record<string,unknown>[]=[];const unexpected:string[]=[];const downloads:unknown[]=[];
  page.on('download',download=>downloads.push(download));
  const share={id:'share-a',revision:2,title:options.document?'Freigegebene Projektdatei':'Ihr Dachangebot',sourceKind:options.document?'documents':'quotes',sourceId:options.document?'document-a':'quote-a',projectId:'project-a',sourceDigest:quote.issuedDigest,allowAcceptance:!options.document,expiresAt:new Date(Date.now()+86400000).toISOString(),recipientUserId:'partner-a',revokedAt:null};
  await page.route('**/api/v1/**',route=>{
    const request=route.request(),url=new URL(request.url()),path=url.pathname.slice('/api/v1/'.length);
    if(path==='auth/me')return route.fulfill({json:{user:{id:options.staff?'operator-a':'partner-a',email:options.staff?'office@example.test':'partner@example.test',displayName:options.staff?'Alex Büro':'Alex Portal'},tenants:[{id:'tenant-a',name:'Testbetrieb',role:options.staff?'ManagingDirector':'Partner',modules:['core','commercial','portal'],capabilities:options.staff?['portal-shares.read','portal-shares.write','portal-feedback.read','quotes.read']:['portal-access.read','portal-access.write']}],activeTenantId:'tenant-a'}});
    if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=test-token; Path=/; SameSite=Strict'}});
    if(path==='portal/recipients')return route.fulfill({json:[{userId:'partner-a',displayName:'Alex Portal',email:'partner@example.test'}]});
    if(path==='portal/shares'||path==='portal-shares')return route.fulfill({json:{items:available?[share]:[],total:available?1:0,page:1,pageSize:25}});
    if(path==='portal-shares/share-a')return route.fulfill({json:share});
    if(path==='portal/shares/share-a')return available?route.fulfill({json:{share,quote:options.document?null:quote,document:options.document?{id:'document-a',name:'unterlage.pdf',contentType:'application/pdf',size:8,sha256:createHash('sha256').update('expected document bytes').digest('hex')}:null,feedback:{items:feedback,total:feedback.length,page:1,pageSize:50}}}):route.fulfill({status:404,json:{status:404,code:'not_found'}});
    if(path==='portal/shares/share-a/accept'){accepted=request.postDataJSON();acceptanceRevision=request.headers()['if-match'];quote={...quote,status:'Accepted',revision:8};return route.fulfill({json:{shareId:share.id,quote}});}
    if(path==='portal/shares/share-a/feedback'&&request.method()==='POST'){feedbackRevision=request.headers()['if-match'];const entry={id:'feedback-a',...request.postDataJSON(),fromBusiness:false,createdAt:'2026-10-20T08:30:00Z',sourceDigest:share.sourceDigest};feedback.push(entry);return route.fulfill({json:entry});}
    if(path==='portal/shares/share-a/feedback'||path==='portal/grants/share-a/feedback')return route.fulfill({json:{items:feedback,total:feedback.length,page:1,pageSize:Number(url.searchParams.get('pageSize')||30)}});
    if(path==='portal/shares/share-a/download')return route.fulfill({contentType:'application/pdf',body:Buffer.from('%PDF-BAD')});
    if(path==='portal/grants'){granted=request.postDataJSON()as Record<string,unknown>;return route.fulfill({json:{...share,...granted,id:'share-a'}});}
    if(options.staff&&path==='quotes')return route.fulfill({json:{items:[quote],total:1,page:1,pageSize:100}});
    if(options.staff&&path==='quotes/quote-a')return route.fulfill({json:{...quote,snapshot:{net:quote.net,tax:quote.tax,gross:quote.gross,lines:quote.lines.map(line=>({inputs:{description:line.description,unit:line.unit,quantity:line.quantity,vatPercent:line.vatPercent,unitMaterialCost:'INTERNAL-COST',unitLaborHours:'INTERNAL-HOURS'},unitNet:line.unitNet,net:line.net,tax:line.tax,gross:line.gross}))}}});
    unexpected.push(path);return route.fulfill({status:403,json:{status:403,code:'forbidden'}});
  });
  return {revoke:()=>{available=false;},accepted:()=>accepted,acceptanceRevision:()=>acceptanceRevision,feedbackRevision:()=>feedbackRevision,granted:()=>granted,unexpected:()=>unexpected,downloads:()=>downloads};
}

test('partner sees only released selling values and acceptance uses quote revision while feedback uses share revision',async({page})=>{
  const evidence=await portalFixture(page);await page.goto('/quotes');await expect(page).toHaveURL(/\/portal$/);
  await expect(page.getByRole('link',{name:'Bankumsätze',exact:true})).toHaveCount(0);await expect(page.getByRole('link',{name:'Angebote',exact:true})).toHaveCount(0);await expect(page.getByRole('link',{name:'Synchronisierung',exact:true})).toHaveCount(0);
  await page.getByRole('button',{name:'Ihr Dachangebot',exact:true}).click();await expect(page.getByRole('heading',{name:'Freigegebene Dachleistung',exact:true})).toBeVisible();await expect(page.locator('.portal-detail')).not.toContainText('INTERNAL');
  await page.getByRole('button',{name:'Angebot annehmen',exact:true}).click();const dialog=page.getByRole('dialog');await expect(dialog.getByRole('button',{name:'Verbindlich annehmen',exact:true})).toBeDisabled();await dialog.getByLabel('Ich habe diese Angebotsversion',{exact:false}).check();await dialog.getByRole('button',{name:'Verbindlich annehmen',exact:true}).click();await expect(dialog).not.toBeVisible();
  expect(evidence.accepted()).toEqual({digest:'a'.repeat(64)});expect(evidence.acceptanceRevision()).toBe('"7"');await expect(page.getByText('Diese Angebotsversion wurde verbindlich angenommen.')).toBeVisible();
  await page.getByLabel('Ihre Rückfrage oder Rückmeldung').fill('Synthetische Rückfrage zur freigegebenen Dachfläche');await page.getByRole('button',{name:'Rückmeldung speichern'}).click();await expect(page.locator('.portal-message')).toContainText('Synthetische Rückfrage');expect(evidence.feedbackRevision()).toBe('"2"');expect(evidence.unexpected()).toEqual([]);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});

test('refresh revalidates an open share and removes revoked details',async({page})=>{
  const evidence=await portalFixture(page);await page.goto('/portal');await page.getByRole('button',{name:'Ihr Dachangebot',exact:true}).click();await expect(page.getByRole('heading',{name:'Freigegebene Dachleistung'})).toBeVisible();evidence.revoke();await page.getByRole('button',{name:'Aktualisieren',exact:true}).click();
  await expect(page.getByText('Diese Freigabe ist nicht mehr verfügbar.',{exact:false})).toBeVisible();await expect(page.locator('.portal-detail')).toHaveCount(0);await expect(page.getByRole('button',{name:'Angebot annehmen',exact:true})).toHaveCount(0);
});

test('internal grant names the recipient and exact issued document before enabling acceptance',async({page})=>{
  const evidence=await portalFixture(page,{staff:true});await page.goto('/portal?new=1&kind=quotes&sourceId=quote-a');const dialog=page.getByRole('dialog');await expect(dialog).toBeVisible();await expect(dialog.getByRole('heading',{name:'AN-2026-1 · Freigegebene Dachleistung'})).toBeVisible();
  await dialog.getByLabel('Persönlich angemeldete Person').selectOption('partner-a');await expect(dialog.getByLabel('Diese Person darf genau diese Angebotsversion',{exact:false})).not.toBeChecked();await dialog.getByLabel('Diese Person darf genau diese Angebotsversion',{exact:false}).check();await dialog.getByRole('button',{name:'Zugriff freigeben'}).click();await expect(dialog).not.toBeVisible();
  expect(evidence.granted()).toMatchObject({recipientUserId:'partner-a',sourceKind:'quotes',sourceId:'quote-a',title:'Freigegebene Dachleistung',allowAcceptance:true});expect(Date.parse(String(evidence.granted()?.['expiresAt']))).toBeGreaterThan(Date.now());expect(evidence.unexpected()).toEqual([]);
});

test('a portal download with a mismatching content hash is discarded',async({page})=>{
  const evidence=await portalFixture(page,{document:true});await page.goto('/portal');await page.getByRole('button',{name:'Freigegebene Projektdatei',exact:true}).click();await page.getByRole('button',{name:'Datei herunterladen'}).click();await expect(page.getByText('Die heruntergeladene Datei stimmt nicht mit der freigegebenen Version überein.',{exact:false})).toBeVisible();expect(evidence.downloads()).toHaveLength(0);
});
