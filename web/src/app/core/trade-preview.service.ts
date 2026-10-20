import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { ApiError, recoveryHoldMessage, request } from './transport';
import { TradeKind } from './trade-import.models';

@Injectable({ providedIn: 'root' })
export class TradePreviewService {
  private readonly auth = inject(AuthService);
  async preview(kind: TradeKind, body: Blob): Promise<unknown> {
    if (this.auth.recoveryHold()) throw new Error(recoveryHoldMessage);
    const tenant = this.auth.activeTenantId();
    try { return await request<unknown>(`/api/v1/integrations/v1/${kind}/preview`, { method: 'POST', headers: { 'Content-Type': 'application/xml;charset=utf-8' }, body }, tenant); }
    catch (error) { if (error instanceof ApiError && error.status === 401) this.auth.expire(); if (error instanceof ApiError && error.status === 423 && error.problem.code === 'recovery_hold') this.auth.markRecoveryHold(); throw error; }
  }
}
