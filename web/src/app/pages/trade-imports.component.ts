import { GaebBidExchangeComponent } from './gaeb-bid-exchange.component';
import { TradeArchiveComponent } from './trade-archive.component';
import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { ApiService } from '../core/api.service';
import { TradePreviewService } from '../core/trade-preview.service';
import { ApiError, errorMessage } from '../core/transport';
import { decimalDisplay } from '../core/decimal-format';
import { downloadBlob } from '../core/integrations.models';
import { CatalogPreview, CatalogReceipt, CatalogRequest, CatalogSelection, GaebPreview, InvoicePreview, TradeKind, TradeSource, canSelect, catalogRequest, costFields, emptySelection, readTradeSource, sameCatalogRequest, sourceBudget, validateCatalogPreview, validateCatalogReceipt, validateGaebPreview, validateInvoicePreview } from '../core/trade-import.models';

@Component({ imports: [FormsModule, RouterLink, GaebBidExchangeComponent, TradeArchiveComponent], templateUrl: './trade-imports.component.html', styleUrl: './trade-imports.component.css' })
export class TradeImportsComponent {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService); private readonly previews = inject(TradePreviewService);
  readonly canGaeb = computed(() => !!this.auth.scope() && this.auth.can('catalog-items.write'));
  readonly canInvoice = computed(() => !!this.auth.scope() && this.auth.can('supplier-invoices.read') && ['ManagingDirector', 'Office', 'Accounting'].includes(this.auth.tenant()?.role || ''));
  readonly allowed = computed(() => this.canGaeb() || this.canInvoice()); readonly kind = signal<TradeKind>('gaeb');
  readonly busy = signal(false); readonly error = signal(''); readonly notice = signal(''); readonly file = signal<File | null>(null);
  readonly gaeb = signal<GaebPreview | null>(null); readonly invoice = signal<InvoicePreview | null>(null); readonly mapping = signal<CatalogPreview | null>(null); readonly receipt = signal<CatalogReceipt | null>(null);
  readonly selections = signal<CatalogSelection[]>([]); readonly blockedCompletion = signal(false); readonly costFields = costFields; readonly canSelect = canSelect;
  page = 1; consent = false; private generation = 0; private source: TradeSource | null = null; private reviewedRequest: CatalogRequest | null = null; private importKey = '';
  constructor() {
    inject(DestroyRef).onDestroy(() => { this.generation++; this.source = null; this.file.set(null); });
    effect(() => { this.auth.accessSignature(); untracked(() => { this.reset(); if (!this.canGaeb()) this.kind.set('e-invoice'); }); });
  }
  private current(generation: number, access: string): boolean { return generation === this.generation && access === this.auth.accessSignature(); }
  private reset(): void { this.generation++; this.busy.set(false); this.error.set(''); this.notice.set(''); this.file.set(null); this.source = null; this.gaeb.set(null); this.invoice.set(null); this.mapping.set(null); this.receipt.set(null); this.selections.set([]); this.reviewedRequest = null; this.importKey = ''; this.blockedCompletion.set(false); this.consent = false; this.page = 1; }
  chooseKind(kind: TradeKind): void { if (kind === this.kind()) return; this.kind.set(kind); this.reset(); }
  chooseFile(event: Event): void { const input = event.target as HTMLInputElement, chosen = input.files?.[0]; input.value = ''; this.reset(); if (!chosen) return; try { sourceBudget(chosen.size); this.file.set(chosen); } catch (error) { this.error.set(errorMessage(error)); } }
  restoreOriginal(file: File): void {
    if (this.busy() || !this.allowed()) return;
    if (this.selections().length && !this.receipt() && !window.confirm('Ungespeicherte Katalogauswahl und Kalkulationswerte verwerfen und die gespeicherte Originaldatei öffnen?')) return;
    this.reset(); this.file.set(file); void this.inspect();
  }
  private failed(error: unknown, generation: number, access: string): void {
    if (!this.current(generation, access)) return;
    if (error instanceof ApiError && [401, 403, 423].includes(error.status)) this.reset();
    this.error.set(errorMessage(error));
  }
  async inspect(): Promise<void> {
    const file = this.file(), kind = this.kind(); if (!file || this.busy() || !(kind === 'gaeb' ? this.canGaeb() : this.canInvoice())) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set(''); this.notice.set(''); this.gaeb.set(null); this.invoice.set(null); this.mapping.set(null); this.receipt.set(null); this.selections.set([]); this.consent = false; this.source = null; this.reviewedRequest = null; this.importKey = ''; this.blockedCompletion.set(false);
    try {
      const source = await readTradeSource(file); if (!this.current(generation, access)) return;
      const response = await this.previews.preview(kind, source.blob); if (!this.current(generation, access)) return;
      if (kind === 'gaeb') this.gaeb.set(validateGaebPreview(response, source.sha256)); else this.invoice.set(validateInvoicePreview(response, source.sha256));
      this.source = source; this.page = 1;
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  display(value: string | null): string { return value === null ? 'Nicht angegeben' : decimalDisplay(value, 0, 6) ?? value; }
  date(value: string): string { return value.split('-').reverse().join('.'); }
  pages(): number { return Math.max(1, Math.ceil((this.gaeb()?.items.length || this.invoice()?.lines.length || 0) / 25)); }
  gaebRows() { return this.gaeb()?.items.slice((this.page - 1) * 25, this.page * 25) || []; }
  invoiceRows() { return this.invoice()?.lines.slice((this.page - 1) * 25, this.page * 25) || []; }
  selected(key: string): boolean { return this.selections().some(s => s.positionKey === key); }
  description(key: string): string { return this.gaeb()?.items.find(i => i.positionKey === key)?.description || key; }
  toggle(key: string, checked: boolean): void {
    if (this.busy() || this.receipt()) return;
    if (checked && this.selections().length >= 100) { this.error.set('Höchstens 100 Positionen je Übernahme auswählen.'); return; }
    if (checked && !this.selected(key)) this.selections.set([...this.selections(), emptySelection(key)]); else if (!checked) this.selections.set(this.selections().filter(s => s.positionKey !== key));
    this.mappingChanged();
  }
  mappingChanged(): void { this.generation++; this.mapping.set(null); this.reviewedRequest = null; this.consent = false; this.importKey = ''; this.blockedCompletion.set(false); this.error.set(''); this.notice.set(''); }
  async reviewMapping(): Promise<void> {
    const source = this.source, document = this.gaeb(); if (!source || !document || !this.canGaeb() || this.busy() || this.receipt()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set(''); this.mapping.set(null); this.consent = false; this.reviewedRequest = null;
    try {
      const request = catalogRequest(source, document, this.selections());
      const response = await this.api.command<unknown>('integrations/v1/gaeb/catalog-preview', request);
      if (!this.current(generation, access)) return;
      this.mapping.set(validateCatalogPreview(response, source, document, request)); this.reviewedRequest = request; this.importKey = crypto.randomUUID();
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async importCatalog(): Promise<void> {
    const source = this.source, document = this.gaeb(), preview = this.mapping(), reviewed = this.reviewedRequest;
    if (!source || !document || !preview || !reviewed || !this.consent || !this.canGaeb() || this.busy() || this.receipt() || this.blockedCompletion()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set(''); this.notice.set(''); let acceptedResponse = false;
    try {
      if (!sameCatalogRequest(reviewed, catalogRequest(source, document, this.selections()))) throw new Error('Datei oder eigene Kalkulationswerte wurden nach der Prüfung geändert. Zuordnung erneut prüfen.');
      const response = await this.api.command<unknown>('integrations/v1/gaeb/catalog-import', { ...reviewed, previewDigest: preview.digest }, undefined, this.importKey);
      acceptedResponse = true; if (!this.current(generation, access)) return;
      this.receipt.set(validateCatalogReceipt(response, preview)); this.consent = false;
      this.notice.set('Ausgewählte Leistungen sind im internen Katalog gespeichert. Es wurden keine Angebote, Aufträge oder Bestellungen erzeugt.');
    } catch (error) {
      if (this.current(generation, access) && acceptedResponse) this.blockedCompletion.set(true);
      this.failed(error, generation, access);
      if (this.current(generation, access) && !acceptedResponse && !(error instanceof ApiError)) this.notice.set('Übernahme nicht bestätigt. Eine Wiederholung dieser unveränderten Anfrage verwendet dieselbe Vorgangskennung; zunächst den Katalog prüfen.');
    } finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  exportPreview(): void { const value = this.receipt() || this.mapping() || this.gaeb() || this.invoice(); if (!value || !this.allowed() || this.busy()) return; downloadBlob(new Blob([JSON.stringify(value, null, 2)], { type: 'application/json' }), `FirstBlick-${this.kind()}-Pruefung-${value.sourceSha256.slice(0, 12)}.json`); }
}
