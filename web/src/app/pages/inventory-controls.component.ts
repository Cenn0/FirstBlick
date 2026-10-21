import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';

type Mode = 'count' | 'transfer' | 'projectReturn' | 'supplierReturn';
interface StockBalance { onHand: number | string; reserved: number | string; available: number | string }
interface Reservation extends Entity {
  materialId: string; projectId: string; location: string; quantity: number | string;
  consumedQuantity: number | string; releasedQuantity: number | string;
}
interface PurchaseOrder extends Entity {
  number: string; title: string; status: string;
  lines: { id: string; materialId: string; description: string; quantity: number | string }[];
}

@Component({
  selector: 'app-inventory-controls',
  imports: [FormsModule, RouterLink],
  templateUrl: './inventory-controls.component.html'
})
export class InventoryControlsComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly allowed = computed(() => this.auth.hasModule('purchasing') &&
    this.auth.can('stock-movements.write') &&
    ['ManagingDirector', 'Office', 'SiteManager'].includes(this.auth.tenant()?.role || '') &&
    !this.auth.recoveryHold());
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly mode = signal<Mode>('count');
  readonly modes: { key: Mode; label: string }[] = [
    { key: 'count', label: 'Inventurdifferenz' }, { key: 'transfer', label: 'Lagerumlagerung' },
    { key: 'projectReturn', label: 'Projekt-Rückgabe' }, { key: 'supplierReturn', label: 'Lieferantenretoure' }
  ];
  readonly materials = signal<Entity[]>([]);
  readonly total = signal(0);
  readonly balance = signal<StockBalance | null>(null);
  readonly reservation = signal<Reservation | null>(null);
  readonly purchase = signal<PurchaseOrder | null>(null);
  materialSearch = '';
  materialPage = 1;
  materialId = '';
  location = 'Hauptlager';
  targetLocation = '';
  countedOnHand: number | null = null;
  quantity: number | null = null;
  unitCost: number | null = null;
  evidenceReference = '';
  reason = '';
  reservationId = '';
  consumptionMovementId = '';
  purchaseId = '';
  purchaseLineId = '';
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.generation++);
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }

  private reset(): void {
    this.generation++; this.error.set(''); this.notice.set(''); this.busy.set(false);
    this.materials.set([]); this.total.set(0); this.balance.set(null);
    this.reservation.set(null); this.purchase.set(null);
    this.materialSearch = ''; this.materialPage = 1; this.materialId = '';
    this.location = 'Hauptlager'; this.targetLocation = ''; this.countedOnHand = null;
    this.quantity = null; this.unitCost = null; this.evidenceReference = '';
    this.reason = ''; this.reservationId = ''; this.consumptionMovementId = '';
    this.purchaseId = ''; this.purchaseLineId = '';
  }

  choose(mode: Mode): void {
    if (this.mode() === mode) return;
    this.mode.set(mode); this.balance.set(null); this.notice.set(''); this.error.set('');
    this.countedOnHand = null; this.quantity = null; this.reason = '';
  }

  selectMaterial(): void {
    this.balance.set(null); this.countedOnHand = null; this.notice.set(''); this.error.set('');
  }
  changedLocation(): void { this.balance.set(null); this.countedOnHand = null; this.error.set(''); }

  private current(version: number, signature: string): boolean {
    return version === this.generation && this.auth.accessSignature() === signature;
  }
  private async perform(task: () => Promise<void>, success: string): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    const version = ++this.generation, signature = this.auth.accessSignature();
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      await task();
      if (this.current(version, signature)) this.notice.set(success);
    } catch (e) { if (this.current(version, signature)) this.error.set(errorMessage(e)); }
    finally { if (this.current(version, signature)) this.busy.set(false); }
  }
  private requiredNumber(value: number | null, name: string, allowZero = false): number {
    if (typeof value !== 'number' || !Number.isFinite(value) ||
        (allowZero ? value < 0 : value <= 0) || value > 1_000_000 ||
        Math.round(value * 1_000_000) !== value * 1_000_000)
      throw new Error(name + ': gültigen Wert mit höchstens sechs Nachkommastellen eingeben.');
    return value;
  }
  private requiredText(value: string, name: string, max: number): string {
    if (!value.trim() || value.length > max) throw new Error(name + ': einen Belegtext eingeben.');
    return value.trim();
  }
  private validId(id: string): string {
    if (!/^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/i.test(id)) throw new Error('Eine gültige Vorgangs-ID auswählen.');
    return id;
  }
  number(value: number | string | undefined): string {
    const numeric = typeof value === 'string' ? Number(value) : value;
    return typeof numeric === 'number' && Number.isFinite(numeric)
      ? new Intl.NumberFormat('de-DE', { maximumFractionDigits: 6 }).format(numeric) : '–';
  }
  private stockNumber(value: number | string): number {
    const n = typeof value === 'string' ? Number(value) : value;
    if (!Number.isFinite(n) || n < 0 || n > 1_000_000) throw new Error('Ungültige Lagerbilanz vom Server.');
    return n;
  }
  async loadMaterials(page = 1): Promise<void> {
    await this.perform(async () => {
      const result = await this.api.list('materials', this.materialSearch, page);
      this.materials.set(result.items.filter(x => !x.archived)); this.total.set(result.total);
      this.materialPage = page;
    }, 'Materialstamm geladen.');
  }
  async loadBalance(): Promise<void> {
    await this.perform(async () => {
      const id = this.validId(this.materialId);
      const location = this.requiredText(this.location, 'Lagerort', 120);
      const balance = await this.api.get<StockBalance>(`materials/${id}/stock?location=${encodeURIComponent(location)}`);
      this.stockNumber(balance.onHand); this.stockNumber(balance.reserved); this.stockNumber(balance.available);
      this.balance.set(balance); this.countedOnHand = null;
    }, 'Aktuelle Lagerbilanz abgerufen.');
  }
  async submitCount(): Promise<void> {
    await this.perform(async () => {
      const current = this.balance();
      if (!current) throw new Error('Vor der Buchung die Lagerbilanz laden.');
      const expectedOnHand = this.stockNumber(current.onHand);
      const countedOnHand = this.requiredNumber(this.countedOnHand, 'Gezählter Bestand', true);
      const unitCost = this.requiredNumber(this.unitCost, 'Stückbewertung', true);
      const correction = await this.api.command<{ id: string }>(
        `materials/${this.validId(this.materialId)}/stock-count`,
        { location: this.requiredText(this.location, 'Lagerort', 120), expectedOnHand,
          countedOnHand, unitCost, evidenceReference: this.requiredText(this.evidenceReference, 'Inventurbeleg', 240),
          reason: this.requiredText(this.reason, 'Begründung', 2000) });
      if (!correction.id) throw new Error('Der Server hat keine bestätigte Buchung zurückgegeben.');
      this.balance.set(null);
    }, 'Inventurkorrektur gebucht. Die aktuelle Bilanz erneut laden.');
  }
  async submitTransfer(): Promise<void> {
    await this.perform(async () => {
      const current = this.balance();
      if (!current) throw new Error('Vor der Umbuchung die Quellbilanz laden.');
      const result = await this.api.command<{ outbound: { id: string }; inbound: { id: string } }>(
        `materials/${this.validId(this.materialId)}/transfer`,
        { fromLocation: this.requiredText(this.location, 'Quelllager', 120),
          toLocation: this.requiredText(this.targetLocation, 'Ziellager', 120),
          quantity: this.requiredNumber(this.quantity, 'Menge'),
          unitCost: this.requiredNumber(this.unitCost, 'Stückbewertung', true),
          expectedFromOnHand: this.stockNumber(current.onHand),
          reason: this.requiredText(this.reason, 'Umlagerungsbeleg', 2000) });
      if (!result.outbound?.id || !result.inbound?.id) throw new Error('Das Buchungspaar ist unvollständig.');
      this.balance.set(null);
    }, 'Beide Lagerbewegungen wurden gebucht. Bestände erneut laden.');
  }
  async loadReservation(): Promise<void> {
    await this.perform(async () => {
      const item = await this.api.get<Reservation>(`material-reservations/${this.validId(this.reservationId)}`);
      if (item.archived) throw new Error('Diese Reservierung ist archiviert.');
      this.reservation.set(item);
    }, 'Projektreservierung geladen. Die ID des ursprünglichen Verbrauchs zuordnen.');
  }
  async submitProjectReturn(): Promise<void> {
    await this.perform(async () => {
      const reservation = this.reservation();
      if (!reservation) throw new Error('Reservierung zunächst laden.');
      const result = await this.api.command<{ reservation: Reservation; correction: { id: string } }>(
        `material-reservations/${reservation.id}/return`,
        { consumptionMovementId: this.validId(this.consumptionMovementId),
          quantity: this.requiredNumber(this.quantity),
          reason: this.requiredText(this.reason, 'Rückgabebegründung', 2000) }, reservation.revision);
      if (!result.correction?.id) throw new Error('Es liegt kein Buchungsbeleg vor.');
      this.reservation.set(result.reservation);
    }, 'Projektmaterial zum Originalkostenwert zurückgebucht.');
  }
  async loadPurchase(): Promise<void> {
    await this.perform(async () => {
      const item = await this.api.get<PurchaseOrder>(`purchase-orders/${this.validId(this.purchaseId)}`);
      if (item.archived || !Array.isArray(item.lines)) throw new Error('Die Bestellung ist nicht verfügbar.');
      this.purchase.set(item); this.purchaseLineId = '';
    }, 'Bestellung und feste Positionsmengen geladen.');
  }
  async submitSupplierReturn(): Promise<void> {
    await this.perform(async () => {
      const order = this.purchase();
      if (!order) throw new Error('Zuerst die Bestellung laden.');
      if (!order.lines.some(x => x.id === this.purchaseLineId)) throw new Error('Eine Bestellposition auswählen.');
      const result = await this.api.command<{ order: PurchaseOrder; correction: { id: string } }>(
        `purchase-orders/${order.id}/supplier-return`,
        { lineId: this.purchaseLineId, quantity: this.requiredNumber(this.quantity),
          location: this.requiredText(this.location, 'Lagerort', 120),
          supplierCreditReference: this.requiredText(this.evidenceReference, 'Lieferantenbeleg', 240),
          reason: this.requiredText(this.reason, 'Retourengrund', 2000) }, order.revision);
      if (!result.correction?.id) throw new Error('Der Retourenbeleg fehlt.');
      this.purchase.set(result.order);
    }, 'Lieferantenretoure im Warenbestand erfasst. Gutschrift separat finanziell buchen.');
  }
}
