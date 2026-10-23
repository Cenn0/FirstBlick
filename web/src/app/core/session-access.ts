export interface AccessBaseline {role:string;capabilities:string[];modules:string[];employeeId?:string}
export interface TenantAccess extends AccessBaseline {id:string;name:string;recoveryHold?:boolean;recoveryAccess?:AccessBaseline}
export interface Session {user:{id:string;email:string;displayName:string};tenants:TenantAccess[];activeTenantId?:string;canCreateBusiness?:boolean}

/** A temporary recovery hold cannot authorize work. Its prior access is retained only to detect a later revocation. */
export function rememberRecoveryAccess(previous:Session|null,next:Session):Session{
  return {...next,tenants:next.tenants.map(tenant=>{
    const before=previous?.user.id===next.user.id?previous.tenants.find(item=>item.id===tenant.id):undefined;
    const baseline=before?.recoveryAccess||(before?{role:before.role,capabilities:[...before.capabilities],modules:[...before.modules],employeeId:before.employeeId}:undefined);
    return {...tenant,recoveryAccess:tenant.recoveryHold?baseline:undefined};
  })};
}

export function accessWasRevoked(previous:Session|null,next:Session):boolean{
  if(!previous)return false;
  if(previous.user.id!==next.user.id)return true;
  return previous.tenants.some(tenant=>{
    const current=next.tenants.find(item=>item.id===tenant.id);if(!current)return true;
    const before=tenant.recoveryAccess||tenant;
    return before.role!==current.role||before.employeeId!==current.employeeId||before.modules.some(module=>!current.modules.includes(module))||(!current.recoveryHold&&before.capabilities.some(capability=>!current.capabilities.includes(capability)));
  });
}
