import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { businessTimeChoices, resolveBusinessTime } from '../core/business-time';
import { decimalDisplay, moneyDisplay } from '../core/decimal-format';
import { decimalInput } from '../core/decimal-input';
import { RecordChoice, decimalUnits, recordDate, recordId } from '../core/ledger-contracts';
import { ApiError, errorMessage } from '../core/transport';
import { VehicleCostCategory, VehicleCostItem, VehicleCostRequest, VehicleLedger, VehicleList, VehicleReverseRequest, VehicleTripItem, VehicleTripRequest,
  validCostChange, validTripChange, validVehicleLedger, validVehicleList, validVehicleMonth, vehicleCostCategories } from '../core/vehicle-ledger.models';
import { LedgerPickerComponent } from './ledger-picker.component';

@Component({ selector: 'app-vehicle-ledger', imports: [FormsModule, RouterLink, LedgerPickerComponent], templateUrl: './vehicle-ledger.component.html', styleUrl: './vehicle-ledger.component.css' })
export class VehicleLedgerComponent {
  readonly auth = inject(AuthService); private readonly api = inject(ApiService); private readonly route = inject(ActivatedRoute);
  readonly vehicles = signal<VehicleList | null>(null); readonly ledger = signal<VehicleLedger | null>(null); readonly listLoading = signal(false); readonly loading = signal(false);
  readonly busy = signal(false); readonly stale = signal(false); readonly error = signal(''); readonly notice = signal(''); readonly tab = signal<'trips' | 'costs'>('trips'); readonly editing = signal(false);
  readonly reversing = signal<{ kind: 'trips' | 'costs'; id: string } | null>(null); readonly driver = signal<RecordChoice | null>(null); readonly project = signal<RecordChoice | null>(null);
  readonly allowed = computed(() => ['ManagingDirector', 'Office', 'Accounting', 'SiteManager'].includes(this.auth.tenant()?.role || '') && this.auth.can('vehicle-trips.read') && this.auth.can('vehicle-costs.read'));
  readonly writable = computed(() => this.allowed() && ['ManagingDirector', 'Office', 'SiteManager'].includes(this.auth.tenant()?.role || '') && this.auth.can('vehicle-trips.write') && this.auth.can('vehicle-costs.write') && this.auth.hasModule('purchasing') && !this.auth.recoveryHold());
  readonly categories = vehicleCostCategories; readonly money = moneyDisplay;
  search = ''; month = ''; selectedId = ''; startLocal = ''; endLocal = ''; startOffset = ''; endOffset = ''; odometerStart = ''; odometerEnd = ''; purpose = ''; origin = ''; destination = ''; tripNotes = '';
  spentOn = ''; category: VehicleCostCategory = 'Fuel'; amount = ''; reference = ''; costNotes = ''; confirmed = false; reverseReason = '';
  private generation = 0; private listVersion = 0; private detailVersion = 0; private linkedId = this.route.snapshot.queryParamMap.get('equipmentId'); private linkedMonth = this.route.snapshot.queryParamMap.get('month');

