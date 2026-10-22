import { describe, expect, it } from 'vitest';
import { ActivityReport, activityFilter, activityQuery, activitySourceCsv, validateActivityReport, verifyActivityDownload } from '../src/app/core/business-activity.models';

const tenant = '80000000-0000-4000-8000-000000000001';
const project = '80000000-0000-4000-8000-000000000002';
const filter = activityFilter('2026-10-01', '2026-10-22');
function report(): ActivityReport {
  return {schema:'firstblick.business-activity/1.0.0',tenantId:tenant,timeZone:'Europe/Berlin',capturedAt:'2026-10-22T12:00:00Z',filter,
    current:{from:'2026-10-01',to:'2026-10-22',totals:{projectsCreated:1,tasksCreated:0,tasksDue:1},months:[{from:'2026-10-01',to:'2026-10-22',counts:{projectsCreated:1,tasksCreated:0,tasksDue:1}}]},
    previous:{from:'2026-09-09',to:'2026-09-30',totals:{projectsCreated:0,tasksCreated:1,tasksDue:0},months:[{from:'2026-09-09',to:'2026-09-30',counts:{projectsCreated:0,tasksCreated:1,tasksDue:0}}]},
    sources:[{kind:'projects',id:project,projectId:project,revision:1,title:'Dach Nord',currentStatus:'Active',archived:false,createdOn:'2026-10-02',dueDate:null},
      {kind:'tasks',id:'80000000-0000-4000-8000-000000000003',projectId:project,revision:2,title:'Aufmaß',currentStatus:'Done',archived:false,createdOn:'2026-09-30',dueDate:'2026-10-15'}],digest:'a'.repeat(64)};
}
async function response(text: string, expected: ActivityReport, format='json'): Promise<Response> {
  const bytes = new TextEncoder().encode(text);
  const sha = [...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(x=>x.toString(16).padStart(2,'0')).join('');
  return new Response(text,{headers:{'Content-Type':format==='json'?'application/json':'text/csv','X-Content-Sha256':sha,'X-Report-Digest':expected.digest,'X-Report-Tenant':tenant}});
}
describe('business activity report contract',()=>{
  it('checks totals against sources without treating current status as historic completion',()=>{
    const value=report();expect(validateActivityReport(value,tenant,filter)).toBe(value);expect(value.current.totals.tasksDue).toBe(1);
  });
  it.each(['2026-02-30','not-a-date','2026-1-02'])('rejects non-calendar input %s',date=>expect(()=>activityFilter(date,'2026-10-22')).toThrow());
  it('bounds ranges and encodes the explicit project selector',()=>{
    expect(()=>activityFilter('2025-01-01','2026-10-22')).toThrow();expect(()=>activityFilter('2026-10-22','2026-10-01')).toThrow();
    expect(()=>activityFilter('2026-10-01','2026-10-22','other')).toThrow();
    expect(activityQuery(activityFilter('2026-10-01','2026-10-22',project,true))).toContain(`projectId=${project}`);
  });
  it('rejects wrong scope, mismatched filters, duplicate sources and altered totals',()=>{
    expect(()=>validateActivityReport(report(),project,filter)).toThrow();
    expect(()=>validateActivityReport(report(),tenant,{...filter,includeArchived:true})).toThrow();
    const duplicate=report();duplicate.sources.push(duplicate.sources[0]);expect(()=>validateActivityReport(duplicate,tenant,filter)).toThrow();
    const bad=report();bad.current.totals.projectsCreated=99;expect(()=>validateActivityReport(bad,tenant,filter)).toThrow();
  });
  it('requires complete nonoverlapping month coverage and actual dates',()=>{
    const missing=report();missing.previous.months=[];expect(()=>validateActivityReport(missing,tenant,filter)).toThrow();
    const overlap=report();overlap.current.months[0].to='2026-10-21';expect(()=>validateActivityReport(overlap,tenant,filter)).toThrow();
    const invalid=report();invalid.sources[1].dueDate='2026-02-30';expect(()=>validateActivityReport(invalid,tenant,filter)).toThrow();
  });
  it('validates archived inclusion and safe source identity',()=>{
    const archived=report();archived.sources[1].archived=true;expect(()=>validateActivityReport(archived,tenant,filter)).toThrow();
    const wrong=report();wrong.sources[0].projectId='80000000-0000-4000-8000-000000000009';expect(()=>validateActivityReport(wrong,tenant,filter)).toThrow();
  });
  it('verifies unchanged JSON bytes and rejects substituted sources even with a matching envelope',async()=>{
    const expected=report();const valid=await verifyActivityDownload(await response(JSON.stringify(expected),expected),expected,'json');expect(valid.type).toBe('application/json;charset=utf-8');
    const changed=report();changed.sources[0].title='Substituted';await expect(verifyActivityDownload(await response(JSON.stringify(changed),expected),expected,'json')).rejects.toThrow();
  });
  it('rejects corrupted bytes, missing metadata and wrong tenant on CSV export',async()=>{
    const expected=report();const csv=activitySourceCsv(expected);
    const valid=await response(csv,expected,'csv');expect((await verifyActivityDownload(valid,expected,'csv')).size).toBeGreaterThan(0);
    const corrupt=await response(csv,expected,'csv');corrupt.headers.set('X-Content-Sha256','0'.repeat(64));await expect(verifyActivityDownload(corrupt,expected,'csv')).rejects.toThrow('Prüfsumme');
    const foreign=await response(csv,expected,'csv');foreign.headers.set('X-Report-Tenant',project);await expect(verifyActivityDownload(foreign,expected,'csv')).rejects.toThrow();
    await expect(verifyActivityDownload(new Response(csv),expected,'csv')).rejects.toThrow();
    await expect(verifyActivityDownload(await response(csv.split('\r\n')[0]+'\r\n',expected,'csv'),expected,'csv')).rejects.toThrow();
    const changed=report();changed.timeZone='UTC';await expect(verifyActivityDownload(await response(JSON.stringify(changed),expected),expected,'json')).rejects.toThrow();
  });
});
