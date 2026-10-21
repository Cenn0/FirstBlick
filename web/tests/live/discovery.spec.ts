import {expect,Page,Response,test} from '@playwright/test';
import {createHash,randomUUID} from 'node:crypto';
import {readFile,writeFile} from 'node:fs/promises';
import {inflateRawSync} from 'node:zlib';

function responseFor(page:Page,path:string,method='GET'):Promise<Response>{
  return page.waitForResponse(response=>new URL(response.url()).pathname==='/api/v1/'+path&&response.request().method()===method);
}
async function command(page:Page,tenantId:string,path:string,data:unknown):Promise<Record<string,any>>{
  const token=(await page.context().cookies(page.url())).find(cookie=>cookie.name==='XSRF-TOKEN')?.value;
  if(!token)throw new Error('A real sign-in must establish the CSRF cookie before synthetic setup.');
  const response=await page.request.post('/api/v1/'+path,{headers:{'X-Tenant-Id':tenantId,'X-XSRF-TOKEN':decodeURIComponent(token),'Idempotency-Key':randomUUID()},data});
  const value=await response.json();expect(response.ok(),`${path}: ${response.status()} ${JSON.stringify(value)}`).toBe(true);return value;
}
async function searched(page:Page,query:string):Promise<Record<string,any>>{
  await page.getByLabel('Suchbegriff',{exact:true}).fill(query);const pending=responseFor(page,'search');
  await page.getByRole('button',{name:'Suchen',exact:true}).click();const response=await pending;
  expect(response.ok()).toBe(true);expect(response.headers()['cache-control']).toContain('no-store');
  expect(response.request().headers()['ngsw-bypass']).toBe('1');return response.json();
}

// Independent test reader for the server's four small ordinary ZIP entries. Read sizes
// from the central directory, because streamed ZIP local headers may use data descriptors.
function copiedFiles(bytes:Buffer):Map<string,Buffer>{
  const end=bytes.length-22;expect(bytes.readUInt32LE(end)).toBe(0x06054b50);
  expect(bytes.readUInt16LE(end+4)).toBe(0);expect(bytes.readUInt16LE(end+6)).toBe(0);
  expect(bytes.readUInt16LE(end+8)).toBe(4);expect(bytes.readUInt16LE(end+10)).toBe(4);
  expect(bytes.readUInt16LE(end+20)).toBe(0);
  let cursor=bytes.readUInt32LE(end+16);const files=new Map<string,Buffer>();
  for(let index=0;index<4;index++){
    expect(bytes.readUInt32LE(cursor)).toBe(0x02014b50);
    const method=bytes.readUInt16LE(cursor+10),compressed=bytes.readUInt32LE(cursor+20),size=bytes.readUInt32LE(cursor+24);
    const nameLength=bytes.readUInt16LE(cursor+28),extraLength=bytes.readUInt16LE(cursor+30),commentLength=bytes.readUInt16LE(cursor+32),offset=bytes.readUInt32LE(cursor+42);
    const name=bytes.subarray(cursor+46,cursor+46+nameLength).toString('utf8');expect(files.has(name)).toBe(false);
    expect(size).toBeLessThanOrEqual(12*1024*1024);expect(bytes.readUInt32LE(offset)).toBe(0x04034b50);
    const start=offset+30+bytes.readUInt16LE(offset+26)+bytes.readUInt16LE(offset+28);const body=bytes.subarray(start,start+compressed);
    expect([0,8]).toContain(method);const content=method===8?inflateRawSync(body,{maxOutputLength:12*1024*1024}):body;
    expect(content.length).toBe(size);files.set(name,content);cursor+=46+nameLength+extraLength+commentLength;
  }
  expect([...files.keys()].sort()).toEqual(['manifest.json','membership.json','profile.json','records.json']);return files;
}

