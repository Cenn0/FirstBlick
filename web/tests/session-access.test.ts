import {describe,expect,it} from 'vitest';
import {Session,accessWasRevoked,rememberRecoveryAccess} from '../src/app/core/session-access';

function session():Session{return {user:{id:'user-a',email:'worker@example.test',displayName:'Testperson'},activeTenantId:'tenant-a',tenants:[{id:'tenant-a',name:'Testbetrieb',role:'Worker',employeeId:'employee-a',modules:['core','field'],capabilities:['time-entries.read','time-entries.write','diary-entries.read','diary-entries.write']}]};}
function held(value:Session):Session{return {...value,tenants:value.tenants.map(tenant=>({...tenant,recoveryHold:true,capabilities:[]}))};}

describe('recovery hold and local work privacy',()=>{
  it('preserves local-work access evidence through a temporary hold and a browser restart without authorizing work',()=>{
    const before=session(),during=rememberRecoveryAccess(before,held(before));expect(accessWasRevoked(before,during)).toBe(false);expect(during.tenants[0].capabilities).toEqual([]);expect(during.tenants[0].recoveryAccess?.capabilities).toEqual(before.tenants[0].capabilities);
    const reopened=JSON.parse(JSON.stringify(during))as Session;const refreshed=rememberRecoveryAccess(reopened,held(before));expect(accessWasRevoked(reopened,refreshed)).toBe(false);expect(refreshed.tenants[0].recoveryAccess?.employeeId).toBe('employee-a');expect(accessWasRevoked(refreshed,before)).toBe(false);
  });
  it('detects rights withdrawn during a hold when the business is released again',()=>{
    const before=session(),during=rememberRecoveryAccess(before,held(before)),after=session();after.tenants[0].capabilities=['time-entries.read'];expect(accessWasRevoked(during,rememberRecoveryAccess(during,after))).toBe(true);
  });
  it('still detects removed membership, role changes and disabled modules while a hold is active',()=>{
    const before=session();expect(accessWasRevoked(before,{...held(before),tenants:[]})).toBe(true);
    const role=held(session());role.tenants[0].role='Partner';expect(accessWasRevoked(before,role)).toBe(true);
    const module=held(session());module.tenants[0].modules=['core'];expect(accessWasRevoked(before,module)).toBe(true);
  });
  it('clears old employee-linked work when the personal employee link or signed-in identity changes',()=>{
    const before=session(),changed=session();changed.tenants[0].employeeId='employee-b';expect(accessWasRevoked(before,changed)).toBe(true);changed.user.id='user-b';expect(accessWasRevoked(before,changed)).toBe(true);
  });
});
