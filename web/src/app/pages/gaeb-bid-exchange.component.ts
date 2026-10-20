import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';
import { downloadBlob, sha256Blob } from '../core/integrations.models';
import { TradePreviewService } from '../core/trade-preview.service';
import { GaebPreview, TradeSource, readTradeSource, sourceBudget, validateGaebPreview } from '../core/trade-import.models';

interface BidPrice { positionKey: string; expectedItemDigest: string; unitPrice: string; notOffered: boolean; priceEvidence: string }
interface GaebRule { sourceSha256: string; phase: string; schemaEdition: string; schemaBundleSha256: string; valid: boolean; issues: { code: string; severity: string; message: string }[] }
interface GaebInspection { validation: GaebRule; readyForBid: boolean; originalBytesRetained: boolean }
interface BidPreview {
  contract: string; tenantId: string; projectId: string; projectRevision: number;
  sourceSha256: string; originalSchema: GaebRule; exportSchema: GaebRule;
  sourcePhase: string; outputPhase: string; xmlSha256: string;
  totalNet: string; mappingDigest: string; digest: string; canExport: boolean;
  lines: { positionKey: string; originalItemId: string; quantity: string; unitPrice: string | null; lineNet: string; notOffered: boolean }[];
  phaseExcludedSourceFields: string[]; deliveryPerformed: false; independentlyAcceptedByRecipient: false
}
interface ExportDocument { id: string; name: string; size: number; sha256: string; contentType: string; sourceKind: string; sourceId: string; projectId: string }
interface BidReceipt { id: string; preview: BidPreview; documents: ExportDocument[]; externallySubmitted: false; recipientAcceptanceConfirmed: false }
const digest = (v: unknown): v is string => typeof v === 'string' && /^[a-f0-9]{64}$/.test(v);
const validName = (value: string, name: string, limit = 240): string => {
  if (!value.trim() || value.length > limit || /[\u0000-\u001f\u007f-\u009f]/u.test(value))
    throw new Error(`${name} eingeben (maximal ${limit} Zeichen).`);
  return value.trim();
};
const priceString = (price: string): string => {
  const value = price.trim().replace(',', '.');
  if (!/^(?:0|[1-9]\d{0,8})(?:\.\d{1,6})?$/.test(value))
    throw new Error('Für jedes angebotene Element einen expliziten Einzelpreis in EUR (maximal sechs Nachkommastellen) angeben.');
  return value;
};
const validIsoDate = (date: string): boolean => {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return false;
  const parsed = Date.parse(date + 'T00:00:00Z');
  return Number.isFinite(parsed) && new Date(parsed).toISOString().slice(0, 10) === date;
};