test('real scoped search, safe current project history and verified own-account ZIP copy',async({page,browser},testInfo)=>{
  const email=process.env['FIRSTBLICK_LIVE_EMAIL'],password=process.env['FIRSTBLICK_LIVE_PASSWORD'];
  if(!email||!password)throw new Error('Set FIRSTBLICK_LIVE_EMAIL/PASSWORD to a synthetic operator in the local development business. This test creates real synthetic records.');
  const suffix=randomUUID().slice(0,8),errors:string[]=[];page.on('pageerror',error=>errors.push(error.message));
  await page.goto('/login');await page.getByLabel('E-Mail-Adresse').fill(email);await page.getByLabel('Passwort',{exact:true}).fill(password);
  const login=responseFor(page,'auth/login','POST');await page.getByRole('button',{name:'Anmelden',exact:false}).click();expect((await login).ok()).toBe(true);await expect(page).toHaveURL(/\/overview$/);
  const session=await(await page.request.get('/api/v1/auth/me')).json();const tenantId=session.activeTenantId,userId=session.user.id;expect(tenantId).toBeTruthy();expect(userId).toBeTruthy();

  // These are actual authenticated setup commands. All discovery/history/copy operations below
  // use rendered Angular controls and real responses; this test installs no API interception.
  const privateMarker=`VertraulicheNotiz-${suffix}`,projectTitle=`Suchprobe Dachsanierung ${suffix}`;
  const customer=await command(page,tenantId,'customers',{name:`Suchprobe Kunde ${suffix}`,address:'Synthetischer Suchweg 1, 12345 Teststadt',notes:privateMarker});
  const building=await command(page,tenantId,'buildings',{name:`Suchprobe Objekt ${suffix}`,customerId:customer['id'],address:'Synthetischer Suchweg 1, 12345 Teststadt'});
  const project=await command(page,tenantId,'projects',{name:projectTitle,customerId:customer['id'],buildingId:building['id'],type:'Renovation',status:'Active',description:privateMarker});
  const task=await command(page,tenantId,'tasks',{projectId:project['id'],title:`Suchprobe Aufgabe ${suffix}`,description:privateMarker,status:'Open'});
  const preferencesResponse=await page.request.get('/api/v1/notification-preferences?page=1&pageSize=100',{headers:{'X-Tenant-Id':tenantId}});
  expect(preferencesResponse.ok()).toBe(true);const existing=(await preferencesResponse.json()).items as Record<string,any>[];
  const preference=existing.find(item=>item['userId']===userId)||await command(page,tenantId,'notification-preferences',{userId,morningBriefing:false,localTime:'07:00:00',timeZone:'Europe/Berlin',channels:[],days:['Monday'],includeYesterday:false,includeToday:false,includeMaintenance:false});

  await page.goto('/discovery');await expect(page.getByRole('heading',{level:1,name:'Suche',exact:true})).toBeVisible();
  const excluded=await searched(page,privateMarker);expect(excluded['total']).toBe(0);expect(excluded['items']).toEqual([]);
  await expect(page.getByRole('heading',{name:'Keine zugänglichen Treffer auf dieser Seite.'})).toBeVisible();
  await page.getByLabel('Bereich',{exact:true}).selectOption('projects');const found=await searched(page,projectTitle);
  expect(found['schema']).toBe('firstblick.search/1.0.0');expect(found['page']).toBe(1);expect(found['pageSize']).toBe(25);expect(found['total']).toBe(1);
  expect(found['items'].map((item:{id:string})=>item.id)).toEqual([project['id']]);expect(JSON.stringify(found)).not.toContain(privateMarker);
  await expect(page.getByRole('link',{name:projectTitle,exact:true})).toBeVisible();expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await page.screenshot({path:testInfo.outputPath('discovery-search.png'),fullPage:true});

  const historyPending=responseFor(page,`projects/${project['id']}/history`);await page.getByRole('link',{name:'Projektverlauf →',exact:true}).click();
  const historyResponse=await historyPending;expect(historyResponse.ok()).toBe(true);const history=await historyResponse.json();
  expect(historyResponse.headers()['cache-control']).toContain('no-store');expect(history.associationScope).toBe('current-accessible-project-links');
  expect(history.projectId).toBe(project['id']);expect(history.items.map((item:{entityId:string})=>item.entityId)).toEqual(expect.arrayContaining([project['id'],task['id']]));
  expect(history.items.every((item:{performedByCurrentUser:boolean})=>item.performedByCurrentUser)).toBe(true);
  expect(JSON.stringify(history)).not.toContain(privateMarker);expect(JSON.stringify(history)).not.toContain(customer['name']);
  for(const entry of history.items)for(const key of ['actorId','correlationId','data','amount','employeeId'])expect(entry).not.toHaveProperty(key);
  await expect(page.getByRole('heading',{level:1,name:'Projektverlauf',exact:true})).toBeVisible();await expect(page.getByText('Aufgabe erstellt',{exact:true})).toBeVisible();
  await expect(page.getByText('Projekt erstellt',{exact:true})).toBeVisible();await expect(page.getByText('Der Verlauf folgt den heutigen Projektzuordnungen.',{exact:false})).toBeVisible();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);await page.screenshot({path:testInfo.outputPath('discovery-history.png'),fullPage:true});

  await page.goto('/settings');const widget=page.locator('app-self-copy');await expect(widget.getByRole('heading',{name:'Meine Datenkopie',exact:true})).toBeVisible();await expect(widget.locator('input,select')).toHaveCount(0);
  const copyPending=responseFor(page,'self/data-copy'),downloadPending=page.waitForEvent('download');
  await widget.getByRole('button',{name:'Eigene Daten als ZIP herunterladen',exact:true}).click();const copyResponse=await copyPending;
  expect(copyResponse.ok()).toBe(true);expect(copyResponse.request().url()).not.toContain('?');expect(copyResponse.headers()['cache-control']).toContain('no-store');
  expect(copyResponse.headers()['x-firstblick-export-schema']).toBe('firstblick.self-data-copy/1.0.0');
  const download=await downloadPending,bytes=await readFile((await download.path())!);const actualSha256=createHash('sha256').update(bytes).digest('hex');
  expect(actualSha256).toBe(copyResponse.headers()['x-content-sha256']);
  const files=copiedFiles(bytes),profile=JSON.parse(files.get('profile.json')!.toString()),membership=JSON.parse(files.get('membership.json')!.toString());
  const records=JSON.parse(files.get('records.json')!.toString()),manifest=JSON.parse(files.get('manifest.json')!.toString());
  expect(Object.keys(profile).sort()).toEqual(['displayName','email','emailConfirmed','id','phoneNumber','phoneNumberConfirmed']);expect(profile.id).toBe(userId);expect(profile.email).toBe(email);
  expect(membership.tenantId).toBe(tenantId);expect(membership.userId).toBe(userId);expect(manifest.schema).toBe('firstblick.self-data-copy/1.0.0');expect(manifest.userId).toBe(userId);expect(manifest.tenantId).toBe(tenantId);
  expect(manifest.purpose).toBe('self-service-data-copy');expect(manifest.files).toHaveLength(3);
  for(const item of manifest.files){const content=files.get(item.name);expect(content).toBeDefined();expect(content!.length).toBe(item.bytes);expect(createHash('sha256').update(content!).digest('hex')).toBe(item.sha256);}
  expect(records.some((item:{kind:string;id:string;data:{userId:string}})=>item.kind==='notification-preferences'&&item.id===preference['id']&&item.data.userId===userId)).toBe(true);
  for(const record of records){expect(['time-entries','diary-entries','form-submissions','notification-preferences']).toContain(record.kind);expect(record.data.tenantId).toBe(tenantId);if(record.kind==='notification-preferences')expect(record.data.userId).toBe(userId);if(record.kind==='form-submissions')expect(record.data.submittedBy).toBe(userId);}
  expect(manifest.coverage.reduce((total:number,category:{records:number})=>total+category.records,0)).toBe(records.length);
  expect(JSON.stringify(records)).not.toContain(privateMarker);expect(manifest.exclusions).toContain('credentials-and-recovery-secrets');
  await expect(widget.getByRole('status')).toContainText('geprüft und zum Download bereitgestellt');expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await page.screenshot({path:testInfo.outputPath('discovery-own-copy.png'),fullPage:true});expect(errors).toEqual([]);

  const evidence={synthetic:true,completedAt:new Date().toISOString(),project:testInfo.project.name,viewport:page.viewportSize(),runtime:{browser:browser.version(),node:process.version,origin:new URL(page.url()).origin},
    setup:'Authenticated real HTTP creates synthetic customer, building, project, task and, if absent, an own inactive notification preference. Discovery, history and own ZIP copy use rendered Angular controls without API interception.',
    resourceIds:{tenantId,userId,customerId:customer['id'],projectId:project['id'],taskId:task['id'],notificationPreferenceId:preference['id']},
    assertions:{realPersonalAuthentication:true,noApiInterception:true,privateNotesNotSearchable:true,explicitSearchPaging:true,exactCurrentProjectResult:true,currentAssociationExplanationVisible:true,genericHistoryWithoutPrivatePersonnelFields:true,ownEventsIdentified:true,noSubjectSelector:true,downloadSha256MatchesResponseDigest:true,allManifestEntryHashesVerified:true,profileContainsOnlyOwnSafeFields:true,ownNotificationPreferenceIncluded:true,onlyDeclaredCurrentTenantCategories:true,noHorizontalOverflow:true,browserErrors:errors},
    dataCopy:{schema:manifest.schema,captureId:manifest.captureId,records:records.length,files:[...files.keys()],bytes:bytes.length,sha256:actualSha256}};
  const evidencePath=testInfo.outputPath('discovery-evidence.json');await writeFile(evidencePath,JSON.stringify(evidence,null,2));await testInfo.attach('discovery-evidence',{path:evidencePath,contentType:'application/json'});
});