  constructor() {
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
    inject(DestroyRef).onDestroy(() => { this.generation++; });
  }
  private reset(): void {
    this.generation++; this.listVersion++; this.detailVersion++; this.vehicles.set(null); this.ledger.set(null); this.selectedId = ''; this.search = ''; this.month = '';
    this.error.set(''); this.notice.set(''); this.busy.set(false); this.loading.set(false); this.listLoading.set(false); this.stale.set(false); this.clearForm();
    if (this.allowed()) void this.loadVehicles();
  }
  private current(generation: number, access: string): boolean { return generation === this.generation && access === this.auth.accessSignature() && this.allowed(); }
  async loadVehicles(page = 1): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    const generation = this.generation, access = this.auth.accessSignature(), version = ++this.listVersion, tenant = this.auth.activeTenantId(); this.listLoading.set(true);
    try {
      const raw = await this.api.get<unknown>(`vehicle-ledger/vehicles?${new URLSearchParams({ search: this.search.trim(), page: String(page) })}`);
      if (!this.current(generation, access) || version !== this.listVersion) return;
      const list = validVehicleList(raw, tenant, page); this.vehicles.set(list);
      if (!this.month) this.month = validVehicleMonth(this.linkedMonth) ? this.linkedMonth : list.businessDate.slice(0, 7);
      const linked = this.linkedId; this.linkedId = null; this.linkedMonth = null;
      if (recordId(linked)) await this.open(linked);
    } catch (error) { if (this.current(generation, access) && version === this.listVersion) { this.vehicles.set(null); this.error.set(vehicleLedgerError(error)); await this.refreshAccess(error); } }
    finally { if (this.current(generation, access) && version === this.listVersion) this.listLoading.set(false); }
  }
  async open(id: string): Promise<void> {
    if (this.busy() || !recordId(id)) return;
    this.detailVersion++; this.selectedId = id; this.ledger.set(null); this.clearForm(); this.error.set(''); this.notice.set(''); this.stale.set(false);
    await this.loadLedger(1, 1, true);
  }
  async applyMonth(): Promise<void> {
    if (!validVehicleMonth(this.month)) { this.error.set('Wählen Sie einen gültigen Monat zwischen 1900 und 9998.'); return; }
    if (!this.selectedId || this.busy()) return;
    this.clearForm(); this.notice.set(''); await this.loadLedger(1, 1, true);
  }
  async reload(): Promise<void> { if (!this.busy()) { this.clearForm(); await this.loadLedger(1, 1, true); } }
  async loadLedger(tripPage: number, costPage: number, reset: boolean): Promise<void> {
    if (!this.allowed() || !this.selectedId || !validVehicleMonth(this.month)) return;
    const generation = this.generation, access = this.auth.accessSignature(), version = ++this.detailVersion, id = this.selectedId, month = this.month, tenant = this.auth.activeTenantId(), before = this.ledger();
    this.loading.set(true); this.error.set(''); if (reset) this.stale.set(false);
    try {
      const raw = await this.api.get<unknown>(`equipment/${id}/vehicle-ledger?${new URLSearchParams({ month, tripPage: String(tripPage), costPage: String(costPage) })}`);
      if (!this.current(generation, access) || version !== this.detailVersion || this.selectedId !== id || this.month !== month) return;
      const value = validVehicleLedger(raw, id, tenant, month, tripPage, costPage);
      if (!reset && before && value.vehicle.revision !== before.vehicle.revision) { this.stale.set(true); this.error.set('Die Fahrzeugakte wurde während des Blätterns geändert. Laden Sie sie vor weiteren Buchungen neu.'); }
      this.ledger.set(value);
    } catch (error) {
      if (this.current(generation, access) && version === this.detailVersion) {
        this.stale.set(true); this.error.set(vehicleLedgerError(error));
        if (reset || error instanceof ApiError && [403, 404].includes(error.status)) this.ledger.set(null);
        await this.refreshAccess(error);
      }
    } finally { if (this.current(generation, access) && version === this.detailVersion) this.loading.set(false); }
  }
  private clearForm(): void {
    this.editing.set(false); this.reversing.set(null); this.driver.set(null); this.project.set(null); this.startLocal = ''; this.endLocal = ''; this.startOffset = ''; this.endOffset = '';
    this.odometerStart = ''; this.odometerEnd = ''; this.purpose = ''; this.origin = ''; this.destination = ''; this.tripNotes = ''; this.spentOn = ''; this.category = 'Fuel'; this.amount = ''; this.reference = ''; this.costNotes = ''; this.confirmed = false; this.reverseReason = '';
  }
  selectTab(tab: 'trips' | 'costs'): void { if (!this.busy()) { this.tab.set(tab); this.clearForm(); } }
  actionAllowed(): boolean { const ledger = this.ledger(); return this.writable() && !!ledger && !ledger.vehicle.archived && ledger.month === this.month && !this.busy() && !this.loading() && !this.stale(); }
  newEntry(): void { if (this.actionAllowed()) { this.clearForm(); this.editing.set(true); this.spentOn = this.ledger()!.businessDate; this.error.set(''); this.notice.set(''); } }
  cancel(): void { if (!this.busy()) this.clearForm(); }
  choices(local: string) { const ledger = this.ledger(); return ledger ? businessTimeChoices(local, ledger.timeZone) : []; }
  km(value: string): string { return (decimalDisplay(value, 0, 3) ?? 'Ungültige Strecke') + ' km'; }
  rate(value: string | null): string { return value === null ? 'Ohne Fahrstrecke' : (decimalDisplay(value, 2, 4) ?? 'Ungültiger Wert') + ' €/km'; }
  date(value: string): string { return value.slice(0, 10).split('-').reverse().join('.'); }
  instant(value: string): string { return new Intl.DateTimeFormat('de-DE', { timeZone: this.ledger()?.timeZone, dateStyle: 'short', timeStyle: 'short' }).format(new Date(value)); }
  tripValid(): boolean {
    return this.actionAllowed() && this.editing() && this.tab() === 'trips' && !!this.driver() && !!this.startLocal && !!this.endLocal
      && !!this.purpose.trim() && this.purpose.length <= 1000 && !!this.origin.trim() && !!this.destination.trim() && this.origin.length <= 240 && this.destination.length <= 240
      && !!this.odometerStart && !!this.odometerEnd && this.tripNotes.length <= 4000 && this.confirmed;
  }
  costValid(): boolean {
    return this.actionAllowed() && this.editing() && this.tab() === 'costs' && recordDate(this.spentOn) && this.spentOn <= this.ledger()!.businessDate
      && !!this.amount && !!this.reference.trim() && this.reference.length <= 240 && this.costNotes.length <= 4000 && this.confirmed;
  }
  async saveTrip(): Promise<void> {
    if (!this.tripValid()) return;
    const ledger = this.ledger()!;
    try {
      const start = resolveBusinessTime(this.startLocal, ledger.timeZone, this.startOffset), end = resolveBusinessTime(this.endLocal, ledger.timeZone, this.endOffset);
      if (Date.parse(end) <= Date.parse(start) || Date.parse(end) - Date.parse(start) > 86_400_000 || Date.parse(end) > Date.now()) throw new Error('Eine dokumentierte Fahrt muss beendet sein und zwischen einer Sekunde und 24 Stunden dauern.');
      const odometerStartKm = decimalInput(this.odometerStart), odometerEndKm = decimalInput(this.odometerEnd), from = decimalUnits(odometerStartKm, 3), to = decimalUnits(odometerEndKm, 3);
      if (from === null || to === null || to <= from || to > 9_999_999_999n) throw new Error('Der Kilometerstand bei Ankunft muss größer sein als bei Abfahrt. Verwenden Sie höchstens drei Nachkommastellen.');
      await this.mutateTrip({ driverEmployeeId: this.driver()!.id, projectId: this.project()?.id || null, start, end, odometerStartKm, odometerEndKm,
        purpose: this.purpose.trim(), origin: this.origin.trim(), destination: this.destination.trim(), notes: this.tripNotes.trim() });
    } catch (error) { this.error.set(vehicleLedgerError(error)); }
  }
  async saveCost(): Promise<void> {
    if (!this.costValid()) return;
    try {
      const amount = decimalInput(this.amount), cents = decimalUnits(amount, 2);
      if (cents === null || cents <= 0n || cents > 100_000_000_000_000n) throw new Error('Geben Sie einen positiven Kostenbetrag mit höchstens zwei Nachkommastellen ein.');
      await this.mutateCost({ projectId: this.project()?.id || null, spentOn: this.spentOn, category: this.category, amount, currency: 'EUR', reference: this.reference.trim(), notes: this.costNotes.trim() });
    } catch (error) { this.error.set(vehicleLedgerError(error)); }
  }
  beginReverse(kind: 'trips' | 'costs', item: VehicleTripItem | VehicleCostItem): void { if (this.actionAllowed() && !item.reversal) { this.clearForm(); this.reversing.set({ kind, id: item.entry.id }); this.error.set(''); this.notice.set(''); } }
  async reverseTrip(item: VehicleTripItem): Promise<void> { if (this.reverseValid('trips', item)) await this.mutateTrip({ sourceDigest: item.entry.digest, reason: this.reverseReason.trim() }, item); }
  async reverseCost(item: VehicleCostItem): Promise<void> { if (this.reverseValid('costs', item)) await this.mutateCost({ sourceDigest: item.entry.digest, reason: this.reverseReason.trim() }, item); }
  private reverseValid(kind: 'trips' | 'costs', item: VehicleTripItem | VehicleCostItem): boolean { return this.actionAllowed() && !item.reversal && this.reversing()?.kind === kind && this.reversing()?.id === item.entry.id && !!this.reverseReason.trim() && this.reverseReason.length <= 2000; }
  private async mutateTrip(request: VehicleTripRequest | VehicleReverseRequest, original?: VehicleTripItem): Promise<void> {
    await this.mutate('vehicle-trips', request, original?.entry.id, (raw, before) => validTripChange(raw, before, request, original).vehicle.revision,
      original ? 'Fahrt storniert; das Original bleibt erhalten.' : 'Fahrt dokumentiert.');
  }
  private async mutateCost(request: VehicleCostRequest | VehicleReverseRequest, original?: VehicleCostItem): Promise<void> {
    await this.mutate('vehicle-costs', request, original?.entry.id, (raw, before) => validCostChange(raw, before, request, original).vehicle.revision,
      original ? 'Kostenbeleg storniert; das Original bleibt erhalten.' : 'Fahrzeugkosten dokumentiert.');
  }
  private async mutate(kind: string, request: unknown, sourceId: string | undefined, validate: (raw: unknown, before: VehicleLedger) => number, notice: string): Promise<void> {
    const before = this.ledger(); if (!before || !this.actionAllowed()) return;
    const generation = this.generation, access = this.auth.accessSignature(), id = before.vehicle.id; this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      const raw = await this.api.command<unknown>(`equipment/${id}/${kind}${sourceId ? `/${sourceId}/reverse` : ''}`, request, before.vehicle.revision);
      if (!this.current(generation, access) || this.selectedId !== id) return;
      const revision = validate(raw, before); this.ledger.set({ ...before, vehicle: { ...before.vehicle, revision } }); this.clearForm(); this.notice.set(notice);
      // The confirmation remains visible if the following read fails. Fresh totals require a complete committed snapshot.
      await this.loadLedger(1, 1, true);
    } catch (error) {
      if (this.current(generation, access) && this.selectedId === id) {
        this.error.set(vehicleLedgerError(error)); if (!(error instanceof ApiError) || [409, 412, 503].includes(error.status)) this.stale.set(true);
        await this.refreshAccess(error);
      }
    } finally { if (this.current(generation, access)) this.busy.set(false); }
  }
  private async refreshAccess(error: unknown): Promise<void> { if (error instanceof ApiError && [401, 403].includes(error.status)) await this.auth.refresh().catch(() => undefined); }
}