@Component({
  selector: 'app-gaeb-bid-exchange',
  imports: [FormsModule],
  templateUrl: './gaeb-bid-exchange.component.html',
  styleUrl: './trade-imports.component.css'
})
export class GaebBidExchangeComponent {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService);
  private readonly trade = inject(TradePreviewService);
  readonly allowed = computed(() => !!this.auth.scope() && this.auth.can('catalog-items.write'));
  readonly busy = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly file = signal<File | null>(null);
  readonly structural = signal<GaebPreview | null>(null);
  readonly official = signal<GaebInspection | null>(null);
  readonly preview = signal<BidPreview | null>(null);
  readonly receipt = signal<BidReceipt | null>(null);
  readonly projects = signal<Entity[]>([]);
  readonly projectCount = signal(0); page = 1; projectSearch = ''; projectId = '';
  bidDate = ''; bidder = { name: '', street: '', postalCode: '', city: '', country: 'DE', contact: '', email: '' };
  reviewEvidence = ''; currency = 'EUR'; consent = false;
  readonly prices = signal<BidPrice[]>([]);
  private source: TradeSource | null = null;
  private lockedRequest: Record<string, unknown> | null = null;
  private exportKey = ''; private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => { this.generation++; this.source = null; });
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  private current(generation: number, access: string): boolean {
    return generation === this.generation && access === this.auth.accessSignature();
  }
  private reset(): void {
    this.edited(); this.file.set(null); this.source = null; this.structural.set(null);
    this.official.set(null); this.prices.set([]); this.projects.set([]); this.projectCount.set(0);
    this.projectSearch = ''; this.projectId = ''; this.page = 1;
  }
  edited(): void {
    this.generation++; this.busy.set(false); this.error.set(''); this.notice.set('');
    this.preview.set(null); this.receipt.set(null); this.consent = false; this.exportKey = ''; this.lockedRequest = null;
  }
  chooseFile(event: Event): void {
    const input = event.target as HTMLInputElement, value = input.files?.[0]; input.value = '';
    this.reset();
    if (!value) return;
    try {
      sourceBudget(value.size);
      if (!/\.(xml|x83|x84)$/i.test(value.name)) throw new Error('Nur GAEB .x83, .x84 und .xml als Original akzeptiert.');
      this.file.set(value);
    } catch (error) { this.error.set(errorMessage(error)); }
  }
  private failed(error: unknown, generation: number, access: string): void {
    if (!this.current(generation, access)) return;
    if (error instanceof ApiError && [401, 403, 423].includes(error.status)) this.reset();
    this.error.set(errorMessage(error));
  }
  async inspect(): Promise<void> {
    const file = this.file(); if (!file || !this.allowed() || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true);
    this.error.set(''); this.notice.set(''); this.source = null; this.structural.set(null); this.official.set(null);
    this.prices.set([]); this.preview.set(null); this.receipt.set(null);
    try {
      const source = await readTradeSource(file);
      const raw = await this.trade.preview('gaeb', source.blob);
      if (!this.current(generation, access)) return;
      const structural = validateGaebPreview(raw, source.sha256);
      const result = await this.api.xml<GaebInspection>('integrations/v2/gaeb/validate', source.blob);
      if (!this.current(generation, access)) return;
      if (result.validation.sourceSha256 !== source.sha256 || result.validation.schemaEdition !== '2021-05' ||
          !digest(result.validation.schemaBundleSha256) || !Array.isArray(result.validation.issues) ||
          result.readyForBid !== result.validation.valid || result.originalBytesRetained !== false)
        throw new Error('Die originale GAEB-XSD-Prüfung passt nicht zur ausgewählten Quelldatei.');
      this.source = source; this.structural.set(structural); this.official.set(result);
      if (result.validation.valid) this.prices.set(structural.items.map(item => ({
        positionKey: item.positionKey, expectedItemDigest: item.itemDigest, unitPrice: '', notOffered: false, priceEvidence: ''
      })));
      else this.notice.set('Das Original erfüllt die offizielle XSD nicht. Korrektur an der Quelle erforderlich; kein Rückexport freigegeben.');
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  rows(): { positionKey: string; description: string; unit: string; tenderQuantity: string | null; reviewIssues: string[]; price: BidPrice }[] {
    return this.structural()?.items.map((item, i) => ({ ...item, price: this.prices()[i] })) || [];
  }
  async searchProjects(page = 1): Promise<void> {
    if (!this.allowed() || !this.auth.can('projects.read') || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set('');
    this.preview.set(null); this.receipt.set(null); this.consent = false; this.lockedRequest = null; this.exportKey = '';
    try {
      const data = await this.api.list('projects', this.projectSearch, page);
      if (!this.current(generation, access)) return;
      this.projects.set(data.items.filter(x => !x.archived)); this.projectCount.set(data.total);
      this.projectId = ''; this.page = page;
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  private request(): Record<string, unknown> {
    const source = this.source, doc = this.structural();
    if (!source || !doc || doc.sourceSha256 !== source.sha256 || !this.official()?.validation.valid)
      throw new Error('Kein XSD-validiertes GAEB-Original ausgewählt.');
    const project = this.projects().find(x => x.id === this.projectId);
    if (!project || !Number.isInteger(project.revision) || project.revision < 1)
      throw new Error('Ein aktuelles Projekt aus der Projektliste auswählen.');
    if (!validIsoDate(this.bidDate)) throw new Error('Ein vollständiges reales Angebotsdatum eingeben.');
    const bidder = {
      name: validName(this.bidder.name, 'Name des Bieters'),
      street: validName(this.bidder.street, 'Bieter-Straße'),
      postalCode: validName(this.bidder.postalCode, 'Postleitzahl', 30),
      city: validName(this.bidder.city, 'Ort'),
      country: validName(this.bidder.country, 'Ländercode', 2),
      contact: validName(this.bidder.contact, 'Kontakt'),
      email: validName(this.bidder.email, 'E-Mail-Adresse', 240)
    };
    if (!/^[A-Z]{2}$/.test(bidder.country) || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(bidder.email))
      throw new Error('Bieterland (ISO-3166-Alpha-2) und Kontakt-E-Mail prüfen.');
    if (this.prices().length !== doc.items.length) throw new Error('Die Angebotspositionen sind unvollständig.');
    return {
      projectId: project.id, expectedProjectRevision: project.revision, sourceBase64: source.base64,
      mapping: {
        expectedSourceSha256: source.sha256, bidDate: this.bidDate,
        bidder, reviewedCurrency: this.currency, reviewEvidence: validName(this.reviewEvidence, 'Fachliche Angebotsprüfung', 2000),
        prices: this.prices().map(price => ({
          positionKey: price.positionKey, expectedItemDigest: price.expectedItemDigest,
          unitPrice: price.notOffered ? null : priceString(price.unitPrice),
          notOffered: price.notOffered, priceEvidence: validName(price.priceEvidence, 'Preisgrundlage', 2000)
        }))
      }
    };
  }
  async checkBid(): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true);
    this.error.set(''); this.notice.set(''); this.preview.set(null); this.receipt.set(null); this.consent = false;
    try {
      const body = this.request();
      const reply = await this.api.command<BidPreview>('integrations/v2/gaeb/bid-preview', body);
      if (!this.current(generation, access)) return;
      if (reply.contract !== 'firstblick.gaeb-bid-preview/2.0.0' ||
          reply.tenantId !== this.auth.activeTenantId() || reply.projectId !== body['projectId'] ||
          reply.sourceSha256 !== this.source!.sha256 || !digest(reply.xmlSha256) || !digest(reply.mappingDigest) || !digest(reply.digest) ||
          reply.sourcePhase !== this.structural()!.exchangePhase || reply.outputPhase !== 'X84' ||
          !reply.originalSchema.valid || !reply.exportSchema.valid || !reply.canExport ||
          !Array.isArray(reply.lines) || reply.lines.length !== this.structural()!.items.length ||
          reply.deliveryPerformed !== false || reply.independentlyAcceptedByRecipient !== false)
        throw new Error('Die X84-Vorschau, Quellbindung oder XSD-Prüfung ist nicht vollständig.');
      this.preview.set(reply); this.lockedRequest = body; this.exportKey = crypto.randomUUID();
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async exportBid(): Promise<void> {
    const preview = this.preview(), request = this.lockedRequest;
    if (!preview || !request || !this.consent || !this.allowed() || this.busy() || this.receipt()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true);
    this.error.set(''); this.notice.set('');
    try {
      const raw = await this.api.command<BidReceipt>('integrations/v2/gaeb/bid-export',
        { ...request, previewDigest: preview.digest }, undefined, this.exportKey);
      if (!this.current(generation, access)) return;
      if (!raw || !/^[a-f0-9-]{36}$/i.test(raw.id) || raw.preview.digest !== preview.digest ||
          raw.externallySubmitted !== false || raw.recipientAcceptanceConfirmed !== false ||
          !Array.isArray(raw.documents) || raw.documents.length !== 3 ||
          raw.documents.some(d => d.projectId !== request['projectId'] || d.sourceId !== raw.id || d.sourceKind !== 'gaeb-exchanges' || !digest(d.sha256) || !Number.isSafeInteger(d.size) || d.size <= 0))
        throw new Error('Der Ausgabebeleg wurde nicht vollständig oder im falschen Projekt angelegt.');
      this.receipt.set(raw); this.consent = false;
      this.notice.set('X84, XML-Original und Nachweis wurden unveränderlich abgelegt. Keine automatische Übermittlung an den Auftraggeber.');
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  async download(document: ExportDocument): Promise<void> {
    const receipt = this.receipt();
    if (!receipt || !this.allowed() || this.busy() || !receipt.documents.some(d => d.id === document.id)) return;
    const generation = ++this.generation, access = this.auth.accessSignature(); this.busy.set(true); this.error.set('');
    try {
      const response = await this.api.downloadResponse(`integrations/v2/gaeb/exports/${receipt.id}/documents/${document.id}/download`);
      if (!this.current(generation, access)) return;
      if (response.headers.get('X-Content-SHA256') !== document.sha256 ||
          response.headers.get('Content-Type')?.split(';')[0] !== document.contentType)
        throw new Error('Dateityp oder Prüfsumme des serverseitigen GAEB-Belegs stimmen nicht.');
      const blob = await response.blob();
      if (!this.current(generation, access)) return;
      if (blob.size !== document.size || await sha256Blob(blob) !== document.sha256)
        throw new Error('Die abgerufene GAEB-Datei besteht die SHA-256-Prüfung nicht.');
      downloadBlob(blob, document.name);
    } catch (error) { this.failed(error, generation, access); }
    finally { if (this.current(generation, access)) this.busy.set(false); }
  }
}
