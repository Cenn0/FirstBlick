import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity, Page } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';
import { decimalDisplay } from '../core/decimal-format';
import { decimalInput } from '../core/decimal-input';

type StockMode = 'count' | 'transfer' | 'supplierReturn' | 'projectReturn';
interface Balance { onHand: string; reserved: string; available: string }
interface Movement extends Entity {
  materialId: string; kind: string; quantity: string; location: string; occurredAt: string;
  reference: string; reason: string; unitCost: string;
  purchaseOrderId?: string; transferSourceId?: string;
}
interface TransferTrace {
  sourceMovementId: string; originalReceiptId: string; originalLocation: string; sourceLocation: string;
  unitCost: string; evidenceRemaining: string;
  transfers: { incomingId: string; sourceId: string; fromLocation: string; toLocation: string; quantity: string; occurredAt: string }[];
}
const modes: { key: StockMode; name: string }[] = [
  { key: 'count', name: 'Inventurdifferenz' },
  { key: 'transfer', name: 'Lagerumbuchung' },
  { key: 'supplierReturn', name: 'Lieferantenrückgabe' },
  { key: 'projectReturn', name: 'Baustellenrückgabe' }
];

/** Manual, explicitly authorized stock reconciliations. No synthetic receipts, imports or automatic supplier delivery. */
@Component({
  selector: 'app-inventory-control',
  imports: [FormsModule, RouterLink],
  template: `
  <header class="page-heading">
    <div><p class="eyebrow">LAGERFÜHRUNG</p><h1>Bestand prüfen & korrigieren</h1>
    <p>Gezählte Bestände, Umbuchungen und beleggebundene Rückgaben getrennt erfassen.</p></div>
    <a routerLink="/stock-movements" class="button secondary">Alle Materialbewegungen</a>
  </header>
  @if (!allowed()) {
    <section class="panel empty-state"><h2>Kein Zugriff</h2><p>Materialbewegungen sind für diese Rolle oder diesen Betrieb nicht freigegeben.</p></section>
  } @else {
    <div class="alert information"><strong>Jede Buchung bleibt erhalten.</strong> Inventuren und Rückgaben erzeugen zusätzliche, nachvollziehbare Bewegungen; vorhandene Belege werden nicht überschrieben.</div>
    @if (error()) { <div class="alert error" role="alert">{{error()}}</div> }
    @if (notice()) { <div class="alert information" role="status">{{notice()}}</div> }
    <section class="panel">
      <h2>Material und Lagerort</h2>
      <div class="form-grid">
        <div class="form-field"><label for="warehouse-search">Material suchen</label>
          <input id="warehouse-search" maxlength="200" [(ngModel)]="materialSearch" (keyup.enter)="searchMaterials()">
          <button type="button" class="button secondary" (click)="searchMaterials()" [disabled]="busy() || transferLoading() || chainLoading()">Materialliste aktualisieren</button></div>
        <div class="form-field"><label for="warehouse-material">Material auswählen</label>
          <select id="warehouse-material" [(ngModel)]="materialId" (ngModelChange)="selectMaterial()">
            <option value="">Bitte auswählen</option>
            @for (item of materials(); track item.id) { <option [value]="item.id">{{item['name']}} ({{item['unit']}})</option> }
          </select></div>
        <div class="form-field"><label for="warehouse-location">Lagerort (exakter Name)</label>
          <input id="warehouse-location" required maxlength="120" [(ngModel)]="location" (ngModelChange)="changedLocation()" placeholder="Hauptlager"></div>
        <div class="form-field"><label>Aktuelle Daten</label>
          <button type="button" class="button secondary" [disabled]="busy() || chainLoading() || !materialId" (click)="refresh()">Bestand & Bewegungen laden</button></div>
      </div>
      @if (balance(); as stock) {
        <dl class="result-list">
          <div><dt>Gebucht</dt><dd>{{decimal(stock.onHand)}}</dd></div>
          <div><dt>Reserviert</dt><dd>{{decimal(stock.reserved)}}</dd></div>
          <div><dt>Verfügbar</dt><dd>{{decimal(stock.available)}}</dd></div>
        </dl>
      }
    </section>
    @if (materialId && balance(); as stock) {
      <section class="panel">
        <h2>Bestandsvorgang</h2>
        <div class="section-tabs" role="tablist" aria-label="Materialvorgänge">
          @for (item of modes; track item.key) {
            @if (permitted(item.key)) {
              <button type="button" role="tab" [attr.aria-selected]="mode === item.key"
                [class.active]="mode === item.key" [disabled]="busy()" (click)="switchMode(item.key)">{{item.name}}</button>
            }
          }
        </div>
        @if (!permitted(mode)) { <p>Für diesen Buchungstyp fehlt die erforderliche Rolle.</p> }
        @else {
          <form #stockForm="ngForm" (ngSubmit)="submit()" class="form-grid">
            @if (mode === 'count') {
              <div class="form-field"><label for="stock-expected">Erfasster Buchbestand</label>
                <input id="stock-expected" type="number" [value]="stock.onHand" disabled>
                <p>Wird beim Buchen erneut mit dem Serverbestand abgeglichen.</p></div>
              <div class="form-field"><label for="stock-counted">Tatsächlich gezählter Bestand</label>
                <input id="stock-counted" name="stock-counted" type="text" inputmode="decimal"
                  pattern="[0-9]+([.,][0-9]{1,6})?" required [(ngModel)]="quantity"></div>
              <div class="form-field"><label for="stock-evidence">Inventurbeleg</label>
                <input id="stock-evidence" name="stock-evidence" required maxlength="240" [(ngModel)]="evidenceReference"
                  placeholder="Nummer des Zählprotokolls"></div>
            } @else if (mode === 'transfer') {
              <div class="form-field"><label for="stock-destination">Ziellagerort</label>
                <input id="stock-destination" name="stock-destination" required maxlength="120" [(ngModel)]="destination"></div>
              <div class="form-field"><label for="stock-transfer-source">Herkunftsbeleg der Umbuchung</label>
                <select id="stock-transfer-source" name="stock-transfer-source" [(ngModel)]="transferSourceId" (ngModelChange)="selectTransferSource()">
                  <option value="">Ohne Zuordnung zu einem Wareneingang</option>
                  @for (source of transferSources(); track source.id) {
                    <option [value]="source.id">{{source.kind}} · {{decimal(source.quantity)}} · {{source.id.slice(0,8)}}</option>
                  }
                </select>
                <p>Für eine fortlaufende Herkunftskette den Wareneingang oder eine bereits zugeordnete Umbuchung auswählen. Es werden geeignete Belege dieser Journalseite angeboten.</p>
              </div>
            } @else {
              <div class="form-field full-width"><label for="stock-source">Ursprüngliche gebuchte Bewegung</label>
                <select id="stock-source" name="stock-source" required [(ngModel)]="sourceId">
                  <option value="">Beleg auswählen</option>
                  @for (movement of sourceMovements(); track movement.id) {
                    <option [value]="movement.id">{{movement.kind}} · {{decimal(movement.quantity)}} · {{movement.occurredAt.slice(0,10)}} · {{movement.id.slice(0,8)}}</option>
                  }
                </select>
                <p>Es werden nur passende Original-Wareneingänge beziehungsweise projektgebundene Verbräuche angezeigt.</p>
              </div>
              @if (mode === 'supplierReturn') {
                <div class="form-field full-width"><label for="stock-return-transfer">Rückgabe nach Lagerumbuchung</label>
                  <select id="stock-return-transfer" name="stock-return-transfer" [(ngModel)]="returnTransferId" (ngModelChange)="selectReturnTransfer()" [disabled]="transferLoading()">
                    <option value="">Aus dem ursprünglichen Wareneingangslager zurückgeben</option>
                    @for (transfer of transferReceipts(); track transfer.id) {
                      <option [value]="transfer.id">{{transfer.location}} · {{decimal(transfer.quantity)}} · {{transfer.occurredAt.slice(0,10)}} · {{transfer.id.slice(0,8)}}</option>
                    }
                  </select>
                  <p>Für eine Rückgabe aus einem anderen Lager den letzten Umbuchungsbeleg auswählen. Bei zugeordneten Umbuchungen prüft der Server die gesamte Kette bis zum ausgewählten Wareneingang einschließlich Teilmengen und Verzweigungen.</p>
                  @if (transferTotal() > 25) {
                    <div class="detail-actions">
                      <button type="button" class="button secondary" [disabled]="busy() || transferLoading() || transferPage <= 1" (click)="loadTransferReceipts(transferPage-1)">Vorherige Umbuchungen</button>
                      <span>Umbuchungen · Seite {{transferPage}}</span>
                      <button type="button" class="button secondary" [disabled]="busy() || transferLoading() || transferPage*25 >= transferTotal()" (click)="loadTransferReceipts(transferPage+1)">Weitere Umbuchungen</button>
                    </div>
                  }
                </div>
              }
            }
            @if (chainLoading()) { <p class="full-width" role="status">Herkunftskette wird geprüft …</p> }
            @if (chain(); as trace) {
              <section class="full-width chain-evidence" aria-label="Herkunftskette">
                <h3>Herkunftskette</h3>
                <p>Wareneingang {{trace.originalReceiptId.slice(0,8)}} · {{trace.originalLocation}}</p>
                @if (trace.transfers.length) {
                  <ol>@for (step of trace.transfers; track step.incomingId) {
                    <li>{{step.fromLocation}} → {{step.toLocation}} · {{decimal(step.quantity)}} · {{step.incomingId.slice(0,8)}}</li>
                  }</ol>
                }
                <p>Am ausgewählten Beleg noch nicht zugeordnet: <strong>{{decimal(trace.evidenceRemaining)}}</strong>. Reservierungen und freier Lagerbestand werden beim Buchen zusätzlich geprüft.</p>
                @if (mode === 'supplierReturn' && sourceId && trace.originalReceiptId !== sourceId) {
                  <p role="alert">Diese Umbuchung gehört zu einem anderen Originalwareneingang.</p>
                }
              </section>
            }
            @if (mode !== 'count') {
              <div class="form-field"><label for="stock-quantity">Menge</label>
                <input id="stock-quantity" name="stock-quantity" type="text" inputmode="decimal"
                  pattern="[0-9]+([.,][0-9]{1,6})?" required [(ngModel)]="quantity"></div>
            }
            @if (mode === 'count' || mode === 'transfer') {
              <div class="form-field"><label for="stock-unit-cost">Bewertung je Einheit (€)</label>
                <input id="stock-unit-cost" name="stock-unit-cost" type="text" inputmode="decimal"
                  pattern="[0-9]+([.,][0-9]{1,6})?" required [(ngModel)]="unitCost" [readonly]="mode === 'transfer' && !!transferSourceId"></div>
            }
            <div class="form-field full-width"><label for="stock-reason">Begründung / Belegreferenz</label>
              <textarea id="stock-reason" name="stock-reason" maxlength="2000" rows="3" required [(ngModel)]="reason"
                placeholder="Inventurprotokoll, Lieferschein, Baustellenrückgabe und Bearbeiterhinweis"></textarea></div>
            <div class="detail-actions full-width">
              <button type="submit" class="button primary" [disabled]="busy() || stockForm.invalid || !canSubmit()">{{busy() ? 'Wird gebucht …' : 'Beleggebundene Bewegung buchen'}}</button>
            </div>
          </form>
        }
      </section>
      <section class="panel">
        <h2>Bewegungsjournal – {{location}}</h2>
        <p>Jede Zeile ist ein eigener unveränderlicher Buchungssatz. Rückgaben referenzieren den Ursprung über die Referenz.</p>
        @if (movements().length) {
          <div class="table-scroll"><table><thead><tr><th>Datum</th><th>Art</th><th>Menge</th><th>Referenz</th><th>Begründung</th></tr></thead><tbody>
            @for (movement of movements(); track movement.id) { <tr><td>{{movement.occurredAt.slice(0,10)}}</td>
              <td>{{movement.kind}}</td><td>{{decimal(movement.quantity)}}</td>
              <td><code>{{movement.reference || '–'}}</code></td><td>{{movement.reason || '–'}}</td></tr> }
          </tbody></table></div>
          <div class="detail-actions">
            <button type="button" class="button secondary" [disabled]="busy() || page <= 1" (click)="changePage(page-1)">Zurück</button>
            <span>Seite {{page}} · {{total()}} Bewegungen</span>
            <button type="button" class="button secondary" [disabled]="busy() || page*25 >= total()" (click)="changePage(page+1)">Weiter</button>
          </div>
        } @else { <p>Noch keine Bewegung an diesem Lagerort.</p> }
      </section>
    }
  }
  `,
  styles: [`
    .form-field p { font-size: .86rem; opacity: .8; margin: .35rem 0; }
    .form-field button { margin-top: .5rem; }
    .result-list { margin-top: 1rem; }
    .section-tabs { margin-bottom: 1rem; flex-wrap: wrap; }
    .table-scroll code { white-space: normal; overflow-wrap: anywhere; }
    .chain-evidence { padding: .75rem; border: 1px solid var(--border, #c7ccd3); border-radius: .5rem; }
  `]
})
export class InventoryControlComponent {
  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);
  private generation = 0;
  private transferGeneration = 0;
  private chainGeneration = 0;
  readonly allowed = computed(() => this.auth.hasModule('purchasing') && this.auth.can('stock-movements.read'));
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly materials = signal<Entity[]>([]);
  readonly balance = signal<Balance | null>(null);
  readonly movements = signal<Movement[]>([]);
  readonly total = signal(0);
  readonly transferReceipts = signal<Movement[]>([]);
  readonly transferTotal = signal(0);
  readonly transferLoading = signal(false);
  readonly chain = signal<TransferTrace | null>(null);
  readonly chainLoading = signal(false);
  readonly modes = modes;
  materialSearch = '';
  materialId = '';
  location = 'Hauptlager';
  mode: StockMode = 'count';
  quantity = '';
  unitCost = '';
  reason = '';
  evidenceReference = '';
  destination = '';
  sourceId = '';
  returnTransferId = '';
  transferSourceId = '';
  transferPage = 1;
  page = 1;
  sourceMovements(): Movement[] {
    return this.movements().filter(x => this.mode === 'supplierReturn'
      ? x.kind === 'Receipt' : x.kind === 'Consumption');
  }
  transferSources(): Movement[] {
    return this.movements().filter(x => x.kind === 'Receipt' && !!x.purchaseOrderId
      || x.kind === 'TransferIn' && !!x.transferSourceId);
  }

  constructor() {
    inject(DestroyRef).onDestroy(() => { this.generation++; });
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }

  private reset(): void {
    this.generation++; this.materialId = ''; this.location = 'Hauptlager'; this.mode = 'count';
    this.materials.set([]); this.balance.set(null); this.movements.set([]); this.total.set(0);
    this.quantity = ''; this.unitCost = ''; this.reason = ''; this.evidenceReference = ''; this.destination = ''; this.sourceId = '';
    this.page = 1; this.busy.set(false); this.error.set(''); this.notice.set('');
    this.clearTransfers();
    if (this.allowed()) void this.searchMaterials();
  }
  private current(generation: number, access: string): boolean {
    return generation === this.generation && access === this.auth.accessSignature();
  }
  decimal(value: unknown): string {
    const exact = typeof value === 'number' ? String(value) : value;
    return typeof exact === 'string' ? decimalDisplay(exact, 0, 6) ?? '–' : '–';
  }
  /** Bounded comparison is allowed in JavaScript; the serialized value is never converted to float. */
  private amount(value: string, allowZero: boolean): string | null {
    try {
      const amount = decimalInput(value);
      if (!/^\d+(?:\.\d{1,6})?$/.test(amount)) return null;
      const range = Number(amount);
      return range <= 1_000_000 && (range > 0 || allowZero) ? amount : null;
    } catch { return null; }
  }
  permitted(mode: StockMode): boolean {
    if (!this.allowed() || !this.auth.can('stock-movements.write')) return false;
    const role = this.auth.tenant()?.role;
    return mode === 'supplierReturn'
      ? role === 'ManagingDirector' || role === 'Office' || role === 'SiteManager'
      : role === 'ManagingDirector' || role === 'SiteManager';
  }
  switchMode(mode: StockMode): void {
    if (this.busy()) return;
    this.mode = mode; this.sourceId = ''; this.quantity = ''; this.unitCost = ''; this.reason = ''; this.evidenceReference = '';
    this.clearTransfers();
    this.notice.set(''); this.error.set('');
    if (mode === 'supplierReturn') void this.loadTransferReceipts(1);
  }
  selectMaterial(): void {
    this.invalidateSelection();
    if (this.materialId) void this.refresh();
  }
  changedLocation(): void {
    this.invalidateSelection();
  }
  private invalidateSelection(): void {
    this.generation++; this.balance.set(null); this.movements.set([]); this.total.set(0);
    this.page = 1; this.sourceId = ''; this.quantity = ''; this.unitCost = ''; this.reason = '';
    this.evidenceReference = ''; this.destination = ''; this.busy.set(false); this.notice.set(''); this.error.set('');
    this.clearTransfers();
  }
  private clearTransfers(): void {
    this.transferGeneration++;
    this.returnTransferId = ''; this.transferPage = 1; this.transferReceipts.set([]);
    this.transferTotal.set(0); this.transferLoading.set(false);
    this.transferSourceId = ''; this.clearChain();
  }
  private clearChain(): void { this.chainGeneration++; this.chain.set(null); this.chainLoading.set(false); }
  async selectTransferSource(clearError = true): Promise<void> {
    this.clearChain();
    if (!this.transferSourceId || this.mode !== 'transfer') return;
    const source = this.transferSources().find(x => x.id === this.transferSourceId);
    if (!source) { this.transferSourceId = ''; return; }
    this.unitCost = source.unitCost;
    await this.loadChain(source, clearError);
  }
  async selectReturnTransfer(): Promise<void> {
    this.clearChain();
    const source = this.transferReceipts().find(x => x.id === this.returnTransferId);
    if (this.mode === 'supplierReturn' && source?.transferSourceId) await this.loadChain(source);
  }
  private async loadChain(source: Movement, clearError = true): Promise<void> {
    const gen = this.generation, access = this.auth.accessSignature(), chainGen = ++this.chainGeneration;
    const current = () => this.current(gen, access) && chainGen === this.chainGeneration;
    this.chainLoading.set(true); if (clearError) this.error.set('');
    try {
      const trace = await this.api.get<TransferTrace>(`stock-movements/${source.id}/transfer-chain`);
      if (!current()) return;
      if (trace.sourceMovementId !== source.id || trace.sourceLocation !== source.location
        || typeof trace.originalReceiptId !== 'string' || typeof trace.originalLocation !== 'string'
        || typeof trace.unitCost !== 'string' || trace.unitCost !== source.unitCost
        || typeof trace.evidenceRemaining !== 'string' || !/^\d+(?:\.\d+)?$/.test(trace.evidenceRemaining)
        || !Array.isArray(trace.transfers) || trace.transfers.length > 32
        || trace.transfers.some(x => typeof x.incomingId !== 'string' || typeof x.sourceId !== 'string'
          || typeof x.fromLocation !== 'string' || typeof x.toLocation !== 'string'
          || typeof x.quantity !== 'string' || !/^\d+(?:\.\d+)?$/.test(x.quantity)))
        throw new Error('Die Herkunftskette konnte nicht eindeutig geprüft werden.');
      let previousId = trace.originalReceiptId, previousLocation = trace.originalLocation;
      const visited = new Set<string>([previousId]);
      for (const step of trace.transfers) {
        if (step.sourceId !== previousId || step.fromLocation !== previousLocation
          || step.fromLocation === step.toLocation || visited.has(step.incomingId)
          || this.amount(step.quantity, false) === null)
          throw new Error('Die Herkunftskette konnte nicht eindeutig geprüft werden.');
        visited.add(step.incomingId); previousId = step.incomingId; previousLocation = step.toLocation;
      }
      if (previousId !== source.id || previousLocation !== source.location
        || (source.kind === 'Receipt') !== (trace.transfers.length === 0)
        || Number(trace.evidenceRemaining) > Number(source.quantity))
        throw new Error('Die Herkunftskette konnte nicht eindeutig geprüft werden.');
      this.chain.set(trace);
    } catch (error) { if (current()) this.error.set(errorMessage(error)); }
    finally { if (current()) this.chainLoading.set(false); }
  }
  async loadTransferReceipts(page: number): Promise<void> {
    if (!this.materialId || !this.allowed() || this.mode !== 'supplierReturn' || page < 1) return;
    const gen = this.generation, access = this.auth.accessSignature(), transferGen = ++this.transferGeneration;
    const current = () => this.current(gen, access) && transferGen === this.transferGeneration && this.mode === 'supplierReturn';
    this.transferLoading.set(true); this.returnTransferId = ''; this.clearChain();
    try {
      const result = await this.api.get<Page<Movement>>(`materials/${this.materialId}/movements?kind=TransferIn&page=${page}`);
      if (!current()) return;
      if (!Array.isArray(result.items) || !Number.isInteger(result.total) || result.total < 0
        || result.items.some(item => item.kind !== 'TransferIn' || item.materialId !== this.materialId
          || typeof item.id !== 'string' || typeof item.location !== 'string' || typeof item.occurredAt !== 'string'))
        throw new Error('Die Umbuchungsbelege konnten nicht eindeutig geprüft werden.');
      this.transferReceipts.set(result.items); this.transferTotal.set(result.total); this.transferPage = page;
    } catch (error) { if (current()) this.error.set(errorMessage(error)); }
    finally { if (current()) this.transferLoading.set(false); }
  }
  async searchMaterials(): Promise<void> {
    if (!this.allowed() || this.busy() || this.transferLoading() || this.chainLoading()) return;
    const gen = ++this.generation, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set('');
    try {
      const result = await this.api.list('materials', this.materialSearch, 1);
      if (this.current(gen, access)) this.materials.set(result.items);
    } catch (error) { if (this.current(gen, access)) this.error.set(errorMessage(error)); }
    finally { if (this.current(gen, access)) this.busy.set(false); }
  }
  async refresh(clearError = true): Promise<void> {
    if (!this.materialId || !this.location.trim() || !this.allowed() || this.chainLoading()) return;
    const gen = ++this.generation, access = this.auth.accessSignature(), materialId = this.materialId;
    const location = this.location;
    this.busy.set(true); if (clearError) this.error.set('');
    try {
      const [balance, history] = await Promise.all([
        this.api.get<Balance>(`materials/${materialId}/stock?location=${encodeURIComponent(location)}`),
        this.api.get<Page<Movement>>(`materials/${materialId}/movements?location=${encodeURIComponent(location)}&page=${this.page}`)
      ]);
      if (!this.current(gen, access)) return;
      if (![balance.onHand, balance.reserved, balance.available].every(x => typeof x === 'string' && /^-?\d+(?:\.\d+)?$/.test(x))
        || !Array.isArray(history.items) || !Number.isInteger(history.total)) throw new Error('Unvollständiger Lagerstand vom Server.');
      this.balance.set(balance); this.movements.set(history.items); this.total.set(history.total);
      if (this.mode === 'supplierReturn') await this.loadTransferReceipts(1);
      else if (this.mode === 'transfer' && this.transferSourceId) await this.selectTransferSource(clearError);
    } catch (error) { if (this.current(gen, access)) this.error.set(errorMessage(error)); }
    finally { if (this.current(gen, access)) this.busy.set(false); }
  }
  changePage(page: number): void {
    if (page < 1) return; this.page = page; this.sourceId = ''; this.transferSourceId = ''; this.clearChain(); void this.refresh();
  }
  canSubmit(): boolean {
    if (!this.permitted(this.mode) || this.busy() || this.transferLoading() || this.chainLoading() || !this.balance() || !this.reason.trim()) return false;
    if (this.mode === 'count') return !!this.evidenceReference.trim() && this.amount(this.quantity, true) !== null && this.amount(this.unitCost, true) !== null;
    if (this.amount(this.quantity, false) === null) return false;
    if (this.chain() && Number(this.amount(this.quantity, false)) > Number(this.chain()!.evidenceRemaining)) return false;
    if (this.mode === 'transfer') return !!this.destination.trim() && this.amount(this.unitCost, true) !== null
      && (!this.transferSourceId || this.chain()?.sourceMovementId === this.transferSourceId);
    return !!this.sourceMovements().some(x => x.id === this.sourceId)
      && (!this.returnTransferId || this.transferReceipts().some(x => x.id === this.returnTransferId
        && (!x.transferSourceId || this.chain()?.sourceMovementId === x.id && this.chain()?.originalReceiptId === this.sourceId)));
  }
  async submit(): Promise<void> {
    if (!this.canSubmit() || !this.materialId || !this.balance()) return;
    const gen = ++this.generation, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      if (this.mode === 'count') await this.api.command(`materials/${this.materialId}/inventory-count`, {
        location: this.location, expectedOnHand: this.balance()!.onHand, countedOnHand: this.amount(this.quantity, true),
        unitCost: this.amount(this.unitCost, true), reason: this.reason.trim(), evidenceReference: this.evidenceReference.trim()
      });
      else if (this.mode === 'transfer') await this.api.command(`materials/${this.materialId}/transfer`, {
        fromLocation: this.location, toLocation: this.destination, quantity: this.amount(this.quantity, false),
        unitCost: this.amount(this.unitCost, true), expectedFromOnHand: this.balance()!.onHand, reason: this.reason.trim(),
        ...(this.transferSourceId ? { transferSourceId: this.transferSourceId } : {})
      });
      else {
        const source = this.sourceMovements().find(x => x.id === this.sourceId);
        if (!source) throw new Error('Den Originalbeleg erneut auswählen.');
        await this.api.command(`stock-movements/${source.id}/${this.mode === 'supplierReturn' ? 'supplier-return' : 'project-return'}`,
          { quantity: this.amount(this.quantity, false), reason: this.reason.trim(),
            ...(this.mode === 'supplierReturn' && this.returnTransferId ? { returnTransferId: this.returnTransferId } : {}) }, source.revision);
      }
      if (!this.current(gen, access)) return;
      this.quantity = ''; this.sourceId = ''; this.reason = ''; this.evidenceReference = ''; this.transferSourceId = ''; this.clearChain();
      this.notice.set('Bestandsbewegung mit Buchungsbeleg erfasst. Bestand und Journal werden aktualisiert.');
    } catch (error) { if (this.current(gen, access)) this.error.set(errorMessage(error)); }
    finally { if (this.current(gen, access)) { this.busy.set(false); void this.refresh(false); } }
  }
}