function vehicleLedgerError(error: unknown): string {
  if (error instanceof ApiError) {
    const messages: Record<string, string> = {
      vehicle_trip_overlap: 'Für dieses Fahrzeug ist bereits eine Fahrt im gewählten Zeitfenster dokumentiert.',
      vehicle_driver_overlap: 'Die fahrende Person ist im gewählten Zeitfenster bereits einer anderen dokumentierten Fahrt zugeordnet.',
      vehicle_odometer_conflict: 'Der Kilometerstand widerspricht einer früheren oder späteren Fahrt. Prüfen Sie die Historie und stornieren Sie einen fehlerhaften Beleg ausdrücklich.',
      vehicle_already_reversed: 'Dieser Beleg ist bereits storniert. Laden Sie die Fahrzeugakte neu.',
      vehicle_source_mismatch: 'Der ausgewählte Originalbeleg stimmt nicht mit der Fahrzeugakte überein. Laden Sie die Akte neu.',
      vehicle_driver: 'Wählen Sie einen aktiven Mitarbeiter dieses Betriebs als fahrende Person.',
      vehicle_trip_window: 'Die Fahrt muss bereits beendet sein und darf höchstens 24 Stunden dauern.',
      vehicle_cost_date: 'Der Kostenbeleg darf kein zukünftiges Betriebsdatum haben.',
      vehicle_required: 'Für diese Aktion muss ein vorhandenes Fahrzeug ausgewählt sein.'
    };
    if (error.problem.code && messages[error.problem.code]) return messages[error.problem.code];
  }
  return errorMessage(error);
}
