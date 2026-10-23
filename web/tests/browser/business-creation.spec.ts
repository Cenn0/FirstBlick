import { expect, Page, test } from '@playwright/test';

const createdId='22222222-2222-4222-8222-222222222222';

async function fixture(page:Page, role='ManagingDirector', operator=role==='ManagingDirector'){
  let created=false;
  let submitted:unknown;
  let headers:Record<string,string>|undefined;
  await page.route('**/api/v1/**',async route=>{
    const request=route.request(),path=new URL(request.url()).pathname.slice('/api/v1/'.length);
    if(path==='auth/me')return route.fulfill({json:{user:{id:'synthetic-person',email:'synthetic@example.test',displayName:'Synthetic person'},activeTenantId:'tenant-a',canCreateBusiness:operator,tenants:[
      {id:'tenant-a',name:'Existing business',role,modules:['core','maintenance'],capabilities:role==='ManagingDirector'?['*']:['projects.read']},
      ...(created?[{id:createdId,name:'New synthetic business',role:'ManagingDirector',modules:['core','maintenance'],capabilities:['*']}]:[])]}});
    if(path==='auth/csrf')return route.fulfill({json:{ready:true},headers:{'Set-Cookie':'XSRF-TOKEN=synthetic-token; Path=/; SameSite=Strict'}});
    if(path==='businesses'&&request.method()==='POST'){
      submitted=request.postDataJSON();headers=request.headers();created=true;
      return route.fulfill({json:{id:createdId,name:'New synthetic business',timeZone:'Europe/Berlin',modules:['core','maintenance']}});
    }
    if(path==='dashboard')return route.fulfill({json:{projects:0,quoteDrafts:0,openInvoices:0,tasksDue:0}});
    return route.fulfill({json:{items:[],total:0,page:1,pageSize:25}});
  });
  return {submitted:()=>submitted,headers:()=>headers};
}

test('director creates a separate business and switches only after server confirmation',async({page})=>{
  const evidence=await fixture(page);
  await page.goto('/settings');
  await page.getByRole('link',{name:'Weiteren Betrieb anlegen'}).click();
  await page.getByLabel('Name des Betriebs').fill('New synthetic business');
  await page.getByRole('button',{name:'Betrieb anlegen'}).click();
  await expect(page).toHaveURL(/\/overview$/);
  await expect(page.locator('#active-tenant')).toHaveValue(createdId);
  expect(evidence.submitted()).toEqual({name:'New synthetic business',timeZone:'Europe/Berlin'});
  expect(evidence.headers()?.['x-tenant-id']).toBe('tenant-a');
  expect(evidence.headers()?.['x-xsrf-token']).toBe('synthetic-token');
  expect(evidence.headers()?.['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/i);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});

test('a non-director cannot use the business creation form',async({page})=>{
  await fixture(page,'Office');
  await page.goto('/businesses/new');
  await expect(page.getByLabel('Name des Betriebs')).toHaveCount(0);
  await expect(page.getByText('Für die Anlage benötigen Sie',{exact:false})).toBeVisible();
});

test('a managing director without platform permission cannot see or use business creation',async({page})=>{
  await fixture(page,'ManagingDirector',false);
  await page.goto('/settings');
  await expect(page.getByRole('link',{name:'Weiteren Betrieb anlegen'})).toHaveCount(0);
  await page.goto('/businesses/new');
  await expect(page.getByLabel('Name des Betriebs')).toHaveCount(0);
});

test('an unbound success receipt blocks another creation attempt',async({page})=>{
  await fixture(page);
  let writes=0;
  await page.route('**/api/v1/businesses',route=>{writes++;return route.fulfill({json:{id:'invalid',name:'New synthetic business',timeZone:'Europe/Berlin',modules:['core']}});});
  await page.goto('/businesses/new');
  await page.getByLabel('Name des Betriebs').fill('New synthetic business');
  await page.getByRole('button',{name:'Betrieb anlegen'}).click();
  await expect(page.getByRole('alert')).toContainText('Anlagebeleg ist unvollständig');
  await expect(page.getByRole('button',{name:'Betrieb anlegen'})).toBeDisabled();
  expect(writes).toBe(1);
});
