import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { ApiService } from '../core/api.service';
import { ApiError, errorMessage } from '../core/transport';
import { downloadBlob } from '../core/integrations.models';
import { TradeKind, readTradeSource } from '../core/trade-import.models';
import { OriginalDetail, OriginalPage, OriginalReceipt, OriginalSource, originalDetail, originalPage, originalReceipt, verifiedOriginal } from '../core/trade-source.models';

@Component({ selector: 'app-trade-archive', imports: [RouterLink], templateUrl: './trade-archive.component.html', styleUrl: './trade-archive.component.css' })
export class TradeArchiveComponent {
  readonly kind = input.required<TradeKind>(); readonly file = input<File | null>(null);
  readonly previewSha = input(''); readonly locked = input(false); readonly restoreFile = output<File>();
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  readonly allowed = computed(() => !!this.auth.scope() && this.auth.can(this.kind() === 'gaeb' ? 'catalog-items.read' : 'supplier-invoices.read'));
  readonly canCapture = computed(() => !!this.auth.scope() && this.auth.can(this.kind() === 'gaeb' ? 'catalog-items.write' : 'supplier-invoices.write'));
  readonly opened = signal(false); readonly busy = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly listing = signal<OriginalPage | null>(null); readonly detail = signal<OriginalDetail | null>(null); readonly captured = signal<OriginalReceipt | null>(null);
  private generation = 0; private saveKey = ''; private destroyed = false;
  constructor() {
    effect(() => { this.auth.accessSignature(); this.kind(); this.file(); this.previewSha(); untracked(() => this.reset()); });
    inject(DestroyRef).onDestroy(() => { this.destroyed = true; this.reset(); });
  }
  private reset(): void {
    this.generation++; this.busy.set(false); this.opened.set(false); this.error.set(''); this.notice.set('');
    this.listing.set(null); this.detail.set(null); this.captured.set(null); this.saveKey = '';
  }
  private current(generation: number, access: string): boolean { return !this.destroyed && generation === this.generation && access === this.auth.accessSignature(); }
  private failure(error: unknown, generation: number, access: string): void {
    if (!this.current(generation, access)) return;
    if (error instanceof ApiError && [401, 403, 423].includes(error.status)) this.reset();
    this.error.set(errorMessage(error));
  }
  private path(kind = this.kind()): string { return `integrations/v1/trade-sources/${kind}`; }
  async capture(): Promise<void> {
    const file = this.file(), sha = this.previewSha(); if (!file || !sha || !this.canCapture() || this.busy() || this.locked()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), tenant = this.auth.activeTenantId(), kind = this.kind();
    this.busy.set(true); this.error.set(''); this.notice.set(''); this.captured.set(null); this.saveKey ||= crypto.randomUUID();
    try {
      const selected = await readTradeSource(file); if (!this.current(generation, access)) return;
      if (selected.sha256 !== sha) throw new Error('Die gewählte Datei passt nicht mehr zur Vorschau. Datei erneut prüfen.');
      const result = await this.api.command<unknown>(this.path(kind), { fileName: file.name, sourceBase64: selected.base64, expectedSha256: sha }, undefined, this.saveKey);
      if (!this.current(generation, access)) return;
      const receipt = originalReceipt(result, tenant, kind, sha, file.size); this.captured.set(receipt);
      this.notice.set(receipt.alreadyStored ? 'Diese Originalbytes sind bereits abgelegt. Die bestehende Akte wurde geprüft; keine zweite Datei angelegt.' : 'Originaldatei unverändert abgelegt und zurückgelesen. Es wurde keine Rechnung gebucht und keine Katalogposition erzeugt.');
      this.opened.set(true); this.listing.set(null); this.detail.set(null);
    } catch (error) {
      this.failure(error, generation, access);
      if (this.current(generation, access) && !(error instanceof ApiError)) this.notice.set('Ablage nicht bestätigt. Die unveränderte Wiederholung verwendet dieselbe Vorgangskennung; Originalakten vor einer erneuten Übernahme prüfen.');
    } finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async load(page = 1): Promise<void> {
    if (!this.allowed() || this.busy() || this.locked()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), tenant = this.auth.activeTenantId(), kind = this.kind();
    this.opened.set(true); this.busy.set(true); this.error.set(''); this.detail.set(null); this.listing.set(null);
    try {
      const result = await this.api.get<unknown>(`${this.path(kind)}?page=${page}&pageSize=20`);
      if (this.current(generation, access)) this.listing.set(originalPage(result, tenant, kind, page, 20));
    } catch (error) { this.failure(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async inspect(source: OriginalSource): Promise<void> {
    if (!this.allowed() || this.busy() || this.locked() || source.tenantId !== this.auth.activeTenantId() || source.format !== this.kind()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set(''); this.detail.set(null);
    try {
      const value = await this.api.get<unknown>(`${this.path()}/${source.id}`);
      if (this.current(generation, access)) this.detail.set(originalDetail(value, source));
    } catch (error) { this.failure(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async original(restore: boolean): Promise<void> {
    const detail = this.detail(); if (!detail || !this.allowed() || this.busy() || this.locked()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set('');
    try {
      const response = await this.api.downloadResponse(`${this.path()}/${detail.source.id}/original`);
      if (!this.current(generation, access)) { await response.body?.cancel(); return; }
      const blob = await verifiedOriginal(response, detail.source); if (!this.current(generation, access)) return;
      if (restore) this.restoreFile.emit(new File([blob], detail.source.fileName, { type: 'application/xml' }));
      else downloadBlob(blob, detail.source.fileName);
    } catch (error) { this.failure(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  summary(): string {
    const preview = this.detail()?.preview;
    return !preview ? '' : 'invoiceNumber' in preview ? `E-Rechnung ${preview.invoiceNumber} · ${preview.sellerName}` : `${preview.scheduleName || 'Leistungsverzeichnis'} · ${preview.exchangePhase}`;
  }
}
